using System;
using System.Collections.Generic;
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

        // --- Toplu çağrı ---

        /// <summary>Bir IPC mesajına konacak en fazla çağrı sayısı (büyük listeler bu boyutta parçalanır). Varsayılan 1000.</summary>
        public int BatchChunkSize { get; set; } = 1000;

        public async Task<T?[]> InvokeBatchAsync<T>(string methodName, IReadOnlyList<object?[]> argsList)
        {
            var raw = await InvokeBatchRawAsync(methodName, argsList).ConfigureAwait(false);
            var results = new T?[raw.Length];
            for (int i = 0; i < raw.Length; i++) results[i] = WireValueCodec.ToObject<T>(raw[i]);
            return results;
        }

        public Task ExecuteBatchAsync(string methodName, IReadOnlyList<object?[]> argsList) => InvokeBatchRawAsync(methodName, argsList);

        private async Task<WireValue[]> InvokeBatchRawAsync(string methodName, IReadOnlyList<object?[]> argsList)
        {
            RequireName(methodName, nameof(methodName));
            if (argsList == null) throw new ArgumentNullException(nameof(argsList));
            if (argsList.Count == 0) return Array.Empty<WireValue>();

            var all = new WireValue[argsList.Count];
            int chunk = Math.Max(1, BatchChunkSize);
            for (int start = 0; start < argsList.Count; start += chunk)
            {
                int len = Math.Min(chunk, argsList.Count - start);
                var wire = new WireValue[len][];
                for (int i = 0; i < len; i++)
                {
                    var a = argsList[start + i] ?? Array.Empty<object?>();
                    wire[i] = new WireValue[a.Length];
                    for (int j = 0; j < a.Length; j++) wire[i][j] = WireValueCodec.FromObject(a[j]);
                }
                // Metot, parçanın İLK elemanının argüman şekliyle çözülür (EvokerBuilder.GetFunc'ın sampleArgs'ı gibi);
                // gerçek overload seçimi worker'da her çağrı için argüman değerlerine göre yapılır.
                var codes = wire[0].Select(w => w.TypeCode).ToArray();

                for (int attempt = 0; ; attempt++)
                {
                    int handle = await _handle.ResolveCachedAsync(methodName, codes).ConfigureAwait(false);
                    InvokeBatchReply reply;
                    try
                    {
                        reply = await _handle.InvokeBatchAsync(handle, wire, DefaultTimeoutMs).ConfigureAwait(false);
                    }
                    catch (StaleMethodHandleException) when (attempt == 0)
                    {
                        _handle.ForgetCachedHandle(methodName, codes);
                        continue;
                    }
                    if (!reply.Success)
                        throw PluginInvocationException.FromRemote(methodName, reply.ExceptionType, reply.ExceptionMessage, start + reply.FailedIndex);
                    Array.Copy(reply.Results, 0, all, start, reply.Results.Length);
                    break;
                }
            }
            return all;
        }

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

        // Tipli delegate'ler: sandbox'ta her çağrı zaten IPC - GetFunc/GetAction üzerine tipli kabuk.
        public Func<T1, TResult?> GetFunc<T1, TResult>(string methodName) => PluginTypedDelegates.Func<T1, TResult>(GetFunc<TResult>(methodName));
        public Func<T1, T2, TResult?> GetFunc<T1, T2, TResult>(string methodName) => PluginTypedDelegates.Func<T1, T2, TResult>(GetFunc<TResult>(methodName));
        public Func<T1, T2, T3, TResult?> GetFunc<T1, T2, T3, TResult>(string methodName) => PluginTypedDelegates.Func<T1, T2, T3, TResult>(GetFunc<TResult>(methodName));
        public Func<T1, T2, T3, T4, TResult?> GetFunc<T1, T2, T3, T4, TResult>(string methodName) => PluginTypedDelegates.Func<T1, T2, T3, T4, TResult>(GetFunc<TResult>(methodName));
        public Action<T1> GetAction<T1>(string methodName) => PluginTypedDelegates.Action<T1>(GetAction(methodName));
        public Action<T1, T2> GetAction<T1, T2>(string methodName) => PluginTypedDelegates.Action<T1, T2>(GetAction(methodName));
        public Action<T1, T2, T3> GetAction<T1, T2, T3>(string methodName) => PluginTypedDelegates.Action<T1, T2, T3>(GetAction(methodName));
        public Action<T1, T2, T3, T4> GetAction<T1, T2, T3, T4>(string methodName) => PluginTypedDelegates.Action<T1, T2, T3, T4>(GetAction(methodName));

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

        // --- JSON komut (worker içinde çalışır) ---

        public async Task<DSO.Core.Evoker.Commands.EvokerCommandResult> ExecuteCommandAsync(DSO.Core.Evoker.Commands.EvokerCommand command,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DSO.Core.Evoker.Commands.EvokerCommandResult result;
            try
            {
                // Komutun kendi TimeoutMs'i worker'da uygulanır; host ayrıca DefaultTimeoutMs (+ küçük pay) kadar bekler.
                int? wait = command.TimeoutMs.HasValue ? command.TimeoutMs + 2000 : DefaultTimeoutMs;
                var task = _handle.ExecuteCommandAsync(command.ToJson(), wait);
                var json = cancellationToken.CanBeCanceled ? await WithCancellation(task, cancellationToken).ConfigureAwait(false) : await task.ConfigureAwait(false);
                result = DSO.Core.Evoker.Commands.EvokerCommandResult.FromJson(json);
            }
            catch (TimeoutException ex)
            {
                result = DSO.Core.Evoker.Commands.EvokerCommandResult.Fail(DSO.Core.Evoker.Commands.EvokerErrorCodes.Timeout, ex.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = DSO.Core.Evoker.Commands.EvokerCommandResult.Fail(DSO.Core.Evoker.Commands.EvokerErrorCodes.Cancelled, "Komut iptal edildi (worker'da çalışmaya devam ediyor olabilir).");
            }
            catch (Exception ex)
            {
                // Worker çöktü / bağlantı koptu - plugin'in kendi hatası değil, ortam hatası.
                result = DSO.Core.Evoker.Commands.EvokerCommandResult.Fail(DSO.Core.Evoker.Commands.EvokerErrorCodes.TargetException,
                    $"Sandbox worker cevap veremedi: {ex.Message}", ex.GetType().FullName);
            }
            result.Mode = "Sandbox";
            if (result.ElapsedMs <= 0) result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        private static async Task<T> WithCancellation<T>(Task<T> task, System.Threading.CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => tcs.TrySetResult(true)))
                if (await Task.WhenAny(task, tcs.Task).ConfigureAwait(false) != task)
                    throw new OperationCanceledException(ct);
            return await task.ConfigureAwait(false);
        }

        // --- Event'ler ---

        public async Task<IDisposable> SubscribeAsync(string eventName, Action<PluginEventArgs> handler)
            => await _handle.SubscribeAsync(eventName, handler).ConfigureAwait(false);

        public IDisposable Subscribe(string eventName, Action<PluginEventArgs> handler) => Sync(SubscribeAsync(eventName, handler));

        private string[]? _eventNames;

        /// <summary>Worker'a sormadan, plugin DLL'ini ÇALIŞTIRMADAN (MetadataLoadContext ile) okunur ve cache'lenir.</summary>
        public string[] GetEventNames()
        {
            if (_eventNames != null) return _eventNames;
            var scan = Scanning.PluginScanner.Scan(_handle.PluginFilePath);
            var type = scan.Types.FirstOrDefault(t => string.Equals(t.FullName, TypeFullName, StringComparison.OrdinalIgnoreCase));
            // NOT: tarayıcı sadece public yüzeyi listeler; IncludeNonPublic ise private event'lere abone olunabilir
            // ama burada listelenmezler.
            return _eventNames = type?.Events.Select(e => e.Name).Distinct().ToArray() ?? Array.Empty<string>();
        }

        // --- Tanım ---

        public async Task<Scanning.PluginDescriptor> DescribeAsync(bool includeValues = true)
        {
            var d = Scanning.PluginInspector.Describe(_handle.PluginFilePath, TypeFullName);
            if (includeValues) await Scanning.PluginValueSnapshot.CaptureAsync(d, this).ConfigureAwait(false);
            return d;
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