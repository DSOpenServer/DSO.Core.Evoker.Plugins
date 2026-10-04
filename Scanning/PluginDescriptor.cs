using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSO.Core.Evoker.Plugins.Scanning
{
    /// <summary>
    /// Bir plugin tipinin tam tanımı - admin ekranı / dokümantasyon / tanılama için. İki kaynaktan dolar:
    ///   1) Yapı (PluginInspector.Describe): DLL ÇALIŞTIRILMADAN (MetadataLoadContext) okunur - plugin kodu
    ///      hiç çalışmaz, güvenmediğiniz DLL için de güvenli, yüklemeden önce bile alınabilir.
    ///   2) Değerler (IPluginBuilder.DescribeAsync / PluginManager.DescribeAsync): çalışan instance'tan field ve
    ///      property'lerin O ANKİ değerleri. DİKKAT: property okumak plugin'in getter kodunu çalıştırır.
    ///
    /// JSON (ToJson): PascalCase, enum'lar metin, null/boş alanlar yazılmaz; bool'lar sadece true iken yazılır
    /// (ör. "IsStatic" yalnızca static üyelerde görünür).
    /// </summary>
    public sealed class PluginDescriptor
    {
        public PluginAssemblyDescriptor Assembly { get; init; } = new();
        public PluginTypeDescriptor Type { get; init; } = new();
        public DateTime GeneratedUtc { get; init; } = DateTime.UtcNow;

        /// <summary>Değerler alındıysa nereden/ne zaman; alınmadıysa null.</summary>
        public PluginValuesInfo? Values { get; set; }

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

    public enum PluginTypeKind { Class, StaticClass, AbstractClass, Record, Struct, Enum, Interface, Delegate }

    public enum MemberVisibility { Public, ProtectedInternal, Protected, Internal, PrivateProtected, Private }

    public enum ParameterDirection { In, Out, Ref }

    public sealed class PluginTypeDescriptor
    {
        public string FullName { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Namespace { get; init; }
        public PluginTypeKind Kind { get; init; }
        public string? BaseType { get; init; }
        public List<string>? Interfaces { get; init; }
        public List<PluginConstructorDescriptor>? Constructors { get; init; }
        public List<PluginMethodDescriptor>? Methods { get; init; }
        public List<PluginPropertyDescriptor>? Properties { get; init; }
        public List<PluginFieldDescriptor>? Fields { get; init; }
        public List<PluginEventDescriptor>? Events { get; init; }
    }

    public sealed class PluginConstructorDescriptor
    {
        public MemberVisibility Visibility { get; init; }
        public List<PluginParameterDescriptor>? Parameters { get; init; }
    }

    public sealed class PluginMethodDescriptor
    {
        public string Name { get; init; } = "";
        public MemberVisibility Visibility { get; init; }
        public string ReturnType { get; init; } = "void";
        public List<PluginParameterDescriptor>? Parameters { get; init; }
        public bool? IsStatic { get; init; }
        /// <summary>Task / Task&lt;T&gt; / ValueTask dönüyor (Invoke bunu bekler).</summary>
        public bool? IsAsync { get; init; }
        public bool? IsVirtual { get; init; }
        public bool? IsAbstract { get; init; }
        public bool? IsOverride { get; init; }
        public List<string>? GenericArguments { get; init; }
        /// <summary>Üye bir temel sınıftan geliyorsa o sınıf (tip kendisi tanımladıysa null).</summary>
        public string? DeclaredIn { get; init; }
        /// <summary>Null = IPluginBuilder ile çağrılabilir. Dolu ise neden çağrılamadığı (ref/out, açık generic...).</summary>
        public string? NotCallableReason { get; init; }
    }

    public sealed class PluginParameterDescriptor
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public ParameterDirection? Direction { get; init; }
        public bool? IsOptional { get; init; }
        public bool? HasDefaultValue { get; init; }
        /// <summary>Optional parametrenin varsayılan değeri (HasDefaultValue=true ve bu alan yoksa varsayılan null'dır).</summary>
        public object? DefaultValue { get; init; }
        /// <summary>C# params / VB ParamArray.</summary>
        public bool? IsParams { get; init; }
    }

    public sealed class PluginPropertyDescriptor
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        /// <summary>Get erişiminin görünürlüğü (get yoksa null).</summary>
        public MemberVisibility? Getter { get; init; }
        /// <summary>Set erişiminin görünürlüğü (set yoksa null) - ör. Getter=Public, Setter=Private.</summary>
        public MemberVisibility? Setter { get; init; }
        public bool? IsInitOnly { get; init; }
        public bool? IsStatic { get; init; }
        public List<PluginParameterDescriptor>? IndexerParameters { get; init; }
        public string? DeclaredIn { get; init; }

        // --- Değer anlık görüntüsü (DescribeAsync) ---
        public JsonElement? Value { get; set; }
        public bool? ValueTruncated { get; set; }
        public string? ValueError { get; set; }
    }

    public sealed class PluginFieldDescriptor
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public MemberVisibility Visibility { get; init; }
        public bool? IsStatic { get; init; }
        public bool? IsReadOnly { get; init; }
        public bool? IsConst { get; init; }
        public object? ConstValue { get; init; }
        public string? DeclaredIn { get; init; }

        // --- Değer anlık görüntüsü (DescribeAsync) ---
        public JsonElement? Value { get; set; }
        public bool? ValueTruncated { get; set; }
        public string? ValueError { get; set; }
    }

    public sealed class PluginEventDescriptor
    {
        public string Name { get; init; } = "";
        public MemberVisibility Visibility { get; init; }
        public bool? IsStatic { get; init; }
        public string HandlerType { get; init; } = "";
        /// <summary>Handler'ın parametreleri - IPluginBuilder.Subscribe'da PluginEventArgs[i] sırası.</summary>
        public List<PluginParameterDescriptor>? Arguments { get; init; }
        public string? DeclaredIn { get; init; }
    }

    public sealed class PluginValuesInfo
    {
        public DateTime CapturedUtc { get; init; } = DateTime.UtcNow;
        /// <summary>"Sandbox" ya da "InProcess".</summary>
        public string Source { get; init; } = "";
        public string? Note { get; init; }
    }
}