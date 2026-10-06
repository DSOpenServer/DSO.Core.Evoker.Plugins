using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// Pipe koptuğunda/kapandığında IpcReader.ReadFrameAsync'in fırlattığı sinyal. PluginWorkerHandle
    /// bunu yakalayıp "worker öldü" olarak ele alır (bekleyen tüm InvokeAsync/ResolveAsync çağrılarını
    /// hataya düşürür, AutoRestartOnCrash açıksa bir kereliğine yeniden başlatmayı dener).
    /// </summary>
    public sealed class PluginWorkerDisconnectedException : IOException
    {
        public PluginWorkerDisconnectedException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Named pipe üzerinde mesaj sınırlarını taşıyan alt katman.
    /// Frame formatı: [4 byte little-endian uzunluk N][N byte: 1 byte IpcMessageType + (N-1) byte payload]
    /// Uzunluk, type byte'ı DAHİL sonraki byte sayısını gösterir - böylece okuyan taraf tek bir
    /// "N byte oku" adımıyla hem type'ı hem payload'ı aynı buffer'dan çıkarabilir.
    ///
    /// PERFORMANS (optimizasyon turu): başlık + payload artık TEK tamponda ve TEK yazma çağrısıyla gidiyor
    /// (eskiden başlık için ayrı dizi + iki WriteAsync). Mesajlar thread başına yeniden kullanılan bir tampona
    /// encode edilip ArrayPool'dan kiralanan diziye kopyalanır - mesaj başına MemoryStream/BinaryWriter/ToArray
    /// allocation'ı yok.
    /// </summary>
    public sealed class IpcWriter
    {
        private readonly PipeStream _pipe;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public IpcWriter(PipeStream pipe) => _pipe = pipe;

        private const int HeaderSize = 5;
        private const int MaxRetainedEncodeBuffer = 1 << 20; // 1 MB üstü büyümüş tamponu tutma

        // Encode SENKRON yapılır (ilk await'ten önce) - thread'e özel tampon güvenle yeniden kullanılır.
        [ThreadStatic] private static MemoryStream? t_encodeStream;
        [ThreadStatic] private static BinaryWriter? t_encodeWriter;

        /// <summary>
        /// TODO 6: Length-prefix + type byte + payload'ı pipe'a yaz. _writeLock ile serialize edilir -
        /// MaxConcurrency > 1 olduğunda birden fazla thread aynı anda cevap yazmaya çalışabilir,
        /// iki mesajın byte'ları asla iç içe geçmemeli.
        /// </summary>
        public Task WriteFrameAsync(IpcMessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            int total = HeaderSize + payload.Length;
            var buffer = ArrayPool<byte>.Shared.Rent(total);
            BinaryPrimitives.WriteInt32LittleEndian(buffer, checked(payload.Length + 1)); // type byte dahil
            buffer[4] = (byte)type;
            payload.Span.CopyTo(buffer.AsSpan(HeaderSize));
            return SendPooledAsync(buffer, total, ct);
        }

        /// <summary>Mesajı (encode delegesiyle) doğrudan frame tamponuna yazar ve gönderir.</summary>
        internal Task WriteEncodedAsync<TState>(IpcMessageType type, TState state, Action<BinaryWriter, TState> encode, CancellationToken ct = default)
        {
            var ms = t_encodeStream ??= new MemoryStream(256);
            var bw = t_encodeWriter ??= new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            byte[] buffer;
            int total;
            try
            {
                ms.Position = HeaderSize;
                ms.SetLength(HeaderSize);
                encode(bw, state);
                bw.Flush();
                total = (int)ms.Length;
                buffer = ArrayPool<byte>.Shared.Rent(total);
                ms.GetBuffer().AsSpan(HeaderSize, total - HeaderSize).CopyTo(buffer.AsSpan(HeaderSize));
            }
            finally
            {
                if (ms.Capacity > MaxRetainedEncodeBuffer) { t_encodeStream = null; t_encodeWriter = null; }
            }
            BinaryPrimitives.WriteInt32LittleEndian(buffer, total - 4); // type byte dahil uzunluk
            buffer[4] = (byte)type;
            return SendPooledAsync(buffer, total, ct);
        }

        private async Task SendPooledAsync(byte[] buffer, int length, CancellationToken ct)
        {
            try
            {
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // Tek yazma: başlık ile payload asla ayrı ayrı gitmez (iptal/hata yarım frame bırakmasın).
                    await _pipe.WriteAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
                    await _pipe.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // TODO 7: yüksek seviye yardımcı metodlar - IpcMessageCodec'e delege ediyor.

        /// <param name="protocolVersion">
        /// Worker bunu KENDİ kodundan "IpcProtocol.Version" olarak geçmeli: const, çağıranın (worker exe'sinin)
        /// içine derlenir - böylece eski bir exe'nin yanında yeni bir Plugins DLL'i olsa bile exe'nin gerçek sürümü gider.
        /// </param>
        public Task WriteHelloAsync(bool success, string? typeFullName, string? error, int protocolVersion, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.Hello, (success, typeFullName, error, protocolVersion),
                static (bw, s) => IpcMessageCodec.WriteHello(bw, s.success, s.typeFullName, s.error, s.protocolVersion), ct);

        public Task WriteResolveRequestAsync(ResolveRequest request, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.Resolve, request, IpcMessageCodec.WriteResolveRequest, ct);

        public Task WriteResolveReplyAsync(ResolveReply reply, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.ResolveReply, reply, IpcMessageCodec.WriteResolveReply, ct);

        public Task WriteInvokeRequestAsync(InvokeRequest request, CancellationToken ct = default) =>
            WriteInvokeRequestAsync(request.CorrelationId, request.MethodHandle, request.TimeoutMs, request.Args, ct);

        /// <summary>InvokeRequest nesnesi oluşturmadan (sıcak yol).</summary>
        public Task WriteInvokeRequestAsync(long correlationId, int methodHandle, int? timeoutMs, WireValue[] args, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.Invoke, (correlationId, methodHandle, timeoutMs, args),
                static (bw, s) => IpcMessageCodec.WriteInvokeRequest(bw, s.correlationId, s.methodHandle, s.timeoutMs, s.args), ct);

        public Task WriteInvokeReplyAsync(InvokeReply reply, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.InvokeReply, reply, IpcMessageCodec.WriteInvokeReply, ct);

        public Task WriteMemberRequestAsync(MemberRequest request, CancellationToken ct = default) =>
            WriteMemberRequestAsync(request.CorrelationId, request.Operation, request.MemberName, request.Value, ct);

        /// <summary>MemberRequest nesnesi oluşturmadan (sıcak yol).</summary>
        public Task WriteMemberRequestAsync(long correlationId, MemberOperation operation, string memberName, WireValue value, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.Member, (correlationId, operation, memberName, value),
                static (bw, s) => IpcMessageCodec.WriteMemberRequest(bw, s.correlationId, s.operation, s.memberName, s.value), ct);

        public Task WriteInvokeBatchRequestAsync(InvokeBatchRequest request, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.InvokeBatch, request, IpcMessageCodec.WriteInvokeBatchRequest, ct);

        public Task WriteInvokeBatchReplyAsync(InvokeBatchReply reply, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.InvokeBatchReply, reply, IpcMessageCodec.WriteInvokeBatchReply, ct);

        public Task WriteEventRaisedAsync(int subscriptionId, WireValue[] args, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.EventRaised, (subscriptionId, args),
                static (bw, s) => IpcMessageCodec.WriteEventRaised(bw, s.subscriptionId, s.args), ct);

        /// <summary>v2: bağlantıdan hemen sonra host → worker (constructor argümanları JSON'u; null = argümansız).</summary>
        public Task WriteInitAsync(string? constructorArgsJson, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.Init, constructorArgsJson, static (bw, j) => IpcMessageCodec.WriteInit(bw, j), ct);

        public Task WriteCommandAsync(long correlationId, string commandJson, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.Command, (correlationId, commandJson), static (bw, x) => IpcMessageCodec.WriteJsonMessage(bw, x.correlationId, x.commandJson), ct);

        public Task WriteCommandReplyAsync(long correlationId, string resultJson, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.CommandReply, (correlationId, resultJson), static (bw, x) => IpcMessageCodec.WriteJsonMessage(bw, x.correlationId, x.resultJson), ct);

        public Task WritePingAsync(CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.Ping, ReadOnlyMemory<byte>.Empty, ct);

        public Task WritePongAsync(CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.Pong, ReadOnlyMemory<byte>.Empty, ct);

        public Task WriteShutdownAsync(CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.Shutdown, ReadOnlyMemory<byte>.Empty, ct);

        public Task WriteFaultAsync(string message, CancellationToken ct = default) =>
            WriteEncodedAsync(IpcMessageType.Fault, message, static (bw, m) => IpcMessageCodec.WriteFault(bw, m), ct);
    }

    /// <summary>
    /// Frame okuyucu. TEK bir okuma döngüsü tarafından kullanılır (eşzamanlı ReadFrameAsync desteklenmez).
    ///
    /// PERFORMANS (optimizasyon turu): pipe'tan 64 KB'lık bloklar halinde okunur ve frame'ler bu tampondan
    /// çıkarılır - küçük bir mesaj (başlık + gövde) çoğu zaman TEK okuma sistem çağrısıyla gelir (eskiden en az
    /// iki: önce 4 byte uzunluk, sonra gövde) ve art arda gelen frame'ler aynı okumayla alınır. Payload doğrudan
    /// kendi dizisine kopyalanır (eskiden gövde dizisi + payload'a ikinci kopya).
    /// </summary>
    public sealed class IpcReader
    {
        private readonly PipeStream _pipe;
        private readonly byte[] _buf = new byte[64 * 1024];
        private int _start, _end; // _buf[_start.._end) okunmuş ama tüketilmemiş veri

        public IpcReader(PipeStream pipe) => _pipe = pipe;

        private int Buffered => _end - _start;

        /// <summary>
        /// TODO 9: Bir frame'in tamamını okuyup (length-prefix'e göre) (IpcMessageType, payload)
        /// döndür. Pipe TEMİZ kapanmışsa (uzunluk önekinin İLK byte'ında 0 byte okunur - karşı taraf
        /// bir frame'in ORTASINDA değil, iki frame ARASINDA kapatmış) PluginWorkerDisconnectedException
        /// fırlatılır; frame'in ORTASINDA kopmuşsa (beklenenden az byte) da aynı sinyal, ama farklı bir
        /// mesajla - ikisi de PluginWorkerHandle tarafında AYNI şekilde ("worker öldü") ele alınır.
        /// </summary>
        public async Task<(IpcMessageType Type, byte[] Payload)> ReadFrameAsync(CancellationToken ct = default)
        {
            // Başlık (uzunluk + type) = 5 byte; uzunluk >= 1 olduğu için her geçerli frame'de en az 5 byte vardır.
            if (Buffered < 5)
            {
                bool cleanClose = await FillAtLeastAsync(5, ct).ConfigureAwait(false);
                if (cleanClose)
                    throw new PluginWorkerDisconnectedException("Pipe iki frame arasında temiz şekilde kapandı (worker kapandı/öldü).");
            }

            int totalLength = BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(_start, 4));
            if (totalLength < 1)
                throw new PluginWorkerDisconnectedException($"Bozuk frame: uzunluk {totalLength} (en az 1 - type byte'ı - olmalı).");
            var type = (IpcMessageType)_buf[_start + 4];
            _start += 5;

            int payloadLength = totalLength - 1;
            if (payloadLength == 0) return (type, Array.Empty<byte>());

            var payload = new byte[payloadLength];
            int copied = Math.Min(Buffered, payloadLength);
            Buffer.BlockCopy(_buf, _start, payload, 0, copied);
            _start += copied;

            // Tampondakinden büyük payload: kalanı DOĞRUDAN hedef diziye oku (ara kopya yok).
            while (copied < payloadLength)
            {
                int n = await _pipe.ReadAsync(payload.AsMemory(copied), ct).ConfigureAwait(false);
                if (n == 0)
                    throw new PluginWorkerDisconnectedException("Pipe bir frame'in ORTASINDA koptu (worker beklenmedik şekilde öldü).");
                copied += n;
            }
            return (type, payload);
        }

        // Tamponda en az 'count' byte olana kadar okur. true = hiç veri yokken pipe temiz kapandı.
        private async Task<bool> FillAtLeastAsync(int count, CancellationToken ct)
        {
            if (_start > 0)
            {
                // Tüketilmemiş kısmı başa kaydır (en fazla birkaç byte).
                int left = Buffered;
                if (left > 0) Buffer.BlockCopy(_buf, _start, _buf, 0, left);
                _start = 0;
                _end = left;
            }
            while (_end < count)
            {
                int n = await _pipe.ReadAsync(_buf.AsMemory(_end), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    if (_end == 0) return true;
                    throw new PluginWorkerDisconnectedException("Pipe bir frame'in ORTASINDA koptu (worker beklenmedik şekilde öldü).");
                }
                _end += n;
            }
            return false;
        }
    }
}