using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace DSO.Core.Evoker.Plugins.Loading
{
    /// <summary>
    /// Her in-process plugin için AYRI, kaldırılabilir (collectible) bir AssemblyLoadContext.
    ///
    /// Çözümleme kuralları (Load):
    ///   1) Host ile PAYLAŞILMASI gerekenler -> null (varsayılan context'ten gelir):
    ///        - .NET'in kendi assembly'leri (TRUSTED_PLATFORM_ASSEMBLIES listesi - System.*, Microsoft.*)
    ///        - DSO.Core.* (EvokerBuilder vb. - host ile aynı tip kimliği gerekir)
    ///        - <see cref="SharedAssemblyNames"/>'e eklenenler (host'un plugin'le bilerek paylaştığı sözleşmeler)
    ///   2) Plugin'in KENDİ bağımlılıkları -> bu context'e: önce plugin'in .deps.json'u
    ///      (AssemblyDependencyResolver), yoksa plugin klasöründe "{Ad}.dll".
    ///      Böylece plugin, host'ta farklı sürümü bulunan bir kütüphaneyi (ör. Newtonsoft.Json 12 vs 13)
    ///      kendi sürümüyle kullanır - çakışma olmaz.
    ///   3) Hiçbiri değilse -> null (varsayılan context denesin).
    ///
    /// Native DLL'ler (LoadUnmanagedDll) de önce .deps.json'dan, sonra plugin klasöründen çözülür.
    /// </summary>
    public sealed class PluginLoadContext : AssemblyLoadContext
    {
        // SADECE .NET'in kendi (shared framework klasöründeki) assembly'leri. TRUSTED_PLATFORM_ASSEMBLIES
        // listesi host uygulamanın KENDİ bağımlılıklarını da (ör. host'un Newtonsoft.Json 13'ü) içerir -
        // onları da "paylaşılan" saysaydık, plugin klasöründe kendi Newtonsoft.Json 12'si olan bir plugin
        // yine host'un 13'ünü alırdı (sürüm izolasyonu bozulurdu). Bu yüzden uygulama klasöründekiler hariç.
        private static readonly Lazy<HashSet<string>> PlatformAssemblies = new(() =>
        {
            var appDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !Path.GetFullPath(p).StartsWith(appDir, StringComparison.OrdinalIgnoreCase));
            return new HashSet<string>(tpa.Select(Path.GetFileNameWithoutExtension)!, StringComparer.OrdinalIgnoreCase);
        });

        /// <summary>
        /// Host'un plugin'lerle PAYLAŞTIĞI ek assembly adları (ör. ileride plugin'lerin opsiyonel olarak
        /// uygulayabileceği ortak bir arayüz DLL'i). Bunlar her zaman host'un kopyasından gelir.
        /// </summary>
        public static ISet<string> SharedAssemblyNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _pluginDir;

        public string PluginPath { get; }

        public PluginLoadContext(string pluginPath)
            : base(name: "Plugin:" + Path.GetFileName(pluginPath) + ":" + Guid.NewGuid().ToString("N").Substring(0, 8), isCollectible: true)
        {
            PluginPath = Path.GetFullPath(pluginPath);
            _pluginDir = Path.GetDirectoryName(PluginPath)!;
            _resolver = new AssemblyDependencyResolver(PluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var name = assemblyName.Name;
            if (string.IsNullOrEmpty(name) || IsShared(name))
                return null;

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path == null)
            {
                var candidate = Path.Combine(_pluginDir, name + ".dll");
                if (File.Exists(candidate)) path = candidate;
            }

            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path == null)
            {
                var candidate = Path.Combine(_pluginDir, unmanagedDllName);
                if (File.Exists(candidate)) path = candidate;
            }
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }

        private static bool IsShared(string name) =>
            PlatformAssemblies.Value.Contains(name)
            || name.StartsWith("DSO.Core.", StringComparison.OrdinalIgnoreCase)
            || SharedAssemblyNames.Contains(name);
    }
}