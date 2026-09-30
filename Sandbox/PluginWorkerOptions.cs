namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// Bir plugin worker'ının (sandbox process) davranışını belirleyen parametreler.
    /// Hepsi bilinçli olarak parametrik - "doğru" bir varsayılan yok, admin DLL'i tanıdıkça karar verir.
    /// </summary>
    public sealed class PluginWorkerOptions
    {
        /// <summary>
        /// Worker'ın aynı anda kaç Invoke isteğini paralel işleyeceği.
        /// Varsayılan 1 (güvenli taraf - DLL'in thread-safe olduğunu bilmiyoruz).
        /// Admin DLL'in thread-safe olduğunu biliyorsa artırabilir.
        /// </summary>
        public int MaxConcurrency { get; init; } = 1;

        /// <summary>Worker çökerse otomatik yeniden başlatılsın mı (bir kere denenir, crash-loop yapılmaz).</summary>
        public bool AutoRestartOnCrash { get; init; } = false;

        /// <summary>Worker çöktüğünde/restart edildiğinde bildirim yazılsın mı (şimdilik txt, ileride gerçek bildirim).</summary>
        public bool NotifyOnCrash { get; init; } = true;

        /// <summary>
        /// Heartbeat (Ping/Pong) aralığı. Bu, worker'ın MESAJ DÖNGÜSÜNÜN canlı olup olmadığını
        /// kontrol eder - uzun süren bir Invoke'tan ETKİLENMEZ (bkz. IpcMessageType.Ping notu).
        /// </summary>
        public int HeartbeatIntervalMs { get; init; } = 5000;

        /// <summary>Art arda kaç heartbeat cevapsız kalırsa worker "hung/dead" sayılıp Process.Kill() edilir.</summary>
        public int MissedHeartbeatsBeforeKill { get; init; } = 3;

        /// <summary>
        /// Worker process'inin çalıştırılacağı yol. HostIsDotnetDll=true (varsayılan) ise bu bir
        /// derlenmiş "DSO.Core.Evoker.PluginHost.dll" yoludur ve `dotnet &lt;HostPath&gt; ...` ile
        /// başlatılır (platform bağımsız, SDK/runtime kurulu her yerde çalışır - bu sandbox dahil).
        /// HostIsDotnetDll=false ise (ör. Windows'ta self-contained publish edilmiş bir .exe)
        /// doğrudan HostPath çalıştırılır. Boş bırakılırsa StartAsync net bir hata fırlatır.
        /// </summary>
        public string HostPath { get; init; } = "";

        /// <summary>Bkz. HostPath açıklaması.</summary>
        public bool HostIsDotnetDll { get; init; } = true;

        /// <summary>
        /// Worker başlatma/handshake için üst sınır. Aşılırsa StartAsync TimeoutException fırlatır
        /// (process zombi kalmaması için Kill edilmeye çalışılır).
        /// </summary>
        public int StartupTimeoutMs { get; init; } = 15000;

        /// <summary>
        /// NotifyOnCrash=true iken crash bilgisinin yazılacağı dosya. Null ise
        /// "&lt;çalışma dizini&gt;/plugin-crashes.log" kullanılır (bkz. PluginWorkerOptions üstündeki not:
        /// "şimdilik txt, ileride gerçek bildirim").
        /// </summary>
        public string? CrashLogFilePath { get; init; }
    }
}