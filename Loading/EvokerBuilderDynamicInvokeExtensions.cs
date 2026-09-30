using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DSO.Core.Evoker;

namespace DSO.Core.Evoker.Plugins.Loading
{
    /// <summary>
    /// "Metodun dönüş şekli (void / Task / Task&lt;T&gt; / senkron) derleme zamanında BİLİNMİYOR,
    /// sadece isimle çağırıyoruz" problemi SADECE plugin senaryosuna özgü - EvokerBuilder'ın genel
    /// kullanıcılarını ilgilendirmez, bu yüzden DSO.Core.Evoker'ın kendisine değil, buraya
    /// (DSO.Core.Evoker.Plugins) bir extension olarak konuldu.
    ///
    /// Loader'ın (ManagedDotNetPluginLoader) görevi SADECE güvenli yükleme - yükleme bitince
    /// topu tamamen buraya, EvokerBuilder'a bağlı bu extension'a bırakıyor.
    /// </summary>
    public static class EvokerBuilderDynamicInvokeExtensions
    {
        // EvokerBuilder.Type'a göre (instance'a göre DEĞİL) cache'leniyor - tıpkı EvokerBuilder'ın
        // kendi statik Cache'inin Type kimliğine göre tutulması gibi. Aynı plugin tipinden birden
        // fazla loader/instance varsa bu cache'i PAYLAŞIRLAR.
        // IncludeNonPublic de key'e dahil: aynı Type için bir loader includeNonPublic:false, başka
        // bir loader includeNonPublic:true ile oluşturulmuş olabilir - ikisi FARKLI görünürlükte metot
        // arıyor, aynı cache girdisini PAYLAŞMAMALI (aksi halde ilk çözülen "şekil" ikincisine sızar).
        private static readonly ConcurrentDictionary<(Type Type, string Method, int ArgCount, bool IncludeNonPublic), MethodInfo> ResolvedMethods = new();

        /// <summary>
        /// methodName'i çağırır; metodun void/Task/Task&lt;T&gt;/senkron olduğunu reflection ile
        /// (bir kere, sonrası cache'ten) tespit edip EvokerBuilder'ın doğru üyesini kullanır.
        /// </summary>
        public static async Task<object?> InvokeDynamicAsync(this EvokerBuilder builder, string methodName, object?[] args)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("methodName boş olamaz.", nameof(methodName));

            var objArgs = args ?? Array.Empty<object?>();
            var callArgs = objArgs.Select(a => a!).ToArray(); // EvokerBuilder object[] bekliyor

            var methodInfo = ResolvedMethods.GetOrAdd(
                (builder.Type, methodName, callArgs.Length, builder.IncludeNonPublic),
                key => ResolveMethodForReturnType(key.Type, key.Method, key.ArgCount, key.IncludeNonPublic));

            // void
            if (methodInfo.ReturnType == typeof(void))
            {
                builder.Execute(methodName, callArgs);
                return null;
            }

            // Task (generic olmayan)
            if (methodInfo.ReturnType == typeof(Task))
            {
                await builder.ExecuteAsync(methodName, callArgs).ConfigureAwait(false);
                return null;
            }

            // Task<T> - T derleme zamanında bilinmiyor, reflection ile generic InvokeAsync<T>'ye bağlanıyoruz
            if (methodInfo.ReturnType.IsGenericType &&
                methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = methodInfo.ReturnType.GetGenericArguments()[0];
                var invokeAsyncOpenGeneric = typeof(EvokerBuilder)
                    .GetMethods()
                    .First(m => m.Name == nameof(EvokerBuilder.InvokeAsync) && m.IsGenericMethodDefinition);
                var invokeAsyncClosed = invokeAsyncOpenGeneric.MakeGenericMethod(resultType);

                var taskObj = (Task)invokeAsyncClosed.Invoke(builder, new object[] { methodName, callArgs })!;
                await taskObj.ConfigureAwait(false);

                var resultProperty = taskObj.GetType().GetProperty(nameof(Task<object>.Result))!;
                return resultProperty.GetValue(taskObj);
            }

            // düz senkron dönüş değeri
            return builder.Invoke(methodName, callArgs);
        }

        /// <summary>
        /// Sadece ReturnType'ı öğrenmek için basit bir overload seçimi (isim + parametre SAYISI).
        /// EvokerBuilder.GetMethodInfo private olduğu için burada aynı basit stratejiyi tekrar
        /// ediyoruz - amaç tam overload çözümü değil, hangi "şekil" ile karşı karşıya olduğumuzu
        /// belirlemek. includeNonPublic, builder'ın kendi ayarıyla (builder.IncludeNonPublic) BİREBİR
        /// aynı olmalı - aksi halde burada görünmeyen (private) bir metot, EvokerBuilder.Invoke/Execute
        /// tarafında GetMethodInfo'nun kendi flags'iyle bulunabilir ama "şekli" burada hiç tespit
        /// edilemediği için InvokeDynamicAsync baştan MissingMethodException ile patlardı. NOT (bilinen
        /// sınırlama): overload seçimi argüman SAYISINA göre - VB'nin Optional parametreleriyle
        /// (sağlanan argüman sayısı &lt; parametre sayısı) ilgili EvokerBuilder.GetMethodInfo'daki
        /// bilinen açık burada da geçerli.
        /// </summary>
        private static MethodInfo ResolveMethodForReturnType(Type type, string methodName, int argCount, bool includeNonPublic)
        {
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;

            var candidates = type.GetMethods(flags).Where(m => m.Name == methodName).ToList();

            if (candidates.Count == 0)
                throw new MissingMethodException($"'{type.FullName}' üzerinde '{methodName}' metodu bulunamadı.");

            var byCount = candidates.FirstOrDefault(m => m.GetParameters().Length == argCount);
            return byCount ?? candidates[0];
        }
    }
}