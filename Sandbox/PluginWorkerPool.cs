using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// (Plugin dosya yolu + tip)'e göre PluginWorkerHandle'ları yöneten, long-lived pool.
    /// Bir plugin+tip için worker bir kere başlar, admin açıkça kapatana/promote edene kadar yaşar.
    ///
    /// Aynı plugin+tip FARKLI ayarlarla (includeNonPublic, MaxConcurrency, HostPath, AutoRestartOnCrash...)
    /// istenirse sessizce eski worker'ı döndürmek yerine InvalidOperationException fırlatılır - ayar
    /// değiştirmek için önce StopAsync.
    /// </summary>
    public sealed class PluginWorkerPool : IAsyncDisposable
    {
        private sealed class Entry
        {
            public Entry(PluginWorkerOptions options, bool includeNonPublic, Lazy<Task<PluginWorkerHandle>> handle)
            {
                Options = options;
                IncludeNonPublic = includeNonPublic;
                Handle = handle;
            }

            public PluginWorkerOptions Options { get; }
            public bool IncludeNonPublic { get; }
            public Lazy<Task<PluginWorkerHandle>> Handle { get; }
        }

        // Değer Lazy<Task<...>> (Task'ın kendisi DEĞİL): ConcurrentDictionary.GetOrAdd, aynı anahtar için
        // birden fazla thread eş zamanlı çağırırsa factory'yi BİRDEN FAZLA KEZ çalıştırabilir - Lazy
        // (varsayılan ExecutionAndPublication modu) bunu tek bir gerçek başlatmaya indirger.
        private readonly ConcurrentDictionary<string, Entry> _workers = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Pool'daki herhangi bir worker çöktüğünde (bkz. PluginWorkerHandle.Crashed).</summary>
        public event EventHandler<PluginWorkerCrashedEventArgs>? WorkerCrashed;

        /// <summary>Pool'daki herhangi bir worker yeniden başlatıldığında (bkz. PluginWorkerHandle.Restarted).</summary>
        public event EventHandler<PluginWorkerRestartedEventArgs>? WorkerRestarted;

        private static string Key(string pluginFilePath, string typeFullName) =>
            $"{System.IO.Path.GetFullPath(pluginFilePath)}|{typeFullName}";

        /// <summary>
        /// TODO 20: varsa mevcut (aynı dosya+tip için) worker'ı döndür, yoksa yeni bir
        /// PluginWorkerHandle yaratıp StartAsync çağırarak döndürür.
        /// </summary>
        public Task<PluginWorkerHandle> GetOrStartAsync(string pluginFilePath, string typeFullName, PluginWorkerOptions options, bool includeNonPublic = false)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string key = Key(pluginFilePath, typeFullName);

            var entry = _workers.GetOrAdd(key, _ => new Entry(options, includeNonPublic, new Lazy<Task<PluginWorkerHandle>>(async () =>
            {
                var handle = new PluginWorkerHandle(pluginFilePath, typeFullName, options, includeNonPublic);
                handle.Crashed += (s, e) => WorkerCrashed?.Invoke(s, e);
                handle.Restarted += (s, e) => WorkerRestarted?.Invoke(s, e);
                await handle.StartAsync().ConfigureAwait(false);
                return handle;
            })));

            var diff = Differences(entry, options, includeNonPublic);
            if (diff.Count > 0)
                throw new InvalidOperationException(
                    $"[PluginWorkerPool] '{typeFullName}' için zaten FARKLI ayarlarla çalışan bir worker var " +
                    $"({string.Join(", ", diff)}). Ayarları değiştirmek için önce StopAsync çağırın.");

            return AwaitAndEvictOnFailure(key, entry);
        }

        /// <summary>Çalışan (başlatılmış) worker'lar - admin ekranı/tanılama için.</summary>
        public IReadOnlyList<PluginWorkerHandle> RunningWorkers =>
            _workers.Values
                .Where(e => e.Handle.IsValueCreated && e.Handle.Value.IsCompletedSuccessfully)
                .Select(e => e.Handle.Value.Result)
                .ToList();

        private static List<string> Differences(Entry e, PluginWorkerOptions o, bool includeNonPublic)
        {
            var d = new List<string>();
            var a = e.Options;
            if (ReferenceEquals(a, o) && e.IncludeNonPublic == includeNonPublic) return d;
            if (e.IncludeNonPublic != includeNonPublic) d.Add($"includeNonPublic {e.IncludeNonPublic}->{includeNonPublic}");
            if (a.MaxConcurrency != o.MaxConcurrency) d.Add($"MaxConcurrency {a.MaxConcurrency}->{o.MaxConcurrency}");
            if (a.AutoRestartOnCrash != o.AutoRestartOnCrash) d.Add($"AutoRestartOnCrash {a.AutoRestartOnCrash}->{o.AutoRestartOnCrash}");
            if (!string.Equals(a.HostPath, o.HostPath, StringComparison.OrdinalIgnoreCase)) d.Add("HostPath");
            if (a.HostIsDotnetDll != o.HostIsDotnetDll) d.Add("HostIsDotnetDll");
            if (a.HeartbeatIntervalMs != o.HeartbeatIntervalMs) d.Add("HeartbeatIntervalMs");
            if (a.MissedHeartbeatsBeforeKill != o.MissedHeartbeatsBeforeKill) d.Add("MissedHeartbeatsBeforeKill");
            return d;
        }

        // StartAsync başarısız olursa (ör. DLL bozuk, worker açılamadı) başarısız Lazy'i dictionary'de
        // BIRAKMAK, o key için TÜM sonraki GetOrStartAsync çağrılarını SONSUZA KADAR aynı hatayla
        // patlatır (Lazy bir kere başarısız olunca hep aynı exception'ı fırlatır). Bu yüzden
        // başarısızlıkta kaydı sözlükten çıkarıyoruz ki bir sonraki çağrı YENİDEN denesin.
        private async Task<PluginWorkerHandle> AwaitAndEvictOnFailure(string key, Entry entry)
        {
            try
            {
                return await entry.Handle.Value.ConfigureAwait(false);
            }
            catch
            {
                _workers.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                throw;
            }
        }

        /// <summary>
        /// TODO 21: belirli bir plugin+tip'in worker'ını (varsa) düzgün kapat (Shutdown handshake +
        /// graceful exit bekle) ve pool'dan çıkar.
        /// </summary>
        public async Task StopAsync(string pluginFilePath, string typeFullName)
        {
            if (!_workers.TryRemove(Key(pluginFilePath, typeFullName), out var entry))
                return; // hiç başlatılmamış - yapacak bir şey yok

            if (!entry.Handle.IsValueCreated)
                return;

            try
            {
                var handle = await entry.Handle.Value.ConfigureAwait(false);
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // StartAsync zaten başarısız olmuşsa (exception fırlatmışsa) kapatacak bir şey yok.
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var key in _workers.Keys)
            {
                if (_workers.TryRemove(key, out var entry) && entry.Handle.IsValueCreated)
                {
                    try
                    {
                        var handle = await entry.Handle.Value.ConfigureAwait(false);
                        await handle.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // sıradaki worker'ın kapanmasını engellememeli
                    }
                }
            }
        }
    }
}