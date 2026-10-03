using System;
using System.Threading.Tasks;

namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>Sandbox event aboneliği - Dispose/DisposeAsync ile çıkılır (birden fazla çağrı güvenli).</summary>
    public sealed class PluginEventSubscription : IDisposable, IAsyncDisposable
    {
        private PluginWorkerHandle? _handle;
        private readonly int _id;

        internal PluginEventSubscription(PluginWorkerHandle handle, int id)
        {
            _handle = handle;
            _id = id;
        }

        public ValueTask DisposeAsync()
        {
            var h = System.Threading.Interlocked.Exchange(ref _handle, null);
            return h == null ? default : new ValueTask(h.UnsubscribeAsync(_id));
        }

        // Senkron Dispose: çıkış isteği gönderilir, beklenmez (UI thread'inde bloklamamak için). Host tarafı
        // kayıt ANINDA silinir - bu andan sonra gelen event'ler handler'a ULAŞMAZ.
        public void Dispose() => _ = DisposeAsync().AsTask();
    }
}