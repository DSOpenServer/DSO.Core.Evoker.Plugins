namespace DSO.Core.Evoker.Plugins.Scanning
{
    /// <summary>
    /// Tarama sırasında tek bir tipin/üyenin çözülememesi durumunu taşır.
    /// Amaç: ReflectionTypeLoadException gibi "tüm assembly'yi düşüren" hatalardan kaçınmak -
    /// her tip kendi try/catch'i içinde denenir, başarısız olan tek tek burada raporlanır,
    /// geri kalan tipler taranmaya devam eder.
    /// </summary>
    public sealed class PluginScanError
    {
        /// <summary>Hatanın oluştuğu tip adı (biliniyorsa).</summary>
        public string? TypeName { get; init; }

        /// <summary>Kısa, insan-okunur açıklama. Örn: "Type X çözülemedi: Y assembly bulunamadı."</summary>
        public string Message { get; init; } = "";

        /// <summary>Orijinal exception'ın tip adı (tanı için, UI'da ayrıca gösterilebilir).</summary>
        public string? ExceptionType { get; init; }
    }
}
