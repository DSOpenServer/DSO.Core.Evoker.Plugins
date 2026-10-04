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

// CurrentUserOnly: sadece AYNI kullanıcının açtığı pipe'a bağlan (başka bir kullanıcının aynı isimle
// açtığı sahte bir pipe'a plugin'i bağlamayız). Host tarafı da aynı bayrakla açıyor.
using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
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
    await writer.WriteHelloAsync(success: true, typeFullName: loader.Builder!.Type.FullName, error: null, protocolVersion: IpcProtocol.Version).ConfigureAwait(false);
}
catch (Exception ex)
{
    try { await writer.WriteHelloAsync(success: false, typeFullName: null, error: ex.Message, protocolVersion: IpcProtocol.Version).ConfigureAwait(false); }
    catch { /* pipe zaten kopmuş olabilir - önemli değil, zaten çıkıyoruz */ }
    return 1;
}

// ParamTypes resolve anında BİR KEZ çıkarılır (eskiden her çağrıda GetParameters + LINQ).
var handleTable = new System.Collections.Concurrent.ConcurrentDictionary<int, (string MethodName, MethodInfo Representative, Type[] ParamTypes)>();
int handleCounter = 0;
var concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
// Event abonelikleri: host'un verdiği abonelik id'si -> worker'daki gerçek abonelik (Dispose = çık).
var eventSubscriptions = new System.Collections.Concurrent.ConcurrentDictionary<int, IDisposable>();
// Plugin event'leri plugin'in KENDİ thread'lerinden (ör. arka arkaya ilerleme bildirimleri) gelir; sıralarının
// korunması için tek bir tüketici bunları sırayla pipe'a yazar. Plugin thread'i ASLA pipe yazımını beklemez.
var eventOutbox = System.Threading.Channels.Channel.CreateUnbounded<(int Id, WireValue[] Args)>(
    new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
_ = Task.Run(async () =>
{
    await foreach (var (id, eventArgs) in eventOutbox.Reader.ReadAllAsync().ConfigureAwait(false))
        await SafeWriteAsync(() => writer.WriteEventRaisedAsync(id, eventArgs)).ConfigureAwait(false);
});

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

        case IpcMessageType.InvokeBatch:
            _ = HandleInvokeBatchAsync(IpcMessageCodec.DecodeInvokeBatchRequest(payload));
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
        // Invoke ile AYNI kural (EvokerBuilder.FindMethodCandidates): birebir/case-insensitive isim +
        // argüman sayısı (fazla parametreler optional olabilir - VB.NET). Gerçek overload seçimi Invoke
        // sırasında, argüman DEĞERLERİNE bakan EvokerBuilder.FindMethod ile yapılır; burada sadece
        // "çağrılabilir bir metot var mı" fail-fast kontrolü + parametre tipi ipucu (decode için).
        var candidates = loader.Builder!.FindMethodCandidates(request.MethodName, request.ArgTypeCodes.Count);
        if (candidates.Count == 0)
        {
            bool anyByName = loadedType.GetMethods(bindingFlags).Any(m => string.Equals(m.Name, request.MethodName, StringComparison.OrdinalIgnoreCase));
            reply = new ResolveReply
            {
                Success = false,
                Error = anyByName
                    ? $"'{request.MethodName}' için {request.ArgTypeCodes.Count} argümanla çağrılabilen bir overload yok."
                    : $"'{request.MethodName}' metodu bulunamadı. '{loadedType.FullName}' ({loadedType.Assembly.Location}) " +
                      $"içindeki metotlar: {string.Join(", ", loadedType.GetMethods(bindingFlags).Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object)).Select(m => m.Name).Distinct().OrderBy(n => n))}"
            };
        }
        else
        {
            var representative = candidates[0];
            int handle = Interlocked.Increment(ref handleCounter);
            handleTable[handle] = (request.MethodName, representative, representative.GetParameters().Select(p => p.ParameterType).ToArray());
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
                var decodedArgs = DecodeArgs(request.Args, entry.ParamTypes);

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

object?[] DecodeArgs(WireValue[] wireArgs, Type[] paramTypes)
{
    var decodedArgs = new object?[wireArgs.Length];
    for (int i = 0; i < wireArgs.Length; i++)
    {
        var hint = i < paramTypes.Length ? paramTypes[i] : null;
        var decoded = WireValueCodec.ToObject(wireArgs[i], hint);
        // Enum'lar tel üzerinde sayı olarak gelir, tipi belirsiz Complex'ler JsonElement olarak -
        // parametre tipine SADECE bu iki durumda çeviriyoruz (diğer durumlarda overload seçimini
        // EvokerBuilder argümanların GERÇEK tiplerine bakarak yapsın).
        if (hint != null && decoded != null &&
            ((Nullable.GetUnderlyingType(hint) ?? hint).IsEnum || decoded is System.Text.Json.JsonElement))
            decoded = WireValueCodec.ConvertTo(decoded, hint);
        decodedArgs[i] = decoded;
    }
    return decodedArgs;
}

async Task HandleInvokeBatchAsync(InvokeBatchRequest request)
{
    await concurrencyGate.WaitAsync().ConfigureAwait(false);
    InvokeBatchReply reply;
    int index = 0;
    try
    {
        if (!handleTable.TryGetValue(request.MethodHandle, out var entry))
            throw new MissingMethodException($"Bilinmeyen MethodHandle: {request.MethodHandle} (önce Resolve çağrılmalı).");

        var paramTypes = entry.ParamTypes;
        var results = new WireValue[request.ArgsList.Length];
        for (index = 0; index < request.ArgsList.Length; index++)
        {
            var result = await loader.Builder!.InvokeDynamicAsync(entry.MethodName, DecodeArgs(request.ArgsList[index], paramTypes)).ConfigureAwait(false);
            results[index] = WireValueCodec.FromObject(result);
        }
        reply = new InvokeBatchReply { CorrelationId = request.CorrelationId, Success = true, Results = results };
    }
    catch (Exception ex)
    {
        var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
        reply = new InvokeBatchReply
        {
            CorrelationId = request.CorrelationId,
            Success = false,
            FailedIndex = index,
            ExceptionType = actual.GetType().FullName,
            ExceptionMessage = actual.Message
        };
    }
    finally
    {
        concurrencyGate.Release();
    }

    await SafeWriteAsync(() => writer.WriteInvokeBatchReplyAsync(reply)).ConfigureAwait(false);
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
            ?? SingleIgnoreCase(type.GetProperties(flags), name)?.PropertyType
            ?? SingleIgnoreCase(type.GetFields(flags), name)?.FieldType
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

static T? SingleIgnoreCase<T>(T[] members, string name) where T : MemberInfo
{
    var m = members.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
    return m.Count == 1 ? m[0] : null;
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
            case MemberOperation.Subscribe:
                {
                    int subId = (int)WireValueCodec.ToObject(request.Value)!;
                    var instance = loader.Builder!.Instance;
                    var sub = DSO.Core.Evoker.EvokerBuilderEventExtensions.AddEventHandler(loader.Builder!, request.MemberName, raw =>
                    {
                        var wire = new WireValue[raw.Length];
                        for (int i = 0; i < raw.Length; i++)
                        {
                            // Plugin'in kendisi (sender) process sınırını geçemez -> null (bkz. PluginEventArgs).
                            var a = ReferenceEquals(raw[i], instance) ? null : raw[i];
                            try { wire[i] = WireValueCodec.FromObject(a); }
                            catch (Exception ex)
                            {
                                // Serileştirilemeyen argüman event'i DÜŞÜRMESİN - o argüman null gider, sebep stderr'e.
                                Console.Error.WriteLine($"[PluginHost] '{request.MemberName}' event argümanı {i} serileştirilemedi: {ex.Message}");
                                wire[i] = WireValue.Null;
                            }
                        }
                        eventOutbox.Writer.TryWrite((subId, wire));
                    });
                    eventSubscriptions[subId] = sub;
                    break;
                }
            case MemberOperation.Unsubscribe:
                {
                    int subId = (int)WireValueCodec.ToObject(request.Value)!;
                    if (eventSubscriptions.TryRemove(subId, out var sub)) sub.Dispose();
                    break;
                }
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