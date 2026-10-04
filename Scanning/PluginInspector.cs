using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

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
        private const string CompilerGeneratedAttr = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

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
                Type = DescribeType(type, asm, options, warnings)
            };
            foreach (var w in warnings) descriptor.AddWarning(w);
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

        // ================= Tip =================

        private static PluginTypeDescriptor DescribeType(Type type, Assembly pluginAsm, PluginDescribeOptions options, List<string> warnings)
        {
            var flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                        | (options.IncludeNonPublic ? BindingFlags.NonPublic : 0);

            var ctors = new List<PluginConstructorDescriptor>();
            var methods = new List<PluginMethodDescriptor>();
            var props = new List<PluginPropertyDescriptor>();
            var fields = new List<PluginFieldDescriptor>();
            var events = new List<PluginEventDescriptor>();
            var seenMethods = new HashSet<string>();
            var seenMembers = new HashSet<string>();

            // Tipin kendisi + (istenirse) plugin assembly'sindeki temel sınıfları - System.* temellere inilmez.
            for (var level = type; level != null; level = options.IncludeInherited ? level.BaseType : null)
            {
                if (level != type && (level.Assembly != pluginAsm || IsSystemRoot(level))) break;
                string? declaredIn = level == type ? null : Friendly(level, pluginAsm);

                Safe(warnings, $"{level.Name} event'leri", () =>
                {
                    foreach (var e in level.GetEvents(flags))
                    {
                        if (IsNoise(e) || !seenMembers.Add("E:" + e.Name)) continue;
                        var add = e.GetAddMethod(true);
                        var invoke = e.EventHandlerType?.GetMethod("Invoke");
                        events.Add(new PluginEventDescriptor
                        {
                            Name = e.Name,
                            Visibility = Visibility(add),
                            IsStatic = add?.IsStatic == true ? true : null,
                            HandlerType = e.EventHandlerType != null ? Friendly(e.EventHandlerType, pluginAsm) : "?",
                            Arguments = NullIfEmpty(invoke?.GetParameters().Select(p => Param(p, pluginAsm)).ToList()),
                            DeclaredIn = declaredIn
                        });
                    }
                });
                var eventNames = new HashSet<string>(level.GetEvents(flags).Select(e => e.Name));

                if (level == type)
                {
                    Safe(warnings, "constructor'lar", () =>
                    {
                        foreach (var c in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | (options.IncludeNonPublic ? BindingFlags.NonPublic : 0)))
                            ctors.Add(new PluginConstructorDescriptor
                            {
                                Visibility = Visibility(c),
                                Parameters = NullIfEmpty(c.GetParameters().Select(p => Param(p, pluginAsm)).ToList())
                            });
                    });
                }

                Safe(warnings, $"{level.Name} property'leri", () =>
                {
                    foreach (var p in level.GetProperties(flags))
                    {
                        if (IsNoise(p) || !seenMembers.Add("P:" + p.Name)) continue;
                        var get = p.GetGetMethod(true);
                        var set = p.GetSetMethod(true);
                        if (!options.IncludeNonPublic) { if (get != null && !get.IsPublic) get = null; if (set != null && !set.IsPublic) set = null; }
                        if (get == null && set == null) continue;
                        bool initOnly = set != null && set.ReturnParameter.GetRequiredCustomModifiers()
                            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
                        props.Add(new PluginPropertyDescriptor
                        {
                            Name = p.Name,
                            Type = Friendly(p.PropertyType, pluginAsm),
                            Getter = get != null ? Visibility(get) : null,
                            Setter = set != null ? Visibility(set) : null,
                            IsInitOnly = initOnly ? true : null,
                            IsStatic = (get ?? set)!.IsStatic ? true : null,
                            IndexerParameters = NullIfEmpty(p.GetIndexParameters().Select(x => Param(x, pluginAsm)).ToList()),
                            DeclaredIn = declaredIn
                        });
                    }
                });

                Safe(warnings, $"{level.Name} field'ları", () =>
                {
                    foreach (var f in level.GetFields(flags))
                    {
                        if (IsNoise(f) || f.IsSpecialName) continue; // enum'ların value__ alanı vb.
                        // Event'in arka plan alanı: C# "{Ad}", VB.NET "{Ad}Event" - event zaten listede.
                        if (eventNames.Contains(f.Name) || (f.Name.EndsWith("Event") && eventNames.Contains(f.Name[..^5]))) continue;
                        if (!seenMembers.Add("F:" + f.Name)) continue;
                        object? constValue = null;
                        if (f.IsLiteral) { try { constValue = f.GetRawConstantValue(); } catch { } }
                        fields.Add(new PluginFieldDescriptor
                        {
                            Name = f.Name,
                            Type = Friendly(f.FieldType, pluginAsm),
                            Visibility = Visibility(f),
                            IsStatic = f.IsStatic && !f.IsLiteral ? true : null,
                            IsReadOnly = f.IsInitOnly ? true : null,
                            IsConst = f.IsLiteral ? true : null,
                            ConstValue = constValue,
                            DeclaredIn = declaredIn
                        });
                    }
                });

                Safe(warnings, $"{level.Name} metotları", () =>
                {
                    foreach (var m in level.GetMethods(flags))
                    {
                        if (m.IsSpecialName || IsNoise(m)) continue;
                        var ps = m.GetParameters();
                        // Aynı imza (override) alt sınıfta zaten listelendiyse temel sınıftakini tekrar yazma.
                        string sig = m.Name + "(" + string.Join(",", ps.Select(x => x.ParameterType.FullName)) + ")";
                        if (!seenMethods.Add(sig)) continue;

                        string ret = Friendly(m.ReturnType, pluginAsm);
                        string? notCallable =
                            ps.Any(x => x.ParameterType.IsByRef && !x.IsIn) ? "ref/out parametre (EvokerBuilder object[] sözleşmesi geri taşıyamaz)"
                            : m.ContainsGenericParameters ? "açık generic metot (tip argümanı belirtilemiyor)"
                            : m.IsAbstract ? "abstract (gövdesi yok)"
                            : ps.Any(x => x.ParameterType.IsPointer) ? "pointer parametre"
                            : null;
                        string retFull = m.ReturnType.FullName ?? "";
                        methods.Add(new PluginMethodDescriptor
                        {
                            Name = m.Name,
                            Visibility = Visibility(m),
                            ReturnType = ret,
                            Parameters = NullIfEmpty(ps.Select(x => Param(x, pluginAsm)).ToList()),
                            IsStatic = m.IsStatic ? true : null,
                            IsAsync = retFull.StartsWith("System.Threading.Tasks.Task") || retFull.StartsWith("System.Threading.Tasks.ValueTask") ? true : null,
                            IsVirtual = m.IsVirtual && !m.IsFinal && (m.Attributes & MethodAttributes.NewSlot) != 0 && !m.IsAbstract ? true : null,
                            IsAbstract = m.IsAbstract ? true : null,
                            IsOverride = m.IsVirtual && (m.Attributes & MethodAttributes.NewSlot) == 0 ? true : null,
                            GenericArguments = m.IsGenericMethodDefinition ? m.GetGenericArguments().Select(g => g.Name).ToList() : null,
                            DeclaredIn = declaredIn,
                            NotCallableReason = notCallable
                        });
                    }
                });
            }

            return new PluginTypeDescriptor
            {
                FullName = type.FullName ?? type.Name,
                Name = type.Name,
                Namespace = type.Namespace,
                Kind = Kind(type),
                BaseType = type.BaseType != null && !IsSystemRoot(type.BaseType) ? Friendly(type.BaseType, pluginAsm) : null,
                Interfaces = NullIfEmpty(type.GetInterfaces().Select(i => Friendly(i, pluginAsm)).OrderBy(x => x).ToList()),
                Constructors = NullIfEmpty(ctors),
                Methods = NullIfEmpty(methods.OrderBy(m => m.DeclaredIn != null).ThenBy(m => m.Name).ToList()),
                Properties = NullIfEmpty(props.OrderBy(p => p.DeclaredIn != null).ThenBy(p => p.Name).ToList()),
                Fields = NullIfEmpty(fields.OrderBy(f => f.DeclaredIn != null).ThenBy(f => f.Name).ToList()),
                Events = NullIfEmpty(events.OrderBy(e => e.Name).ToList())
            };
        }

        // ================= yardımcılar =================

        private static PluginParameterDescriptor Param(ParameterInfo p, Assembly pluginAsm)
        {
            var t = p.ParameterType;
            ParameterDirection? dir = null;
            if (t.IsByRef) { dir = p.IsOut ? ParameterDirection.Out : p.IsIn ? ParameterDirection.In : ParameterDirection.Ref; t = t.GetElementType()!; }

            bool hasDefault = false;
            object? def = null;
            if (p.IsOptional)
            {
                try
                {
                    var raw = p.RawDefaultValue; // MetadataLoadContext'te DefaultValue değil RawDefaultValue kullanılır
                    if (raw != DBNull.Value && raw != System.Reflection.Missing.Value) { hasDefault = true; def = raw; }
                }
                catch { }
                // decimal / DateTime varsayılanları attribute ile saklanır
                var dec = p.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.DecimalConstantAttribute");
                if (dec != null && dec.ConstructorArguments.Count == 5)
                {
                    try
                    {
                        var a = dec.ConstructorArguments;
                        def = new decimal(Convert.ToInt32(a[4].Value), Convert.ToInt32(a[3].Value), Convert.ToInt32(a[2].Value), Convert.ToByte(a[1].Value) != 0, Convert.ToByte(a[0].Value));
                        hasDefault = true;
                    }
                    catch { }
                }
                // Enum varsayılanı metadata'da sayı olarak durur - okunur olsun diye enum adına çevir.
                if (def != null && t.IsEnum)
                {
                    var name = t.GetFields(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(f => { try { return Equals(Convert.ToInt64(f.GetRawConstantValue()), Convert.ToInt64(def)); } catch { return false; } })?.Name;
                    if (name != null) def = $"{t.Name}.{name}";
                }
            }

            return new PluginParameterDescriptor
            {
                Name = p.Name ?? "",
                Type = Friendly(t, pluginAsm),
                Direction = dir,
                IsOptional = p.IsOptional ? true : null,
                HasDefaultValue = hasDefault ? true : null,
                DefaultValue = def,
                IsParams = p.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.ParamArrayAttribute") ? true : null
            };
        }

        private static bool IsNoise(MemberInfo m)
        {
            var n = m.Name;
            if (n.StartsWith("<") || n.Contains('$')) return true; // C# derleyici / VB.NET kalıntıları
            if (m is MethodInfo mi && mi.DeclaringType?.FullName == "System.Object") return true;
            if (m is ConstructorInfo ci && ci.IsStatic) return true;
            try { return m.GetCustomAttributesData().Any(a => a.AttributeType.FullName == CompilerGeneratedAttr); }
            catch { return false; }
        }

        private static bool IsSystemRoot(Type t) =>
            t.FullName is "System.Object" or "System.ValueType" or "System.Enum" or "System.MulticastDelegate" or "System.Delegate";

        private static PluginTypeKind Kind(Type t)
        {
            if (t.IsInterface) return PluginTypeKind.Interface;
            if (t.IsEnum) return PluginTypeKind.Enum;
            if (t.IsValueType) return PluginTypeKind.Struct;
            if (t.BaseType?.FullName == "System.MulticastDelegate") return PluginTypeKind.Delegate;
            if (t.IsAbstract && t.IsSealed) return PluginTypeKind.StaticClass;
            if (t.IsAbstract) return PluginTypeKind.AbstractClass;
            if (t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(m => m.Name == "<Clone>$")) return PluginTypeKind.Record;
            return PluginTypeKind.Class;
        }

        private static MemberVisibility Visibility(MethodBase? m)
        {
            if (m == null) return MemberVisibility.Private;
            if (m.IsPublic) return MemberVisibility.Public;
            if (m.IsFamilyOrAssembly) return MemberVisibility.ProtectedInternal;
            if (m.IsFamily) return MemberVisibility.Protected;
            if (m.IsAssembly) return MemberVisibility.Internal;
            if (m.IsFamilyAndAssembly) return MemberVisibility.PrivateProtected;
            return MemberVisibility.Private;
        }

        private static MemberVisibility Visibility(FieldInfo f)
        {
            if (f.IsPublic) return MemberVisibility.Public;
            if (f.IsFamilyOrAssembly) return MemberVisibility.ProtectedInternal;
            if (f.IsFamily) return MemberVisibility.Protected;
            if (f.IsAssembly) return MemberVisibility.Internal;
            if (f.IsFamilyAndAssembly) return MemberVisibility.PrivateProtected;
            return MemberVisibility.Private;
        }

        private static readonly Dictionary<string, string> Keywords = new()
        {
            ["System.Void"] = "void",
            ["System.Object"] = "object",
            ["System.String"] = "string",
            ["System.Boolean"] = "bool",
            ["System.Byte"] = "byte",
            ["System.SByte"] = "sbyte",
            ["System.Int16"] = "short",
            ["System.UInt16"] = "ushort",
            ["System.Int32"] = "int",
            ["System.UInt32"] = "uint",
            ["System.Int64"] = "long",
            ["System.UInt64"] = "ulong",
            ["System.Single"] = "float",
            ["System.Double"] = "double",
            ["System.Decimal"] = "decimal",
            ["System.Char"] = "char"
        };

        /// <summary>
        /// Okunur C# tipi adı: Task&lt;int&gt;, int?, List&lt;string&gt;, Dictionary&lt;string, Point&gt;, byte[].
        /// .NET'in kendi tipleri ve plugin'in kendi tipleri KISA adla; üçüncü parti kütüphanelerin tipleri tam adla.
        /// </summary>
        public static string Friendly(Type t, Assembly? pluginAsm = null)
        {
            if (t.IsByRef) return Friendly(t.GetElementType()!, pluginAsm);
            if (t.IsPointer) return Friendly(t.GetElementType()!, pluginAsm) + "*";
            if (t.IsArray) return Friendly(t.GetElementType()!, pluginAsm) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (t.IsGenericParameter) return t.Name;
            if (t.FullName != null && Keywords.TryGetValue(t.FullName, out var kw)) return kw;

            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                var args = t.GetGenericArguments();
                if (def.FullName == "System.Nullable`1") return Friendly(args[0], pluginAsm) + "?";
                string baseName = ShortOrFull(def, pluginAsm);
                int tick = baseName.IndexOf('`');
                if (tick >= 0) baseName = baseName[..tick];
                return $"{baseName}<{string.Join(", ", args.Select(a => Friendly(a, pluginAsm)))}>";
            }
            return ShortOrFull(t, pluginAsm);
        }

        private static string ShortOrFull(Type t, Assembly? pluginAsm)
        {
            bool shortName = (t.Namespace?.StartsWith("System") ?? false) || (pluginAsm != null && t.Assembly == pluginAsm);
            string name = shortName ? t.Name : (t.FullName ?? t.Name);
            if (t.IsNested && t.DeclaringType != null && shortName) name = ShortOrFull(t.DeclaringType, pluginAsm) + "." + t.Name;
            return name.Replace('+', '.');
        }

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