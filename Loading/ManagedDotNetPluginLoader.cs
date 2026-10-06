using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DSO.Core.Evoker;
using DSO.Core.Evoker.Plugins.Sandbox;
using DSO.Core.Evoker.Plugins.Scanning;

namespace DSO.Core.Evoker.Plugins.Loading
{
    /// <summary>
    /// In-process yol: plugin DLL'i host'un kendi process'ine, ama KENDİNE AİT, kaldırılabilir
    /// (collectible) bir <see cref="PluginLoadContext"/>'e yüklenir. Böylece:
    ///   - plugin'in bağımlılıkları host'unkilerle çakışmaz (her plugin kendi sürümlerini kullanır),
    ///   - <see cref="UnloadAsync"/> ile plugin bellekten GERÇEKTEN atılır ve DLL dosyası serbest kalır
    ///     (sonra yeni sürümü yüklenebilir) - uygulamayı yeniden başlatmadan.
    /// Crash izolasyonu YOKTUR (aynı process) - sadece admin'in "bu DLL'e güveniyorum" dediği durumda.
    ///
    /// Bir loader BİR plugin instance'ına bağlıdır. Görevi güvenli yükleme ve boşaltma; çağırma
    /// sorumluluğu Builder'da (public EvokerBuilder) ya da Builder.AsPluginBuilder()'da.
    /// </summary>
    public sealed class ManagedDotNetPluginLoader : IPluginLoader
    {
        public PluginKind SupportedKind => PluginKind.ManagedDotNet;

        public object? Instance { get; private set; }

        /// <summary>
        /// Load sonrası dolu, instance'a SetInstance ile bağlı. PUBLIC: loader'ın görevi bitti, bundan
        /// sonrası EvokerBuilder'ın. UnloadAsync sonrası null.
        /// </summary>
        public EvokerBuilder? Builder { get; private set; }

        /// <summary>Plugin'in yüklendiği context (tanılama). UnloadAsync sonrası null.</summary>
        public PluginLoadContext? LoadContext { get; private set; }

        /// <summary>Plugin'in ana assembly'si. UnloadAsync sonrası null.</summary>
        public Assembly? PluginAssembly { get; private set; }

        public bool IsUnloaded { get; private set; }

        /// <summary>
        /// UnloadAsync sonrası context'in GC tarafından gerçekten toplanıp toplanmadığı (anlık kontrol,
        /// GC tetiklemez). UnloadAsync false döndüyse, referanslar bırakıldıktan sonra bu tekrar kontrol edilebilir.
        /// </summary>
        public bool IsMemoryReleased => IsUnloaded && _contextRef != null && !_contextRef.IsAlive;

        private WeakReference? _contextRef;

        /// <summary>
        /// Plugin DLL'ini kendi PluginLoadContext'ine yükler, tipi bulur (ignoreCase - VB.NET) ve BİR instance oluşturur.
        /// <paramref name="constructorArgs"/>: constructor argümanları JSON olarak (dizi ya da parametre adlarıyla nesne);
        /// verilmezse parametresiz ya da tüm parametreleri optional olan constructor kullanılır. Constructor seçimi metot
        /// seçimiyle aynı kural (bkz. DSO.Core.Evoker.Commands.EvokerTarget.CreateInstance). includeNonPublic: bkz.
        /// IPluginLoader. Bir loader üzerinde bir kez çağrılabilir.
        /// </summary>
        public Task LoadInProcessAsync(string filePath, string typeFullName, bool includeNonPublic = false,
            System.Text.Json.JsonElement? constructorArgs = null)
        {
            if (Instance != null || IsUnloaded)
                throw new InvalidOperationException(
                    "Bu loader zaten kullanıldı - her plugin yüklemesi için yeni bir ManagedDotNetPluginLoader oluşturun.");
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("filePath boş olamaz.", nameof(filePath));
            if (string.IsNullOrWhiteSpace(typeFullName))
                throw new ArgumentException("typeFullName boş olamaz.", nameof(typeFullName));

            var context = new PluginLoadContext(filePath);
            try
            {
                var assembly = context.LoadFromAssemblyPath(context.PluginPath);

                var type = assembly.GetType(typeFullName, throwOnError: false, ignoreCase: true)
                    ?? throw new TypeLoadException(
                        $"'{typeFullName}' tipi '{filePath}' içinde bulunamadı (case-insensitive arama dahil).");

                object instance;
                try
                {
                    instance = DSO.Core.Evoker.Commands.EvokerTarget.CreateInstance(type, constructorArgs, includeNonPublic);
                }
                catch (DSO.Core.Evoker.Commands.EvokerCommandException ex) when (ex.Code != DSO.Core.Evoker.Commands.EvokerErrorCodes.TargetException)
                {
                    // Uyan constructor yok / argüman çevrilemedi - çağıran hatası (eskisiyle aynı tip).
                    throw new MissingMethodException($"'{type.FullName}' oluşturulamadı: {ex.Message}", ex);
                }
                catch (DSO.Core.Evoker.Commands.EvokerCommandException ex)
                {
                    // Plugin'in kendi constructor'ı hata verdi - asıl exception iç hata olarak korunur.
                    throw new PluginInvocationException(".ctor", ex.TargetExceptionType, ex.Message, ex.InnerException);
                }

                LoadContext = context;
                PluginAssembly = assembly;
                Instance = instance;
                Builder = new EvokerBuilder(type, includeNonPublic).SetInstance(instance);
                _contextRef = new WeakReference(context);
                return Task.CompletedTask;
            }
            catch
            {
                // Yarım kalan yüklemeyi de boşalt - dosya kilitli kalmasın.
                PurgeCaches(context);
                context.Unload();
                throw;
            }
        }

        /// <summary>
        /// Plugin'i bellekten atar:
        ///   1) plugin tiplerine referans tutan TÜM statik cache'leri temizler (EvokerBuilder,
        ///      DynamicEntityAccessor, InvokeDynamicAsync, WireValueCodec/JSON) - bunlar temizlenmezse
        ///      context ASLA toplanamaz,
        ///   2) Instance/Builder referanslarını bırakır, context.Unload() çağırır,
        ///   3) GC ile context'in gerçekten toplandığını <paramref name="timeoutMs"/> içinde doğrular.
        ///
        /// true = plugin bellekten gitti, DLL serbest. false = hâlâ bir yerden referans tutuluyor:
        /// ÇAĞIRANIN elinde kalan bir plugin nesnesi, Builder/AsPluginBuilder(), GetFunc/GetAction ile
        /// alınmış delegate, kapatılmamış event aboneliği ya da plugin'in kendi başlattığı ve bitmeyen bir
        /// thread/timer. Bunlar bırakılınca context yine de toplanır (Unload geri alınmaz).
        /// </summary>
        public async Task<bool> UnloadAsync(int timeoutMs = 10_000)
        {
            if (IsUnloaded) return _contextRef == null || !_contextRef.IsAlive;
            if (LoadContext == null) throw new InvalidOperationException("Yüklenmemiş bir loader boşaltılamaz.");

            IsUnloaded = true;
            UnloadCore();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (_contextRef!.IsAlive && sw.ElapsedMilliseconds < timeoutMs)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (!_contextRef.IsAlive) break;
                await Task.Delay(50).ConfigureAwait(false);
            }
            return !_contextRef.IsAlive;
        }

        // Ayrı ve inline EDİLMEYEN metot: context/assembly/instance'a işaret eden yerel değişkenler bu
        // metodun stack frame'inde kalır ve metot dönünce GC için görünmez olur.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UnloadCore()
        {
            var context = LoadContext!;
            PurgeCaches(context);

            Instance = null;
            Builder = null;
            PluginAssembly = null;
            LoadContext = null;
            context.Unload();
        }

        /// <summary>Bu context'teki assembly'lerin tiplerine dokunan tüm statik cache'leri bırakır.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PurgeCaches(PluginLoadContext context)
        {
            foreach (var asm in context.Assemblies)
            {
                foreach (var t in SafeGetTypes(asm))
                {
                    EvokerBuilder.ForgetType(t);
                    DynamicEntityAccessor.ForgetType(t);
                }
                EvokerBuilderDynamicInvokeExtensions.ForgetAssembly(asm);
                WireValueCodec.ForgetAssembly(asm);
            }
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
            catch { return Array.Empty<Type>(); }
        }

        /// <summary>
        /// IPluginLoader sözleşmesi için ince bir köprü - loader-türü bilinmeyen genel çağırıcılar
        /// için. Managed tarafta bunun ötesine geçmek isteyen kod doğrudan Builder'ı kullanmalı.
        /// </summary>
        public Task<object?> InvokeAsync(string methodName, object?[] args)
        {
            if (Builder == null)
                throw new InvalidOperationException(IsUnloaded ? "Plugin boşaltıldı (UnloadAsync)." : "Önce LoadInProcessAsync çağrılmalı.");

            return Builder.InvokeDynamicAsync(methodName, args);
        }
    }
}