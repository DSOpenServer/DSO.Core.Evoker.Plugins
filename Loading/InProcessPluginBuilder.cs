using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DSO.Core.Evoker;
using DSO.Core.Evoker.Plugins.Sandbox;
using DSO.Core.Evoker.Plugins.Scanning;

namespace DSO.Core.Evoker.Plugins.Loading
{
    /// <summary>
    /// Host'un kendi içinde (in-process) yüklenmiş plugin için <see cref="IPluginBuilder"/> - doğrudan
    /// EvokerBuilder'a delege eder (IPC yok). SandboxBuilder ile AYNI davranış sözleşmesi (bkz. IPluginBuilder);
    /// fark sadece performans: GetValue/SetValue ve senkron metotlar için GetFunc/GetAction, EvokerBuilder'ın
    /// derlenmiş delegate'lerini DOĞRUDAN döndürür (milyonlarca çağrılık döngüler için sıcak yol).
    ///
    /// Almak için: loader.Builder!.AsPluginBuilder()  (ya da herhangi bir SetInstance'lı EvokerBuilder üzerinde).
    /// EvokerBuilder'ın kendisi (loader.Builder) HÂLÂ public ve doğrudan kullanılabilir - bu sınıf onun
    /// yerine geçmiyor, sandbox ile ortak bir yüzey sağlıyor.
    /// </summary>
    public sealed class InProcessPluginBuilder : IPluginBuilder
    {
        public EvokerBuilder Builder { get; }

        public InProcessPluginBuilder(EvokerBuilder builder)
        {
            Builder = builder ?? throw new ArgumentNullException(nameof(builder));
            if (builder.Instance == null)
                throw new InvalidOperationException(
                    "[InProcessPluginBuilder] EvokerBuilder SetInstance(...) ile bir plugin instance'ına bağlı olmalı " +
                    "(ManagedDotNetPluginLoader.Builder zaten öyle).");
        }

        public string TypeFullName => Builder.Type.FullName ?? Builder.Type.Name;
        public bool IncludeNonPublic => Builder.IncludeNonPublic;
        public bool IsSandboxed => false;
        public int? DefaultTimeoutMs { get; set; }

        // --- Metot çağırma ---
        // PERFORMANS: her (metot adı, argüman tip imzası) için BİR KEZ bir "çağrı planı" çıkarılır: hangi metot,
        // dönüş şekli (void / senkron / Task / Task<T>), hangi argümanların parametre tipine çevrilmesi gerektiği
        // (enum / host DTO'su -> plugin tipi) ve derlenmiş invoker. Sonraki çağrılar: plan (önce son kullanılan,
        // sonra sözlük) + gerekiyorsa argüman dönüşümü + derlenmiş delegate. Senkron metotlar async/Task
        // katmanına HİÇ girmez (eskiden Invoke = InvokeAsync'in bloklanmış hali idi).
        // Davranış (metot seçimi, dönüşümler, hata sözleşmesi, timeout) önceki ile birebir aynı.

        public object? Invoke(string methodName, params object?[] args)
        {
            args ??= Array.Empty<object?>();
            var plan = GetPlan(methodName, args);
            return plan.IsSync ? CallSync(plan, methodName, args) : Sync(InvokeAsync(methodName, args));
        }

        public T? Invoke<T>(string methodName, params object?[] args)
        {
            args ??= Array.Empty<object?>();
            var plan = GetPlan(methodName, args);
            return plan.IsSync ? ConvertResult<T>(CallSync(plan, methodName, args)) : Sync(InvokeAsync<T>(methodName, args));
        }

        public void Execute(string methodName, params object?[] args) => Invoke(methodName, args);

        public Task<object?> InvokeAsync(string methodName, params object?[] args)
        {
            args ??= Array.Empty<object?>();
            try
            {
                var plan = GetPlan(methodName, args);
                return plan.IsSync ? Task.FromResult(CallSync(plan, methodName, args)) : RunTaskAsync(plan, methodName, args);
            }
            catch (Exception ex) { return Task.FromException<object?>(ex); }
        }

        public Task<T?> InvokeAsync<T>(string methodName, params object?[] args)
        {
            args ??= Array.Empty<object?>();
            try
            {
                var plan = GetPlan(methodName, args);
                return plan.IsSync
                    ? Task.FromResult(ConvertResult<T>(CallSync(plan, methodName, args)))
                    : ConvertAsync<T>(RunTaskAsync(plan, methodName, args));
            }
            catch (Exception ex) { return Task.FromException<T?>(ex); }
        }

        public Task ExecuteAsync(string methodName, params object?[] args) => InvokeAsync(methodName, args);

        private static async Task<T?> ConvertAsync<T>(Task<object?> t) => ConvertResult<T>(await t.ConfigureAwait(false));

        private static T? ConvertResult<T>(object? r)
        {
            if (r is T t) return t;
            if (r == null) return default;
            return (T?)WireValueCodec.ConvertTo(r, typeof(T));
        }

        // --- Toplu çağrı (in-process'te IPC yok - sadece sıralı döngü, aynı hata sözleşmesiyle) ---

        public async Task<T?[]> InvokeBatchAsync<T>(string methodName, IReadOnlyList<object?[]> argsList)
        {
            if (argsList == null) throw new ArgumentNullException(nameof(argsList));
            var results = new T?[argsList.Count];
            for (int i = 0; i < argsList.Count; i++)
            {
                try
                {
                    var a = argsList[i] ?? Array.Empty<object?>();
                    var plan = GetPlan(methodName, a);
                    results[i] = plan.IsSync
                        ? ConvertResult<T>(CallSync(plan, methodName, a))       // senkron: Task/await yok
                        : await InvokeAsync<T>(methodName, a).ConfigureAwait(false);
                }
                catch (PluginInvocationException ex) when (ex.BatchIndex == null)
                {
                    throw new PluginInvocationException(methodName, ex.RemoteExceptionType, ex.InnerException?.Message ?? ex.Message, ex.InnerException) { BatchIndex = i };
                }
            }
            return results;
        }

        public Task ExecuteBatchAsync(string methodName, IReadOnlyList<object?[]> argsList) => InvokeBatchAsync<object>(methodName, argsList);

        // --- Property / field (doğrudan DynamicEntityAccessor - sıcak yol) ---

        public T? GetValue<T>(string memberName)
        {
            try
            {
                if (IsDirectMember(memberName, typeof(T), forSet: false))
                    return Builder.GetValue<T>(memberName); // sıcak yol: derlenmiş, boxing'siz getter
                // T üyenin tipiyle ilgisiz (ör. plugin "Point", host kendi "PointDto"sunu istiyor) - sandbox ile
                // AYNI kural: şekil eşlemesi (bkz. WireValueCodec.ConvertTo).
                return (T?)WireValueCodec.ConvertTo(Builder.GetValue<object>(memberName), typeof(T));
            }
            catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(memberName, ex); }
        }

        public void SetValue<T>(string memberName, T value)
        {
            try
            {
                if (value == null || IsDirectMember(memberName, typeof(T), forSet: true))
                    Builder.SetValue(memberName, value);
                else
                    Builder.SetValue<object?>(memberName, WireValueCodec.ConvertTo(value, MemberType(memberName)));
            }
            catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(memberName, ex); }
        }

        public Task<T?> GetValueAsync<T>(string memberName)
        {
            try { return Task.FromResult(GetValue<T>(memberName)); }
            catch (Exception ex) { return Task.FromException<T?>(ex); }
        }

        public Task SetValueAsync<T>(string memberName, T value)
        {
            try { SetValue(memberName, value); return Task.CompletedTask; }
            catch (Exception ex) { return Task.FromException(ex); }
        }

        // (üye adı, istenen tip, get/set) -> Expression.Convert doğrudan yapabiliyor mu. Her çağrıda üye tipini
        // bulup karşılaştırmak yerine bir kez hesaplanır.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, Type, bool), bool> _directMembers = new();

        private bool IsDirectMember(string memberName, Type t, bool forSet)
        {
            if (_directMembers.TryGetValue((memberName, t, forSet), out var d)) return d;
            var mt = MemberType(memberName);
            d = forSet ? IsDirectlyConvertible(t, mt) : IsDirectlyConvertible(mt, t);
            _directMembers.TryAdd((memberName, t, forSet), d);
            return d;
        }

        // --- Delegate'ler ---

        public Func<object?[], T?> GetFunc<T>(string methodName, object?[]? sampleArgs = null)
        {
            // Senkron dönüşlü metot: EvokerBuilder'ın derlenmiş delegate'i doğrudan (sadece hata sarmalayıcısı).
            // Task/Task<T> dönüşlü metot: EvokerBuilder.GetFunc<T> Task'ı T'ye çeviremez -> bekleyen sarmalayıcı.
            if (!ReturnsTask(methodName, sampleArgs))
            {
                var direct = Builder.GetFunc<T>(methodName, ToObjArray(sampleArgs));
                return args =>
                {
                    try { return direct((object[])(args ?? Array.Empty<object?>())); }
                    catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); }
                };
            }
            var f = GetFuncAsync<T>(methodName, sampleArgs);
            return args => Sync(f(args));
        }

        public Action<object?[]> GetAction(string methodName, object?[]? sampleArgs = null)
        {
            if (!ReturnsTask(methodName, sampleArgs))
            {
                var direct = Builder.GetAction(methodName, ToObjArray(sampleArgs));
                return args =>
                {
                    try { direct((object[])(args ?? Array.Empty<object?>())); }
                    catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); }
                };
            }
            // EvokerBuilder.GetAction Task döndüren metodu BEKLEMEZ (fire-and-forget olurdu) - burada bekleniyor.
            var a = GetActionAsync(methodName, sampleArgs);
            return args => Sync(a(args));
        }

        public Func<object?[], Task<T?>> GetFuncAsync<T>(string methodName, object?[]? sampleArgs = null)
            => args => InvokeAsync<T>(methodName, args);

        public Func<object?[], Task> GetActionAsync(string methodName, object?[]? sampleArgs = null)
            => args => ExecuteAsync(methodName, args);

        // --- Tipli delegate'ler ---
        // Önce EvokerBuilder'ın boxing'siz derlenmiş delegate'i denenir (sadece hata sarmalayıcısı eklenir). Tipler
        // doğrudan dönüştürülemiyorsa (host DTO'su ↔ plugin tipi) ya da metot Task döndürüyorsa, Invoke ile aynı
        // kurallarla (argüman dönüşümü, Task bekleme, sonuç eşleme) çalışan genel yola düşülür.

        public Func<T1, TResult?> GetFunc<T1, TResult>(string methodName)
        {
            var f = TryTyped(methodName, new[] { typeof(T1) }, () => Builder.GetFunc<T1, TResult>(methodName));
            if (f == null) return PluginTypedDelegates.Func<T1, TResult>(PerCall<TResult>(methodName));
            return a => { try { return f(a); } catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); } };
        }

        public Func<T1, T2, TResult?> GetFunc<T1, T2, TResult>(string methodName)
        {
            var f = TryTyped(methodName, new[] { typeof(T1), typeof(T2) }, () => Builder.GetFunc<T1, T2, TResult>(methodName));
            if (f == null) return PluginTypedDelegates.Func<T1, T2, TResult>(PerCall<TResult>(methodName));
            return (a, b) => { try { return f(a, b); } catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); } };
        }

        public Func<T1, T2, T3, TResult?> GetFunc<T1, T2, T3, TResult>(string methodName)
        {
            var f = TryTyped(methodName, new[] { typeof(T1), typeof(T2), typeof(T3) }, () => Builder.GetFunc<T1, T2, T3, TResult>(methodName));
            if (f == null) return PluginTypedDelegates.Func<T1, T2, T3, TResult>(PerCall<TResult>(methodName));
            return (a, b, c) => { try { return f(a, b, c); } catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); } };
        }

        public Func<T1, T2, T3, T4, TResult?> GetFunc<T1, T2, T3, T4, TResult>(string methodName)
        {
            var f = TryTyped(methodName, new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, () => Builder.GetFunc<T1, T2, T3, T4, TResult>(methodName));
            if (f == null) return PluginTypedDelegates.Func<T1, T2, T3, T4, TResult>(PerCall<TResult>(methodName));
            return (a, b, c, d) => { try { return f(a, b, c, d); } catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); } };
        }

        public Action<T1> GetAction<T1>(string methodName)
        {
            var f = TryTyped(methodName, new[] { typeof(T1) }, () => Builder.GetAction<T1>(methodName));
            if (f == null) return PluginTypedDelegates.Action<T1>(PerCallAction(methodName));
            return a => { try { f(a); } catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); } };
        }

        public Action<T1, T2> GetAction<T1, T2>(string methodName)
        {
            var f = TryTyped(methodName, new[] { typeof(T1), typeof(T2) }, () => Builder.GetAction<T1, T2>(methodName));
            if (f == null) return PluginTypedDelegates.Action<T1, T2>(PerCallAction(methodName));
            return (a, b) => { try { f(a, b); } catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); } };
        }

        public Action<T1, T2, T3> GetAction<T1, T2, T3>(string methodName)
        {
            var f = TryTyped(methodName, new[] { typeof(T1), typeof(T2), typeof(T3) }, () => Builder.GetAction<T1, T2, T3>(methodName));
            if (f == null) return PluginTypedDelegates.Action<T1, T2, T3>(PerCallAction(methodName));
            return (a, b, c) => { try { f(a, b, c); } catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); } };
        }

        public Action<T1, T2, T3, T4> GetAction<T1, T2, T3, T4>(string methodName)
        {
            var f = TryTyped(methodName, new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, () => Builder.GetAction<T1, T2, T3, T4>(methodName));
            if (f == null) return PluginTypedDelegates.Action<T1, T2, T3, T4>(PerCallAction(methodName));
            return (a, b, c, d) => { try { f(a, b, c, d); } catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); } };
        }

        // Derlenmiş tipli delegate kurulabiliyor mu? Metot Task döndürüyorsa (beklenmesi gerekir), tip dönüşümü
        // yoksa (InvalidCastException) ya da void metot için GetFunc istendiyse null -> genel yol.
        // "Metot yok" gibi çağıran hataları olduğu gibi fırlar.
        private D? TryTyped<D>(string methodName, Type[] argTypes, Func<D> build) where D : Delegate
        {
            if (typeof(Task).IsAssignableFrom(Builder.FindMethodByTypes(methodName, argTypes).ReturnType)) return null;
            try { return build(); }
            catch (InvalidCastException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private Func<object?[], T?> PerCall<T>(string methodName) => args => Invoke<T>(methodName, args);
        private Action<object?[]> PerCallAction(string methodName) => args => Execute(methodName, args);

        // --- JSON komut (çekirdekteki EvokerTarget - worker'da da aynısı çalışır) ---

        private DSO.Core.Evoker.Commands.EvokerTarget? _commandTarget;

        public async Task<DSO.Core.Evoker.Commands.EvokerCommandResult> ExecuteCommandAsync(DSO.Core.Evoker.Commands.EvokerCommand command,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            var target = _commandTarget ??= DSO.Core.Evoker.Commands.EvokerTarget.ForInstance(Builder.Instance!, IncludeNonPublic);
            target.DefaultTimeoutMs = DefaultTimeoutMs;
            var result = await target.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            result.Mode = "InProcess";
            return result;
        }

        // --- Event'ler ---

        public IDisposable Subscribe(string eventName, Action<PluginEventArgs> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var instance = Builder.Instance;
            try
            {
                return Builder.AddEventHandler(eventName, raw =>
                {
                    // Sandbox ile aynı: plugin'in kendisi (sender) null; handler hatası plugin'e yansımaz.
                    var args = new object?[raw.Length];
                    for (int i = 0; i < raw.Length; i++) args[i] = ReferenceEquals(raw[i], instance) ? null : raw[i];
                    var e = new PluginEventArgs(eventName, args);
                    try { handler(e); }
                    catch (Exception ex)
                    {
                        var failed = EventHandlerFailed;
                        if (failed != null) { try { failed(e, ex); } catch { } }
                        else System.Diagnostics.Trace.TraceError($"[InProcessPluginBuilder] '{eventName}' handler hatası: {ex}");
                    }
                });
            }
            catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(eventName, ex); }
        }

        public Task<IDisposable> SubscribeAsync(string eventName, Action<PluginEventArgs> handler)
            => Task.FromResult(Subscribe(eventName, handler));

        public string[] GetEventNames() => Builder.GetEventNames();

        /// <summary>Bkz. PluginWorkerHandle.EventHandlerFailed - aynı sözleşme.</summary>
        public event Action<PluginEventArgs, Exception>? EventHandlerFailed;

        // --- Tanım ---

        public async Task<PluginDescriptor> DescribeAsync(bool includeValues = true)
        {
            // Yapı, çalışan tipten DEĞİL dosyadan (MetadataLoadContext) okunur - sandbox ile birebir aynı çıktı.
            var d = PluginInspector.Describe(Builder.Type.Assembly.Location, TypeFullName);
            if (includeValues) await PluginValueSnapshot.CaptureAsync(d, this).ConfigureAwait(false);
            return d;
        }

        // --- Cache ---

        public void ForgetCache() => Builder.ForgetCache();

        public Task ForgetCacheAsync()
        {
            ForgetCache();
            return Task.CompletedTask;
        }

        // --- yardımcılar ---

        // Metot seçimi Invoke ile AYNI kural (EvokerBuilder.FindMethod: optional parametre, büyük/küçük harf, tip).
        private bool ReturnsTask(string methodName, object?[]? sampleArgs) =>
            typeof(Task).IsAssignableFrom(Builder.FindMethod(methodName, sampleArgs).ReturnType);

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Type> _memberTypes = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), ParameterInfo[]?> _uniqueSignatures = new();

        private Type MemberType(string memberName) => _memberTypes.GetOrAdd(memberName, name =>
        {
            var flags = BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.Public
                | (IncludeNonPublic ? BindingFlags.NonPublic : 0);
            // DynamicEntityAccessor ile aynı: birebir isim, yoksa TEK büyük/küçük harf duyarsız eşleşme (VB.NET).
            return Builder.Type.GetProperty(name, flags)?.PropertyType
                ?? Builder.Type.GetField(name, flags)?.FieldType
                ?? SingleIgnoreCase(Builder.Type.GetProperties(flags), name)?.PropertyType
                ?? SingleIgnoreCase(Builder.Type.GetFields(flags), name)?.FieldType
                ?? throw new MissingMemberException(Builder.Type.Name, name);
        });

        // Expression.Convert'in DOĞRUDAN yapabildiği dönüşümler: aynı/atanabilir tip ya da sayısal/enum
        // (ve bunların Nullable'ları) arası. Geri kalanı ConvertTo (JSON şekil eşlemesi) yoluna gider.
        private static bool IsDirectlyConvertible(Type from, Type to)
        {
            if (to.IsAssignableFrom(from) || from.IsAssignableFrom(to)) return true;
            var f = Nullable.GetUnderlyingType(from) ?? from;
            var t = Nullable.GetUnderlyingType(to) ?? to;
            return (f.IsPrimitive || f.IsEnum || f == typeof(decimal)) && (t.IsPrimitive || t.IsEnum || t == typeof(decimal));
        }

        // Sandbox'taki worker ile AYNI kural: metot adı + argüman sayısıyla TEK aday varsa, parametresine
        // atanamayan enum/sayısal ya da Complex (host DTO'su) argümanları parametre tipine çevir.
        // Birden fazla overload varsa DOKUNMA - seçimi EvokerBuilder argümanların gerçek tiplerine göre yapar.
        private object?[] PrepareArgs(string methodName, object?[] args, out Type?[]? convertTo)
        {
            convertTo = null;
            var ps = _uniqueSignatures.GetOrAdd((methodName, args.Length), key =>
            {
                var c = Builder.FindMethodCandidates(key.Item1, key.Item2);
                return c.Count == 1 ? c[0].GetParameters() : null;
            });
            if (ps == null) return args;

            object?[]? copy = null;
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                var pt = ps[i].ParameterType;
                if (a == null || pt.IsInstanceOfType(a)) continue;
                bool enumTarget = (Nullable.GetUnderlyingType(pt) ?? pt).IsEnum;
                bool complexArg = !WireValueCodec.IsLeafType(a.GetType());
                if (!enumTarget && !complexArg) continue;
                copy ??= (object?[])args.Clone();
                copy[i] = WireValueCodec.ConvertTo(a, pt);
                (convertTo ??= new Type?[args.Length])[i] = pt;
            }
            return copy ?? args;
        }

        private static T? SingleIgnoreCase<T>(T[] members, string name) where T : MemberInfo
        {
            var m = members.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            return m.Count == 1 ? m[0] : null;
        }

        private static object[]? ToObjArray(object?[]? args) => args == null ? null : args.Select(a => a!).ToArray();

        // --- Çağrı planı ---

        private sealed class CallPlan
        {
            public string MethodName = "";
            public ArgTypeKey Key;
            public bool IsSync;                       // void ya da senkron dönüş (Task değil)
            public bool IsTaskOfT;                    // Task<T> (sonuç okunacak)
            public Type?[]? ConvertTo;                // null = dönüşüm yok; aksi halde i. argüman bu tipe çevrilir (null eleman = dokunma)
            public Func<object[], object?> Call = null!;
            public Func<Task, object?>? TaskResult;
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, ArgTypeKey), CallPlan> _plans = new();
        private CallPlan? _lastPlan;

        private CallPlan GetPlan(string methodName, object?[] args)
        {
            var key = ArgTypeKey.From(args);
            var last = _lastPlan;
            if (last != null && last.Key.Equals(key) && string.Equals(last.MethodName, methodName, StringComparison.Ordinal))
                return last;
            if (!_plans.TryGetValue((methodName, key), out var plan))
            {
                try { plan = _plans.GetOrAdd((methodName, key), BuildPlan(methodName, args, key)); }
                catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); }
            }
            _lastPlan = plan;
            return plan;
        }

        private CallPlan BuildPlan(string methodName, object?[] args, ArgTypeKey key)
        {
            if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("methodName boş olamaz.", nameof(methodName));

            // Hangi argümanların dönüştürüleceği (sandbox'taki worker ile AYNI kural, bkz. PrepareArgs). Karar
            // sadece argümanların TİPLERİNE bağlı - aynı imzalı sonraki çağrılarda aynı kalır.
            var prepared = PrepareArgs(methodName, args, out var convertTo);
            var method = Builder.FindMethod(methodName, prepared);
            var rt = method.ReturnType;
            var sample = (object[])prepared;

            var plan = new CallPlan { MethodName = methodName, Key = key, ConvertTo = convertTo };
            if (rt == typeof(void))
            {
                var act = Builder.GetAction(methodName, sample);
                plan.Call = a => { act(a); return null; };
                plan.IsSync = true;
            }
            else
            {
                plan.Call = Builder.GetFunc<object>(methodName, sample)!;
                plan.IsSync = !typeof(Task).IsAssignableFrom(rt);
                if (!plan.IsSync && rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    plan.IsTaskOfT = true;
                    var p = System.Linq.Expressions.Expression.Parameter(typeof(Task), "t");
                    plan.TaskResult = System.Linq.Expressions.Expression.Lambda<Func<Task, object?>>(
                        System.Linq.Expressions.Expression.Convert(
                            System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Convert(p, rt), "Result"),
                            typeof(object)), p).Compile();
                }
            }
            return plan;
        }

        private object[] Prepare(CallPlan plan, string methodName, object?[] args)
        {
            var conv = plan.ConvertTo;
            if (conv == null) return (object[])args;
            try
            {
                var copy = (object?[])args.Clone();
                for (int i = 0; i < conv.Length; i++)
                    if (conv[i] != null && copy[i] != null) copy[i] = WireValueCodec.ConvertTo(copy[i], conv[i]!);
                return (object[])copy;
            }
            catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); }
        }

        private object? CallSync(CallPlan plan, string methodName, object?[] args)
        {
            var a = Prepare(plan, methodName, args);
            try { return plan.Call(a); }
            catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); }
        }

        private async Task<object?> RunTaskAsync(CallPlan plan, string methodName, object?[] args)
        {
            var a = Prepare(plan, methodName, args);
            Task? task;
            try { task = (Task?)plan.Call(a); }
            catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); }
            if (task == null) return null;

            if (DefaultTimeoutMs.HasValue)
            {
                var winner = await Task.WhenAny(task, Task.Delay(DefaultTimeoutMs.Value)).ConfigureAwait(false);
                if (winner != task)
                    throw new TimeoutException(
                        $"[InProcessPluginBuilder] '{methodName}' {DefaultTimeoutMs.Value}ms içinde tamamlanmadı " +
                        "(sadece bekleme bırakıldı, plugin kodu çalışmaya devam ediyor olabilir).");
            }

            try
            {
                await task.ConfigureAwait(false);
                return plan.IsTaskOfT ? plan.TaskResult!(task) : null;
            }
            catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); }
        }

        // Kullanıcı/çağıran hataları (boş isim, bağlanmamış builder) olduğu gibi kalır; geri kalan her şey
        // plugin tarafı hatası sayılır - sandbox'ta da worker'dan bu şekilde (PluginInvocationException) gelir.
        // "Bulunamadı" (MissingMethodException/MissingMemberException) da çağıran hatası - sandbox'ta da
        // aynı tiple gelir (bkz. PluginInvocationException.FromRemote).
        private static bool IsPluginFault(Exception ex) =>
            ex is not PluginInvocationException && ex is not ArgumentException && ex is not TimeoutException
            && ex is not MissingMemberException;

        private static PluginInvocationException ToPluginException(string name, Exception ex)
        {
            var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            return new PluginInvocationException(name, actual.GetType().FullName, actual.Message, actual);
        }

        private static T Sync<T>(Task<T> task) => task.ConfigureAwait(false).GetAwaiter().GetResult();
        private static void Sync(Task task) => task.ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public static class InProcessPluginBuilderExtensions
    {
        /// <summary>SetInstance'lı bir EvokerBuilder'ı (ör. loader.Builder) ortak <see cref="IPluginBuilder"/> yüzeyine sarar.</summary>
        public static InProcessPluginBuilder AsPluginBuilder(this EvokerBuilder builder) => new(builder);
    }
}