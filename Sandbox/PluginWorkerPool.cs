using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// (Plugin dosya yolu + tip)'e göre PluginWorkerHandle'ları yöneten, long-lived pool.
    /// Bir plugin+tip için worker bir kere başlar, admin açıkça kapatana/promote edene kadar yaşar.
    /// </summary>
    public sealed class PluginWorkerPool : IAsyncDisposable
    {
        // Değer Lazy<Task<...>> (Task'ın kendisi DEĞİL): ConcurrentDictionary.GetOrAdd, aynı anahtar
        // için birden fazla thread eş zamanlı çağırırsa factory'yi BİRDEN FAZLA KEZ çalıştırabilir -
        // Lazy (varsayılan ExecutionAndPublication modu) bunu tek bir gerçek başlatmaya indirger.
        private readonly ConcurrentDictionary<string, Lazy<Task<PluginWorkerHandle>>> _workers = new();

        private static string Key(string pluginFilePath, string typeFullName) =>
            $"{pluginFilePath}|{typeFullName}";

        /// <summary>
        /// TODO 20: varsa mevcut (aynı dosya+tip için) worker'ı döndür, yoksa yeni bir
        /// PluginWorkerHandle yaratıp StartAsync çağırarak döndürür.
        /// </summary>
        public Task<PluginWorkerHandle> GetOrStartAsync(string pluginFilePath, string typeFullName, PluginWorkerOptions options, bool includeNonPublic = false)
        {
            string key = Key(pluginFilePath, typeFullName);

            var lazy = _workers.GetOrAdd(key, _ => new Lazy<Task<PluginWorkerHandle>>(async () =>
            {
                var handle = new PluginWorkerHandle(pluginFilePath, typeFullName, options, includeNonPublic);
                await handle.StartAsync().ConfigureAwait(false);
                return handle;
            }));

            return AwaitAndEvictOnFailure(key, lazy);
        }

        // StartAsync başarısız olursa (ör. DLL bozuk, worker açılamadı) başarısız Lazy'i dictionary'de
        // BIRAKMAK, o key için TÜM sonraki GetOrStartAsync çağrılarını SONSUZA KADAR aynı hatayla
        // patlatır (Lazy bir kere başarısız olunca hep aynı exception'ı fırlatır). Bu yüzden
        // başarısızlıkta kaydı sözlükten çıkarıyoruz ki bir sonraki çağrı YENİDEN denesin.
        private async Task<PluginWorkerHandle> AwaitAndEvictOnFailure(string key, Lazy<Task<PluginWorkerHandle>> lazy)
        {
            try
            {
                return await lazy.Value.ConfigureAwait(false);
            }
            catch
            {
                _workers.TryRemove(new System.Collections.Generic.KeyValuePair<string, Lazy<Task<PluginWorkerHandle>>>(key, lazy));
                throw;
            }
        }

        /// <summary>
        /// TODO 21: belirli bir plugin+tip'in worker'ını (varsa) düzgün kapat (Shutdown handshake +
        /// graceful exit bekle) ve pool'dan çıkar.
        /// </summary>
        public async Task StopAsync(string pluginFilePath, string typeFullName)
        {
            string key = Key(pluginFilePath, typeFullName);
            if (!_workers.TryRemove(key, out var lazy))
                return; // hiç başlatılmamış - yapacak bir şey yok

            if (!lazy.IsValueCreated)
                return; // henüz StartAsync tamamlanmamış/başlamamış bir kayıt - dispose edilecek bir şey yok

            try
            {
                var handle = await lazy.Value.ConfigureAwait(false);
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
                if (_workers.TryRemove(key, out var lazy) && lazy.IsValueCreated)
                {
                    try
                    {
                        var handle = await lazy.Value.ConfigureAwait(false);
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