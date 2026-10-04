using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DSO.Core.Evoker.Plugins.Loading;
using DSO.Core.Evoker.Plugins.Sandbox;
using DSO.Core.Evoker.Plugins.Scanning;

namespace DSO.Core.Evoker.Plugins.Management
{
    /// <summary>Tüm plugin'ler için ortak (plugin'e özgü olmayan) ayarlar.</summary>
    public sealed class PluginManagerOptions
    {
        /// <summary>Sandbox worker'ı (DSO.Core.Evoker.PluginHost) yolu - bkz. PluginWorkerOptions.HostPath.</summary>
        public string HostPath { get; init; } = "";
        public bool HostIsDotnetDll { get; init; } = true;
        public int StartupTimeoutMs { get; init; } = 15000;
        public bool NotifyOnCrash { get; init; } = true;
        public string? CrashLogFilePath { get; init; }
    }

    /// <summary>
    /// Admin ekranının arkasındaki servis: plugin kayıtlarını (IPluginConfigStore) tutar, her plugin'i kayıttaki
    /// moda göre (Sandbox / InProcess) çalıştırır ve uygulamaya ömür boyu değişmeyen bir IPluginBuilder verir.
    ///
    ///   var mgr = new PluginManager(new JsonFilePluginConfigStore("plugins.json"), new() { HostPath = "...PluginHost.dll" });
    ///   await mgr.InitializeAsync();
    ///   await mgr.RegisterAsync(new PluginRegistration { Id = "erp", FilePath = @"C:\plugins\Erp.dll", TypeFullName = "Erp.Connector" });
    ///   IPluginBuilder erp = mgr.Get("erp");                       // uygulama bunu saklar
    ///   int n = await erp.InvokeAsync&lt;int&gt;("Sync", 100);
    ///   await mgr.SetModeAsync("erp", PluginExecutionMode.InProcess); // admin: "güveniyorum" - erp nesnesi aynen çalışır
    ///
    /// Tüm yönetim işlemleri (mod değişimi, güncelleme, durdurma) plugin başına kilitlidir; farklı plugin'ler
    /// birbirini beklemez.
    /// </summary>
    public sealed class PluginManager : IAsyncDisposable
    {
        private sealed class Slot
        {
            public Slot(PluginRegistration reg) => Registration = reg;
            public PluginRegistration Registration;
            public readonly SemaphoreSlim Lock = new(1, 1);
            public PluginWorkerHandle? Handle;
            public ManagedDotNetPluginLoader? Loader;
            public IPluginBuilder? Inner;
            public int Version;
            public SwitchablePluginBuilder? Proxy;
            public bool? LastUnloadReleased;
            public DateTime? LastCrashUtc;
            public string? LastCrashReason;
        }

        private readonly IPluginConfigStore _store;
        private readonly PluginManagerOptions _options;
        private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _saveLock = new(1, 1);
        private bool _initialized;

        /// <summary>Herhangi bir sandbox plugin'in worker'ı çöktüğünde (id + ayrıntı). UI/e-posta/log'a bağlanabilir.</summary>
        public event Action<string, PluginWorkerCrashedEventArgs>? PluginCrashed;

        /// <summary>Bir plugin'in modu değiştiğinde (id, yeni mod).</summary>
        public event Action<string, PluginExecutionMode>? ModeChanged;

        public PluginManager(IPluginConfigStore store, PluginManagerOptions options)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task InitializeAsync()
        {
            foreach (var reg in await _store.LoadAsync().ConfigureAwait(false))
                _slots[reg.Id] = new Slot(reg.Clone());
            _initialized = true;
        }

        // ================= Kayıt / admin işlemleri =================

        public IReadOnlyList<PluginRegistration> Registrations =>
            _slots.Values.Select(s => s.Registration.Clone()).OrderBy(r => r.Id).ToList();

        public PluginRegistration GetRegistrationCopy(string id) => GetSlot(id).Registration.Clone();

        /// <summary>
        /// Yeni plugin kaydı. DLL çalıştırılmadan doğrulanır (.NET assembly'si mi, tip içinde mi).
        /// Id verilmezse GUID üretilir (bir kez, kalıcı). Aynı DLL + tip farklı Id'lerle tekrar eklenebilir.
        /// Aynı Id zaten varsa, dosya/tip bulunamazsa: exception YOK - Success=false + mesaj, kayıt eklenmez.
        /// </summary>
        public async Task<PluginRegistrationResult> RegisterAsync(PluginRegistration registration)
        {
            EnsureInitialized();
            if (registration == null) throw new ArgumentNullException(nameof(registration));

            var reg = registration.Clone();
            reg.Id = string.IsNullOrWhiteSpace(reg.Id) ? Guid.NewGuid().ToString("N") : reg.Id.Trim();
            if (_slots.TryGetValue(reg.Id, out var existing))
                return PluginRegistrationResult.Fail(
                    $"'{reg.Id}' adı zaten kullanılıyor ({existing.Registration.DisplayName}) - kayıt eklenmedi. Farklı bir ad verin ya da mevcut kaydı UpdateAsync ile değiştirin.",
                    reg.Id);

            var error = ValidationError(reg);
            if (error != null)
                return PluginRegistrationResult.Fail(error + " - kayıt eklenmedi.", reg.Id);

            reg.FilePath = Path.GetFullPath(reg.FilePath);
            reg.UpdatedUtc = DateTime.UtcNow;
            if (!_slots.TryAdd(reg.Id, new Slot(reg)))
                return PluginRegistrationResult.Fail($"'{reg.Id}' adı aynı anda başka bir kayıtla eklendi - kayıt eklenmedi.", reg.Id);

            await SaveAsync().ConfigureAwait(false);
            int sameTypeCount = _slots.Values.Count(x =>
                string.Equals(x.Registration.FilePath, reg.FilePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Registration.TypeFullName, reg.TypeFullName, StringComparison.OrdinalIgnoreCase));
            return PluginRegistrationResult.Ok(reg.Id,
                sameTypeCount > 1 ? $"{reg.DisplayName} eklendi (bu plugin'in {sameTypeCount}. örneği)." : $"{reg.DisplayName} eklendi.");
        }

        /// <summary>
        /// Kaydı değiştirir (dosya yolu, tip, mod, sandbox ayarları...) ve plugin çalışıyorsa YENİ ayarlarla
        /// yeniden başlatır (uygulamanın elindeki IPluginBuilder geçerli kalır; plugin state'i sıfırlanır).
        /// </summary>
        public async Task UpdateAsync(string id, Action<PluginRegistration> change)
        {
            var slot = GetSlot(id);
            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var updated = slot.Registration.Clone();
                change(updated);
                updated.Id = slot.Registration.Id; // Id değiştirilemez
                updated.FilePath = Path.GetFullPath(updated.FilePath);
                Validate(updated);
                updated.UpdatedUtc = DateTime.UtcNow;

                bool wasRunning = slot.Inner != null;
                var oldMode = slot.Registration.Mode;
                await StopCoreAsync(slot).ConfigureAwait(false);
                slot.Registration = updated;
                if (wasRunning && updated.Enabled)
                    await StartCoreAsync(slot).ConfigureAwait(false);
                else if (slot.Proxy != null)
                    await slot.Proxy.RebindAsync(null).ConfigureAwait(false);

                if (oldMode != updated.Mode) RaiseModeChanged(id, updated.Mode);
            }
            finally { slot.Lock.Release(); }
            await SaveAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Admin kararı: plugin'i sandbox'a ya da in-process'e al. Çalışıyorsa canlı geçiş yapılır:
        ///   Sandbox -> InProcess: worker düzgün kapatılır, plugin host'a (kendi AssemblyLoadContext'ine) yüklenir.
        ///   InProcess -> Sandbox: ÖNCE yeni worker başlatılır, çağrılar ona yönlendirilir, SONRA eski in-process
        ///   kopya boşaltılır (bellekten atılır - bkz. GetStatus().LastUnloadReleasedMemory).
        /// Plugin state'i sıfırlanır. Karar kalıcı olarak kaydedilir.
        /// </summary>
        public Task SetModeAsync(string id, PluginExecutionMode mode) => UpdateModeAsync(id, mode);

        private async Task UpdateModeAsync(string id, PluginExecutionMode mode)
        {
            var slot = GetSlot(id);
            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (slot.Registration.Mode == mode) return;
                var reg = slot.Registration.Clone();
                reg.Mode = mode;
                reg.UpdatedUtc = DateTime.UtcNow;

                if (slot.Inner == null)
                {
                    slot.Registration = reg; // çalışmıyor - sadece karar değişir, ilk kullanımda yeni modda başlar
                }
                else if (mode == PluginExecutionMode.InProcess)
                {
                    // Sandbox -> InProcess: kesintisiz - ÖNCE in-process kopya yüklenir ve çağrılar ona yönlendirilir,
                    // SONRA eski worker kapatılır. (PluginWorkerHandle.PromoteToInProcessAsync önce worker'ı kapattığı
                    // için aradaki kısa pencerede gelen çağrılar hata alıyordu - yük altında test edilerek bulundu.)
                    var oldHandle = slot.Handle!;
                    slot.Registration = reg;
                    var loader = new ManagedDotNetPluginLoader();
                    await loader.LoadInProcessAsync(reg.FilePath, reg.TypeFullName, reg.IncludeNonPublic).ConfigureAwait(false);
                    slot.Loader = loader;
                    slot.Handle = null;
                    await BindAsync(slot, loader.Builder!.AsPluginBuilder()).ConfigureAwait(false);
                    await oldHandle.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    // InProcess -> Sandbox: kesintisiz - önce yeni worker, sonra eskiyi boşalt.
                    var oldLoader = slot.Loader;
                    slot.Registration = reg;
                    var handle = await CreateStartedHandleAsync(slot).ConfigureAwait(false);
                    slot.Handle = handle;
                    slot.Loader = null;
                    await BindAsync(slot, handle.Builder).ConfigureAwait(false);
                    if (oldLoader != null)
                        slot.LastUnloadReleased = await oldLoader.UnloadAsync().ConfigureAwait(false);
                }
                RaiseModeChanged(id, mode);
            }
            finally { slot.Lock.Release(); }
            await SaveAsync().ConfigureAwait(false);
        }

        /// <summary>Plugin'i durdurur (sandbox: worker kapanır; in-process: bellekten atılır). Kayıt kalır; bir sonraki kullanımda yeniden başlar.</summary>
        public async Task StopAsync(string id)
        {
            var slot = GetSlot(id);
            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try { await StopCoreAsync(slot).ConfigureAwait(false); }
            finally { slot.Lock.Release(); }
        }

        /// <summary>Plugin'i durdurup kaydını tamamen siler. Uygulamanın elindeki IPluginBuilder bundan sonra hata verir.</summary>
        public async Task UnregisterAsync(string id)
        {
            var slot = GetSlot(id);
            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCoreAsync(slot).ConfigureAwait(false);
                _slots.TryRemove(id, out _);
            }
            finally { slot.Lock.Release(); }
            await SaveAsync().ConfigureAwait(false);
        }

        /// <summary>Plugin DLL'ini ÇALIŞTIRMADAN tarar (metotlar, property/field'lar, event'ler) - admin ekranı için.</summary>
        public PluginScanResult Scan(string id) => PluginScanner.Scan(GetSlot(id).Registration.FilePath);

        /// <summary>
        /// Kayıtlı plugin tipinin tam tanımı (bkz. PluginDescriptor; JSON için .ToJson()). Plugin ÇALIŞIYORSA ve
        /// includeValues=true ise field/property'lerin o anki değerleri de eklenir; çalışmıyorsa SADECE yapı döner
        /// (sadece değer okumak için plugin başlatılmaz) ve Warnings'te belirtilir.
        /// </summary>
        public async Task<PluginDescriptor> DescribeAsync(string id, bool includeValues = true)
        {
            var slot = GetSlot(id);
            if (includeValues && slot.Inner != null && slot.Proxy != null)
                return await slot.Proxy.DescribeAsync(includeValues: true).ConfigureAwait(false);

            var d = PluginInspector.Describe(slot.Registration.FilePath, slot.Registration.TypeFullName,
                new PluginDescribeOptions { IncludeNonPublic = true });
            if (includeValues) d.AddWarning("Plugin şu an çalışmıyor - değerler alınmadı (sadece yapı).");
            return d;
        }

        public PluginStatus GetStatus(string id)
        {
            var slot = GetSlot(id);
            return new PluginStatus
            {
                Id = slot.Registration.Id,
                Mode = slot.Registration.Mode,
                Enabled = slot.Registration.Enabled,
                IsRunning = slot.Inner != null,
                ProcessId = slot.Handle?.ProcessId,
                Generation = slot.Handle?.Generation,
                LastUnloadReleasedMemory = slot.LastUnloadReleased,
                LastCrashUtc = slot.LastCrashUtc,
                LastCrashReason = slot.LastCrashReason
            };
        }

        // ================= Uygulama tarafı =================

        /// <summary>
        /// Uygulamanın saklayıp kullanacağı builder - aynı id için hep AYNI nesne. Plugin ilk çağrıda başlatılır;
        /// mod değişimi/güncelleme/restart'ta da geçerli kalır (bkz. SwitchablePluginBuilder).
        /// </summary>
        public IPluginBuilder Get(string id)
        {
            var slot = GetSlot(id);
            lock (slot)
                return slot.Proxy ??= new SwitchablePluginBuilder(this, slot.Registration.Id, slot.Registration.DefaultTimeoutMs);
        }

        // --- proxy'nin kullandığı iç yüzey ---

        internal int Version(string id) => Volatile.Read(ref GetSlot(id).Version);

        internal IPluginBuilder? CurrentInner(string id) => _slots.TryGetValue(id, out var s) ? s.Inner : null;

        internal async Task<IPluginBuilder> GetInnerAsync(string id)
        {
            var slot = GetSlot(id);
            var inner = slot.Inner;
            if (inner != null) return inner;

            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (slot.Inner != null) return slot.Inner;
                if (!slot.Registration.Enabled)
                    throw new InvalidOperationException($"[PluginManager] '{id}' devre dışı (Enabled=false).");
                await StartCoreAsync(slot).ConfigureAwait(false);
                return slot.Inner!;
            }
            finally { slot.Lock.Release(); }
        }

        // ================= iç işler (slot kilidi altında çağrılır) =================

        private async Task StartCoreAsync(Slot slot)
        {
            // Kurtarılamaz çökmeden kalan ölü handle varsa temizle (bkz. CreateStartedHandleAsync içindeki Crashed).
            if (slot.Handle != null)
            {
                try { await slot.Handle.DisposeAsync().ConfigureAwait(false); } catch { }
                slot.Handle = null;
            }

            var reg = slot.Registration;
            if (reg.Mode == PluginExecutionMode.Sandbox)
            {
                var handle = await CreateStartedHandleAsync(slot).ConfigureAwait(false);
                slot.Handle = handle;
                await BindAsync(slot, handle.Builder).ConfigureAwait(false);
            }
            else
            {
                var loader = new ManagedDotNetPluginLoader();
                await loader.LoadInProcessAsync(reg.FilePath, reg.TypeFullName, reg.IncludeNonPublic).ConfigureAwait(false);
                slot.Loader = loader;
                await BindAsync(slot, loader.Builder!.AsPluginBuilder()).ConfigureAwait(false);
            }
        }

        private async Task<PluginWorkerHandle> CreateStartedHandleAsync(Slot slot)
        {
            var reg = slot.Registration;
            var handle = new PluginWorkerHandle(reg.FilePath, reg.TypeFullName, new PluginWorkerOptions
            {
                HostPath = _options.HostPath,
                HostIsDotnetDll = _options.HostIsDotnetDll,
                StartupTimeoutMs = _options.StartupTimeoutMs,
                NotifyOnCrash = _options.NotifyOnCrash,
                CrashLogFilePath = _options.CrashLogFilePath,
                MaxConcurrency = Math.Max(1, reg.MaxConcurrency),
                AutoRestartOnCrash = reg.AutoRestartOnCrash,
                HeartbeatIntervalMs = reg.HeartbeatIntervalMs,
                MissedHeartbeatsBeforeKill = reg.MissedHeartbeatsBeforeKill
            }, reg.IncludeNonPublic);

            string id = reg.Id;
            handle.Crashed += (_, e) =>
            {
                slot.LastCrashUtc = e.UtcTime;
                slot.LastCrashReason = e.Reason.Message;
                if (!e.WillRestart && ReferenceEquals(slot.Handle, handle))
                {
                    // Kurtarılamaz çökme: bir sonraki çağrıda taze worker başlasın diye bağlantıyı kopar.
                    slot.Inner = null;
                    Interlocked.Increment(ref slot.Version);
                }
                try { PluginCrashed?.Invoke(id, e); } catch { }
            };
            handle.Restarted += (_, _) => Interlocked.Increment(ref slot.Version);

            await handle.StartAsync().ConfigureAwait(false);
            return handle;
        }

        private async Task BindAsync(Slot slot, IPluginBuilder inner)
        {
            inner.DefaultTimeoutMs = slot.Proxy?.DefaultTimeoutMs ?? slot.Registration.DefaultTimeoutMs;
            slot.Inner = inner;
            Interlocked.Increment(ref slot.Version);
            if (slot.Proxy != null) await slot.Proxy.RebindAsync(inner).ConfigureAwait(false);
        }

        private async Task StopCoreAsync(Slot slot)
        {
            if (slot.Inner == null && slot.Handle == null && slot.Loader == null) return;

            // Önce proxy'nin eski taraftaki aboneliklerini/delegate önbelleklerini bırak (unload için şart).
            slot.Inner = null;
            Interlocked.Increment(ref slot.Version);
            if (slot.Proxy != null) await slot.Proxy.RebindAsync(null).ConfigureAwait(false);

            if (slot.Handle != null)
            {
                await slot.Handle.DisposeAsync().ConfigureAwait(false);
                slot.Handle = null;
            }
            if (slot.Loader != null)
            {
                var loader = slot.Loader;
                slot.Loader = null;
                slot.LastUnloadReleased = await loader.UnloadAsync().ConfigureAwait(false);
            }
        }

        // UpdateAsync gibi kodun doğrudan çağırdığı yerlerde exception; RegisterAsync'te mesaj olarak döner.
        private static void Validate(PluginRegistration reg)
        {
            var error = ValidationError(reg);
            if (error != null) throw new ArgumentException(error);
        }

        private static string? ValidationError(PluginRegistration reg)
        {
            if (string.IsNullOrWhiteSpace(reg.FilePath) || !File.Exists(reg.FilePath))
                return $"Plugin dosyası bulunamadı: {reg.FilePath}";
            if (string.IsNullOrWhiteSpace(reg.TypeFullName))
                return "TypeFullName boş olamaz.";

            var scan = PluginScanner.Scan(reg.FilePath);
            if (scan.Kind != PluginKind.ManagedDotNet)
                return $"'{reg.FilePath}' bir .NET assembly'si değil ({scan.Kind}) - şu an sadece managed plugin'ler destekleniyor.";
            if (!scan.Types.Any(t => string.Equals(t.FullName, reg.TypeFullName, StringComparison.OrdinalIgnoreCase)))
                return $"'{reg.TypeFullName}' tipi '{reg.FilePath}' içinde bulunamadı (ya da public değil).";
            return null;
        }

        private void RaiseModeChanged(string id, PluginExecutionMode mode)
        {
            try { ModeChanged?.Invoke(id, mode); } catch { }
        }

        private Slot GetSlot(string id)
        {
            EnsureInitialized();
            return _slots.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException($"[PluginManager] '{id}' kayıtlı değil.");
        }

        private void EnsureInitialized()
        {
            if (!_initialized) throw new InvalidOperationException("[PluginManager] Önce InitializeAsync çağrılmalı.");
        }

        private async Task SaveAsync()
        {
            await _saveLock.WaitAsync().ConfigureAwait(false);
            try { await _store.SaveAsync(_slots.Values.Select(s => s.Registration.Clone()).OrderBy(r => r.Id).ToList()).ConfigureAwait(false); }
            finally { _saveLock.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var slot in _slots.Values)
            {
                await slot.Lock.WaitAsync().ConfigureAwait(false);
                try { await StopCoreAsync(slot).ConfigureAwait(false); }
                catch { /* diğerlerini kapatmaya devam */ }
                finally { slot.Lock.Release(); }
            }
        }
    }
}