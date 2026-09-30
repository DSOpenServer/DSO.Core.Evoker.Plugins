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

    /// <summary>Taranan bir type'ın (class/struct) özeti.</summary>
    public sealed class PluginTypeInfo
    {
        public string FullName { get; init; } = "";
        public IReadOnlyList<PluginMethodInfo> Methods { get; init; } = new List<PluginMethodInfo>();
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
