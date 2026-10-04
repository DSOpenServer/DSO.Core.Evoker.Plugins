using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DSO.Core.Evoker.Plugins.Scanning
{
    /// <summary>
    /// Bir plugin DLL'ini ÇALIŞTIRMADAN taramak için tek giriş noktası.
    /// Assembly.Load KULLANILMAZ - kod hiçbir şekilde tetiklenmemeli (static ctor dahil).
    /// Bunun yerine System.Reflection.MetadataLoadContext kullanılır.
    /// </summary>
    public static class PluginScanner
    {
        /// <summary>
        /// Dosyanın .NET (managed) mi, native/başka bir runtime mı olduğunu belirler.
        /// Uzantıya güvenmez: MetadataLoadContext ile açmayı dener, CLR metadata yoksa
        /// (BadImageFormatException) native/başka bir ikili olduğuna karar verir.
        /// </summary>
        public static PluginKind DetectKind(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return PluginKind.Unknown;

            try
            {
                using var mlc = CreateMetadataLoadContext(filePath);
                var assembly = mlc.LoadFromAssemblyPath(filePath); // Assembly IDisposable değil - mlc.Dispose() yeterli
                _ = assembly.GetName(); // metadata gerçekten okunabiliyor mu diye erken tetikleme
                return PluginKind.ManagedDotNet;
            }
            catch (BadImageFormatException)
            {
                // CLR metadata directory yok - native PE (ya da tamamen farklı bir dosya formatı).
                return PluginKind.Native;
            }
            catch
            {
                // Dosya bozuk, kilitli, erişilemez vb. - "hangi kind" sorusuna cevap veremiyoruz.
                return PluginKind.Unknown;
            }
        }

        /// <summary>
        /// Asıl tarama. MetadataLoadContext ile açar, ReflectionTypeLoadException'ı (assembly'nin
        /// TAMAMINI düşürmeden) yakalayıp içindeki Types/LoaderExceptions dizilerinden hangi
        /// tiplerin çözülemediğini tek tek çıkarır; geri kalan tipler taranmaya devam eder.
        /// </summary>
        public static PluginScanResult Scan(string filePath)
        {
            var errors = new List<PluginScanError>();
            var typeInfos = new List<PluginTypeInfo>();

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return new PluginScanResult
                {
                    FilePath = filePath ?? "",
                    Kind = PluginKind.Unknown,
                    FatalFailure = true,
                    Errors = new List<PluginScanError>
                    {
                        new PluginScanError { Message = $"Dosya bulunamadı: {filePath}" }
                    }
                };
            }

            var kind = DetectKind(filePath);
            if (kind != PluginKind.ManagedDotNet)
            {
                // Native/Unknown için PluginScanner sorumluluk almıyor - NativePluginLoader'daki
                // yer tutucu notuna bakınız. Burada sadece kind'ı raporlayıp çıkıyoruz.
                return new PluginScanResult { FilePath = filePath, Kind = kind };
            }

            try
            {
                using var mlc = CreateMetadataLoadContext(filePath);
                var assembly = mlc.LoadFromAssemblyPath(filePath); // Assembly IDisposable değil - mlc.Dispose() yeterli

                Type?[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException rtle)
                {
                    // KRİTİK NOKTA: GetTypes() bir tip çözülemediğinde TÜM assembly için exception
                    // fırlatır, ama exception'ın kendisi hem kısmi Types dizisini (başarısız
                    // olanlar null) hem de index-hizalı LoaderExceptions dizisini taşır.
                    // Assembly'yi düşürmek yerine bunları kullanarak devam ediyoruz.
                    types = rtle.Types;
                    for (int i = 0; i < types.Length; i++)
                    {
                        if (types[i] != null) continue;
                        var loaderEx = (rtle.LoaderExceptions != null && i < rtle.LoaderExceptions.Length)
                            ? rtle.LoaderExceptions[i]
                            : null;
                        errors.Add(new PluginScanError
                        {
                            Message = $"Bir tip çözülemedi: {loaderEx?.Message ?? "bilinmeyen sebep"}",
                            ExceptionType = loaderEx?.GetType().Name
                        });
                    }
                }

                foreach (var type in types)
                {
                    if (type == null) continue;
                    if (!type.IsPublic && !type.IsNestedPublic) continue; // dışarıdan çağrılamayan tiplerle ilgilenmiyoruz

                    try
                    {
                        typeInfos.Add(ScanType(type, errors));
                    }
                    catch (Exception ex)
                    {
                        // Tipin kendisi listede ama üyelerine erişim (ör. eksik bir referans tipi
                        // yüzünden) patlıyor olabilir - bu TEK tipi atla, diğerlerine devam et.
                        errors.Add(new PluginScanError
                        {
                            TypeName = type.FullName,
                            Message = $"Tip taranamadı: {ex.Message}",
                            ExceptionType = ex.GetType().Name
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return new PluginScanResult
                {
                    FilePath = filePath,
                    Kind = kind,
                    FatalFailure = true,
                    Errors = new List<PluginScanError>
                    {
                        new PluginScanError { Message = $"Assembly açılamadı: {ex.Message}", ExceptionType = ex.GetType().Name }
                    }
                };
            }

            return new PluginScanResult
            {
                FilePath = filePath,
                Kind = kind,
                Types = typeInfos,
                Errors = errors
            };
        }

        /// <summary>
        /// PluginScanResult'ı insan-okunur satırlara çevirir. Loglama BU MODELDEN türetilir -
        /// ayrı bir "log formatı" yoktur, ileride aynı PluginScanResult JSON olarak da dönecektir.
        /// </summary>
        public static IEnumerable<string> ToLogLines(PluginScanResult result)
        {
            yield return $"[{result.Kind}] {result.FilePath}";

            if (result.FatalFailure)
            {
                yield return "  DURUM: taranamadı (fatal).";
            }

            foreach (var type in result.Types)
            {
                yield return $"  TİP: {type.FullName} ({type.Methods.Count} metot, {type.Properties.Count} property, {type.Fields.Count} field, {type.Events.Count} event)";
                foreach (var method in type.Methods)
                {
                    var stat = method.IsStatic ? "static " : "";
                    var parms = string.Join(", ", method.Parameters.Select(p =>
                        $"{p.TypeName} {p.Name}" + (p.IsOptional ? " = ?" : "") + (p.IsByRef ? " (ref/out)" : "")));
                    yield return $"    {stat}{method.ReturnTypeName} {method.Name}({parms})";
                }
                foreach (var p in type.Properties)
                {
                    var acc = (p.CanRead ? "get; " : "") + (p.CanWrite ? "set; " : "");
                    yield return $"    {(p.IsStatic ? "static " : "")}property {p.TypeName} {p.Name} {{ {acc}}}";
                }
                foreach (var f in type.Fields)
                    yield return $"    {(f.IsStatic ? "static " : "")}field {f.TypeName} {f.Name}{(f.CanWrite ? "" : " (readonly)")}";
                foreach (var e in type.Events)
                    yield return $"    {(e.IsStatic ? "static " : "")}event {e.Name}({string.Join(", ", e.ArgumentTypeNames)})";
            }

            foreach (var error in result.Errors)
            {
                var typePrefix = error.TypeName != null ? $"{error.TypeName}: " : "";
                yield return $"  [HATA] {typePrefix}{error.Message}" +
                              (error.ExceptionType != null ? $" ({error.ExceptionType})" : "");
            }
        }

        private static PluginTypeInfo ScanType(Type type, List<PluginScanError> errors)
        {
            var methods = new List<PluginMethodInfo>();
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

            foreach (var m in type.GetMethods(flags))
            {
                if (m.IsSpecialName) continue; // property get_/set_, event add_/remove_ vb. dışla

                try
                {
                    var parameters = m.GetParameters().Select(p => new PluginParameterInfo
                    {
                        Name = p.Name ?? "",
                        TypeName = (p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType)?.FullName
                                   ?? p.ParameterType.Name,
                        IsOptional = p.IsOptional,
                        IsByRef = p.ParameterType.IsByRef
                    }).ToList();

                    methods.Add(new PluginMethodInfo
                    {
                        Name = m.Name,
                        ReturnTypeName = m.ReturnType.FullName ?? m.ReturnType.Name,
                        Parameters = parameters,
                        IsPublic = m.IsPublic,
                        IsStatic = m.IsStatic
                    });
                }
                catch (Exception ex)
                {
                    errors.Add(new PluginScanError
                    {
                        TypeName = type.FullName,
                        Message = $"'{m.Name}' metodu okunamadı: {ex.Message}",
                        ExceptionType = ex.GetType().Name
                    });
                }
            }

            var properties = new List<PluginMemberInfo>();
            var fields = new List<PluginMemberInfo>();
            var events = new List<PluginEventInfo>();

            // Sadece PUBLIC yüzey (tarama admin'e "dışarı açılan" API'yi gösterir; includeNonPublic
            // çalışma zamanında verilen bir karar - private üyeler bilerek listelenmiyor).
            TryScan(type, errors, "property", () =>
            {
                foreach (var p in type.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length > 0) continue; // indexer - GetValue/SetValue ile erişilemez
                    var getter = p.GetGetMethod();
                    var setter = p.GetSetMethod();
                    properties.Add(new PluginMemberInfo
                    {
                        Name = p.Name,
                        TypeName = p.PropertyType.FullName ?? p.PropertyType.Name,
                        IsField = false,
                        CanRead = getter != null,
                        CanWrite = setter != null,
                        IsPublic = true,
                        IsStatic = (getter ?? setter)?.IsStatic ?? false
                    });
                }
            });

            TryScan(type, errors, "field", () =>
            {
                foreach (var f in type.GetFields(flags))
                {
                    if (f.IsSpecialName) continue;
                    fields.Add(new PluginMemberInfo
                    {
                        Name = f.Name,
                        TypeName = f.FieldType.FullName ?? f.FieldType.Name,
                        IsField = true,
                        CanRead = true,
                        CanWrite = !f.IsInitOnly && !f.IsLiteral,
                        IsPublic = f.IsPublic,
                        IsStatic = f.IsStatic
                    });
                }
            });

            TryScan(type, errors, "event", () =>
            {
                foreach (var e in type.GetEvents(flags))
                {
                    var handlerType = e.EventHandlerType;
                    var invoke = handlerType?.GetMethod("Invoke");
                    events.Add(new PluginEventInfo
                    {
                        Name = e.Name,
                        HandlerTypeName = handlerType?.FullName ?? handlerType?.Name ?? "?",
                        ArgumentTypeNames = invoke?.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToList()
                                            ?? new List<string>(),
                        IsPublic = true,
                        IsStatic = e.GetAddMethod()?.IsStatic ?? false
                    });
                }
            });

            return new PluginTypeInfo
            {
                FullName = type.FullName ?? type.Name,
                Methods = methods,
                Properties = properties,
                Fields = fields,
                Events = events
            };
        }

        // Tek bir üye grubu (ör. bir property'nin tipi bağımlılık eksikliğinden çözülemiyor) patlarsa
        // tüm tipi DÜŞÜRMEDEN hatayı kaydet, diğer gruplara devam et - metot taramasıyla aynı felsefe.
        private static void TryScan(Type type, List<PluginScanError> errors, string what, Action scan)
        {
            try { scan(); }
            catch (Exception ex)
            {
                errors.Add(new PluginScanError
                {
                    TypeName = type.FullName,
                    Message = $"{what} listesi okunamadı: {ex.Message}",
                    ExceptionType = ex.GetType().Name
                });
            }
        }

        /// <summary>
        /// PathAssemblyResolver, DLL'in bulunduğu klasördeki tüm assembly'leri + çalışan
        /// runtime'ın kendi assembly'lerini görebilsin diye kurulur. Bu, VB.NET/.NET Framework 4.x
        /// DLL'lerinin referans ettiği bağımlılıkların (mümkün olduğunca) çözülebilmesi için gerekli -
        /// yine de bazı tipler çözülemeyebilir, bu durumda Scan() metodundaki ReflectionTypeLoadException
        /// yakalama mantığı devreye girer, tüm tarama düşmez.
        /// </summary>
        internal static MetadataLoadContext CreateMetadataLoadContext(string filePath)
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            var pluginDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? runtimeDir;

            var runtimeAssemblies = Directory.Exists(runtimeDir)
                ? Directory.GetFiles(runtimeDir, "*.dll")
                : Array.Empty<string>();
            var pluginDirAssemblies = Directory.Exists(pluginDir)
                ? Directory.GetFiles(pluginDir, "*.dll")
                : Array.Empty<string>();

            // Aynı dosya adı hem runtime'da hem plugin klasöründe varsa PLUGIN klasöründeki
            // öncelikli olsun istiyoruz (kendi taşıdığı bağımlılık sürümü) - bu yüzden onu önce ekliyoruz.
            var allPaths = pluginDirAssemblies
                .Concat(runtimeAssemblies)
                .Concat(new[] { filePath })
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();

            var resolver = new PathAssemblyResolver(allPaths);
            return new MetadataLoadContext(resolver);
        }
    }
}