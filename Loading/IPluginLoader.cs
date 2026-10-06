using System.Threading.Tasks;
using DSO.Core.Evoker.Plugins.Scanning;

namespace DSO.Core.Evoker.Plugins.Loading
{
    /// <summary>
    /// PluginKind bazlı yükleme soyutlaması. Bugün tek implementasyon ManagedDotNetPluginLoader.
    /// NativePluginLoader (C/C++/Python vb.) ileride bu arayüzün başka bir implementasyonu
    /// olarak, muhtemelen ayrı bir projede (bu projeye bağımlı) eklenecek.
    ///
    /// Bir loader BİR plugin instance'ına bağlıdır (1 loader = 1 yüklenmiş instance, ömrü boyunca) -
    /// stateless/paylaşımlı bir servis DEĞİLDİR. LoadInProcessAsync sadece BİR KEZ çağrılır;
    /// bundan sonra loader'ın görevi biter, çağırma sorumluluğu (managed implementasyonlar için)
    /// ManagedDotNetPluginLoader.Builder üzerinden EvokerBuilder'a geçer. InvokeAsync burada
    /// SADECE loader-türü bilinmeyen (IPluginLoader üzerinden çalışan) genel çağırıcılar için
    /// ince bir köprüdür.
    /// </summary>
    public interface IPluginLoader
    {
        PluginKind SupportedKind { get; }

        /// <summary>Load sonrası dolu - yüklenen instance. Öncesinde null.</summary>
        object? Instance { get; }

        /// <summary>
        /// In-process yükleme: DLL host process'in kendi belleğine yüklenir, BİR instance oluşturulur.
        /// Bir loader üzerinde birden fazla kez çağrılamaz (yeni bir plugin için yeni bir loader oluşturun).
        /// <paramref name="includeNonPublic"/>: true ise sonraki Builder üzerinden yapılan
        /// Invoke/Execute/GetValue/SetValue çağrıları private/protected/internal metot, property ve
        /// field'lara da erişebilir (constructor dahil - parametresiz ctor private/protected olsa bile
        /// bulunur). Varsayılan false - eskisi gibi sadece public yüzey görünür. Admin/tanılama amaçlı,
        /// güvendiğiniz plugin'ler için bilinçli bir tercih olarak açın; "yabancı" bir DLL'in iç
        /// durumuna serbestçe erişmek güvenlik sınırını genişletir.
        /// </summary>
        /// <param name="constructorArgs">Constructor argümanları JSON (dizi ya da isimli nesne); null = parametresiz / tüm parametreleri optional constructor.</param>
        Task LoadInProcessAsync(string filePath, string typeFullName, bool includeNonPublic = false, System.Text.Json.JsonElement? constructorArgs = null);

        /// <summary>
        /// Loader-türü bilinmeyen genel çağırıcılar için ince bir köprü. Managed tarafta
        /// (ManagedDotNetPluginLoader) doğrudan Builder'a delege eder - asıl "hangi şekil
        /// (void/Task/Task&lt;T&gt;/senkron)" mantığı burada değil, EvokerBuilder'a bağlı bir
        /// extension'da yaşar (bkz. EvokerBuilderDynamicInvokeExtensions).
        /// </summary>
        Task<object?> InvokeAsync(string methodName, object?[] args);

        /// <summary>
        /// Plugin'i bellekten atar (in-process: kendi AssemblyLoadContext'i unload edilir, DLL dosyası
        /// serbest kalır). true = gerçekten boşaltıldı; false = çağıranın elinde hâlâ plugin'e ait bir
        /// referans var (bkz. ManagedDotNetPluginLoader.UnloadAsync). Loader bundan sonra kullanılamaz.
        /// </summary>
        Task<bool> UnloadAsync(int timeoutMs = 10_000);
    }
}