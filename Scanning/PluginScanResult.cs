using System.Collections.Generic;

namespace DSO.Core.Evoker.Plugins.Scanning
{
    /// <summary>Taranan bir type içindeki tek bir metodun UI'da gösterilecek özeti.</summary>
    public sealed class PluginMethodInfo
    {
        public string Name { get; init; } = "";
        public string ReturnTypeName { get; init; } = "";
        public IReadOnlyList<PluginParameterInfo> Parameters { get; init; } = new List<PluginParameterInfo>();
        public bool IsPublic { get; init; }
        public bool IsStatic { get; init; }
    }

    public sealed class PluginParameterInfo
    {
        public string Name { get; init; } = "";
        public string TypeName { get; init; } = "";
        public bool IsOptional { get; init; }
        public bool IsByRef { get; init; }
    }

    /// <summary>Property ya da field (GetValue/SetValue ile erişilebilen üye) özeti.</summary>
    public sealed class PluginMemberInfo
    {
        public string Name { get; init; } = "";
        public string TypeName { get; init; } = "";
        public bool IsField { get; init; }
        public bool CanRead { get; init; }
        /// <summary>Property'de set yoksa, field'da readonly/const ise false.</summary>
        public bool CanWrite { get; init; }
        public bool IsPublic { get; init; }
        public bool IsStatic { get; init; }
    }

    /// <summary>Event özeti (IPluginBuilder.Subscribe ile abone olunabilir).</summary>
    public sealed class PluginEventInfo
    {
        public string Name { get; init; } = "";
        public string HandlerTypeName { get; init; } = "";
        /// <summary>Handler'ın parametre tipleri (ör. EventHandler&lt;int&gt; için object, int).</summary>
        public IReadOnlyList<string> ArgumentTypeNames { get; init; } = new List<string>();
        public bool IsPublic { get; init; }
        public bool IsStatic { get; init; }
    }

    /// <summary>Taranan bir type'ın (class/struct) özeti.</summary>
    public sealed class PluginTypeInfo
    {
        public string FullName { get; init; } = "";
        public IReadOnlyList<PluginMethodInfo> Methods { get; init; } = new List<PluginMethodInfo>();
        public IReadOnlyList<PluginMemberInfo> Properties { get; init; } = new List<PluginMemberInfo>();
        public IReadOnlyList<PluginMemberInfo> Fields { get; init; } = new List<PluginMemberInfo>();
        public IReadOnlyList<PluginEventInfo> Events { get; init; } = new List<PluginEventInfo>();
    }

    /// <summary>
    /// PluginScanner'ın tek bir DLL için ürettiği sonuç. Bu model, hem şimdiki txt loglamanın
    /// hem de ileride UI'a dönecek JSON'un TEK kaynağı olacak - loglama buradan üretilir,
    /// tersi değil (bkz. PluginScanner.WriteLogLines).
    /// </summary>
    public sealed class PluginScanResult
    {
        public string FilePath { get; init; } = "";
        public PluginKind Kind { get; init; }
        public IReadOnlyList<PluginTypeInfo> Types { get; init; } = new List<PluginTypeInfo>();
        public IReadOnlyList<PluginScanError> Errors { get; init; } = new List<PluginScanError>();

        /// <summary>Tarama tamamen başarısız oldu mu (dosya hiç açılamadı vb.) - Errors dolu olsa bile Types kısmi olabilir.</summary>
        public bool FatalFailure { get; init; }
    }
}