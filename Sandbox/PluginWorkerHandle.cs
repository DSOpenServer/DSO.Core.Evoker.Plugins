using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using DSO.Core.Evoker.Plugins.Loading;

namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// Host tarafında TEK bir worker process'i temsil eder. Bir plugin (dosya yolu + tip) için
    /// PluginWorkerPool tarafından yaratılır ve pool'un ömrü boyunca (long-lived) yaşar.
    ///
    /// Yaşam döngüsü: StartAsync (bir kez) -> ResolveAsync (metot başına bir kez, handle alınır) ->
    /// InvokeAsync (handle ile milyonlarca kez) -> PromoteToInProcessAsync (opsiyonel, state kaybı
    /// bilinen davranış) ya da DisposeAsync (kapanış).
    /// </summary>
    public sealed class PluginWorkerHandle : IAsyncDisposable
    {
        public string PluginFilePath { get; }
        public string TypeFullName { get; }
        public bool IncludeNonPublic { get; }
        public PluginWorkerOptions Options { get; }

        // TODO 10
        private Process? _process;
        // TODO 11
        private NamedPipeServerStream? _pipe;
        private IpcWriter? _writer;
        private IpcReader? _reader;
        // TODO 12
        private SemaphoreSlim? _concurrencyGate;
        // TODO 13
        private readonly ConcurrentDictionary<long, TaskCompletionSource<InvokeReply>> _pending = new();
        private readonly ConcurrentDictionary<long, TaskCompletionSource<InvokeBatchReply>> _pendingBatch = new();
        private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _pendingCommands = new();
        private long _correlationCounter;
        // Resolve'un kendi CorrelationId'si YOK (bkz. ResolveMessages.cs) - bu yüzden aynı anda
        // sadece BİR Resolve isteği beklenebilir, bu kilit onu garanti eder.
        private readonly SemaphoreSlim _resolveLock = new(1, 1);
        private TaskCompletionSource<ResolveReply>? _pendingResolve;
        // TODO 14
        private int _missedHeartbeats;
        private Task? _readLoopTask;
        private Task? _heartbeatTask;
        private CancellationTokenSource? _lifetimeCts;

        // Worker "nesli": her başarılı LaunchAsync'te (ilk başlatma + her auto-restart) artar.
        // Worker'ın MethodHandle sayacı her yeni process'te 1'den başlar - restart ÖNCESİ alınmış bir
        // handle, yeni worker'da BAŞKA bir metoda denk gelebilir (sessizce yanlış metot çağırmak!).
        // Bu yüzden ResolveAsync çağırana worker'ın ham numarasını DEĞİL, host tarafında TEKİL (nesiller
        // boyunca asla tekrar etmeyen) bir "public handle" veriyoruz; _handleMap onu (nesil, worker
        // handle) ikilisine çevirir. Eski nesilden bir public handle ile InvokeAsync çağrılırsa net bir
        // StaleMethodHandleException fırlatılır.
        private int _generation;
        private int _publicHandleCounter;
        private readonly ConcurrentDictionary<int, (int Generation, int WorkerHandle)> _handleMap = new();

        // CallAsync'in (isimle çağırma kolaylığı) kendi resolve cache'i - restart'ta temizlenir.
        private readonly ConcurrentDictionary<(string Method, ShapeKey ArgShape), int> _callHandleCache = new();

        // Argüman tip kodları şekli: 8 argümana kadar tek bir ulong'a paketlenir (allocation yok);
        // daha fazlası için string. Eskiden her çağrıda string.Join ile string üretiliyordu.
        private readonly struct ShapeKey : IEquatable<ShapeKey>
        {
            private readonly int _count;
            private readonly ulong _packed;
            private readonly string? _overflow;

            public ShapeKey(WireTypeCode[] codes)
            {
                _count = codes.Length;
                _packed = 0;
                _overflow = null;
                if (codes.Length > 8) { _overflow = string.Join(",", codes); return; }
                for (int i = 0; i < codes.Length; i++) _packed |= (ulong)(byte)codes[i] << (i * 8);
            }

            public bool Equals(ShapeKey o) => _count == o._count && _packed == o._packed && string.Equals(_overflow, o._overflow, StringComparison.Ordinal);
            public override bool Equals(object? obj) => obj is ShapeKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(_count, _packed, _overflow);
        }

        // --- Event abonelikleri ---
        // Host tarafında tutulan kayıt restart'tan SAĞ ÇIKAR: yeni worker'a otomatik yeniden abone olunur.
        private readonly ConcurrentDictionary<int, (string EventName, Action<PluginEventArgs> Handler)> _eventSubs = new();
        private int _eventSubCounter;
        // EventRaised'lar okuma döngüsünden BURAYA bırakılır, tek bir dağıtıcı sırayla handler'ları çağırır:
        // yavaş bir handler okuma döngüsünü (dolayısıyla Pong'ları/cevapları) bloklamaz, sıra korunur.
        private readonly System.Threading.Channels.Channel<(int Id, WireValue[] Args)> _eventDispatch =
            System.Threading.Channels.Channel.CreateUnbounded<(int, WireValue[])>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        private Task? _eventDispatchTask;

        /// <summary>
        /// Bir event handler'ı exception fırlattığında (handler host kodudur - plugin'i ETKİLEMEZ, sonraki
        /// event'ler gelmeye devam eder). Bağlanmazsa hata System.Diagnostics.Trace'e yazılır.
        /// </summary>
        public event Action<PluginEventArgs, Exception>? EventHandlerFailed;

        /// <summary>Worker nesli - 1 = ilk başlatma, her auto-restart'ta +1. Tanılama amaçlı.</summary>
        public int Generation => Volatile.Read(ref _generation);

        private int _deadFlag; // Interlocked ile korunan bool (0/1) - MarkDead'i idempotent yapmak için
        private bool _restartedOnce;
        private bool _disposed;
        private bool _promoted;

        public bool IsDead => Volatile.Read(ref _deadFlag) != 0;

        /// <summary>Worker process'inin OS process id'si - tanılama/gözlemleme amaçlı (ör. testte kasıtlı kill etmek için). StartAsync öncesi/sonrası dispose edilmişse null.</summary>
        public int? ProcessId { get { try { return _process?.Id; } catch (InvalidOperationException) { return null; } } }

        /// <param name="constructorArgs">
        /// Plugin'in constructor argümanları (JSON dizi ya da parametre adlarıyla nesne). Worker'a bağlantıdan hemen sonra
        /// Init mesajıyla gider (komut satırında görünmez); her yeniden başlatmada aynı değerler kullanılır. null =
        /// parametresiz ya da tüm parametreleri optional constructor.
        /// </param>
        public PluginWorkerHandle(string pluginFilePath, string typeFullName, PluginWorkerOptions options, bool includeNonPublic = false,
            System.Text.Json.JsonElement? constructorArgs = null)
        {
            PluginFilePath = pluginFilePath ?? throw new ArgumentNullException(nameof(pluginFilePath));
            TypeFullName = typeFullName ?? throw new ArgumentNullException(nameof(typeFullName));
            Options = options ?? throw new ArgumentNullException(nameof(options));
            IncludeNonPublic = includeNonPublic;
            ConstructorArgsJson = constructorArgs is { ValueKind: not (System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null) } c
                ? c.GetRawText() : null;
        }

        /// <summary>Worker'a Init ile gönderilen constructor argümanları (JSON) - null = argümansız.</summary>
        public string? ConstructorArgsJson { get; }

        /// <summary>TODO 15: Process.Start (worker) + pipe bağlantısı + Hello handshake.</summary>
        public Task StartAsync()
        {
            if (_process != null)
                throw new InvalidOperationException("Bu handle zaten başlatılmış - her plugin için yeni bir PluginWorkerHandle oluşturun.");

            return LaunchAsync();
        }

        private async Task LaunchAsync()
        {
            if (string.IsNullOrWhiteSpace(Options.HostPath))
                throw new InvalidOperationException(
                    "[PluginWorkerHandle] Options.HostPath boş - worker'ı çalıştıracak DSO.Core.Evoker.PluginHost " +
                    "yolunu (dll ya da exe) belirtmelisiniz.");

            string pipeName = "dso-plugin-" + Guid.NewGuid().ToString("N");
            // CurrentUserOnly: pipe'a SADECE bu process'i çalıştıran kullanıcı bağlanabilir (Windows'ta ACL,
            // Unix'te soket izni + karşı tarafın kimlik kontrolü). Worker tarafı da aynı bayrakla, sadece aynı
            // kullanıcının açtığı pipe'a bağlanır. Pipe adı ayrıca rastgele (GUID) ve tek bağlantılık.
            var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            var psi = new ProcessStartInfo
            {
                FileName = Options.HostIsDotnetDll ? "dotnet" : Options.HostPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (Options.HostIsDotnetDll)
                psi.ArgumentList.Add(Options.HostPath);
            psi.ArgumentList.Add(pipeName);
            psi.ArgumentList.Add(PluginFilePath);
            psi.ArgumentList.Add(TypeFullName);
            psi.ArgumentList.Add(IncludeNonPublic ? "true" : "false");
            psi.ArgumentList.Add(Options.MaxConcurrency.ToString());

            using var startupCts = new CancellationTokenSource(Options.StartupTimeoutMs);

            Process process;
            try
            {
                process = Process.Start(psi)
                    ?? throw new InvalidOperationException("[PluginWorkerHandle] Process.Start null döndürdü.");
            }
            catch (Exception ex)
            {
                pipeServer.Dispose();
                throw new InvalidOperationException(
                    $"[PluginWorkerHandle] Worker process başlatılamadı ('{psi.FileName}' {string.Join(' ', psi.ArgumentList)}): {ex.Message}", ex);
            }

            // stdout/stderr redirect edildi - OKUNMAZSA işletim sistemi pipe buffer'ı (~4-64KB) dolunca
            // plugin'in Console.WriteLine'ı worker'ı SONSUZA KADAR bloklar (klasik redirect deadlock'u).
            // Bu yüzden asenkron olarak tüketiyoruz; stderr satırları (worker'ın kendi hata çıktısı)
            // tanılama için crash log'a da düşülüyor.
            var stderrTail = new ConcurrentQueue<string>();
            process.OutputDataReceived += (_, _) => { /* bilinçli olarak yutuluyor */ };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data == null) return;
                stderrTail.Enqueue(e.Data);
                while (stderrTail.Count > 20 && stderrTail.TryDequeue(out _)) { }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Worker (ör. yanlış HostPath, eksik runtime) hemen ölürse StartupTimeoutMs'in tamamını
            // beklemek yerine HEMEN hata ver: bağlantı ile process çıkışı arasında yarış.
            var connectTask = pipeServer.WaitForConnectionAsync(startupCts.Token);
            var exitTask = process.WaitForExitAsync(startupCts.Token);
            try
            {
                var first = await Task.WhenAny(connectTask, exitTask).ConfigureAwait(false);
                if (first == exitTask && !connectTask.IsCompletedSuccessfully)
                {
                    await exitTask.ConfigureAwait(false); // iptal ise OperationCanceledException fırlatır
                    pipeServer.Dispose();
                    throw new InvalidOperationException(
                        $"[PluginWorkerHandle] Worker process pipe'a bağlanamadan çıktı (exit code {SafeExitCode(process)}). " +
                        $"stderr: {string.Join(" | ", stderrTail)}");
                }
                await connectTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                pipeServer.Dispose();
                throw new TimeoutException(
                    $"[PluginWorkerHandle] Worker {Options.StartupTimeoutMs}ms içinde pipe'a bağlanmadı - başlatma iptal edildi.");
            }

            var writer = new IpcWriter(pipeServer);
            var reader = new IpcReader(pipeServer);

            // Protokol v2: plugin yüklenmeden ÖNCE constructor argümanları (eski worker bunu okumaz; Hello'daki sürüm
            // kontrolünde zaten reddedilir).
            try { await writer.WriteInitAsync(ConstructorArgsJson).ConfigureAwait(false); }
            catch (Exception ex)
            {
                KillQuietly(process);
                pipeServer.Dispose();
                throw new InvalidOperationException("[PluginWorkerHandle] Worker'a başlangıç (Init) mesajı gönderilemedi.", ex);
            }

            IpcMessageType helloType;
            byte[] helloPayload;
            try
            {
                (helloType, helloPayload) = await ReadWithTimeoutAsync(reader, Options.StartupTimeoutMs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                KillQuietly(process);
                pipeServer.Dispose();
                throw new InvalidOperationException("[PluginWorkerHandle] Hello handshake beklenirken hata oluştu.", ex);
            }

            if (helloType != IpcMessageType.Hello)
            {
                KillQuietly(process);
                pipeServer.Dispose();
                throw new InvalidOperationException($"[PluginWorkerHandle] Beklenen ilk mesaj Hello, gelen: {helloType}.");
            }

            var (success, _, error, workerProtocol) = IpcMessageCodec.DecodeHelloWithVersion(helloPayload);
            if (workerProtocol != IpcProtocol.Version)
            {
                KillQuietly(process);
                pipeServer.Dispose();
                string hostFile = Options.HostPath;
                string stamp = File.Exists(hostFile) ? File.GetLastWriteTime(hostFile).ToString("yyyy-MM-dd HH:mm:ss") : "?";
                throw new InvalidOperationException(
                    $"[PluginWorkerHandle] PluginHost sürümü uyumsuz: worker protokol v{workerProtocol}, bu kütüphane v{IpcProtocol.Version}. " +
                    $"HostPath '{hostFile}' (dosya tarihi {stamp}) muhtemelen ESKİ bir build - DSO.Core.Evoker.PluginHost projesini " +
                    "yeniden derleyin ve HostPath'i o build çıktısına yönlendirin." +
                    (success ? "" : $" (Worker ayrıca şunu bildirdi: {error})"));
            }
            if (!success)
            {
                KillQuietly(process);
                pipeServer.Dispose();
                throw new InvalidOperationException($"[PluginWorkerHandle] Worker plugin'i yükleyemedi: {error}");
            }

            // Handshake tamam - state'i ata ve arka plan döngülerini başlat.
            _process = process;
            _pipe = pipeServer;
            _writer = writer;
            _reader = reader;
            _concurrencyGate ??= new SemaphoreSlim(Options.MaxConcurrency, Options.MaxConcurrency);
            _lifetimeCts = new CancellationTokenSource();
            Interlocked.Increment(ref _generation);

            _eventDispatchTask ??= Task.Run(EventDispatchLoopAsync);
            _readLoopTask = Task.Run(() => ReadLoopAsync(_lifetimeCts.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_lifetimeCts.Token));
        }

        private static async Task<(IpcMessageType, byte[])> ReadWithTimeoutAsync(IpcReader reader, int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            return await reader.ReadFrameAsync(cts.Token).ConfigureAwait(false);
        }

        /// <summary>TODO 16: ResolveRequest gönder, ResolveReply'yi bekle, MethodHandle döndür.</summary>
        public async Task<int> ResolveAsync(string typeName, string methodName, WireTypeCode[] argTypeCodes)
        {
            EnsureAlive();

            await _resolveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var tcs = new TaskCompletionSource<ResolveReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingResolve = tcs;

                await _writer!.WriteResolveRequestAsync(new ResolveRequest
                {
                    TypeName = typeName,
                    MethodName = methodName,
                    ArgTypeCodes = argTypeCodes
                }).ConfigureAwait(false);

                var reply = await tcs.Task.ConfigureAwait(false);
                if (!reply.Success)
                    throw new MissingMethodException(reply.Error ?? $"'{methodName}' resolve edilemedi.");

                int publicHandle = Interlocked.Increment(ref _publicHandleCounter);
                _handleMap[publicHandle] = (Generation, reply.MethodHandle);
                return publicHandle;
            }
            finally
            {
                _pendingResolve = null;
                _resolveLock.Release();
            }
        }

        /// <summary>
        /// TODO 17: _concurrencyGate ile sınırlı şekilde InvokeRequest gönder, CorrelationId'yi
        /// _pending'e ekle, cevabı bekle. timeoutMs SADECE bu çağrıyı etkiler - worker'ı ETKİLEMEZ,
        /// zaman aşımında sadece bu bekleyiş bırakılır (bkz. InvokeRequest.TimeoutMs'in kendi notu).
        /// </summary>
        public Task<InvokeReply> InvokeAsync(int methodHandle, WireValue[] args, int? timeoutMs = null)
        {
            try { EnsureAlive(); }
            catch (Exception ex) { return Task.FromException<InvokeReply>(ex); } // eski async davranışı: hata Task'ta

            // Hiç verilmemiş bir handle: worker'a HİÇ gönderilmeden temiz bir Success=false dönülür
            // (worker'ın kendi handle numaraları ile tesadüfen çakışıp yanlış metodu çağırmasın diye).
            // ÖNCEKİ bir nesilde verilmiş bir handle: StaleMethodHandleException (bkz. _generation notu).
            if (!_handleMap.TryGetValue(methodHandle, out var mapped))
            {
                return Task.FromResult(new InvokeReply
                {
                    Success = false,
                    ExceptionType = nameof(MissingMethodException),
                    ExceptionMessage = $"Bilinmeyen MethodHandle: {methodHandle} (önce ResolveAsync çağrılmalı)."
                });
            }
            if (mapped.Generation != Generation)
                return Task.FromException<InvokeReply>(new StaleMethodHandleException(methodHandle, mapped.Generation, Generation));

            return SendInvokeAsync(mapped.WorkerHandle, args, timeoutMs);
        }

        // Invoke'un sıcak yolu: gate -> CorrelationId -> (InvokeRequest nesnesi ve closure OLMADAN) yaz -> bekle.
        private async Task<InvokeReply> SendInvokeAsync(int workerHandle, WireValue[] args, int? timeoutMs)
        {
            await _concurrencyGate!.WaitAsync().ConfigureAwait(false);
            long correlationId = Interlocked.Increment(ref _correlationCounter);
            var tcs = new TaskCompletionSource<InvokeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[correlationId] = tcs;
            try
            {
                await _writer!.WriteInvokeRequestAsync(correlationId, workerHandle, timeoutMs, args).ConfigureAwait(false);
                if (timeoutMs.HasValue) await WaitWithTimeoutAsync(tcs.Task, timeoutMs.Value, "Invoke").ConfigureAwait(false);
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(correlationId, out _);
                _concurrencyGate.Release();
            }
        }

        // Zaman aşımı: Task.Delay yerine iptal edilebilir bekleme - cevap ZAMANINDA gelirse zamanlayıcı hemen
        // bırakılır (eskiden her zaman aşımlı çağrı, süre dolana kadar yaşayan bir Task.Delay bırakıyordu).
        private static async Task WaitWithTimeoutAsync(Task task, int timeoutMs, string what)
        {
            if (task.IsCompleted) return;
            using var cts = new CancellationTokenSource();
            var delay = Task.Delay(timeoutMs, cts.Token);
            var winner = await Task.WhenAny(task, delay).ConfigureAwait(false);
            cts.Cancel();
            if (winner != task)
                throw new TimeoutException(
                    $"[PluginWorkerHandle] {what} {timeoutMs}ms içinde cevap vermedi (worker ETKİLENMEDİ, " +
                    "sadece bu çağrı zaman aşımına uğradı - bkz. InvokeRequest.TimeoutMs).");
        }

        /// <summary>
        /// Property/field okuma-yazma ve worker tarafı cache temizliği (bkz. MemberRequest). Invoke ile
        /// AYNI concurrency gate'i ve bekleyen-cevap mekanizmasını kullanır - MaxConcurrency=1 iken bir
        /// alanın, plugin'in bir metodu çalışırken ARADAN yazılması gibi thread-safety ihlalleri olmaz.
        /// </summary>
        public Task<InvokeReply> MemberAsync(MemberOperation operation, string memberName, WireValue value, int? timeoutMs = null)
        {
            EnsureAlive();
            return SendAndWaitAsync(
                (correlationId, ct) => _writer!.WriteMemberRequestAsync(correlationId, operation, memberName ?? "", value, ct),
                timeoutMs, operation.ToString());
        }

        /// <summary>
        /// Toplu çağrı: aynı metoda N argüman seti, TEK frame, worker'da sırayla. Concurrency gate'ten BİR kez
        /// geçer (tüm toplu iş tek bir "çağrı" sayılır). Hata olursa FailedIndex'e kadar olanlar çalışmıştır.
        /// </summary>
        public async Task<InvokeBatchReply> InvokeBatchAsync(int methodHandle, WireValue[][] argsList, int? timeoutMs = null)
        {
            EnsureAlive();
            if (!_handleMap.TryGetValue(methodHandle, out var mapped))
                return new InvokeBatchReply
                {
                    Success = false,
                    FailedIndex = 0,
                    ExceptionType = nameof(MissingMethodException),
                    ExceptionMessage = $"Bilinmeyen MethodHandle: {methodHandle} (önce ResolveAsync çağrılmalı)."
                };
            if (mapped.Generation != Generation)
                throw new StaleMethodHandleException(methodHandle, mapped.Generation, Generation);

            await _concurrencyGate!.WaitAsync().ConfigureAwait(false);
            long correlationId = Interlocked.Increment(ref _correlationCounter);
            var tcs = new TaskCompletionSource<InvokeBatchReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingBatch[correlationId] = tcs;
            try
            {
                await _writer!.WriteInvokeBatchRequestAsync(new InvokeBatchRequest
                {
                    CorrelationId = correlationId,
                    MethodHandle = mapped.WorkerHandle,
                    ArgsList = argsList
                }).ConfigureAwait(false);

                if (timeoutMs.HasValue) await WaitWithTimeoutAsync(tcs.Task, timeoutMs.Value, "InvokeBatch").ConfigureAwait(false);
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _pendingBatch.TryRemove(correlationId, out _);
                _concurrencyGate.Release();
            }
        }

        /// <summary>
        /// JSON komutu (DSO.Core.Evoker.Commands.EvokerCommand) worker İÇİNDE çalıştırır - worker plugin'in gerçek tipleriyle
        /// in-process ile AYNI çalıştırıcıyı (EvokerTarget) kullanır, sonuç JSON'u (EvokerCommandResult) döner. Concurrency
        /// gate'ten bir çağrı gibi geçer. timeoutMs sadece bu bekleyişi keser (worker etkilenmez).
        /// </summary>
        public async Task<string> ExecuteCommandAsync(string commandJson, int? timeoutMs = null)
        {
            EnsureAlive();
            await _concurrencyGate!.WaitAsync().ConfigureAwait(false);
            long correlationId = Interlocked.Increment(ref _correlationCounter);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingCommands[correlationId] = tcs;
            try
            {
                await _writer!.WriteCommandAsync(correlationId, commandJson).ConfigureAwait(false);
                if (timeoutMs.HasValue) await WaitWithTimeoutAsync(tcs.Task, timeoutMs.Value, "Command").ConfigureAwait(false);
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _pendingCommands.TryRemove(correlationId, out _);
                _concurrencyGate.Release();
            }
        }

        /// <summary>
        /// (metot adı, argüman tip şekli) için public handle - cache'li; restart sonrası cache temizlenir.
        /// CallRawAsync ve SandboxBuilder'ın toplu çağrısı bunu kullanır.
        /// </summary>
        internal async Task<int> ResolveCachedAsync(string methodName, WireTypeCode[] codes)
        {
            var key = (methodName, new ShapeKey(codes));
            if (_callHandleCache.TryGetValue(key, out int handle)) return handle;
            handle = await ResolveAsync(TypeFullName, methodName, codes).ConfigureAwait(false);
            _callHandleCache[key] = handle;
            return handle;
        }

        internal void ForgetCachedHandle(string methodName, WireTypeCode[] codes) =>
            _callHandleCache.TryRemove((methodName, new ShapeKey(codes)), out _);

        // Invoke ve Member isteklerinin ortak yolu: gate -> CorrelationId -> yaz -> (opsiyonel timeout ile) bekle.
        private async Task<InvokeReply> SendAndWaitAsync(Func<long, CancellationToken, Task> write, int? timeoutMs, string what)
        {
            await _concurrencyGate!.WaitAsync().ConfigureAwait(false);
            long correlationId = Interlocked.Increment(ref _correlationCounter);
            var tcs = new TaskCompletionSource<InvokeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[correlationId] = tcs;
            try
            {
                await write(correlationId, CancellationToken.None).ConfigureAwait(false);

                if (timeoutMs.HasValue) await WaitWithTimeoutAsync(tcs.Task, timeoutMs.Value, what).ConfigureAwait(false);

                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(correlationId, out _);
                _concurrencyGate.Release();
            }
        }

        /// <summary>
        /// Kolaylık katmanı: isimle çağır, argümanları/sonucu otomatik encode/decode et. Resolve'u
        /// (metot adı + argüman tip şekli başına) BİR KEZ yapıp cache'ler; auto-restart sonrası
        /// cache temizlendiği için bir sonraki çağrı otomatik olarak yeniden resolve eder - çağıranın
        /// handle/nesil yönetimiyle uğraşması gerekmez. Plugin metodu exception fırlatırsa
        /// <see cref="PluginInvocationException"/> fırlatılır (worker ÇÖKMEZ, sadece bu çağrı başarısız).
        /// Complex dönüşler, host plugin tipini yüklemediyse JsonElement olarak gelir (bkz. WireValueCodec).
        /// Ham ResolveAsync/InvokeAsync ise milyonlarca çağrılık döngüler için hâlâ doğrudan kullanılabilir.
        /// </summary>
        public async Task<object?> CallAsync(string methodName, object?[] args, int? timeoutMs = null)
        {
            var raw = await CallRawAsync(methodName, args, timeoutMs).ConfigureAwait(false);
            return WireValueCodec.ToObject(raw);
        }

        /// <summary>
        /// Sandbox'ta, worker içindeki EvokerBuilder'ın TÜM yüzeyine (Invoke/Execute/GetValue/SetValue/
        /// GetFunc/GetAction/ForgetCache, sync+async, tipli dönüş) erişim. Bkz. <see cref="SandboxBuilder"/> ve
        /// <see cref="DSO.Core.Evoker.Plugins.IPluginBuilder"/> - promote sonrası in-process tarafta da aynı arayüz
        /// (loader.Builder.AsPluginBuilder()) kullanılabilir. StartAsync öncesi de alınabilir, ilk çağrıda
        /// worker başlatılmış olmalı. Restart sonrası da geçerli kalır.
        /// </summary>
        public SandboxBuilder Builder => _builder ??= new SandboxBuilder(this);
        private SandboxBuilder? _builder;

        /// <summary>CallAsync'in ham hali - decode edilmemiş WireValue döner (tipli decode için SandboxBuilder kullanır).</summary>
        internal async Task<WireValue> CallRawAsync(string methodName, object?[] args, int? timeoutMs = null)
        {
            if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("methodName boş olamaz.", nameof(methodName));
            args ??= Array.Empty<object?>();

            var wireArgs = new WireValue[args.Length];
            var codes = new WireTypeCode[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                wireArgs[i] = WireValueCodec.FromObject(args[i]);
                codes[i] = wireArgs[i].TypeCode;
            }

            var key = (methodName, new ShapeKey(codes));
            for (int attempt = 0; ; attempt++)
            {
                if (!_callHandleCache.TryGetValue(key, out int handle))
                {
                    handle = await ResolveAsync(TypeFullName, methodName, codes).ConfigureAwait(false);
                    _callHandleCache[key] = handle;
                }

                InvokeReply reply;
                try
                {
                    reply = await InvokeAsync(handle, wireArgs, timeoutMs).ConfigureAwait(false);
                }
                catch (StaleMethodHandleException) when (attempt == 0)
                {
                    _callHandleCache.TryRemove(key, out _);
                    continue; // bir kez yeniden resolve et
                }

                if (!reply.Success)
                    throw PluginInvocationException.FromRemote(methodName, reply.ExceptionType, reply.ExceptionMessage);

                return reply.Result ?? WireValue.Null;
            }
        }

        /// <summary>
        /// TODO 18: Sandbox'tan in-process'e terfi. State KAYBOLUR (dokümante edilen, bilinen davranış) -
        /// worker'ı düzgün (Shutdown) ya da gerekirse zorla kapatır, host içinde ManagedDotNetPluginLoader
        /// ile TAZE bir instance yükler ve onu döner. Bu handle terfi sonrası KULLANILAMAZ hale gelir.
        /// </summary>
        public async Task<ManagedDotNetPluginLoader> PromoteToInProcessAsync()
        {
            EnsureAlive();
            _promoted = true;

            await ShutdownInternalAsync(graceful: true).ConfigureAwait(false);

            var loader = new ManagedDotNetPluginLoader();
            await loader.LoadInProcessAsync(PluginFilePath, TypeFullName, IncludeNonPublic).ConfigureAwait(false);
            return loader;
        }

        /// <summary>
        /// TODO 19: heartbeat döngüsü. Worker'ın MESAJ DÖNGÜSÜNÜN canlı olup olmadığını kontrol eder -
        /// Ping/Pong, worker'ın okuma döngüsünden (iş mantığı thread-pool'undan DEĞİL) cevaplanır,
        /// böylece uzun süren bir Invoke hang tespitini yanlış tetiklemez (bkz. IpcMessageType.Ping notu
        /// ve PluginHost/Program.cs'in dispatch mantığı).
        /// </summary>
        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Options.HeartbeatIntervalMs));
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    if (IsDead) return;

                    int missed = Interlocked.Increment(ref _missedHeartbeats);
                    if (missed > Options.MissedHeartbeatsBeforeKill)
                    {
                        var ex = new TimeoutException(
                            $"[PluginWorkerHandle] Worker {Options.MissedHeartbeatsBeforeKill} heartbeat'e art arda cevap vermedi - " +
                            "hung/dead sayıldı, Process.Kill() ediliyor.");
                        _pendingCrashReason = ex; // read loop'un catch'i (pipe kopunca) bunu asıl sebep olarak raporlar
                        KillQuietly(_process);
                        MarkDeadAndFailPending(ex);
                        // Loglama/bildirim ve restart kararı ve işlemi TEK bir yerde (read loop'un catch'i, pipe koptuğunda
                        // zaten tetiklenecek) veriliyor - burada sadece kill ediyoruz, çift restart'ı
                        // önlemek için.
                        return;
                    }

                    try
                    {
                        await _writer!.WritePingAsync(ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Yazma başarısız oldu - pipe muhtemelen koptu, ReadLoopAsync bunu zaten
                        // yakalayıp "worker öldü" akışını işletecek. Burada sessizce çık.
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal kapanış (DisposeAsync/PromoteToInProcessAsync) - sinyal.
            }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var (type, payload) = await _reader!.ReadFrameAsync(ct).ConfigureAwait(false);
                    switch (type)
                    {
                        case IpcMessageType.ResolveReply:
                            _pendingResolve?.TrySetResult(IpcMessageCodec.DecodeResolveReply(payload));
                            break;

                        case IpcMessageType.InvokeReply:
                            var reply = IpcMessageCodec.DecodeInvokeReply(payload);
                            if (_pending.TryRemove(reply.CorrelationId, out var tcs))
                                tcs.TrySetResult(reply);
                            break;

                        case IpcMessageType.CommandReply:
                            var (commandId, resultJson) = IpcMessageCodec.DecodeJsonMessage(payload);
                            if (_pendingCommands.TryRemove(commandId, out var ctcs2))
                                ctcs2.TrySetResult(resultJson);
                            break;

                        case IpcMessageType.InvokeBatchReply:
                            var batchReply = IpcMessageCodec.DecodeInvokeBatchReply(payload);
                            if (_pendingBatch.TryRemove(batchReply.CorrelationId, out var btcs))
                                btcs.TrySetResult(batchReply);
                            break;

                        case IpcMessageType.EventRaised:
                            _eventDispatch.Writer.TryWrite(IpcMessageCodec.DecodeEventRaised(payload));
                            break;

                        case IpcMessageType.Pong:
                            Interlocked.Exchange(ref _missedHeartbeats, 0);
                            break;

                        case IpcMessageType.Fault:
                            var message = IpcMessageCodec.DecodeFault(payload);
                            MarkDeadAndFailPending(new InvalidOperationException($"[PluginWorker] Protokol hatası: {message}"));
                            return;

                        default:
                            // Hello (sadece başlangıçta beklenir) / Ping / Shutdown burada gelmemeli -
                            // sessizce yok sayılıyor, protokolü bir hata için tek başına bozmuyoruz.
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal kapanış (DisposeAsync/PromoteToInProcessAsync CancellationToken'ı iptal etti).
            }
            catch (Exception ex) when (Volatile.Read(ref _disposed) || _promoted)
            {
                // Bilerek kapatıldı (DisposeAsync / in-process'e terfi): worker Shutdown'ı alıp çıktığı için pipe
                // iptalden ÖNCE kapanabilir - bu bir çökme DEĞİL; Crashed olayı ve crash log'u yok.
                MarkDeadAndFailPending(new ObjectDisposedException("Worker kapatıldı.", ex));
            }
            catch (Exception ex)
            {
                // Beklenmedik kopma - "worker öldü" (ya da heartbeat onu öldürdü - sebep oradan gelir).
                var reason = Interlocked.Exchange(ref _pendingCrashReason, null) ?? ex;
                MarkDeadAndFailPending(reason);
                LogCrash(reason);

                bool willRestart = Options.AutoRestartOnCrash && !_restartedOnce && !_disposed && !_promoted;
                RaiseCrashed(reason, willRestart);

                if (willRestart)
                {
                    _restartedOnce = true;
                    try
                    {
                        await RestartAsync().ConfigureAwait(false);
                        RaiseRestarted();
                    }
                    catch (Exception restartEx)
                    {
                        LogCrash(restartEx);
                        RaiseCrashed(restartEx, willRestart: false);
                    }
                }
            }
        }

        private async Task RestartAsync()
        {
            // ÖNCE eski neslin döngülerini durdur: aksi halde eski heartbeat döngüsü (deadFlag aşağıda
            // sıfırlandığı için "hâlâ canlı" sanıp) yenisiyle PARALEL çalışmaya devam eder - çift Ping,
            // çift missed-heartbeat sayımı. (Bu metot eski read loop'un İÇİNDEN çağrılıyor, o zaten
            // bu çağrıdan sonra sonlanıyor.)
            try { _lifetimeCts?.Cancel(); } catch { /* zaten dispose edilmiş olabilir */ }
            try { if (_heartbeatTask != null) await _heartbeatTask.ConfigureAwait(false); } catch { /* ignore */ }

            // Eski nesilde verilmiş handle'lar yeni worker'da GEÇERSİZ (bkz. _generation notu).
            _callHandleCache.Clear();

            DisposeProcessAndPipeQuietly();
            _process = null;
            _pipe = null;
            _writer = null;
            _reader = null;
            Interlocked.Exchange(ref _deadFlag, 0);
            Interlocked.Exchange(ref _missedHeartbeats, 0);
            _pending.Clear();
            _pendingBatch.Clear();
            _pendingCommands.Clear();

            await LaunchAsync().ConfigureAwait(false);

            // Host tarafında hâlâ açık olan abonelikleri yeni worker'da yeniden kur (aynı id'lerle).
            foreach (var kv in _eventSubs)
            {
                try
                {
                    var reply = await MemberAsync(MemberOperation.Subscribe, kv.Value.EventName, WireValueCodec.FromObject(kv.Key)).ConfigureAwait(false);
                    if (!reply.Success) LogCrash(new InvalidOperationException($"Restart sonrası '{kv.Value.EventName}' aboneliği yenilenemedi: {reply.ExceptionMessage}"));
                }
                catch (Exception ex) { LogCrash(ex); }
            }
        }

        /// <summary>
        /// Plugin event'ine abone ol. Handler, host'ta ayrı bir dağıtıcı thread'inde, event'lerin tetiklenme
        /// SIRASIYLA çağrılır (plugin handler'ı beklemez - bildirim). Worker yeniden başlarsa abonelik
        /// otomatik yenilenir. Dönen nesneyi Dispose/DisposeAsync ederek çıkın.
        /// </summary>
        public async Task<PluginEventSubscription> SubscribeAsync(string eventName, Action<PluginEventArgs> handler)
        {
            if (string.IsNullOrWhiteSpace(eventName)) throw new ArgumentException("eventName boş olamaz.", nameof(eventName));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            EnsureAlive();

            int id = Interlocked.Increment(ref _eventSubCounter);
            _eventSubs[id] = (eventName, handler);
            InvokeReply reply;
            try
            {
                reply = await MemberAsync(MemberOperation.Subscribe, eventName, WireValueCodec.FromObject(id)).ConfigureAwait(false);
            }
            catch
            {
                _eventSubs.TryRemove(id, out _);
                throw;
            }
            if (!reply.Success)
            {
                _eventSubs.TryRemove(id, out _);
                throw PluginInvocationException.FromRemote(eventName, reply.ExceptionType, reply.ExceptionMessage);
            }
            return new PluginEventSubscription(this, id);
        }

        internal async Task UnsubscribeAsync(int id)
        {
            if (!_eventSubs.TryRemove(id, out var sub)) return;
            if (_disposed || _promoted || IsDead || _process == null) return; // worker yok - yapacak iş yok
            try { await MemberAsync(MemberOperation.Unsubscribe, sub.EventName, WireValueCodec.FromObject(id)).ConfigureAwait(false); }
            catch { /* worker o arada öldüyse sorun değil */ }
        }

        private async Task EventDispatchLoopAsync()
        {
            await foreach (var (id, wireArgs) in _eventDispatch.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!_eventSubs.TryGetValue(id, out var sub)) continue; // yarışta çıkılmış abonelik
                PluginEventArgs? e = null;
                try
                {
                    var args = new object?[wireArgs.Length];
                    for (int i = 0; i < wireArgs.Length; i++) args[i] = WireValueCodec.ToObject(wireArgs[i]);
                    e = new PluginEventArgs(sub.EventName, args);
                    sub.Handler(e);
                }
                catch (Exception ex)
                {
                    var handlerFailed = EventHandlerFailed;
                    if (handlerFailed != null) { try { handlerFailed(e ?? new PluginEventArgs(sub.EventName, Array.Empty<object?>()), ex); } catch { } }
                    else System.Diagnostics.Trace.TraceError($"[PluginWorkerHandle] '{sub.EventName}' handler hatası: {ex}");
                }
            }
        }

        private void EnsureAlive()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PluginWorkerHandle));
            if (_promoted) throw new InvalidOperationException("[PluginWorkerHandle] Bu handle in-process'e terfi etti, artık kullanılamaz.");
            if (IsDead) throw new InvalidOperationException("[PluginWorkerHandle] Worker öldü/hung - yeniden kullanılamaz (AutoRestartOnCrash kapalıysa yeni bir handle oluşturun).");
            if (_process == null) throw new InvalidOperationException("[PluginWorkerHandle] Önce StartAsync çağrılmalı.");
        }

        private void MarkDeadAndFailPending(Exception reason)
        {
            if (Interlocked.CompareExchange(ref _deadFlag, 1, 0) != 0)
                return; // zaten dead işaretlenmiş - idempotent

            _pendingResolve?.TrySetException(reason);
            foreach (var kvp in _pendingCommands)
            {
                if (_pendingCommands.TryRemove(kvp.Key, out var ctcs))
                    ctcs.TrySetException(reason);
            }
            foreach (var kvp in _pendingBatch)
            {
                if (_pendingBatch.TryRemove(kvp.Key, out var btcs))
                    btcs.TrySetException(reason);
            }
            foreach (var kvp in _pending)
            {
                if (_pending.TryRemove(kvp.Key, out var tcs))
                    tcs.TrySetException(reason);
            }
        }

        private Exception? _pendingCrashReason;

        /// <summary>
        /// Worker çöktü / kilitlendi (heartbeat) / beklenmedik şekilde kapandı. NotifyOnCrash'ten BAĞIMSIZ her
        /// zaman tetiklenir - uygulama bunu UI bildirimi, e-posta, merkezi log vb.'ye bağlar (crash log dosyası
        /// NotifyOnCrash açıksa yine yazılır). Handler thread-pool'da çağrılır, worker'ı/restart'ı bloklamaz.
        /// Restart denemesi de başarısız olursa bir kez daha (WillRestart=false) tetiklenir.
        /// </summary>
        public event EventHandler<PluginWorkerCrashedEventArgs>? Crashed;

        /// <summary>AutoRestartOnCrash ile worker başarıyla yeniden başlatıldı (yeni nesil, yeni process).</summary>
        public event EventHandler<PluginWorkerRestartedEventArgs>? Restarted;

        private void RaiseCrashed(Exception reason, bool willRestart)
        {
            var handler = Crashed;
            if (handler == null) return;
            var e = new PluginWorkerCrashedEventArgs(PluginFilePath, TypeFullName, reason, Generation, willRestart);
            _ = Task.Run(() => { try { handler(this, e); } catch { /* bildirim handler'ı worker'ı etkilememeli */ } });
        }

        private void RaiseRestarted()
        {
            var handler = Restarted;
            if (handler == null) return;
            var e = new PluginWorkerRestartedEventArgs(PluginFilePath, TypeFullName, Generation, ProcessId);
            _ = Task.Run(() => { try { handler(this, e); } catch { } });
        }

        private void LogCrash(Exception ex)
        {
            if (!Options.NotifyOnCrash) return;
            try
            {
                string path = Options.CrashLogFilePath ?? Path.Combine(AppContext.BaseDirectory, "plugin-crashes.log");
                string line = $"[{DateTime.UtcNow:O}] Plugin='{PluginFilePath}' Type='{TypeFullName}' - {ex.GetType().Name}: {ex.Message}{Environment.NewLine}";
                File.AppendAllText(path, line);
            }
            catch
            {
                // Loglama ASLA crash handler'ı çökertmemeli.
            }
        }

        private static string SafeExitCode(Process process)
        {
            try { return process.ExitCode.ToString(); } catch { return "?"; }
        }

        private static void KillQuietly(Process? process)
        {
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* zaten ölmüş olabilir - önemli değil */ }
        }

        private async Task ShutdownInternalAsync(bool graceful)
        {
            if (_process == null) return;

            if (graceful && _writer != null && !IsDead)
            {
                try
                {
                    await _writer.WriteShutdownAsync().ConfigureAwait(false);
                    // Kısa bir süre düzgün çıkışı bekle, sonra zorla kapat.
                    using var exitCts = new CancellationTokenSource(3000);
                    try { await _process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { KillQuietly(_process); }
                }
                catch
                {
                    KillQuietly(_process);
                }
            }
            else
            {
                KillQuietly(_process);
            }

            _lifetimeCts?.Cancel();
            try { if (_readLoopTask != null) await _readLoopTask.ConfigureAwait(false); } catch { /* ignore */ }
            try { if (_heartbeatTask != null) await _heartbeatTask.ConfigureAwait(false); } catch { /* ignore */ }

            DisposeProcessAndPipeQuietly();
            Interlocked.Exchange(ref _deadFlag, 1);
        }

        private void DisposeProcessAndPipeQuietly()
        {
            try { _pipe?.Dispose(); } catch { /* ignore */ }
            try { _process?.Dispose(); } catch { /* ignore */ }
            try { _lifetimeCts?.Dispose(); } catch { /* ignore */ }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _eventSubs.Clear();
            _eventDispatch.Writer.TryComplete();

            if (_process != null && !_promoted)
                await ShutdownInternalAsync(graceful: true).ConfigureAwait(false);
        }
    }
}