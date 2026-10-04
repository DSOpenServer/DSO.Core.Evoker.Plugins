using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DSO.Core.Evoker.Plugins
{
    /// <summary>
    /// Yüklenmiş BİR plugin instance'ı üzerinde EvokerBuilder'ın tüm yüzeyi - plugin nerede çalışıyor
    /// olursa olsun (sandbox worker process'i ya da host'un kendi içi) AYNI API.
    ///
    ///   - Sandbox:    handle.Builder                      (Sandbox.SandboxBuilder - IPC üzerinden)
    ///   - In-process: loader.Builder.AsPluginBuilder()    (Loading.InProcessPluginBuilder - doğrudan EvokerBuilder)
    ///
    /// Böylece uygulama kodu IPluginBuilder'a karşı bir kez yazılır; admin bir plugin'i sandbox'tan
    /// in-process'e terfi ettirdiğinde (PromoteToInProcessAsync) çağıran kodda HİÇBİR değişiklik gerekmez.
    ///
    /// Ortak davranış sözleşmesi (iki implementasyon da testlerde AYNI senaryo setiyle doğrulanıyor):
    ///   - Invoke/InvokeAsync her dönüş şeklini kabul eder: void, senkron değer, Task, Task&lt;T&gt;
    ///     (Task'lar beklenir, sonucu döner). Execute/ExecuteAsync sonucu atar.
    ///   - Tipli dönüş (Invoke&lt;T&gt;, GetValue&lt;T&gt;): sayısal genişletme/daraltma, enum, Nullable ve
    ///     Complex nesneler için JSON üzerinden şekil eşlemesi (host kendi DTO'sunu ya da JsonElement'i
    ///     isteyebilir - plugin'in kendi tipini yüklemek ZORUNDA değil).
    ///   - GetValue/SetValue: property, yoksa aynı isimli field; IncludeNonPublic ise private/protected de.
    ///   - Plugin kodunun kendisi exception fırlatırsa: <see cref="Sandbox.PluginInvocationException"/>
    ///     (RemoteExceptionType orijinal tip adı; in-process'te InnerException orijinal exception).
    ///   - DefaultTimeoutMs: SADECE async çağrıların BEKLEMESİNİ keser (TimeoutException), plugin'in
    ///     çalışan kodunu durdurmaz. Null = sınırsız.
    ///   - Sync metotlar async olanların bloklayan halidir (ConfigureAwait(false) ile - UI thread'inde
    ///     deadlock yapmaz). Mümkünse async olanları tercih edin.
    ///   - ref/out parametreli metotlar desteklenmez (EvokerBuilder ile aynı kısıt).
    /// </summary>
    public interface IPluginBuilder
    {
        /// <summary>Yüklenen plugin tipinin tam adı.</summary>
        string TypeFullName { get; }

        /// <summary>private/protected/internal üyeler görülüyor mu (yükleme anında verilen karar).</summary>
        bool IncludeNonPublic { get; }

        /// <summary>true = ayrı worker process (sandbox), false = host'un kendi içinde.</summary>
        bool IsSandboxed { get; }

        /// <summary>Async çağrılar için varsayılan bekleme üst sınırı (ms). Null = sınırsız.</summary>
        int? DefaultTimeoutMs { get; set; }

        // --- Metot çağırma ---
        object? Invoke(string methodName, params object?[] args);
        T? Invoke<T>(string methodName, params object?[] args);
        Task<object?> InvokeAsync(string methodName, params object?[] args);
        Task<T?> InvokeAsync<T>(string methodName, params object?[] args);
        void Execute(string methodName, params object?[] args);
        Task ExecuteAsync(string methodName, params object?[] args);

        // --- Toplu çağrı ---
        /// <summary>
        /// Aynı metodu N argüman setiyle, sırayla çağırır; sonuçlar aynı sırayla döner. Sandbox'ta hepsi
        /// tek (ya da parçalı - bkz. SandboxBuilder.BatchChunkSize) mesajla gider: process sınırı maliyeti N'e
        /// bölünür (milyonlarca çağrılık döngüler için). Bir çağrı hata verirse PluginInvocationException
        /// fırlatılır, BatchIndex hata verenin sırasıdır (öncekiler çalışmıştır).
        /// </summary>
        Task<T?[]> InvokeBatchAsync<T>(string methodName, IReadOnlyList<object?[]> argsList);
        Task ExecuteBatchAsync(string methodName, IReadOnlyList<object?[]> argsList);

        // --- Property / field ---
        T? GetValue<T>(string memberName);
        void SetValue<T>(string memberName, T value);
        Task<T?> GetValueAsync<T>(string memberName);
        Task SetValueAsync<T>(string memberName, T value);

        // --- Tekrar tekrar çağrılacak delegate'ler (çözümleme BİR KEZ, sonra sadece çağrı) ---
        Func<object?[], T?> GetFunc<T>(string methodName, object?[]? sampleArgs = null);
        Func<object?[], Task<T?>> GetFuncAsync<T>(string methodName, object?[]? sampleArgs = null);
        Action<object?[]> GetAction(string methodName, object?[]? sampleArgs = null);
        Func<object?[], Task> GetActionAsync(string methodName, object?[]? sampleArgs = null);

        // --- Tipli delegate'ler (sıkı döngüler için; argüman/dönüş tipleri derleme zamanında belli) ---
        /// <summary>
        /// Örn: <c>var add = b.GetTypedFunc&lt;int, int, int&gt;("Add"); int s = add(1, 2);</c>
        /// In-process'te mümkün olduğunda EvokerBuilder'ın derlenmiş, object[]/boxing'siz delegate'i kullanılır
        /// (argüman/dönüş tipi metodunkiyle doğrudan dönüştürülebiliyorsa). Değilse (host DTO'su ↔ plugin tipi,
        /// Task dönüşü) Invoke ile aynı kurallarla çalışan genel yola düşer - sonuç aynı, sadece daha yavaş.
        /// Sandbox'ta her çağrı yine IPC'dir (GetFunc ile aynı). Hata sözleşmesi Invoke ile aynı.
        /// </summary>
        Func<TResult?> GetTypedFunc<TResult>(string methodName);
        Func<T1, TResult?> GetTypedFunc<T1, TResult>(string methodName);
        Func<T1, T2, TResult?> GetTypedFunc<T1, T2, TResult>(string methodName);
        Func<T1, T2, T3, TResult?> GetTypedFunc<T1, T2, T3, TResult>(string methodName);
        Func<T1, T2, T3, T4, TResult?> GetTypedFunc<T1, T2, T3, T4, TResult>(string methodName);
        Action GetTypedAction(string methodName);
        Action<T1> GetTypedAction<T1>(string methodName);
        Action<T1, T2> GetTypedAction<T1, T2>(string methodName);
        Action<T1, T2, T3> GetTypedAction<T1, T2, T3>(string methodName);
        Action<T1, T2, T3, T4> GetTypedAction<T1, T2, T3, T4>(string methodName);

        // --- Event'ler ---
        /// <summary>
        /// Plugin event'ine abone ol; dönen nesne Dispose edilince çıkılır. Argümanlar için bkz. PluginEventArgs
        /// (tipli okuma: e.Get&lt;T&gt;(i)). İsim büyük/küçük harf duyarsız da bulunur; IncludeNonPublic ise private
        /// event'ler de. Static event'ler de desteklenir.
        /// FARK: in-process'te handler plugin'in event'i tetiklediği thread'de SENKRON çağrılır; sandbox'ta host'ta
        /// ayrı bir thread'de, tetiklenme sırasıyla, ASENKRON (plugin beklemez). İki modda da handler'ın fırlattığı
        /// exception plugin'e yansımaz. Sandbox'ta worker yeniden başlarsa abonelik kendiliğinden yenilenir.
        /// </summary>
        IDisposable Subscribe(string eventName, Action<PluginEventArgs> handler);
        Task<IDisposable> SubscribeAsync(string eventName, Action<PluginEventArgs> handler);

        /// <summary>Plugin tipinin (IncludeNonPublic'e göre görülebilen) event adları.</summary>
        string[] GetEventNames();

        // --- Tanım (bilgi) ---
        /// <summary>
        /// Plugin tipinin tam tanımı: assembly bilgisi, constructor/metot (parametreleri, default değerleri)/
        /// property (get-set görünürlüğü)/field/event - private'lar dahil; includeValues=true ise field ve
        /// property'lerin O ANKİ değerleri de. JSON için: (await b.DescribeAsync()).ToJson(). Yapı DLL
        /// çalıştırılmadan okunur; değerler çalışan instance'tan (property getter'ları çalıştırılır).
        /// </summary>
        Task<Scanning.PluginDescriptor> DescribeAsync(bool includeValues = true);

        // --- Cache ---
        /// <summary>Plugin tipinin DynamicEntityAccessor cache'ini temizler (sandbox'ta worker içinde).</summary>
        void ForgetCache();
        Task ForgetCacheAsync();
    }
}