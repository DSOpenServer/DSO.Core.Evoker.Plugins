using System;
using System.Threading.Tasks;
using DSO.Core.Evoker;
using DSO.Core.Evoker.Plugins.Scanning;

namespace DSO.Core.Evoker.Plugins.Loading
{
    /// <summary>
    /// In-process yol: gerçek Assembly.Load + EvokerBuilder kullanır. Host process'in kendi
    /// belleğinde çalışır - izolasyon YOKTUR, sadece admin'in "bu DLL'e güveniyorum" dediği
    /// durumda kullanılır (bkz. PluginWorkerHandle.PromoteAsync).
    ///
    /// Bir loader BİR plugin instance'ına bağlıdır (bkz. IPluginLoader). Görevi SADECE güvenli
    /// yükleme - LoadInProcessAsync tamamlanınca çağırma sorumluluğu tamamen Builder'a
    /// (public EvokerBuilder) geçer. Builder'ın tüm public yüzeyi (Invoke&lt;T&gt;, InvokeAsync&lt;T&gt;,
    /// Execute, ExecuteAsync, GetFunc, GetAction...) doğrudan kullanılabilir - loader bunun
    /// üstüne kısıtlayıcı bir sarmalayıcı koymuyor.
    /// </summary>
    public sealed class ManagedDotNetPluginLoader : IPluginLoader
    {
        public PluginKind SupportedKind => PluginKind.ManagedDotNet;

        public object? Instance { get; private set; }

        /// <summary>
        /// Load sonrası dolu, instance'a SetInstance ile bağlı. PUBLIC: loader'ın görevi bitti,
        /// bundan sonrası EvokerBuilder'ın - tip bilerek Invoke&lt;T&gt;/InvokeAsync&lt;T&gt; çağırmak,
        /// Execute/ExecuteAsync kullanmak, GetFunc/GetAction ile tekrar tekrar çağrılacak bir
        /// delegate çıkarmak vb. hepsi doğrudan burada.
        /// </summary>
        public EvokerBuilder? Builder { get; private set; }

        /// <summary>
        /// Assembly.LoadFrom ile GERÇEK yükleme + parametresiz constructor'ı DynamicEntityAccessor
        /// üzerinden (Activator.CreateInstance DEĞİL - derlenmiş/cache'li yol, Evoker'ın geri kalanıyla
        /// tutarlı) oluşturur. Tip adı ignoreCase:true ile aranıyor - VB.NET case-insensitive bir dil.
        /// Bir loader üzerinde birden fazla kez çağrılamaz.
        /// <paramref name="includeNonPublic"/> - bkz. IPluginLoader.LoadInProcessAsync: true ise
        /// parametresiz constructor private/protected olsa da bulunur, VE sonrasında Builder üzerinden
        /// yapılan tüm Invoke/Execute/GetValue/SetValue çağrıları private/protected/internal üyelere de
        /// erişebilir (EvokerBuilder'a bu tek bayrak - constructor'ında - geçiliyor, bkz.
        /// EvokerBuilder.IncludeNonPublic).
        /// </summary>
        public Task LoadInProcessAsync(string filePath, string typeFullName, bool includeNonPublic = false)
        {
            if (Instance != null)
                throw new InvalidOperationException(
                    "Bu loader zaten bir instance'a bağlı - her plugin yüklemesi için yeni bir ManagedDotNetPluginLoader oluşturun.");

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("filePath boş olamaz.", nameof(filePath));
            if (string.IsNullOrWhiteSpace(typeFullName))
                throw new ArgumentException("typeFullName boş olamaz.", nameof(typeFullName));

            var assembly = System.Reflection.Assembly.LoadFrom(filePath);

            var type = assembly.GetType(typeFullName, throwOnError: false, ignoreCase: true)
                ?? throw new TypeLoadException(
                    $"'{typeFullName}' tipi '{filePath}' içinde bulunamadı (case-insensitive arama dahil).");

            object instance;
            try
            {
                instance = DynamicEntityAccessor.GetConstructor(type, includeNonPublic)();
            }
            catch (MissingMethodException ex)
            {
                throw new MissingMethodException(
                    $"'{type.FullName}' türünün{(includeNonPublic ? "" : " (public)")} parametresiz " +
                    "constructor'ı yok - plugin tipleri şu an parametresiz constructor'a sahip olmalı.", ex);
            }

            Instance = instance;
            Builder = new EvokerBuilder(type, includeNonPublic).SetInstance(instance);

            return Task.CompletedTask;
        }

        /// <summary>
        /// IPluginLoader sözleşmesi için ince bir köprü - loader-türü bilinmeyen genel çağırıcılar
        /// için. Managed tarafta bunun ötesine geçmek isteyen kod doğrudan Builder'ı kullanmalı.
        /// </summary>
        public Task<object?> InvokeAsync(string methodName, object?[] args)
        {
            if (Builder == null)
                throw new InvalidOperationException("Önce LoadInProcessAsync çağrılmalı.");

            return Builder.InvokeDynamicAsync(methodName, args);
        }
    }
}