using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DSO.Core.Evoker.Plugins.Management
{
    /// <summary>Plugin kayıtlarının (admin kararlarının) kalıcı deposu. JSON dosyası yerine DB'ye bağlamak için kendi implementasyonunuzu verin.</summary>
    public interface IPluginConfigStore
    {
        Task<IReadOnlyList<PluginRegistration>> LoadAsync();
        Task SaveAsync(IReadOnlyList<PluginRegistration> registrations);
    }

    /// <summary>
    /// Varsayılan depo: okunabilir, elle de düzenlenebilir bir JSON dosyası (Mode "Sandbox"/"InProcess" olarak yazılır).
    /// Yazma atomiktir (geçici dosya + yer değiştirme) - yazma sırasında çökme dosyayı yarım bırakmaz.
    /// </summary>
    public sealed class JsonFilePluginConfigStore : IPluginConfigStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly SemaphoreSlim _lock = new(1, 1);

        public string FilePath { get; }

        public JsonFilePluginConfigStore(string filePath) => FilePath = Path.GetFullPath(filePath);

        public async Task<IReadOnlyList<PluginRegistration>> LoadAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(FilePath)) return Array.Empty<PluginRegistration>();
                await using var fs = File.OpenRead(FilePath);
                return await JsonSerializer.DeserializeAsync<List<PluginRegistration>>(fs, Json).ConfigureAwait(false)
                       ?? new List<PluginRegistration>();
            }
            finally { _lock.Release(); }
        }

        public async Task SaveAsync(IReadOnlyList<PluginRegistration> registrations)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string tmp = FilePath + ".tmp";
                await using (var fs = File.Create(tmp))
                    await JsonSerializer.SerializeAsync(fs, registrations, Json).ConfigureAwait(false);
                File.Move(tmp, FilePath, overwrite: true);
            }
            finally { _lock.Release(); }
        }
    }

    /// <summary>
    /// Kayıtlardaki dosya yollarını bir kök klasöre (ör. "Plugins") GÖRELİ saklayan sarmalayıcı: diskte
    /// "Siparis/1.0/Siparis.dll" yazılır, manager'a tam yol verilir. Kök klasör taşınsa da (başka sunucu, başka disk)
    /// kayıtlar geçerli kalır. Kökün dışındaki yollar olduğu gibi (tam yol) saklanır.
    /// </summary>
    public sealed class PluginsRootConfigStore : IPluginConfigStore
    {
        private readonly IPluginConfigStore _inner;

        public string RootPath { get; }

        public PluginsRootConfigStore(IPluginConfigStore inner, string rootPath)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            RootPath = Path.GetFullPath(rootPath);
        }

        public async Task<IReadOnlyList<PluginRegistration>> LoadAsync()
        {
            var list = new List<PluginRegistration>();
            foreach (var r in await _inner.LoadAsync().ConfigureAwait(false))
            {
                var c = r.Clone();
                c.FilePath = ToFullPath(c.FilePath);
                list.Add(c);
            }
            return list;
        }

        public Task SaveAsync(IReadOnlyList<PluginRegistration> registrations)
        {
            var list = new List<PluginRegistration>(registrations.Count);
            foreach (var r in registrations)
            {
                var c = r.Clone();
                c.FilePath = ToRelativePath(c.FilePath);
                list.Add(c);
            }
            return _inner.SaveAsync(list);
        }

        /// <summary>Kök'e göre yolu tam yola çevirir (zaten tam yolsa olduğu gibi).</summary>
        public string ToFullPath(string path) =>
            string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(RootPath, path));

        /// <summary>Kökün altındaysa "/" ayraçlı göreli yol; değilse tam yol.</summary>
        public string ToRelativePath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return fullPath;
            var full = Path.GetFullPath(fullPath);
            var rel = Path.GetRelativePath(RootPath, full);
            if (rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(rel)) return full;
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}