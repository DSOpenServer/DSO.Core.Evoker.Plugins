// DSO.Core.Evoker.PluginHost - generic worker process.
// Host tarafından Process.Start ile başlatılır (bkz. PluginWorkerHandle.LaunchAsync). Görevi:
//   1) Argümanlardan pipe adını, plugin DLL yolunu, tip adını, includeNonPublic ve MaxConcurrency'yi al.
//   2) NamedPipeClientStream ile host'a bağlan.
//   3) ManagedDotNetPluginLoader ile plugin DLL'ini KENDİ İÇİNDE yükle, Hello (başarı/hata) gönder.
//   4) Okuma döngüsünde Resolve/Invoke/Ping isteklerini işle:
//      - Ping HER ZAMAN hemen (döngünün kendisinden, inline) cevaplanır.
//      - Resolve inline işlenir (basit bir reflection lookup - hızlı, döngüyü bloklamaz).
//      - Invoke, ayrı bir Task'a dispatch edilir (concurrency gate MaxConcurrency boyutunda) -
//        döngü İşin bitmesini BEKLEMEZ, hemen bir sonraki frame'i (ör. bir Ping) okumaya devam eder.
//   5) Shutdown mesajı gelirse düzgün kapan; pipe koparsa (host öldü) da kendini kapat.
//
// Argümanlar: <pipeName> <pluginFilePath> <typeFullName> <includeNonPublic:true|false> <maxConcurrency>

using System.IO.Pipes;
using System.Reflection;
using DSO.Core.Evoker.Plugins.Loading;
using DSO.Core.Evoker.Plugins.Sandbox;

if (args.Length < 5)
{
    Console.Error.WriteLine(
        "Kullanım: DSO.Core.Evoker.PluginHost <pipeName> <pluginFilePath> <typeFullName> <includeNonPublic:true|false> <maxConcurrency>");
    return 2;
}

string pipeName = args[0];
string pluginFilePath = args[1];
string typeFullName = args[2];
bool includeNonPublic = string.Equals(args[3], "true", StringComparison.OrdinalIgnoreCase);
int maxConcurrency = int.TryParse(args[4], out var mc) && mc > 0 ? mc : 1;

using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
try
{
    using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
}
catch (Exception ex)
{
    // Host'a hiç bağlanamadıysak Hello göndermenin bir anlamı yok - sadece stderr'e yaz ve çık.
    Console.Error.WriteLine($"[PluginHost] Host pipe'ına bağlanılamadı: {ex.Message}");
    return 1;
}

var writer = new IpcWriter(pipe);
var reader = new IpcReader(pipe);

// TODO 22 adım 3: plugin'i yükle, Hello ile sonucu bildir.
ManagedDotNetPluginLoader loader;
try
{
    loader = new ManagedDotNetPluginLoader();
    await loader.LoadInProcessAsync(pluginFilePath, typeFullName, includeNonPublic).ConfigureAwait(false);
    await writer.WriteHelloAsync(success: true, typeFullName: loader.Builder!.Type.FullName, error: null).ConfigureAwait(false);
}
catch (Exception ex)
{
    try { await writer.WriteHelloAsync(success: false, typeFullName: null, error: ex.Message).ConfigureAwait(false); }
    catch { /* pipe zaten kopmuş olabilir - önemli değil, zaten çıkıyoruz */ }
    return 1;
}

var handleTable = new System.Collections.Concurrent.ConcurrentDictionary<int, (string MethodName, MethodInfo Representative)>();
int handleCounter = 0;
var concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
// Member (property/field) erişimi için üye başına tip-özel getter/setter cache'i (bkz. HandleMemberAsync).
var memberAccessors = new System.Collections.Concurrent.ConcurrentDictionary<string, (Type MemberType, Func<object?> Get, Action<object?> Set)>();
var bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
    | (includeNonPublic ? BindingFlags.NonPublic : 0);

// TODO 22 adım 4/5: okuma döngüsü.
while (true)
{
    IpcMessageType type;
    byte[] payload;
    try
    {
        (type, payload) = await reader.ReadFrameAsync().ConfigureAwait(false);
    }
    catch
    {
        // Host öldü/pipe koptu - kendimizi kapatmaktan başka yapacak bir şey yok.
        return 0;
    }

    switch (type)
    {
        case IpcMessageType.Ping:
            // HER ZAMAN hemen cevapla - Invoke'ların arka planda ne kadar sürdüğünden bağımsız
            // (bkz. dosyanın üstündeki not ve IpcMessageType.Ping'in kendi açıklaması).
            _ = SafeWriteAsync(() => writer.WritePongAsync());
            break;

        case IpcMessageType.Resolve:
            HandleResolve(IpcMessageCodec.DecodeResolveRequest(payload));
            break;

        case IpcMessageType.Invoke:
            var invokeRequest = IpcMessageCodec.DecodeInvokeRequest(payload);
            // BİLEREK await EDİLMİYOR - döngü hemen bir sonraki frame'i (ör. bir Ping) okumaya
            // devam etsin diye. Concurrency sınırı HandleInvokeAsync içindeki gate ile korunuyor.
            _ = HandleInvokeAsync(invokeRequest);
            break;

        case IpcMessageType.Member:
            // Invoke ile aynı: arka plana at, AYNI concurrency gate'ten geç (MaxConcurrency=1 iken bir
            // alan, plugin'in bir metodu çalışırken araya girip yazılamaz).
            _ = HandleMemberAsync(IpcMessageCodec.DecodeMemberRequest(payload));
            break;

        case IpcMessageType.Shutdown:
            return 0;

        default:
            // Hello/ResolveReply/InvokeReply/Pong/Fault worker'a host'tan gelmez - yok say.
            break;
    }
}

void HandleResolve(ResolveRequest request)
{
    ResolveReply reply;
    var loadedType = loader.Builder!.Type;

    if (!string.Equals(request.TypeName, loadedType.FullName, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(request.TypeName, loadedType.Name, StringComparison.OrdinalIgnoreCase))
    {
        reply = new ResolveReply
        {
            Success = false,
            Error = $"Bu worker '{loadedType.FullName}' yükledi, '{request.TypeName}' değil."
        };
    }
    else
    {
        var candidates = loadedType.GetMethods(bindingFlags).Where(m => m.Name == request.MethodName).ToList();
        if (candidates.Count == 0)
        {
            reply = new ResolveReply { Success = false, Error = $"'{request.MethodName}' metodu bulunamadı." };
        }
        else
        {
            // NOT (bilinen sınırlama - EvokerBuilder/EvokerBuilderDynamicInvokeExtensions'daki ile
            // AYNI ruhta): burada TAM overload çözümü yapılmıyor, sadece "böyle bir metot var mı" fail-fast
            // kontrolü + iyi bir parametre-tipi ipucu seçimi. GERÇEK overload seçimi Invoke sırasında,
            // builder.InvokeDynamicAsync -> EvokerBuilder.GetMethodInfo'nun ÇÖZÜLMÜŞ argüman
            // DEĞERLERİNE bakan, zaten test edilmiş mantığıyla yapılır.
            var representative = candidates.FirstOrDefault(m => m.GetParameters().Length == request.ArgTypeCodes.Count) ?? candidates[0];
            int handle = Interlocked.Increment(ref handleCounter);
            handleTable[handle] = (request.MethodName, representative);
            reply = new ResolveReply { Success = true, MethodHandle = handle };
        }
    }

    _ = SafeWriteAsync(() => writer.WriteResolveReplyAsync(reply));
}

async Task HandleInvokeAsync(InvokeRequest request)
{
    await concurrencyGate.WaitAsync().ConfigureAwait(false);
    InvokeReply reply;
    try
    {
        if (!handleTable.TryGetValue(request.MethodHandle, out var entry))
        {
            reply = new InvokeReply
            {
                CorrelationId = request.CorrelationId,
                Success = false,
                ExceptionType = nameof(MissingMethodException),
                ExceptionMessage = $"Bilinmeyen MethodHandle: {request.MethodHandle} (önce Resolve çağrılmalı)."
            };
        }
        else
        {
            try
            {
                var paramTypes = entry.Representative.GetParameters().Select(p => p.ParameterType).ToArray();
                var decodedArgs = new object?[request.Args.Length];
                for (int i = 0; i < request.Args.Length; i++)
                {
                    var hint = i < paramTypes.Length ? paramTypes[i] : null;
                    var decoded = WireValueCodec.ToObject(request.Args[i], hint);
                    // Enum'lar tel üzerinde sayı olarak gelir, tipi belirsiz Complex'ler JsonElement olarak -
                    // parametre tipine SADECE bu iki durumda çeviriyoruz (diğer durumlarda overload seçimini
                    // EvokerBuilder argümanların GERÇEK tiplerine bakarak yapsın).
                    if (hint != null && decoded != null &&
                        ((Nullable.GetUnderlyingType(hint) ?? hint).IsEnum || decoded is System.Text.Json.JsonElement))
                        decoded = WireValueCodec.ConvertTo(decoded, hint);
                    decodedArgs[i] = decoded;
                }

                // builder.InvokeDynamicAsync: void/Task/Task<T>/senkron şeklini kendisi tespit
                // eder (bkz. EvokerBuilderDynamicInvokeExtensions) - burada TEKRAR yazılmıyor.
                var result = await loader.Builder!.InvokeDynamicAsync(entry.MethodName, decodedArgs).ConfigureAwait(false);

                reply = new InvokeReply
                {
                    CorrelationId = request.CorrelationId,
                    Success = true,
                    Result = WireValueCodec.FromObject(result)
                };
            }
            catch (Exception ex)
            {
                var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                reply = new InvokeReply
                {
                    CorrelationId = request.CorrelationId,
                    Success = false,
                    ExceptionType = actual.GetType().FullName,
                    ExceptionMessage = actual.Message
                };
            }
        }
    }
    finally
    {
        concurrencyGate.Release();
    }

    await SafeWriteAsync(() => writer.WriteInvokeReplyAsync(reply)).ConfigureAwait(false);
}

// --- Member (property/field get/set, ForgetCache) ---
// builder.GetValue<T>/SetValue<T> generic - worker, T'yi üyenin GERÇEK tipinden (reflection ile, üye
// başına BİR KEZ) bulup kapalı generic metodu delegate olarak cache'liyor. Böylece host tarafının tip
// bilmesine gerek kalmadan, ama in-process ile AYNI kod yolu (DynamicEntityAccessor) kullanılıyor.

(Type MemberType, Func<object?> Get, Action<object?> Set) GetMemberAccessor(string memberName)
{
    return memberAccessors.GetOrAdd(memberName, name =>
    {
        var type = loader.Builder!.Type;
        var flags = BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.Public
            | (includeNonPublic ? BindingFlags.NonPublic : 0);
        Type memberType = type.GetProperty(name, flags)?.PropertyType
            ?? type.GetField(name, flags)?.FieldType
            ?? throw new MissingMemberException(type.Name, name);

        var getOpen = typeof(DSO.Core.Evoker.EvokerBuilderPropertyExtensions).GetMethod(nameof(DSO.Core.Evoker.EvokerBuilderPropertyExtensions.GetValue))!;
        var setOpen = typeof(DSO.Core.Evoker.EvokerBuilderPropertyExtensions).GetMethod(nameof(DSO.Core.Evoker.EvokerBuilderPropertyExtensions.SetValue))!;
        var getClosed = getOpen.MakeGenericMethod(memberType);
        var setClosed = setOpen.MakeGenericMethod(memberType);
        var builder = loader.Builder!;

        return (memberType,
            () => getClosed.Invoke(null, new object?[] { builder, name }),
            v => setClosed.Invoke(null, new object?[] { builder, name, v }));
    });
}

async Task HandleMemberAsync(MemberRequest request)
{
    await concurrencyGate.WaitAsync().ConfigureAwait(false);
    InvokeReply reply;
    try
    {
        object? result = null;
        switch (request.Operation)
        {
            case MemberOperation.Get:
                result = GetMemberAccessor(request.MemberName).Get();
                break;
            case MemberOperation.Set:
                var accessor = GetMemberAccessor(request.MemberName);
                var value = WireValueCodec.ConvertTo(WireValueCodec.ToObject(request.Value, accessor.MemberType), accessor.MemberType);
                accessor.Set(value);
                break;
            case MemberOperation.ForgetCache:
                DSO.Core.Evoker.EvokerBuilderPropertyExtensions.ForgetCache(loader.Builder!);
                memberAccessors.Clear();
                break;
            default:
                throw new NotSupportedException($"Bilinmeyen MemberOperation: {request.Operation}");
        }
        reply = new InvokeReply { CorrelationId = request.CorrelationId, Success = true, Result = WireValueCodec.FromObject(result) };
    }
    catch (Exception ex)
    {
        var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
        reply = new InvokeReply
        {
            CorrelationId = request.CorrelationId,
            Success = false,
            ExceptionType = actual.GetType().FullName,
            ExceptionMessage = actual.Message
        };
    }
    finally
    {
        concurrencyGate.Release();
    }

    await SafeWriteAsync(() => writer.WriteInvokeReplyAsync(reply)).ConfigureAwait(false);
}

// IpcWriter kendi içinde write-lock'lu olduğu için Ping-cevabı/Resolve-cevabı/Invoke-cevabı
// yollarının hepsi AYNI ANDA çağrılsa bile pipe'a yazımlar birbirine karışmaz - burada sadece
// (host zaten öldüyse) bir yazma hatasının tüm worker'ı çökertmesini engelliyoruz.
async Task SafeWriteAsync(Func<Task> write)
{
    try { await write().ConfigureAwait(false); }
    catch { /* pipe kopmuş olabilir - ana döngü zaten bir sonraki ReadFrameAsync'de bunu yakalayıp çıkacak */ }
}