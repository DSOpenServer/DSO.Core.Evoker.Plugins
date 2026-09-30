namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>Named pipe üzerinde taşınan her frame'in ilk byte'ı - mesajın tipini belirtir.</summary>
    public enum IpcMessageType : byte
    {
        /// <summary>Worker açılınca host'a gönderir: "hazırım, şu DLL'i yükledim".</summary>
        Hello = 1,

        /// <summary>
        /// TypeName+MethodName(+argüman tipleri) -> MethodHandle (int) isteği.
        /// Bir kez yapılır, sonraki milyonlarca Invoke bu handle'ı taşır
        /// (DynamicClass'taki "resolve-once, invoke-fast" ile aynı felsefe).
        /// </summary>
        Resolve = 2,
        ResolveReply = 3,

        /// <summary>MethodHandle + argümanlarla gerçek çağrı.</summary>
        Invoke = 4,
        InvokeReply = 5,

        /// <summary>
        /// Heartbeat. Worker'ın okuma/dispatch döngüsünden (iş mantığı thread-pool'undan DEĞİL)
        /// cevaplanır - böylece uzun süren bir Invoke, hang tespitini yanlış tetiklemez.
        /// </summary>
        Ping = 6,
        Pong = 7,

        /// <summary>Host -> worker, düzgün kapanış iste.</summary>
        Shutdown = 8,

        /// <summary>Protokol seviyesi hata (uygulama/plugin hatası değil - örn. bozuk frame).</summary>
        Fault = 9,

        /// <summary>
        /// Property/field okuma-yazma ya da worker tarafı cache temizliği (bkz. MemberRequest).
        /// Cevabı InvokeReply (5) ile döner - aynı CorrelationId/bekleyen-cevap mekanizması kullanılır.
        /// </summary>
        Member = 10
    }
}