using System;
using System.Threading;
using System.Threading.Tasks;
using DSO.Core.Evoker.Commands;
using DSO.Core.Evoker.Description;

namespace DSO.Core.Evoker.Plugins.Management
{
    /// <summary>
    /// PluginManager'daki bir kaydın EvokerCatalog hedefi (IEvokerTarget) - aynı Guid anahtarla. JSON komutlar plugin'in
    /// o anki moduna gider: in-process'te host'ta, sandbox'ta worker'ın içinde (sonuç ikisinde de aynı). Plugin pasifse
    /// komut Inactive hatasıyla döner; aktif ama yüklü değilse (Stopped/Crashed) önce yüklenir.
    /// Tanım (DescribeAsync) plugin aktif olsun olmasın verilir; değerler sadece plugin yüklüyse okunur.
    /// </summary>
    public sealed class PluginTarget : IEvokerTarget
    {
        private readonly PluginManager _manager;

        public Guid Key { get; }

        internal PluginTarget(PluginManager manager, Guid key)
        {
            _manager = manager;
            Key = key;
        }

        public string Kind => "Plugin";

        public string TypeFullName => _manager.TryGetSlot(Key, out var s) ? s.Registration.TypeFullName : "";

        public async Task<EvokerCommandResult> ExecuteAsync(EvokerCommand command, CancellationToken cancellationToken = default)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!_manager.TryGetSlot(Key, out var slot))
                return EvokerCommandResult.Fail(EvokerErrorCodes.TargetNotFound, $"'{Key}' anahtarlı plugin kaydı yok.");
            if (!slot.Registration.IsActive)
                return EvokerCommandResult.Fail(EvokerErrorCodes.Inactive,
                    $"'{slot.Registration.DisplayName}' pasif - kullanmak için önce aktifleştirin.");
            try
            {
                // Yüklü değilse burada yüklenir (hata verirse aşağıda yakalanır).
                await _manager.GetInnerAsync(Key).ConfigureAwait(false);
                return await _manager.Get(Key).ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return EvokerCommandResult.Fail(EvokerErrorCodes.TargetException,
                    $"'{slot.Registration.DisplayName}' yüklenemedi/çalıştırılamadı: {ex.Message}", ex.GetType().FullName);
            }
        }

        public async Task<EvokerTypeDescriptor> DescribeAsync(EvokerDescribeOptions? options = null, CancellationToken cancellationToken = default)
        {
            options ??= new EvokerDescribeOptions();
            var d = await _manager.DescribeAsync(Key, options.IncludeValues, options.IncludeSamples, options.IncludeNonPublic).ConfigureAwait(false);
            if (d.Warnings != null)
                foreach (var w in d.Warnings)
                    if (d.Type.Warnings == null || !d.Type.Warnings.Contains(w)) (d.Type.Warnings ??= new()).Add(w);
            return d.Type;
        }
    }
}