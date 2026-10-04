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

        public async Task<object?> InvokeAsync(string methodName, params object?[] args)
            => await RunAsync(methodName, () => Builder.InvokeDynamicAsync(methodName, PrepareArgs(methodName, args ?? Array.Empty<object?>()))).ConfigureAwait(false);

        public async Task<T?> InvokeAsync<T>(string methodName, params object?[] args)
            => (T?)WireValueCodec.ConvertTo(await InvokeAsync(methodName, args).ConfigureAwait(false), typeof(T));

        public Task ExecuteAsync(string methodName, params object?[] args) => InvokeAsync(methodName, args);

        public object? Invoke(string methodName, params object?[] args) => Sync(InvokeAsync(methodName, args));
        public T? Invoke<T>(string methodName, params object?[] args) => Sync(InvokeAsync<T>(methodName, args));
        public void Execute(string methodName, params object?[] args) => Sync(ExecuteAsync(methodName, args));

        // --- Toplu çağrı (in-process'te IPC yok - sadece sıralı döngü, aynı hata sözleşmesiyle) ---

        public async Task<T?[]> InvokeBatchAsync<T>(string methodName, IReadOnlyList<object?[]> argsList)
        {
            if (argsList == null) throw new ArgumentNullException(nameof(argsList));
            var results = new T?[argsList.Count];
            for (int i = 0; i < argsList.Count; i++)
            {
                try { results[i] = await InvokeAsync<T>(methodName, argsList[i] ?? Array.Empty<object?>()).ConfigureAwait(false); }
                catch (PluginInvocationException ex) when (ex.BatchIndex == null)
                {
                    throw new PluginInvocationException(methodName, ex.RemoteExceptionType, ex.InnerException?.Message ?? ex.Message, ex.InnerException) { BatchIndex = i };
                }
            }
            return results;
        }

        public Task ExecuteBatchAsync(string methodName, IReadOnlyList<object?[]> argsList) => InvokeBatchAsync<object>(methodName, argsList);

        // --- Property / field (doğrudan DynamicEntityAccessor - sıcak yol) ---

        public T? GetValue<T>(string memberName) => Wrap(memberName, () =>
        {
            var memberType = MemberType(memberName);
            if (IsDirectlyConvertible(memberType, typeof(T)))
                return Builder.GetValue<T>(memberName); // sıcak yol: derlenmiş, boxing'siz getter
            // T üyenin tipiyle ilgisiz (ör. plugin "Point", host kendi "PointDto"sunu istiyor) - sandbox ile
            // AYNI kural: şekil eşlemesi (bkz. WireValueCodec.ConvertTo).
            return (T?)WireValueCodec.ConvertTo(Builder.GetValue<object>(memberName), typeof(T));
        });

        public void SetValue<T>(string memberName, T value) => Wrap<object?>(memberName, () =>
        {
            var memberType = MemberType(memberName);
            if (value == null || IsDirectlyConvertible(typeof(T), memberType))
                Builder.SetValue(memberName, value);
            else
                Builder.SetValue<object?>(memberName, WireValueCodec.ConvertTo(value, memberType));
            return null;
        });

        public Task<T?> GetValueAsync<T>(string memberName) => Task.FromResult(GetValue<T>(memberName));

        public Task SetValueAsync<T>(string memberName, T value)
        {
            SetValue(memberName, value);
            return Task.CompletedTask;
        }

        // --- Delegate'ler ---

        public Func<object?[], T?> GetFunc<T>(string methodName, object?[]? sampleArgs = null)
        {
            // Senkron dönüşlü metot: EvokerBuilder'ın derlenmiş delegate'i DOĞRUDAN (sıfır ek katman).
            // Task/Task<T> dönüşlü metot: EvokerBuilder.GetFunc<T> Task'ı T'ye çeviremez -> bekleyen sarmalayıcı.
            if (!ReturnsTask(methodName, sampleArgs))
            {
                var direct = Builder.GetFunc<T>(methodName, ToObjArray(sampleArgs));
                return args => Wrap(methodName, () => direct(ToObjArray(args)!));
            }
            var f = GetFuncAsync<T>(methodName, sampleArgs);
            return args => Sync(f(args));
        }

        public Action<object?[]> GetAction(string methodName, object?[]? sampleArgs = null)
        {
            if (!ReturnsTask(methodName, sampleArgs))
            {
                var direct = Builder.GetAction(methodName, ToObjArray(sampleArgs));
                return args => Wrap<object?>(methodName, () => { direct(ToObjArray(args)!); return null; });
            }
            // EvokerBuilder.GetAction Task döndüren metodu BEKLEMEZ (fire-and-forget olurdu) - burada bekleniyor.
            var a = GetActionAsync(methodName, sampleArgs);
            return args => Sync(a(args));
        }

        public Func<object?[], Task<T?>> GetFuncAsync<T>(string methodName, object?[]? sampleArgs = null)
            => args => InvokeAsync<T>(methodName, args);

        public Func<object?[], Task> GetActionAsync(string methodName, object?[]? sampleArgs = null)
            => args => ExecuteAsync(methodName, args);

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
        private object?[] PrepareArgs(string methodName, object?[] args)
        {
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
            }
            return copy ?? args;
        }

        private static T? SingleIgnoreCase<T>(T[] members, string name) where T : MemberInfo
        {
            var m = members.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            return m.Count == 1 ? m[0] : null;
        }

        private static object[]? ToObjArray(object?[]? args) => args == null ? null : args.Select(a => a!).ToArray();

        private async Task<object?> RunAsync(string methodName, Func<Task<object?>> call)
        {
            Task<object?> task;
            try { task = call(); }
            catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); }

            if (DefaultTimeoutMs.HasValue)
            {
                var winner = await Task.WhenAny(task, Task.Delay(DefaultTimeoutMs.Value)).ConfigureAwait(false);
                if (winner != task)
                    throw new TimeoutException(
                        $"[InProcessPluginBuilder] '{methodName}' {DefaultTimeoutMs.Value}ms içinde tamamlanmadı " +
                        "(sadece bekleme bırakıldı, plugin kodu çalışmaya devam ediyor olabilir).");
            }

            try { return await task.ConfigureAwait(false); }
            catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(methodName, ex); }
        }

        private static T Wrap<T>(string name, Func<T> call)
        {
            try { return call(); }
            catch (Exception ex) when (IsPluginFault(ex)) { throw ToPluginException(name, ex); }
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