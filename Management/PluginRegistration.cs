using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSO.Core.Evoker.Plugins.Management
{
    public enum PluginExecutionMode
    {
        /// <summary>Ayrı worker process - çökmesi host'u etkilemez (varsayılan, güvenmediğiniz DLL'ler için).</summary>
        Sandbox = 0,

        /// <summary>Host'un kendi process'inde, kendi AssemblyLoadContext'inde - en hızlı, izolasyon yok.</summary>
        InProcess = 1
    }

    /// <summary>Plugin'in anlık durumu.</summary>
    public enum PluginState
    {
        /// <summary>Pasif: sadece listede, bellekte yok, çağrılar reddedilir (Inactive).</summary>
        Inactive,
        /// <summary>Aktif ama şu an yüklü değil (durduruldu); ilk çağrıda kendiliğinden yüklenir.</summary>
        Stopped,
        /// <summary>Yükleniyor.</summary>
        Starting,
        /// <summary>Aktif ve yüklü - kullanıma açık.</summary>
        Running,
        /// <summary>Aktif ama yüklenemedi (bkz. LastError) - bir sonraki çağrı/aktifleştirmede yeniden denenir.</summary>
        Faulted,
        /// <summary>Sandbox worker çöktü ve yeniden başlamadı (bkz. LastCrashReason) - bir sonraki çağrıda taze worker açılır.</summary>
        Crashed
    }

    /// <summary>
    /// Admin'in bir plugin için verdiği kararlar - IPluginConfigStore ile kalıcı saklanır (varsayılan: JSON dosyası).
    /// </summary>
    public sealed class PluginRegistration
    {
        /// <summary>
        /// Kaydın anahtarı - her zaman sistemin ürettiği Guid (boş verilirse kayıt anında üretilir, kalıcı saklanır).
        /// Uygulama ve API plugin'e bununla erişir. Aynı DLL + tip istendiği kadar eklenebilir; her kayıt kendi
        /// worker'ında / kendi context'inde, kendi state'iyle çalışır.
        /// </summary>
        public Guid Key { get; set; }

        /// <summary>Sadece açıklama (admin bu plugin'in ne iş yaptığını görsün diye). Boş olabilir, tekrar edebilir.</summary>
        public string? Name { get; set; }

        /// <summary>Ekranlar için: "Ad (Tip)" ya da sadece tip. Saklanmaz.</summary>
        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? TypeFullName : $"{Name} ({TypeFullName})";

        public string FilePath { get; set; } = "";
        public string TypeFullName { get; set; } = "";

        /// <summary>
        /// Plugin constructor argümanları (JSON dizi ya da parametre adlarıyla nesne) - her yüklemede bu değerlerle oluşturulur.
        /// null = parametresiz ya da tüm parametreleri optional constructor. Constructor seçimi metot seçimiyle aynı kural.
        /// </summary>
        public JsonElement? ConstructorArgs { get; set; }

        public PluginExecutionMode Mode { get; set; } = PluginExecutionMode.Sandbox;

        /// <summary>Aktif = belleğe yüklü ve kullanıma açık; pasif = sadece listede (çağrılar reddedilir).</summary>
        public bool IsActive { get; set; } = true;

        public bool IncludeNonPublic { get; set; }

        // --- Sadece Sandbox modunda anlamlı ---
        public int MaxConcurrency { get; set; } = 1;
        public bool AutoRestartOnCrash { get; set; }
        public int HeartbeatIntervalMs { get; set; } = 5000;
        public int MissedHeartbeatsBeforeKill { get; set; } = 3;

        /// <summary>Async çağrıların ve komutların varsayılan bekleme üst sınırı (ms), null = sınırsız.</summary>
        public int? DefaultTimeoutMs { get; set; }

        public string? Notes { get; set; }

        /// <summary>DLL'in assembly sürümü - kayıt/güncelleme anında DLL'den OTOMATİK okunur (elle girilmez).</summary>
        public string? AssemblyVersion { get; set; }

        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        // --- Eski (protokol öncesi) kayıt dosyalarından taşıma için - yeni kayıtlarda yazılmaz ---

        /// <summary>ESKİ: isim anahtarı. Yüklenirken Key'e (Guid ise) ya da Name'e taşınır.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }

        /// <summary>ESKİ: IsActive'in önceki adı. Yüklenirken IsActive'e taşınır.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Enabled { get; set; }

        public PluginRegistration Clone()
        {
            var c = (PluginRegistration)MemberwiseClone();
            c.ConstructorArgs = ConstructorArgs?.Clone();
            return c;
        }
    }

    /// <summary>Bir plugin'in anlık durumu (admin ekranı / izleme / API için).</summary>
    public sealed class PluginStatus
    {
        public Guid Key { get; init; }
        public string? Name { get; init; }
        public string DisplayName { get; init; } = "";
        public string TypeFullName { get; init; } = "";
        public string FilePath { get; init; } = "";
        public string? AssemblyVersion { get; init; }
        public PluginExecutionMode Mode { get; init; }
        public bool IsActive { get; init; }
        public PluginState State { get; init; }
        public bool IsRunning { get; init; }
        public int? ProcessId { get; init; }
        /// <summary>Sandbox worker nesli (restart sayısı + 1).</summary>
        public int? Generation { get; init; }
        /// <summary>Son in-process boşaltmanın belleği gerçekten serbest bırakıp bırakmadığı (null = hiç boşaltılmadı).</summary>
        public bool? LastUnloadReleasedMemory { get; init; }
        /// <summary>Son yükleme hatası (State=Faulted iken).</summary>
        public string? LastError { get; init; }
        public DateTime? LastErrorUtc { get; init; }
        public DateTime? LastCrashUtc { get; init; }
        public string? LastCrashReason { get; init; }
        public DateTime UpdatedUtc { get; init; }
        public string? Notes { get; init; }
    }

    /// <summary>
    /// RegisterAsync / ActivateAsync sonucu. Beklenen hatalar (dosya/tip yok, .NET değil, uyan constructor yok) exception
    /// DEĞİL, Success=false + Message ile döner ve kayıt EKLENMEZ - admin ekranı mesajı doğrudan gösterebilir.
    /// </summary>
    public sealed class PluginRegistrationResult
    {
        public bool Success { get; init; }
        /// <summary>Başarılıysa kaydın anahtarı.</summary>
        public Guid? Key { get; init; }
        public string Message { get; init; } = "";
        /// <summary>Başarılıysa kayıttan sonraki durum (aktif kayıt yüklenemediyse Faulted - kayıt yine de eklenmiştir).</summary>
        public PluginState? State { get; init; }

        public static PluginRegistrationResult Ok(Guid key, string message, PluginState state) => new() { Success = true, Key = key, Message = message, State = state };
        public static PluginRegistrationResult Fail(string message) => new() { Success = false, Message = message };
    }
}