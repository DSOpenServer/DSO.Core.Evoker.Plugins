using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSO.Core.Evoker.Description;

namespace DSO.Core.Evoker.Plugins.Scanning
{
    /// <summary>
    /// Bir plugin tipinin tam tanımı - admin ekranı / dokümantasyon / tanılama için. İki kaynaktan dolar:
    ///   1) Yapı (PluginInspector.Describe): DLL ÇALIŞTIRILMADAN (MetadataLoadContext) okunur - plugin kodu
    ///      hiç çalışmaz, güvenmediğiniz DLL için de güvenli, yüklemeden önce bile alınabilir.
    ///   2) Değerler (IPluginBuilder.DescribeAsync / PluginManager.DescribeAsync): çalışan instance'tan field ve
    ///      property'lerin O ANKİ değerleri. DİKKAT: property okumak plugin'in getter kodunu çalıştırır.
    ///
    /// Tip kısmı (Type) çekirdekteki DSO.Core.Evoker.Description.EvokerTypeDescriptor'dır - plugin olmayan hedefler de
    /// (EvokerCatalog) aynı modeli kullanır.
    ///
    /// JSON (ToJson): PascalCase, enum'lar metin, null/boş alanlar yazılmaz; bool'lar sadece true iken yazılır
    /// (ör. "IsStatic" yalnızca static üyelerde görünür).
    /// </summary>
    public sealed class PluginDescriptor
    {
        public PluginAssemblyDescriptor Assembly { get; init; } = new();
        public EvokerTypeDescriptor Type { get; init; } = new();
        public DateTime GeneratedUtc { get; init; } = DateTime.UtcNow;

        /// <summary>Değerler alındıysa nereden/ne zaman; alınmadıysa null.</summary>
        public EvokerValuesInfo? Values { get; set; }

        public List<string>? Warnings { get; set; }

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Türkçe karakterler ş yerine ş olarak
            Converters = { new JsonStringEnumConverter() }
        };

        public string ToJson(bool indented = true) =>
            JsonSerializer.Serialize(this, indented ? JsonOptions : new JsonSerializerOptions(JsonOptions) { WriteIndented = false });

        internal void AddWarning(string w) => (Warnings ??= new List<string>()).Add(w);
    }

    public sealed class PluginAssemblyDescriptor
    {
        public string Name { get; init; } = "";
        public string? Version { get; init; }
        public string? FileVersion { get; init; }
        public string? InformationalVersion { get; init; }
        public string? TargetFramework { get; init; }
        public string FilePath { get; init; } = "";
        public long FileSize { get; init; }
        public DateTime LastWriteUtc { get; init; }
        /// <summary>Dosyanın SHA-256'sı - "çalışan DLL gerçekten hangisi" sorusunun kesin cevabı.</summary>
        public string? Sha256 { get; init; }
        public List<PluginReferenceDescriptor>? References { get; init; }
    }

    public sealed class PluginReferenceDescriptor
    {
        public string Name { get; init; } = "";
        public string? Version { get; init; }
        /// <summary>true = plugin klasöründe de runtime'da da bulunamadı (plugin çalışırken bu bağımlılık patlayabilir).</summary>
        public bool? Missing { get; init; }
    }

}