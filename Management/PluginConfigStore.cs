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
}