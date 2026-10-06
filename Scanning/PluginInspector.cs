using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using DSO.Core.Evoker.Description;

namespace DSO.Core.Evoker.Plugins.Scanning
{
    public sealed class PluginDescribeOptions
    {
        /// <summary>private/protected/internal üyeler de listelensin (varsayılan: evet).</summary>
        public bool IncludeNonPublic { get; init; } = true;

        /// <summary>Plugin'in KENDİ assembly'sindeki temel sınıflardan gelen üyeler de (DeclaredIn ile işaretli). System.* temel sınıflarına inilmez.</summary>
        public bool IncludeInherited { get; init; } = true;

        /// <summary>Referans verilen assembly'leri ve bulunup bulunamadıklarını listele.</summary>
        public bool IncludeReferences { get; init; } = true;

        /// <summary>Her metot/property/constructor için hazır komut şablonu (Sample) üret.</summary>
        public bool IncludeSamples { get; init; }
    }

    /// <summary>
    /// Bir plugin tipinin PluginDescriptor'ını DLL'i ÇALIŞTIRMADAN üretir (MetadataLoadContext). Değerler için
    /// bkz. IPluginBuilder.DescribeAsync (çalışan instance gerekir).
    ///
    /// Bilerek DIŞARIDA bırakılanlar (JSON'u kirletmesin diye): derleyicinin ürettiği her şey (auto-property
    /// backing field'ları, async/iterator/lambda kalıntıları - [CompilerGenerated] ya da adı '&lt;' ile başlayan),
    /// VB.NET'in '$' içeren alanları (_Closure$__, $STATIC$...), get_/set_/add_/remove_ metotları (property ve
    /// event'lerin içinde zaten var), event'lerin arka plan alanları (C#: aynı ad, VB: "{Ad}Event"),
    /// System.Object'ten gelen ve override edilmemiş metotlar, static constructor.
    /// </summary>
    public static class PluginInspector
    {

        public static PluginDescriptor Describe(string filePath, string typeFullName, PluginDescribeOptions? options = null)
        {
            options ??= new PluginDescribeOptions();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException($"Plugin dosyası bulunamadı: {filePath}", filePath);

            filePath = Path.GetFullPath(filePath);
            using var mlc = PluginScanner.CreateMetadataLoadContext(filePath);
            var asm = mlc.LoadFromAssemblyPath(filePath);

            // MetadataLoadContext ignoreCase:true'yu desteklemiyor - önce birebir, sonra elle case-insensitive (VB.NET).
            var type = asm.GetType(typeFullName, throwOnError: false)
                ?? SafeTypes(asm).FirstOrDefault(t => string.Equals(t.FullName, typeFullName, StringComparison.OrdinalIgnoreCase))
                ?? throw new TypeLoadException($"'{typeFullName}' tipi '{filePath}' içinde bulunamadı.");

            var warnings = new List<string>();
            var descriptor = new PluginDescriptor
            {
                Assembly = DescribeAssembly(asm, mlc, filePath, options),
                Type = EvokerDescriber.Describe(type, new EvokerDescribeOptions
                {
                    IncludeNonPublic = options.IncludeNonPublic,
                    IncludeInherited = options.IncludeInherited,
                    IncludeSamples = options.IncludeSamples
                })
            };
            foreach (var w in warnings.Concat(descriptor.Type.Warnings ?? new List<string>())) descriptor.AddWarning(w);
            return descriptor;
        }

        // ================= Assembly =================

        private static PluginAssemblyDescriptor DescribeAssembly(Assembly asm, MetadataLoadContext mlc, string filePath, PluginDescribeOptions options)
        {
            var name = asm.GetName();
            var fi = new FileInfo(filePath);
            string sha;
            using (var fs = File.OpenRead(filePath))
            using (var sha256 = SHA256.Create())
                sha = Convert.ToHexString(sha256.ComputeHash(fs));

            List<PluginReferenceDescriptor>? refs = null;
            if (options.IncludeReferences)
            {
                refs = new List<PluginReferenceDescriptor>();
                foreach (var r in asm.GetReferencedAssemblies().OrderBy(r => r.Name))
                {
                    bool missing = false;
                    try { mlc.LoadFromAssemblyName(r); } catch { missing = true; }
                    refs.Add(new PluginReferenceDescriptor { Name = r.Name ?? "?", Version = r.Version?.ToString(), Missing = missing ? true : null });
                }
            }

            return new PluginAssemblyDescriptor
            {
                Name = name.Name ?? Path.GetFileNameWithoutExtension(filePath),
                Version = name.Version?.ToString(),
                FileVersion = AttrArg(asm.GetCustomAttributesData(), "System.Reflection.AssemblyFileVersionAttribute"),
                InformationalVersion = AttrArg(asm.GetCustomAttributesData(), "System.Reflection.AssemblyInformationalVersionAttribute"),
                TargetFramework = TargetFramework(asm),
                FilePath = filePath,
                FileSize = fi.Length,
                LastWriteUtc = fi.LastWriteTimeUtc,
                Sha256 = sha,
                References = refs
            };
        }

        private static string? TargetFramework(Assembly asm)
        {
            var tf = asm.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
            if (tf == null) return null;
            var display = tf.NamedArguments.FirstOrDefault(n => n.MemberName == "FrameworkDisplayName").TypedValue.Value as string;
            return !string.IsNullOrEmpty(display) ? display : tf.ConstructorArguments.FirstOrDefault().Value as string;
        }

        private static string? AttrArg(IList<CustomAttributeData> attrs, string attrFullName) =>
            attrs.FirstOrDefault(a => a.AttributeType.FullName == attrFullName)?.ConstructorArguments.FirstOrDefault().Value as string;

        /// <summary>Okunur C# tipi adı - bkz. EvokerDescriber.Friendly (tek yer).</summary>
        public static string Friendly(Type t, Assembly? pluginAsm = null) => EvokerDescriber.Friendly(t, pluginAsm);

        private static IEnumerable<Type> SafeTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
        }

        private static List<T>? NullIfEmpty<T>(List<T>? list) => list == null || list.Count == 0 ? null : list;

        private static void Safe(List<string> warnings, string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { warnings.Add($"{what} okunamadı: {ex.Message}"); }
        }
    }
}