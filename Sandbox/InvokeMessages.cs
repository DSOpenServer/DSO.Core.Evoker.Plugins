namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// Gerçek çağrı isteği. CorrelationId, MaxConcurrency > 1 olduğunda cevapların sırasız
    /// dönebilmesi yüzünden gerekli (host hangi cevabın hangi isteğe ait olduğunu bununla eşleştirir).
    /// </summary>
    public sealed class InvokeRequest
    {
        public long CorrelationId { get; init; }

        /// <summary>ResolveReply'den gelen handle - TypeName/MethodName burada TAŞINMAZ.</summary>
        public int MethodHandle { get; init; }

        public WireValue[] Args { get; init; } = System.Array.Empty<WireValue>();

        /// <summary>
        /// Opsiyonel, ÇAĞRI BAZLI ipucu - global bir timeout DEĞİLDİR. Null = sınırsız
        /// (API çağrısı yapan / büyük veri taşıyan metotlar için varsayılan budur).
        /// Süre dolarsa SADECE bu CorrelationId host tarafında "timed out" işaretlenir,
        /// worker öldürülmez (başka eşzamanlı çağrılar meşru şekilde sürüyor olabilir).
        /// Gerçek "worker öldü/kilitlendi" kararı heartbeat'e (Ping/Pong) bağlıdır.
        /// </summary>
        public int? TimeoutMs { get; init; }
    }

    public sealed class InvokeReply
    {
        public long CorrelationId { get; init; }
        public bool Success { get; init; }

        /// <summary>Success ise geçerli.</summary>
        public WireValue? Result { get; init; }

        /// <summary>Success değilse - plugin metodu exception fırlattı (worker/protokol hatası değil).</summary>
        public string? ExceptionType { get; init; }
        public string? ExceptionMessage { get; init; }
    }

    /// <summary>
    /// Toplu çağrı: aynı MethodHandle, N farklı argüman seti - worker bunları SIRAYLA çalıştırır ve TEK cevapla
    /// döner. Process sınırı geçme maliyeti (context switch + pipe I/O) N çağrıya bölünür.
    /// </summary>
    public sealed class InvokeBatchRequest
    {
        public long CorrelationId { get; init; }
        public int MethodHandle { get; init; }
        public WireValue[][] ArgsList { get; init; } = System.Array.Empty<WireValue[]>();
    }

    public sealed class InvokeBatchReply
    {
        public long CorrelationId { get; init; }
        public bool Success { get; init; }

        /// <summary>Success ise: her çağrının sonucu, sırasıyla.</summary>
        public WireValue[] Results { get; init; } = System.Array.Empty<WireValue>();

        /// <summary>Success değilse: hata veren çağrının sırası (ondan SONRAKİLER çalıştırılmadı, öncekiler çalıştı).</summary>
        public int FailedIndex { get; init; } = -1;
        public string? ExceptionType { get; init; }
        public string? ExceptionMessage { get; init; }
    }
}