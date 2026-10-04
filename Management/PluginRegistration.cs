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
        /// <summary>
        /// Kaydın anahtarı - uygulama plugin'i bununla ister (PluginManager.Get(id)). Admin'in verdiği isim
        /// (ör. "erp-sirketA"); boş bırakılırsa kayıt anında BİR KEZ GUID üretilir ve kalıcı saklanır (sonraki
        /// açılışlarda aynı kalır). Aynı DLL + tip farklı Id'lerle istendiği kadar eklenebilir; her kayıt kendi
        /// worker'ında / kendi context'inde, kendi state'iyle çalışır. Büyük/küçük harf duyarsız benzersizdir.
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>Admin ekranı için: "Tip [Id]" - aynı plugin'in örnekleri sınıf adıyla birlikte ayırt edilir. Saklanmaz.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DisplayName => $"{TypeFullName} [{Id}]";
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

    /// <summary>
    /// RegisterAsync sonucu. Admin'in yapabileceği beklenen hatalar (aynı isim, dosya/tip yok, .NET değil) exception
    /// DEĞİL, Success=false + Message ile döner ve kayıt EKLENMEZ - admin ekranı mesajı doğrudan gösterebilir.
    /// </summary>
    public sealed class PluginRegistrationResult
    {
        public bool Success { get; init; }
        /// <summary>Başarılıysa kaydın Id'si (verilmediyse üretilen GUID).</summary>
        public string? Id { get; init; }
        public string Message { get; init; } = "";

        public static PluginRegistrationResult Ok(string id, string message) => new() { Success = true, Id = id, Message = message };
        public static PluginRegistrationResult Fail(string message, string? id = null) => new() { Success = false, Id = id, Message = message };
    }
}