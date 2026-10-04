using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DSO.Core.Evoker.Plugins.Management
{
    /// <summary>
    /// PluginManager'ın uygulamaya verdiği, ömür boyu DEĞİŞMEYEN IPluginBuilder. Arkasında o an hangi mod
    /// çalışıyorsa (SandboxBuilder ya da InProcessPluginBuilder) ona yönlendirir. Admin modu değiştirdiğinde
    /// (sandbox ↔ in-process), yeniden başlattığında ya da plugin ilk kez kullanıldığında:
    ///   - uygulamanın elindeki bu nesne geçerli kalır,
    ///   - bundan alınmış GetFunc/GetAction delegate'leri bir sonraki çağrıda yeni tarafa kendiliğinden bağlanır,
    ///   - event abonelikleri yeni tarafta otomatik yeniden kurulur,
    ///   - geçiş ANINDA sürmekte olan bir çağrı eski taraf kapandığı için hata alırsa, bir kez yeni tarafta denenir.
    /// Plugin'in iç durumu (alan değerleri) geçişte KAYBOLUR - her geçiş yeni bir instance demektir.
    /// Plugin ilk çağrıda (lazy) başlatılır.
    /// </summary>
    public sealed class SwitchablePluginBuilder : IPluginBuilder
    {
        private readonly PluginManager _manager;
        private readonly string _id;
        private readonly object _subsLock = new();
        private readonly List<ProxySubscription> _subs = new();
        private int? _defaultTimeoutMs;

        // PERFORMANS: manager'ın bu plugin'e ait slot'u doğrudan tutulur - her çağrıda id ile sözlük araması
        // (Version + CurrentInner = 2 arama) yapılmaz. Kayıt silinirse (Removed) hızlı yol devre dışı kalır ve
        // manager üzerinden gidilir (o da "kayıtlı değil" hatası verir - eski davranış).
        private readonly PluginManager.Slot _slot;

        internal SwitchablePluginBuilder(PluginManager manager, PluginManager.Slot slot, string id, int? defaultTimeoutMs)
        {
            _manager = manager;
            _slot = slot;
            _id = id;
            _defaultTimeoutMs = defaultTimeoutMs;
        }

        public string Id => _id;
        public string TypeFullName => _manager.GetRegistrationCopy(_id).TypeFullName;
        public bool IncludeNonPublic => _manager.GetRegistrationCopy(_id).IncludeNonPublic;
        public bool IsSandboxed => _manager.GetRegistrationCopy(_id).Mode == PluginExecutionMode.Sandbox;

        public int? DefaultTimeoutMs
        {
            get => _defaultTimeoutMs;
            set
            {
                _defaultTimeoutMs = value;
                var inner = CurrentInnerFast();
                if (inner != null) inner.DefaultTimeoutMs = value;
            }
        }

        // --- yönlendirme çekirdeği ---

        private Task<IPluginBuilder> InnerAsync() => _manager.GetInnerAsync(_id);

        private async Task<T> Run<T>(Func<IPluginBuilder, Task<T>> call)
        {
            int version = CurrentVersion();
            var inner = await InnerAsync().ConfigureAwait(false);
            try
            {
                return await call(inner).ConfigureAwait(false);
            }
            catch (Exception) when (CurrentVersion() != version)
            {
                // Çağrı sürerken plugin başka moda geçti/yeniden başlatıldı ve eski taraf kapandı - yenisinde bir kez dene.
                return await call(await InnerAsync().ConfigureAwait(false)).ConfigureAwait(false);
            }
        }

        private Task Run(Func<IPluginBuilder, Task> call) => Run<object?>(async b => { await call(b).ConfigureAwait(false); return null; });

        private static T Sync<T>(Task<T> t) => t.ConfigureAwait(false).GetAwaiter().GetResult();
        private static void Sync(Task t) => t.ConfigureAwait(false).GetAwaiter().GetResult();

        // PERFORMANS (hızlı yol): plugin zaten çalışıyorsa (en yaygın durum) alttaki tarafa DOĞRUDAN gidilir -
        // async sarmalayıcı / closure yok. Senkron çağrılar senkron kalır (eskiden async yolun bloklanmış haliydi).
        // Geçiş anında hata alan çağrı, eskisi gibi, yeni tarafta bir kez tekrar denenir.
        private int CurrentVersion() => _slot.Removed ? _manager.Version(_id) : Volatile.Read(ref _slot.Version);
        private IPluginBuilder? CurrentInnerFast() => _slot.Removed ? _manager.CurrentInner(_id) : _slot.Inner;

        private IPluginBuilder InnerSync() => CurrentInnerFast() ?? Sync(InnerAsync());

        private Task<T> RunFast<T>(Func<IPluginBuilder, Task<T>> call)
        {
            int version = CurrentVersion();
            var inner = CurrentInnerFast();
            if (inner == null) return Run(call);
            Task<T> task;
            try { task = call(inner); }
            catch (Exception) when (CurrentVersion() != version) { return Run(call); }
            return task.IsCompletedSuccessfully ? task : AwaitWithRetry(task, version, call);
        }

        private async Task<T> AwaitWithRetry<T>(Task<T> task, int version, Func<IPluginBuilder, Task<T>> call)
        {
            try { return await task.ConfigureAwait(false); }
            catch (Exception) when (CurrentVersion() != version)
            {
                return await call(await InnerAsync().ConfigureAwait(false)).ConfigureAwait(false);
            }
        }

        // --- Metot çağırma ---
        public Task<object?> InvokeAsync(string methodName, params object?[] args) => RunFast(b => b.InvokeAsync(methodName, args));
        public Task<T?> InvokeAsync<T>(string methodName, params object?[] args) => RunFast(b => b.InvokeAsync<T>(methodName, args));
        // Execute*: sonuç DECODE edilmez (sandbox'ta host'ta çözülemeyen bir dönüş tipi olabilir) - alttakinin Execute'u.
        public Task ExecuteAsync(string methodName, params object?[] args) => RunFast(async b => { await b.ExecuteAsync(methodName, args).ConfigureAwait(false); return (object?)null; });

        public object? Invoke(string methodName, params object?[] args)
        {
            int version = CurrentVersion();
            try { return InnerSync().Invoke(methodName, args); }
            catch (Exception) when (CurrentVersion() != version) { return InnerSync().Invoke(methodName, args); }
        }

        public T? Invoke<T>(string methodName, params object?[] args)
        {
            int version = CurrentVersion();
            try { return InnerSync().Invoke<T>(methodName, args); }
            catch (Exception) when (CurrentVersion() != version) { return InnerSync().Invoke<T>(methodName, args); }
        }

        public void Execute(string methodName, params object?[] args)
        {
            int version = CurrentVersion();
            try { InnerSync().Execute(methodName, args); }
            catch (Exception) when (CurrentVersion() != version) { InnerSync().Execute(methodName, args); }
        }

        // --- Toplu ---
        public Task<T?[]> InvokeBatchAsync<T>(string methodName, IReadOnlyList<object?[]> argsList) => Run(b => b.InvokeBatchAsync<T>(methodName, argsList));
        public Task ExecuteBatchAsync(string methodName, IReadOnlyList<object?[]> argsList) => Run(b => b.ExecuteBatchAsync(methodName, argsList));

        // --- Property / field ---
        public Task<T?> GetValueAsync<T>(string memberName) => RunFast(b => b.GetValueAsync<T>(memberName));
        public Task SetValueAsync<T>(string memberName, T value) => Run(b => b.SetValueAsync(memberName, value));

        public T? GetValue<T>(string memberName)
        {
            int version = CurrentVersion();
            try { return InnerSync().GetValue<T>(memberName); }
            catch (Exception) when (CurrentVersion() != version) { return InnerSync().GetValue<T>(memberName); }
        }

        public void SetValue<T>(string memberName, T value)
        {
            int version = CurrentVersion();
            try { InnerSync().SetValue(memberName, value); }
            catch (Exception) when (CurrentVersion() != version) { InnerSync().SetValue(memberName, value); }
        }

        // --- Delegate'ler: alttaki tarafın delegate'i önbellekte tutulur; taraf değişince (Version) yeniden alınır ---
        // Önbelleklenen alt-taraf delegate'i bir "slot"ta durur; proxy slot'ları ZAYIF referansla tanır ve geçişte
        // (RebindAsync) boşaltır. Aksi halde uygulamanın elinde kalan, geçişten sonra hiç çağrılmayan bir GetFunc
        // delegate'i eski in-process tarafın delegate'ini (-> plugin instance'ını) tutar ve eski context unload OLAMAZ.
        private sealed class DelegateSlot { public Delegate? Cached; public int Version = -1; }
        private readonly List<WeakReference<DelegateSlot>> _slots = new();

        private DelegateSlot NewSlot()
        {
            var slot = new DelegateSlot();
            lock (_subsLock)
            {
                _slots.RemoveAll(w => !w.TryGetTarget(out _));
                _slots.Add(new WeakReference<DelegateSlot>(slot));
            }
            return slot;
        }

        public Func<object?[], T?> GetFunc<T>(string methodName, object?[]? sampleArgs = null)
        {
            var slot = NewSlot();
            return args =>
            {
                int cur = CurrentVersion();
                if (slot.Cached is not Func<object?[], T?> f || slot.Version != cur)
                {
                    f = InnerSync().GetFunc<T>(methodName, sampleArgs);
                    slot.Cached = f;
                    slot.Version = CurrentVersion();
                }
                return f(args);
            };
        }

        public Action<object?[]> GetAction(string methodName, object?[]? sampleArgs = null)
        {
            var slot = NewSlot();
            return args =>
            {
                int cur = CurrentVersion();
                if (slot.Cached is not Action<object?[]> a || slot.Version != cur)
                {
                    a = InnerSync().GetAction(methodName, sampleArgs);
                    slot.Cached = a;
                    slot.Version = CurrentVersion();
                }
                a(args);
            };
        }

        public Func<object?[], Task<T?>> GetFuncAsync<T>(string methodName, object?[]? sampleArgs = null)
            => args => InvokeAsync<T>(methodName, args);

        // Tipli delegate'ler: GetFunc ile aynı slot mekanizması - alttaki tarafın (in-process'te boxing'siz) tipli
        // delegate'i önbellekte; taraf değişince bir sonraki çağrıda yeniden alınır.
        private Func<TDel> TypedSlot<TDel>(Func<IPluginBuilder, TDel> get) where TDel : Delegate
        {
            var slot = NewSlot();
            return () =>
            {
                int cur = CurrentVersion();
                if (slot.Cached is TDel d && slot.Version == cur) return d;
                d = get(InnerSync());
                slot.Cached = d;
                slot.Version = cur;
                return d;
            };
        }

        public Func<T1, TResult?> GetFunc<T1, TResult>(string methodName)
        { var s = TypedSlot(b => b.GetFunc<T1, TResult>(methodName)); return a => s()(a); }
        public Func<T1, T2, TResult?> GetFunc<T1, T2, TResult>(string methodName)
        { var s = TypedSlot(b => b.GetFunc<T1, T2, TResult>(methodName)); return (a, c) => s()(a, c); }
        public Func<T1, T2, T3, TResult?> GetFunc<T1, T2, T3, TResult>(string methodName)
        { var s = TypedSlot(b => b.GetFunc<T1, T2, T3, TResult>(methodName)); return (a, c, d) => s()(a, c, d); }
        public Func<T1, T2, T3, T4, TResult?> GetFunc<T1, T2, T3, T4, TResult>(string methodName)
        { var s = TypedSlot(b => b.GetFunc<T1, T2, T3, T4, TResult>(methodName)); return (a, c, d, e) => s()(a, c, d, e); }
        public Action<T1> GetAction<T1>(string methodName)
        { var s = TypedSlot(b => b.GetAction<T1>(methodName)); return a => s()(a); }
        public Action<T1, T2> GetAction<T1, T2>(string methodName)
        { var s = TypedSlot(b => b.GetAction<T1, T2>(methodName)); return (a, c) => s()(a, c); }
        public Action<T1, T2, T3> GetAction<T1, T2, T3>(string methodName)
        { var s = TypedSlot(b => b.GetAction<T1, T2, T3>(methodName)); return (a, c, d) => s()(a, c, d); }
        public Action<T1, T2, T3, T4> GetAction<T1, T2, T3, T4>(string methodName)
        { var s = TypedSlot(b => b.GetAction<T1, T2, T3, T4>(methodName)); return (a, c, d, e) => s()(a, c, d, e); }

        public Func<object?[], Task> GetActionAsync(string methodName, object?[]? sampleArgs = null)
            => args => ExecuteAsync(methodName, args);

        // --- Event'ler: abonelik kaydı proxy'de; taraf değişince yeniden kurulur ---
        public async Task<IDisposable> SubscribeAsync(string eventName, Action<PluginEventArgs> handler)
        {
            var sub = new ProxySubscription(this, eventName, handler);
            var inner = await InnerAsync().ConfigureAwait(false);
            sub.Inner = await inner.SubscribeAsync(eventName, handler).ConfigureAwait(false); // hata varsa (event yok) burada fırlar
            lock (_subsLock) _subs.Add(sub);
            return sub;
        }

        public IDisposable Subscribe(string eventName, Action<PluginEventArgs> handler) => Sync(SubscribeAsync(eventName, handler));

        public string[] GetEventNames() => Sync(InnerAsync()).GetEventNames();

        // Manager çağırır: yeni taraf hazır. Eski taraftaki abonelikler kapatılır (in-process'te bu, eski
        // context'in unload olabilmesi için ŞART), yeni tarafta yeniden kurulur.
        internal async Task RebindAsync(IPluginBuilder? newInner)
        {
            ProxySubscription[] subs;
            lock (_subsLock)
            {
                subs = _subs.ToArray();
                foreach (var w in _slots)
                    if (w.TryGetTarget(out var slot)) { slot.Cached = null; slot.Version = -1; }
            }
            foreach (var s in subs)
            {
                try { s.Inner?.Dispose(); } catch { }
                s.Inner = null;
                if (newInner != null)
                {
                    try { s.Inner = await newInner.SubscribeAsync(s.EventName, s.Handler).ConfigureAwait(false); }
                    catch (Exception ex) { System.Diagnostics.Trace.TraceError($"[SwitchablePluginBuilder] '{s.EventName}' aboneliği yeni tarafta kurulamadı: {ex.Message}"); }
                }
            }
            if (newInner != null) newInner.DefaultTimeoutMs = _defaultTimeoutMs;
        }

        private void Remove(ProxySubscription s)
        {
            lock (_subsLock) _subs.Remove(s);
        }

        // --- Tanım ---
        public Task<Scanning.PluginDescriptor> DescribeAsync(bool includeValues = true) => Run(b => b.DescribeAsync(includeValues));

        // --- Cache ---
        public Task ForgetCacheAsync() => Run(b => b.ForgetCacheAsync());
        public void ForgetCache() => Sync(ForgetCacheAsync());

        private sealed class ProxySubscription : IDisposable
        {
            private SwitchablePluginBuilder? _owner;
            public ProxySubscription(SwitchablePluginBuilder owner, string eventName, Action<PluginEventArgs> handler)
            {
                _owner = owner;
                EventName = eventName;
                Handler = handler;
            }
            public string EventName { get; }
            public Action<PluginEventArgs> Handler { get; }
            public IDisposable? Inner { get; set; }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;
                owner.Remove(this);
                try { Inner?.Dispose(); } catch { }
                Inner = null;
            }
        }
    }
}