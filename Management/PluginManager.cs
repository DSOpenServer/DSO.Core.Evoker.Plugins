using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DSO.Core.Evoker.Commands;
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

        /// <summary>
        /// true (varsayılan): AKTİF plugin'ler InitializeAsync'te ARKA PLANDA hemen yüklenir ("aktif = yüklü"). İlk çağrı
        /// yüklemeyi beklemez. Başarısız olursa durum Faulted olur (LastError) ve ilk çağrıda yeniden denenir.
        /// false: aktif plugin'ler ilk kullanımda (lazy) yüklenir.
        /// </summary>
        public bool WarmStart { get; init; } = true;
    }

    /// <summary>
    /// Admin ekranının / API'nin arkasındaki servis: plugin kayıtlarını (IPluginConfigStore) tutar, her plugin'i kayıttaki
    /// moda göre (Sandbox / InProcess) çalıştırır, uygulamaya ömür boyu değişmeyen bir IPluginBuilder verir ve her kaydı
    /// <see cref="Catalog"/>'a (EvokerCatalog - aynı Guid anahtarla) JSON komut hedefi olarak ekler.
    ///
    ///   var mgr = new PluginManager(new JsonFilePluginConfigStore("plugins.json"), new() { HostPath = "...PluginHost.dll" });
    ///   await mgr.InitializeAsync();
    ///   var r = await mgr.RegisterAsync(new PluginRegistration { Name = "ERP", FilePath = @"C:\plugins\Erp.dll", TypeFullName = "Erp.Connector" });
    ///   IPluginBuilder erp = mgr.Get(r.Key!.Value);                 // uygulama bunu saklar
    ///   int n = await erp.InvokeAsync&lt;int&gt;("Sync", 100);
    ///   await mgr.SetModeAsync(r.Key.Value, PluginExecutionMode.InProcess); // admin: "güveniyorum" - erp nesnesi aynen çalışır
    ///   var res = await mgr.Catalog.ExecuteAsync(r.Key.Value, json);  // JSON komut (API bunu kullanır)
    ///
    /// Aktif = yüklü ve kullanıma açık; Pasif = sadece listede. Tüm yönetim işlemleri plugin başına kilitlidir.
    /// </summary>
    public sealed class PluginManager : IAsyncDisposable
    {
        internal sealed class Slot
        {
            public Slot(PluginRegistration reg) => Registration = reg;
            public PluginRegistration Registration;
            public readonly SemaphoreSlim Lock = new(1, 1);
            public PluginWorkerHandle? Handle;
            public ManagedDotNetPluginLoader? Loader;
            public IPluginBuilder? Inner;
            public int Version;
            public SwitchablePluginBuilder? Proxy;
            public volatile bool Removed; // UnregisterAsync sonrası - proxy'nin hızlı yolu bunu görür
            public volatile bool Starting;
            public bool? LastUnloadReleased;
            public DateTime? LastCrashUtc;
            public string? LastCrashReason;
            public bool CrashedUnrecovered;
            public string? LastError;
            public DateTime? LastErrorUtc;
        }

        private readonly IPluginConfigStore _store;
        private readonly PluginManagerOptions _options;
        private readonly ConcurrentDictionary<Guid, Slot> _slots = new();
        private readonly SemaphoreSlim _saveLock = new(1, 1);
        private bool _initialized;

        /// <summary>Herhangi bir sandbox plugin'in worker'ı çöktüğünde (anahtar + ayrıntı). UI/e-posta/log'a bağlanabilir.</summary>
        public event Action<Guid, PluginWorkerCrashedEventArgs>? PluginCrashed;

        /// <summary>Bir plugin'in modu değiştiğinde (anahtar, yeni mod).</summary>
        public event Action<Guid, PluginExecutionMode>? ModeChanged;

        /// <summary>
        /// JSON komut kataloğu: her plugin kaydı burada aynı Guid ile bir hedef (PluginTarget) olarak durur. Dışarıdan bir
        /// katalog verilirse (ör. API'nin plugin olmayan hedefleri de içeren kataloğu) plugin'ler ona eklenir.
        /// </summary>
        public EvokerCatalog Catalog { get; }

        public PluginManager(IPluginConfigStore store, PluginManagerOptions options, EvokerCatalog? catalog = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            Catalog = catalog ?? new EvokerCatalog();
        }

        public async Task InitializeAsync()
        {
            bool migrated = false;
            foreach (var stored in await _store.LoadAsync().ConfigureAwait(false))
            {
                var reg = stored.Clone();
                migrated |= Migrate(reg);
                if (_slots.ContainsKey(reg.Key)) { reg.Key = Guid.NewGuid(); migrated = true; }
                AddSlot(new Slot(reg));
            }
            _initialized = true;
            if (migrated) await SaveAsync().ConfigureAwait(false);
            if (_options.WarmStart)
                foreach (var s in _slots.Values.Where(s => s.Registration.IsActive)) WarmUpInBackground(s.Registration.Key);
        }

        // Eski kayıt dosyası: Id (isim anahtarı) -> Key (Guid ise) / Name; Enabled -> IsActive.
        private static bool Migrate(PluginRegistration reg)
        {
            bool changed = false;
            if (reg.Id != null)
            {
                if (reg.Key == Guid.Empty && Guid.TryParse(reg.Id, out var g)) reg.Key = g;
                else if (string.IsNullOrWhiteSpace(reg.Name)) reg.Name = reg.Id;
                reg.Id = null;
                changed = true;
            }
            if (reg.Enabled.HasValue) { reg.IsActive = reg.Enabled.Value; reg.Enabled = null; changed = true; }
            if (reg.Key == Guid.Empty) { reg.Key = Guid.NewGuid(); changed = true; }
            return changed;
        }

        private void AddSlot(Slot slot)
        {
            _slots[slot.Registration.Key] = slot;
            Catalog.Unregister(slot.Registration.Key);
            Catalog.Register(new PluginTarget(this, slot.Registration.Key), slot.Registration.Name, slot.Registration.Key);
        }

        /// <summary>
        /// Plugin'i (aktifse ve henüz yüklenmediyse) şimdi yükler. true = çalışıyor; false = pasif ya da yüklenemedi
        /// (ayrıntı GetStatus().LastError'da).
        /// </summary>
        public async Task<bool> WarmUpAsync(Guid key)
        {
            try
            {
                var slot = GetSlot(key);
                if (!slot.Registration.IsActive) return false;
                await GetInnerAsync(key).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"[PluginManager] '{key}' yüklenemedi: {ex.Message}");
                return false;
            }
        }

        private void WarmUpInBackground(Guid key) => _ = Task.Run(() => WarmUpAsync(key));

        // ================= Kayıt / admin işlemleri =================

        public IReadOnlyList<PluginRegistration> Registrations =>
            _slots.Values.Select(s => s.Registration.Clone()).OrderBy(r => r.UpdatedUtc).ToList();

        public bool Contains(Guid key) => _slots.ContainsKey(key);

        public PluginRegistration GetRegistrationCopy(Guid key) => GetSlot(key).Registration.Clone();

        /// <summary>
        /// Yeni plugin kaydı. DLL çalıştırılmadan doğrulanır (.NET assembly'si mi, tip içinde mi, verilen constructor
        /// argümanlarına uyan bir constructor var mı). Key her zaman yeni bir Guid'dir (verilen değer yok sayılır).
        /// IsActive=true ise kayıttan hemen sonra yüklenir - yüklenemezse kayıt YİNE eklenir, durum Faulted olur ve mesajda
        /// sebep yazılır. Beklenen hatalar exception DEĞİL: Success=false + mesaj, kayıt eklenmez.
        /// </summary>
        public async Task<PluginRegistrationResult> RegisterAsync(PluginRegistration registration)
        {
            EnsureInitialized();
            if (registration == null) throw new ArgumentNullException(nameof(registration));

            var reg = registration.Clone();
            reg.Key = Guid.NewGuid();
            reg.Id = null;
            reg.Enabled = null;
            reg.Name = string.IsNullOrWhiteSpace(reg.Name) ? null : reg.Name!.Trim();

            var error = ValidationError(reg);
            if (error != null)
                return PluginRegistrationResult.Fail(error + " - kayıt eklenmedi.");

            reg.FilePath = Path.GetFullPath(reg.FilePath);
            reg.UpdatedUtc = DateTime.UtcNow;
            var slot = new Slot(reg);
            AddSlot(slot);
            await SaveAsync().ConfigureAwait(false);

            string message = $"{reg.DisplayName} eklendi";
            int sameTypeCount = _slots.Values.Count(x =>
                string.Equals(x.Registration.FilePath, reg.FilePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Registration.TypeFullName, reg.TypeFullName, StringComparison.OrdinalIgnoreCase));
            if (sameTypeCount > 1) message += $" (bu plugin'in {sameTypeCount}. örneği)";

            if (reg.IsActive)
            {
                bool ok = await StartUnderLockAsync(slot).ConfigureAwait(false);
                message += ok ? " ve yüklendi." : $" ama YÜKLENEMEDİ: {slot.LastError}";
            }
            else message += " (pasif - aktifleştirilince yüklenecek).";
            return PluginRegistrationResult.Ok(reg.Key, message, StateOf(slot));
        }

        /// <summary>Plugin'i aktif eder ve hemen yükler. Yüklenemezse durum Faulted (LastError) - aktif kalır, sonra yeniden denenir.</summary>
        public async Task<PluginStatus> ActivateAsync(Guid key)
        {
            var slot = GetSlot(key);
            if (!slot.Registration.IsActive)
            {
                await slot.Lock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var reg = slot.Registration.Clone();
                    reg.IsActive = true;
                    reg.UpdatedUtc = DateTime.UtcNow;
                    slot.Registration = reg;
                }
                finally { slot.Lock.Release(); }
                await SaveAsync().ConfigureAwait(false);
            }
            await StartUnderLockAsync(slot).ConfigureAwait(false);
            return GetStatus(key);
        }

        /// <summary>Plugin'i pasif yapar: bellekten atılır (sandbox: worker kapanır), listede kalır, çağrılar reddedilir.</summary>
        public async Task<PluginStatus> DeactivateAsync(Guid key)
        {
            var slot = GetSlot(key);
            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCoreAsync(slot).ConfigureAwait(false);
                var reg = slot.Registration.Clone();
                reg.IsActive = false;
                reg.UpdatedUtc = DateTime.UtcNow;
                slot.Registration = reg;
                slot.LastError = null;
                slot.CrashedUnrecovered = false;
            }
            finally { slot.Lock.Release(); }
            await SaveAsync().ConfigureAwait(false);
            return GetStatus(key);
        }

        /// <summary>Aktif plugin'i yeniden yükler (ör. DLL dosyası güncellendiyse). Plugin state'i sıfırlanır.</summary>
        public async Task<PluginStatus> ReloadAsync(Guid key)
        {
            var slot = GetSlot(key);
            if (!slot.Registration.IsActive)
                throw new InvalidOperationException($"[PluginManager] '{slot.Registration.DisplayName}' pasif - önce aktifleştirin.");
            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCoreAsync(slot).ConfigureAwait(false);
                RefreshVersion(slot.Registration);
            }
            finally { slot.Lock.Release(); }
            await StartUnderLockAsync(slot).ConfigureAwait(false);
            return GetStatus(key);
        }

        /// <summary>
        /// Kaydı değiştirir (ad, dosya yolu, tip, constructor argümanları, mod, sandbox ayarları...) ve plugin çalışıyorsa YENİ
        /// ayarlarla yeniden başlatır (uygulamanın elindeki IPluginBuilder geçerli kalır; plugin state'i sıfırlanır). Key değişmez.
        /// Sadece Name / Notes değiştiyse plugin yeniden başlatılmaz.
        /// </summary>
        public async Task UpdateAsync(Guid key, Action<PluginRegistration> change)
        {
            var slot = GetSlot(key);
            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var old = slot.Registration;
                var updated = old.Clone();
                change(updated);
                updated.Key = old.Key; // anahtar değişmez
                updated.Id = null;
                updated.Enabled = null;
                updated.Name = string.IsNullOrWhiteSpace(updated.Name) ? null : updated.Name!.Trim();
                updated.FilePath = Path.GetFullPath(updated.FilePath);
                Validate(updated);
                updated.UpdatedUtc = DateTime.UtcNow;

                bool runtimeChanged = !string.Equals(old.FilePath, updated.FilePath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(old.TypeFullName, updated.TypeFullName, StringComparison.Ordinal)
                    || old.Mode != updated.Mode || old.IncludeNonPublic != updated.IncludeNonPublic
                    || old.MaxConcurrency != updated.MaxConcurrency || old.AutoRestartOnCrash != updated.AutoRestartOnCrash
                    || old.HeartbeatIntervalMs != updated.HeartbeatIntervalMs || old.MissedHeartbeatsBeforeKill != updated.MissedHeartbeatsBeforeKill
                    || old.IsActive != updated.IsActive
                    || (old.ConstructorArgs?.GetRawText() ?? "") != (updated.ConstructorArgs?.GetRawText() ?? "");

                if (runtimeChanged)
                {
                    bool wasRunning = slot.Inner != null;
                    await StopCoreAsync(slot).ConfigureAwait(false);
                    slot.Registration = updated;
                    slot.LastError = null;
                    if (wasRunning && updated.IsActive)
                    {
                        try { await StartCoreAsync(slot).ConfigureAwait(false); }
                        catch (Exception ex) { RecordError(slot, ex); }
                    }
                    else if (slot.Proxy != null)
                        await slot.Proxy.RebindAsync(null).ConfigureAwait(false);
                    if (old.Mode != updated.Mode) RaiseModeChanged(key, updated.Mode);
                }
                else
                {
                    slot.Registration = updated;
                }
                if (slot.Proxy != null && old.DefaultTimeoutMs != updated.DefaultTimeoutMs)
                    slot.Proxy.DefaultTimeoutMs = updated.DefaultTimeoutMs;
                Catalog.Rename(key, updated.Name);
            }
            finally { slot.Lock.Release(); }
            await SaveAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Admin kararı: plugin'i sandbox'a ya da in-process'e al. Çalışıyorsa canlı geçiş yapılır:
        ///   Sandbox -> InProcess: ÖNCE in-process kopya yüklenir ve çağrılar ona yönlendirilir, SONRA worker kapatılır.
        ///   InProcess -> Sandbox: ÖNCE yeni worker başlatılır, SONRA eski in-process kopya boşaltılır (bellekten atılır).
        /// Plugin state'i sıfırlanır. Karar kalıcı olarak kaydedilir.
        /// </summary>
        public async Task SetModeAsync(Guid key, PluginExecutionMode mode)
        {
            var slot = GetSlot(key);
            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (slot.Registration.Mode == mode) return;
                var reg = slot.Registration.Clone();
                reg.Mode = mode;
                reg.UpdatedUtc = DateTime.UtcNow;

                if (slot.Inner == null)
                {
                    slot.Registration = reg; // çalışmıyor - sadece karar değişir, sonraki yüklemede yeni modda başlar
                }
                else if (mode == PluginExecutionMode.InProcess)
                {
                    var oldHandle = slot.Handle!;
                    slot.Registration = reg;
                    var loader = new ManagedDotNetPluginLoader();
                    await loader.LoadInProcessAsync(reg.FilePath, reg.TypeFullName, reg.IncludeNonPublic, reg.ConstructorArgs).ConfigureAwait(false);
                    slot.Loader = loader;
                    slot.Handle = null;
                    await BindAsync(slot, loader.Builder!.AsPluginBuilder()).ConfigureAwait(false);
                    await oldHandle.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    var oldLoader = slot.Loader;
                    slot.Registration = reg;
                    var handle = await CreateStartedHandleAsync(slot).ConfigureAwait(false);
                    slot.Handle = handle;
                    slot.Loader = null;
                    await BindAsync(slot, handle.Builder).ConfigureAwait(false);
                    if (oldLoader != null)
                        slot.LastUnloadReleased = await oldLoader.UnloadAsync().ConfigureAwait(false);
                }
                RaiseModeChanged(key, mode);
            }
            finally { slot.Lock.Release(); }
            await SaveAsync().ConfigureAwait(false);
        }

        /// <summary>Plugin'i bellekten atar ama AKTİF bırakır (durum Stopped) - bir sonraki çağrıda kendiliğinden yüklenir.</summary>
        public async Task StopAsync(Guid key)
        {
            var slot = GetSlot(key);
            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try { await StopCoreAsync(slot).ConfigureAwait(false); }
            finally { slot.Lock.Release(); }
        }

        /// <summary>Plugin'i durdurup kaydını tamamen siler. Uygulamanın elindeki IPluginBuilder bundan sonra hata verir.</summary>
        public async Task UnregisterAsync(Guid key)
        {
            var slot = GetSlot(key);
            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCoreAsync(slot).ConfigureAwait(false);
                slot.Removed = true;
                _slots.TryRemove(key, out _);
                Catalog.Unregister(key);
            }
            finally { slot.Lock.Release(); }
            await SaveAsync().ConfigureAwait(false);
        }

        /// <summary>Plugin DLL'ini ÇALIŞTIRMADAN tarar (metotlar, property/field'lar, event'ler).</summary>
        public PluginScanResult Scan(Guid key) => PluginScanner.Scan(GetSlot(key).Registration.FilePath);

        /// <summary>
        /// Kayıtlı plugin tipinin tam tanımı - plugin aktif olsun olmasın (yapı DLL çalıştırılmadan okunur). Plugin
        /// ÇALIŞIYORSA ve includeValues=true ise field/property'lerin o anki değerleri de eklenir; çalışmıyorsa sadece
        /// yapı (sadece değer okumak için plugin yüklenmez). includeSamples: her üye için hazır komut şablonu.
        /// includeNonPublic: private/protected/internal üyeler de listelensin.
        /// </summary>
        public async Task<PluginDescriptor> DescribeAsync(Guid key, bool includeValues = true, bool includeSamples = false, bool includeNonPublic = true)
        {
            var slot = GetSlot(key);
            var reg = slot.Registration;
            var d = PluginInspector.Describe(reg.FilePath, reg.TypeFullName,
                new PluginDescribeOptions { IncludeNonPublic = includeNonPublic, IncludeSamples = includeSamples });
            if (!includeValues) return d;
            var inner = slot.Inner;
            if (inner != null)
            {
                try { await PluginValueSnapshot.CaptureAsync(d, inner).ConfigureAwait(false); }
                catch (Exception ex) { d.AddWarning($"Değerler okunamadı: {ex.Message}"); }
            }
            else d.AddWarning("Plugin şu an yüklü değil - değerler alınmadı (sadece yapı).");
            return d;
        }

        public PluginStatus GetStatus(Guid key) => StatusOf(GetSlot(key));

        public IReadOnlyList<PluginStatus> GetStatuses() =>
            _slots.Values.Select(StatusOf).OrderBy(s => s.UpdatedUtc).ToList();

        private PluginStatus StatusOf(Slot slot)
        {
            var r = slot.Registration;
            return new PluginStatus
            {
                Key = r.Key,
                Name = r.Name,
                DisplayName = r.DisplayName,
                TypeFullName = r.TypeFullName,
                FilePath = r.FilePath,
                AssemblyVersion = r.AssemblyVersion,
                Mode = r.Mode,
                IsActive = r.IsActive,
                State = StateOf(slot),
                IsRunning = slot.Inner != null,
                ProcessId = slot.Handle?.ProcessId,
                Generation = slot.Handle?.Generation,
                LastUnloadReleasedMemory = slot.LastUnloadReleased,
                LastError = slot.LastError,
                LastErrorUtc = slot.LastErrorUtc,
                LastCrashUtc = slot.LastCrashUtc,
                LastCrashReason = slot.LastCrashReason,
                UpdatedUtc = r.UpdatedUtc,
                Notes = r.Notes
            };
        }

        private static PluginState StateOf(Slot slot)
        {
            if (!slot.Registration.IsActive) return PluginState.Inactive;
            if (slot.Inner != null) return PluginState.Running;
            if (slot.Starting) return PluginState.Starting;
            if (slot.LastError != null) return PluginState.Faulted;
            if (slot.CrashedUnrecovered) return PluginState.Crashed;
            return PluginState.Stopped;
        }

        // ================= Uygulama tarafı =================

        /// <summary>
        /// Uygulamanın saklayıp kullanacağı builder - aynı anahtar için hep AYNI nesne. Plugin aktifse ilk çağrıda (yüklü
        /// değilse) yüklenir; mod değişimi/güncelleme/restart'ta da geçerli kalır (bkz. SwitchablePluginBuilder).
        /// </summary>
        public IPluginBuilder Get(Guid key)
        {
            var slot = GetSlot(key);
            lock (slot)
                return slot.Proxy ??= new SwitchablePluginBuilder(this, slot, key, slot.Registration.DefaultTimeoutMs);
        }

        // --- proxy'nin ve PluginTarget'ın kullandığı iç yüzey ---

        internal int Version(Guid key) => Volatile.Read(ref GetSlot(key).Version);

        internal IPluginBuilder? CurrentInner(Guid key) => _slots.TryGetValue(key, out var s) ? s.Inner : null;

        internal bool TryGetSlot(Guid key, out Slot slot) => _slots.TryGetValue(key, out slot!);

        internal async Task<IPluginBuilder> GetInnerAsync(Guid key)
        {
            var slot = GetSlot(key);
            var inner = slot.Inner;
            if (inner != null) return inner;

            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (slot.Inner != null) return slot.Inner;
                if (_disposed) throw new ObjectDisposedException(nameof(PluginManager));
                if (!slot.Registration.IsActive)
                    throw new InvalidOperationException($"[PluginManager] '{slot.Registration.DisplayName}' pasif (IsActive=false).");
                try { await StartCoreAsync(slot).ConfigureAwait(false); }
                catch (Exception ex) { RecordError(slot, ex); throw; }
                return slot.Inner!;
            }
            finally { slot.Lock.Release(); }
        }

        // ================= iç işler =================

        // Kilidi alıp yükler; hata durum olarak kaydedilir (exception fırlatmaz). true = çalışıyor.
        private async Task<bool> StartUnderLockAsync(Slot slot)
        {
            await slot.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (slot.Inner != null) return true;
                if (_disposed) return false;
                await StartCoreAsync(slot).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                RecordError(slot, ex);
                return false;
            }
            finally { slot.Lock.Release(); }
        }

        private static void RecordError(Slot slot, Exception ex)
        {
            var actual = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            slot.LastError = actual.Message;
            slot.LastErrorUtc = DateTime.UtcNow;
        }

        private async Task StartCoreAsync(Slot slot)
        {
            // Kurtarılamaz çökmeden kalan ölü handle varsa temizle.
            if (slot.Handle != null)
            {
                try { await slot.Handle.DisposeAsync().ConfigureAwait(false); } catch { }
                slot.Handle = null;
            }

            slot.Starting = true;
            try
            {
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
                    await loader.LoadInProcessAsync(reg.FilePath, reg.TypeFullName, reg.IncludeNonPublic, reg.ConstructorArgs).ConfigureAwait(false);
                    slot.Loader = loader;
                    await BindAsync(slot, loader.Builder!.AsPluginBuilder()).ConfigureAwait(false);
                }
                slot.LastError = null;
                slot.LastErrorUtc = null;
                slot.CrashedUnrecovered = false;
            }
            finally { slot.Starting = false; }
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
            }, reg.IncludeNonPublic, reg.ConstructorArgs);

            Guid key = reg.Key;
            handle.Crashed += (_, e) =>
            {
                slot.LastCrashUtc = e.UtcTime;
                slot.LastCrashReason = e.Reason.Message;
                if (!e.WillRestart && ReferenceEquals(slot.Handle, handle))
                {
                    // Kurtarılamaz çökme: bir sonraki çağrıda taze worker başlasın diye bağlantıyı kopar.
                    slot.Inner = null;
                    slot.CrashedUnrecovered = true;
                    Interlocked.Increment(ref slot.Version);
                }
                try { PluginCrashed?.Invoke(key, e); } catch { }
            };
            handle.Restarted += (_, _) => Interlocked.Increment(ref slot.Version);

            try
            {
                await handle.StartAsync().ConfigureAwait(false);
            }
            catch
            {
                try { await handle.DisposeAsync().ConfigureAwait(false); } catch { }
                throw;
            }
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
            // Açık durdurma/yeniden başlatma: önceki çökme artık "kurtarılmamış" sayılmaz (LastCrash* bilgisi kalır).
            slot.CrashedUnrecovered = false;
            if (slot.Inner == null && slot.Handle == null && slot.Loader == null) return;

            // Önce proxy'nin eski taraftaki aboneliklerini/delegate önbelleklerini bırak (unload için şart).
            slot.Inner = null;
            Interlocked.Increment(ref slot.Version);
            if (slot.Proxy != null) await slot.Proxy.RebindAsync(null).ConfigureAwait(false);

            if (slot.Handle != null)
            {
                // Önce slot'tan ayır: kapanış sırasında gelen Crashed olayı bu slot'u "çöktü" diye işaretlemesin.
                var handle = slot.Handle;
                slot.Handle = null;
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            slot.CrashedUnrecovered = false;
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

        /// <summary>DLL'i çalıştırmadan doğrular; geçerliyse AssemblyVersion'ı da doldurur. null = geçerli.</summary>
        private static string? ValidationError(PluginRegistration reg)
        {
            if (string.IsNullOrWhiteSpace(reg.FilePath) || !File.Exists(reg.FilePath))
                return $"Plugin dosyası bulunamadı: {reg.FilePath}";
            if (string.IsNullOrWhiteSpace(reg.TypeFullName))
                return "TypeFullName boş olamaz.";
            if (reg.ConstructorArgs is { } ca && ca.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
                return "ConstructorArgs bir dizi (sıralı) ya da nesne (parametre adlarıyla) olmalı.";

            var scan = PluginScanner.Scan(reg.FilePath);
            if (scan.Kind != PluginKind.ManagedDotNet)
                return $"'{reg.FilePath}' bir .NET assembly'si değil ({scan.Kind}) - şu an sadece managed plugin'ler destekleniyor.";
            var t = scan.Types.FirstOrDefault(x => string.Equals(x.FullName, reg.TypeFullName, StringComparison.OrdinalIgnoreCase));
            if (t == null)
                return $"'{reg.TypeFullName}' tipi '{reg.FilePath}' içinde bulunamadı (ya da public değil).";
            reg.TypeFullName = t.FullName; // büyük/küçük harf: DLL'deki gerçek ad

            try
            {
                using var mlc = PluginScanner.CreateMetadataLoadContext(Path.GetFullPath(reg.FilePath));
                var asm = mlc.LoadFromAssemblyPath(Path.GetFullPath(reg.FilePath));
                reg.AssemblyVersion = asm.GetName().Version?.ToString();
                var type = asm.GetType(reg.TypeFullName, throwOnError: false);
                if (type != null)
                {
                    if (type.IsAbstract)
                        return $"'{reg.TypeFullName}' {(type.IsSealed ? "static" : "abstract")} bir sınıf - plugin nesnesi oluşturulamaz.";
                    var ctorError = ConstructorError(type, reg.ConstructorArgs, reg.IncludeNonPublic);
                    if (ctorError != null) return ctorError;
                }
            }
            catch (Exception ex)
            {
                return $"'{reg.FilePath}' okunamadı: {ex.Message}";
            }
            return null;
        }

        // Verilen constructor argümanlarına (sayı / parametre adları) uyan bir constructor var mı - DLL çalıştırılmadan.
        // Tip uyumu burada kontrol edilmez (MetadataLoadContext tipleri); yüklemede kesin kontrol yapılır.
        private static string? ConstructorError(Type type, JsonElement? args, bool includeNonPublic)
        {
            var ctors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | (includeNonPublic ? BindingFlags.NonPublic : 0));
            if (ctors.Length == 0) return $"'{type.Name}' tipinin {(includeNonPublic ? "" : "public ")}constructor'ı yok.";

            bool Fits(ConstructorInfo c)
            {
                var ps = c.GetParameters();
                if (args is not { } a || a.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    return ps.All(p => p.IsOptional);
                if (a.ValueKind == JsonValueKind.Array)
                {
                    int n = a.GetArrayLength();
                    return n <= ps.Length && ps.Skip(n).All(p => p.IsOptional);
                }
                var names = a.EnumerateObject().Select(p => p.Name).ToList();
                bool namedOk = names.All(n => ps.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
                    && ps.Where(p => !p.IsOptional).All(p => names.Any(n => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)));
                // Nesnenin kendisi tek parametre olarak (ör. new Plugin(Ayarlar ayarlar)) - sadece ilk parametre karmaşık tipse.
                return namedOk || (ps.Length >= 1 && ps.Skip(1).All(p => p.IsOptional) && IsComplex(ps[0].ParameterType));
            }

            // MetadataLoadContext tipleri için ada dayalı: enum / primitive / System.* yaprak tipler (string, decimal, Guid,
            // DateTime, Nullable<>...) nesneden bağlanamaz. System.Object her şeyi alır.
            static bool IsComplex(Type t)
            {
                if (t.IsByRef) t = t.GetElementType()!;
                if (t.FullName == "System.Object") return true;
                if (t.IsEnum || t.IsPrimitive) return false;
                return t.Namespace != "System";
            }

            if (ctors.Any(Fits)) return null;
            string sigs = string.Join("; ", ctors.Select(c => "(" + string.Join(", ", c.GetParameters().Select(p =>
                Description.EvokerDescriber.Friendly(p.ParameterType, type.Assembly) + " " + p.Name + (p.IsOptional ? " = …" : ""))) + ")"));
            return args is { } x && x.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? $"Verilen ConstructorArgs '{type.Name}' constructor'larından hiçbirine uymuyor. Mevcut: {sigs}"
                : $"'{type.Name}' parametresiz oluşturulamıyor - ConstructorArgs verilmeli. Mevcut constructor'lar: {sigs}";
        }

        private static void RefreshVersion(PluginRegistration reg)
        {
            try
            {
                using var mlc = PluginScanner.CreateMetadataLoadContext(reg.FilePath);
                reg.AssemblyVersion = mlc.LoadFromAssemblyPath(reg.FilePath).GetName().Version?.ToString();
            }
            catch { }
        }

        private void RaiseModeChanged(Guid key, PluginExecutionMode mode)
        {
            try { ModeChanged?.Invoke(key, mode); } catch { }
        }

        private Slot GetSlot(Guid key)
        {
            EnsureInitialized();
            return _slots.TryGetValue(key, out var s) ? s : throw new KeyNotFoundException($"[PluginManager] '{key}' kayıtlı değil.");
        }

        private void EnsureInitialized()
        {
            if (!_initialized) throw new InvalidOperationException("[PluginManager] Önce InitializeAsync çağrılmalı.");
        }

        private async Task SaveAsync()
        {
            await _saveLock.WaitAsync().ConfigureAwait(false);
            try { await _store.SaveAsync(_slots.Values.Select(s => s.Registration.Clone()).OrderBy(r => r.UpdatedUtc).ToList()).ConfigureAwait(false); }
            finally { _saveLock.Release(); }
        }

        private volatile bool _disposed;

        public async ValueTask DisposeAsync()
        {
            _disposed = true; // arka plan yüklemesi dispose'dan sonra yeni worker açmasın
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