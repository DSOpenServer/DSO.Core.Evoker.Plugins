using System;

namespace DSO.Core.Evoker.Plugins.Management
{
    public enum PluginExecutionMode
    {
        /// <summary>Ayrı worker process - çökmesi host'u etkilemez (varsayılan, güvenmediğiniz DLL'ler için).</summary>
        Sandbox = 0,

        /// <summary>Host'un kendi process'inde, kendi AssemblyLoadContext'inde - en hızlı, izolasyon yok.</summary>
        InProcess = 1
    }

    /// <summary>
    /// Admin'in bir plugin için verdiği kararlar - IPluginConfigStore ile kalıcı saklanır (varsayılan: JSON dosyası).
    /// Admin ekranı bu nesneyi düzenler; PluginManager.UpdateAsync / SetModeAsync ile uygulanır.
    /// </summary>
    public sealed class PluginRegistration
    {
        /// <summary>Uygulamanın plugin'e verdiği sabit kimlik (ör. "erp-entegrasyon"). Uygulama kodu buna göre ister.</summary>
        public string Id { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string TypeFullName { get; set; } = "";

        public PluginExecutionMode Mode { get; set; } = PluginExecutionMode.Sandbox;
        public bool Enabled { get; set; } = true;
        public bool IncludeNonPublic { get; set; }

        // --- Sadece Sandbox modunda anlamlı ---
        public int MaxConcurrency { get; set; } = 1;
        public bool AutoRestartOnCrash { get; set; }
        public int HeartbeatIntervalMs { get; set; } = 5000;
        public int MissedHeartbeatsBeforeKill { get; set; } = 3;

        /// <summary>Async çağrıların varsayılan bekleme üst sınırı (ms), null = sınırsız. Bkz. IPluginBuilder.DefaultTimeoutMs.</summary>
        public int? DefaultTimeoutMs { get; set; }

        public string? Notes { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public PluginRegistration Clone() => (PluginRegistration)MemberwiseClone();
    }

    /// <summary>Bir plugin'in anlık durumu (admin ekranı / izleme için).</summary>
    public sealed class PluginStatus
    {
        public string Id { get; init; } = "";
        public PluginExecutionMode Mode { get; init; }
        public bool Enabled { get; init; }
        public bool IsRunning { get; init; }
        public int? ProcessId { get; init; }
        /// <summary>Sandbox worker nesli (restart sayısı + 1).</summary>
        public int? Generation { get; init; }
        /// <summary>Son in-process boşaltmanın belleği gerçekten serbest bırakıp bırakmadığı (null = hiç boşaltılmadı).</summary>
        public bool? LastUnloadReleasedMemory { get; init; }
        public DateTime? LastCrashUtc { get; init; }
        public string? LastCrashReason { get; init; }
    }
}