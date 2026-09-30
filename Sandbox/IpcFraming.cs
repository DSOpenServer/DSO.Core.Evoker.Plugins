using System;
using System.IO;
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
    /// </summary>
    public sealed class IpcWriter
    {
        private readonly PipeStream _pipe;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public IpcWriter(PipeStream pipe) => _pipe = pipe;

        /// <summary>
        /// TODO 6: Length-prefix + type byte + payload'ı pipe'a yaz. _writeLock ile serialize edilir -
        /// MaxConcurrency > 1 olduğunda birden fazla thread aynı anda cevap yazmaya çalışabilir,
        /// iki mesajın byte'ları asla iç içe geçmemeli.
        /// </summary>
        public async Task WriteFrameAsync(IpcMessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            // Header'ı (uzunluk + type) TEK bir buffer'da hazırlıyoruz ki pipe'a yazma ARASINDA
            // (write lock altında bile) iki ayrı WriteAsync çağrısı arasında bir iptal/hata payload'ı
            // header'sız veya header'ı payload'sız bırakmasın - tek bir mantıksal "frame header" yazımı.
            var header = new byte[5];
            int totalLength = checked(payload.Length + 1); // type byte dahil
            BitConverter.GetBytes(totalLength).CopyTo(header, 0);
            header[4] = (byte)type;

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _pipe.WriteAsync(header, ct).ConfigureAwait(false);
                if (payload.Length > 0)
                    await _pipe.WriteAsync(payload, ct).ConfigureAwait(false);
                await _pipe.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // TODO 7: yüksek seviye yardımcı metodlar - IpcMessageCodec'e (TODO 7/8) delege ediyor.

        public Task WriteHelloAsync(bool success, string? typeFullName, string? error, CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.Hello, IpcMessageCodec.EncodeHello(success, typeFullName, error), ct);

        public Task WriteResolveRequestAsync(ResolveRequest request, CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.Resolve, IpcMessageCodec.EncodeResolveRequest(request), ct);

        public Task WriteResolveReplyAsync(ResolveReply reply, CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.ResolveReply, IpcMessageCodec.EncodeResolveReply(reply), ct);

        public Task WriteInvokeRequestAsync(InvokeRequest request, CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.Invoke, IpcMessageCodec.EncodeInvokeRequest(request), ct);

        public Task WriteInvokeReplyAsync(InvokeReply reply, CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.InvokeReply, IpcMessageCodec.EncodeInvokeReply(reply), ct);

        public Task WriteMemberRequestAsync(MemberRequest request, CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.Member, IpcMessageCodec.EncodeMemberRequest(request), ct);

        public Task WritePingAsync(CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.Ping, ReadOnlyMemory<byte>.Empty, ct);

        public Task WritePongAsync(CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.Pong, ReadOnlyMemory<byte>.Empty, ct);

        public Task WriteShutdownAsync(CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.Shutdown, ReadOnlyMemory<byte>.Empty, ct);

        public Task WriteFaultAsync(string message, CancellationToken ct = default) =>
            WriteFrameAsync(IpcMessageType.Fault, IpcMessageCodec.EncodeFault(message), ct);
    }

    public sealed class IpcReader
    {
        private readonly PipeStream _pipe;

        public IpcReader(PipeStream pipe) => _pipe = pipe;

        /// <summary>
        /// TODO 9: Bir frame'in tamamını okuyup (length-prefix'e göre) (IpcMessageType, payload)
        /// döndür. Pipe TEMİZ kapanmışsa (uzunluk önekinin İLK byte'ında 0 byte okunur - karşı taraf
        /// bir frame'in ORTASINDA değil, iki frame ARASINDA kapatmış) PluginWorkerDisconnectedException
        /// fırlatılır; frame'in ORTASINDA kopmuşsa (beklenenden az byte) da aynı sinyal, ama farklı bir
        /// mesajla - ikisi de PluginWorkerHandle tarafında AYNI şekilde ("worker öldü") ele alınır.
        /// </summary>
        public async Task<(IpcMessageType Type, byte[] Payload)> ReadFrameAsync(CancellationToken ct = default)
        {
            var lengthBuffer = new byte[4];
            int read = await ReadExactAsync(lengthBuffer, allowZeroAtStart: true, ct).ConfigureAwait(false);
            if (read == 0)
                throw new PluginWorkerDisconnectedException("Pipe iki frame arasında temiz şekilde kapandı (worker kapandı/öldü).");

            int totalLength = BitConverter.ToInt32(lengthBuffer, 0);
            if (totalLength < 1)
                throw new PluginWorkerDisconnectedException($"Bozuk frame: uzunluk {totalLength} (en az 1 - type byte'ı - olmalı).");

            var body = new byte[totalLength];
            int bodyRead = await ReadExactAsync(body, allowZeroAtStart: false, ct).ConfigureAwait(false);
            if (bodyRead < totalLength)
                throw new PluginWorkerDisconnectedException("Pipe bir frame'in ORTASINDA koptu (worker beklenmedik şekilde öldü).");

            var type = (IpcMessageType)body[0];
            byte[] payload = totalLength == 1 ? Array.Empty<byte>() : new byte[totalLength - 1];
            if (payload.Length > 0)
                Array.Copy(body, 1, payload, 0, payload.Length);

            return (type, payload);
        }

        /// <summary>
        /// buffer'ı TAMAMEN dolduruncaya kadar okur (PipeStream.ReadAsync kısmi okuma yapabilir).
        /// allowZeroAtStart=true iken İLK ReadAsync çağrısı 0 dönerse (pipe iki frame arasında temiz
        /// kapanmış) bunu 0 olarak yukarı taşır - exception DEĞİL, normal bir "bağlantı bitti" sinyali.
        /// Kısmi okuma sonrası bağlantı koparsa (0 dönerse) IOException fırlatılır - bu GERÇEKTEN
        /// beklenmedik bir kopma (frame'in ortasında).
        /// </summary>
        private async Task<int> ReadExactAsync(byte[] buffer, bool allowZeroAtStart, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int n = await _pipe.ReadAsync(buffer.AsMemory(totalRead), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    if (totalRead == 0 && allowZeroAtStart)
                        return 0;
                    throw new IOException("Pipe bir okuma ORTASINDA (kısmi veri sonrası) kapandı.");
                }
                totalRead += n;
            }
            return totalRead;
        }
    }
}