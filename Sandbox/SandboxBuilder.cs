using System;
using System.Linq;
using System.Threading.Tasks;

namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// Sandbox (ayrı worker process) içindeki plugin instance'ı için <see cref="IPluginBuilder"/>.
    /// Her işlem IPC ile worker'daki GERÇEK EvokerBuilder'a (ve DynamicEntityAccessor'a) gider -
    /// yani in-process ile aynı çözümleme/overload/property-field kuralları worker'da uygulanır;
    /// bu sınıf sadece taşıma (encode/decode) ve tipli dönüşümü yapar.
    ///
    /// Örnek:
    ///   var b = handle.Builder;                         // PluginWorkerHandle.Builder
    ///   int sum    = await b.InvokeAsync&lt;int&gt;("Add", 3, 4);
    ///   string ad  = await b.GetValueAsync&lt;string&gt;("DisplayName");
    ///   await b.SetValueAsync("DisplayName", "yeni");
    ///   var add    = b.GetFuncAsync&lt;int&gt;("Add", new object?[] { 0, 0 });   // bir kez çözülür
    ///   for (...) total += (await add(new object?[] { i, i }))!;
    ///
    /// Worker otomatik yeniden başlatılırsa (AutoRestartOnCrash) builder ve ondan alınmış delegate'ler
    /// geçerli kalır (metotlar kendiliğinden yeniden çözülür) - ama plugin'in İÇ DURUMU (alan değerleri)
    /// yeni process'te sıfırdan başlar.
    /// </summary>
    public sealed class SandboxBuilder : IPluginBuilder
    {
        private readonly PluginWorkerHandle _handle;

        internal SandboxBuilder(PluginWorkerHandle handle) => _handle = handle;

        public string TypeFullName => _handle.TypeFullName;
        public bool IncludeNonPublic => _handle.IncludeNonPublic;
        public bool IsSandboxed => true;
        public int? DefaultTimeoutMs { get; set; }

        // --- Metot çağırma ---

        public async Task<object?> InvokeAsync(string methodName, params object?[] args)
            => WireValueCodec.ToObject(await _handle.CallRawAsync(methodName, args ?? Array.Empty<object?>(), DefaultTimeoutMs).ConfigureAwait(false));

        public async Task<T?> InvokeAsync<T>(string methodName, params object?[] args)
            => WireValueCodec.ToObject<T>(await _handle.CallRawAsync(methodName, args ?? Array.Empty<object?>(), DefaultTimeoutMs).ConfigureAwait(false));

        public Task ExecuteAsync(string methodName, params object?[] args)
            => _handle.CallRawAsync(methodName, args ?? Array.Empty<object?>(), DefaultTimeoutMs);

        public object? Invoke(string methodName, params object?[] args) => Sync(InvokeAsync(methodName, args));
        public T? Invoke<T>(string methodName, params object?[] args) => Sync(InvokeAsync<T>(methodName, args));
        public void Execute(string methodName, params object?[] args) => Sync(ExecuteAsync(methodName, args));

        // --- Property / field ---

        public async Task<T?> GetValueAsync<T>(string memberName)
        {
            RequireName(memberName, nameof(memberName));
            var reply = await _handle.MemberAsync(MemberOperation.Get, memberName, WireValue.Null, DefaultTimeoutMs).ConfigureAwait(false);
            ThrowIfFailed(reply, memberName);
            return WireValueCodec.ToObject<T>(reply.Result ?? WireValue.Null);
        }

        public async Task SetValueAsync<T>(string memberName, T value)
        {
            RequireName(memberName, nameof(memberName));
            var reply = await _handle.MemberAsync(MemberOperation.Set, memberName, WireValueCodec.FromObject(value), DefaultTimeoutMs).ConfigureAwait(false);
            ThrowIfFailed(reply, memberName);
        }

        public T? GetValue<T>(string memberName) => Sync(GetValueAsync<T>(memberName));
        public void SetValue<T>(string memberName, T value) => Sync(SetValueAsync(memberName, value));

        // --- Delegate'ler ---
        // Metot bir kez çözülüp (Resolve) handle delegate içinde tutulur; her çağrı sadece
        // encode + Invoke + decode. Worker restart olursa (StaleMethodHandleException) delegate
        // kendini bir kez yeniden çözer - dışarıdan fark edilmez.

        public Func<object?[], Task<T?>> GetFuncAsync<T>(string methodName, object?[]? sampleArgs = null)
        {
            var call = CreateResolvedCall(methodName, sampleArgs);
            return async args => WireValueCodec.ToObject<T>(await call(args).ConfigureAwait(false));
        }

        public Func<object?[], Task> GetActionAsync(string methodName, object?[]? sampleArgs = null)
        {
            var call = CreateResolvedCall(methodName, sampleArgs);
            return args => call(args);
        }

        public Func<object?[], T?> GetFunc<T>(string methodName, object?[]? sampleArgs = null)
        {
            var f = GetFuncAsync<T>(methodName, sampleArgs);
            return args => Sync(f(args));
        }

        public Action<object?[]> GetAction(string methodName, object?[]? sampleArgs = null)
        {
            var a = GetActionAsync(methodName, sampleArgs);
            return args => Sync(a(args));
        }

        private Func<object?[], Task<WireValue>> CreateResolvedCall(string methodName, object?[]? sampleArgs)
        {
            RequireName(methodName, nameof(methodName));
            int? handle = null;
            WireTypeCode[]? resolvedShape = null;

            return async args =>
            {
                args ??= Array.Empty<object?>();
                var wire = new WireValue[args.Length];
                var codes = new WireTypeCode[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    wire[i] = WireValueCodec.FromObject(args[i]);
                    codes[i] = wire[i].TypeCode;
                }

                for (int attempt = 0; ; attempt++)
                {
                    // İlk çağrıda (ya da restart sonrası) çöz. sampleArgs verildiyse onun şekli, yoksa ilk
                    // gerçek çağrının şekli kullanılır - EvokerBuilder.GetFunc(name, sampleArgs) ile aynı mantık.
                    if (handle == null)
                    {
                        resolvedShape ??= sampleArgs != null
                            ? sampleArgs.Select(a => WireValueCodec.FromObject(a).TypeCode).ToArray()
                            : codes;
                        handle = await _handle.ResolveAsync(TypeFullName, methodName, resolvedShape).ConfigureAwait(false);
                    }

                    InvokeReply reply;
                    try
                    {
                        reply = await _handle.InvokeAsync(handle.Value, wire, DefaultTimeoutMs).ConfigureAwait(false);
                    }
                    catch (StaleMethodHandleException) when (attempt == 0)
                    {
                        handle = null;
                        continue;
                    }

                    if (!reply.Success)
                        throw PluginInvocationException.FromRemote(methodName, reply.ExceptionType, reply.ExceptionMessage);
                    return reply.Result ?? WireValue.Null;
                }
            };
        }

        // --- Cache ---

        public async Task ForgetCacheAsync()
        {
            var reply = await _handle.MemberAsync(MemberOperation.ForgetCache, "", WireValue.Null, DefaultTimeoutMs).ConfigureAwait(false);
            ThrowIfFailed(reply, nameof(ForgetCache));
        }

        public void ForgetCache() => Sync(ForgetCacheAsync());

        // --- yardımcılar ---

        private static void ThrowIfFailed(InvokeReply reply, string memberName)
        {
            if (!reply.Success)
                throw PluginInvocationException.FromRemote(memberName, reply.ExceptionType, reply.ExceptionMessage);
        }

        private static void RequireName(string name, string paramName)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("İsim boş olamaz.", paramName);
        }

        // Tüm iç await'ler ConfigureAwait(false) - UI/ASP.NET SynchronizationContext'inde bloklamak deadlock yapmaz.
        private static T Sync<T>(Task<T> task) => task.ConfigureAwait(false).GetAwaiter().GetResult();
        private static void Sync(Task task) => task.ConfigureAwait(false).GetAwaiter().GetResult();
    }
}