using System;
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

        // --- Cache ---
        /// <summary>Plugin tipinin DynamicEntityAccessor cache'ini temizler (sandbox'ta worker içinde).</summary>
        void ForgetCache();
        Task ForgetCacheAsync();
    }
}