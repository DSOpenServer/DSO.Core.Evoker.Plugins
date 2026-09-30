namespace DSO.Core.Evoker.Plugins.Scanning
{
    /// <summary>
    /// Bir plugin dosyasının hangi çalışma zamanı ailesine ait olduğu.
    /// Tarama (PluginScanner) bu bilgiyi PE header + CLR metadata varlığına bakarak belirler,
    /// dosya uzantısına güvenmez.
    /// </summary>
    public enum PluginKind
    {
        /// <summary>Yönetilen (managed) .NET assembly. Bugün desteklenen tek yol: ManagedDotNetPluginLoader.</summary>
        ManagedDotNet = 0,

        /// <summary>
        /// PE/CLR metadata'sı olmayan native ikili (C/C++ .dll/.so vb.) veya Python gibi başka bir runtime.
        /// TODO (ayrı proje - DSO.Core.Evoker.Native veya benzeri): şimdilik sadece sınıflandırma amaçlı,
        /// yükleme tarafı NativePluginLoader'da placeholder olarak duruyor.
        /// </summary>
        Native = 1,

        /// <summary>Dosya okunamadı / tanınamadı / bozuk.</summary>
        Unknown = 2
    }
}
