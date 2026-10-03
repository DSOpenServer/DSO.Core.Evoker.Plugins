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
    /// Metot SEÇİMİ artık EvokerBuilder.FindMethod ile yapılıyor (önceden burada ayrı, sadece parametre
    /// sayısına bakan bir kopya vardı) - böylece optional parametreler (VB.NET), büyük/küçük harf
    /// duyarsızlığı ve argüman tipine göre overload seçimi Invoke ile BİREBİR aynı kuralla işliyor.
    /// </summary>
    public static class EvokerBuilderDynamicInvokeExtensions
    {
        // (Type, metot adı, argüman TİPLERİ imzası, NonPublic) -> çözülmüş metot. Aynı plugin tipinden birden
        // fazla loader/instance varsa PAYLAŞILIR. Plugin unload'unda Forget ile temizlenmeli (bkz. ForgetType).
        private static readonly ConcurrentDictionary<(Type Type, string Method, string ArgSig, bool NonPublic), MethodInfo> ResolvedMethods = new();

        private static readonly MethodInfo InvokeAsyncOpenGeneric = typeof(EvokerBuilder)
            .GetMethods()
            .First(m => m.Name == nameof(EvokerBuilder.InvokeAsync) && m.IsGenericMethodDefinition);

        private static readonly ConcurrentDictionary<Type, MethodInfo> ClosedInvokeAsync = new();

        /// <summary>
        /// methodName'i çağırır; metodun void/Task/Task&lt;T&gt;/senkron olduğunu (bir kere, sonrası cache'ten)
        /// tespit edip EvokerBuilder'ın doğru üyesini kullanır.
        /// </summary>
        public static async Task<object?> InvokeDynamicAsync(this EvokerBuilder builder, string methodName, object?[] args)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("methodName boş olamaz.", nameof(methodName));

            var callArgs = (args ?? Array.Empty<object?>()).Select(a => a!).ToArray(); // EvokerBuilder object[] bekliyor
            var methodInfo = ResolveMethod(builder, methodName, callArgs);
            var returnType = methodInfo.ReturnType;

            if (returnType == typeof(void))
            {
                builder.Execute(methodName, callArgs);
                return null;
            }

            if (returnType == typeof(Task))
            {
                await builder.ExecuteAsync(methodName, callArgs).ConfigureAwait(false);
                return null;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                var closed = ClosedInvokeAsync.GetOrAdd(resultType, t => InvokeAsyncOpenGeneric.MakeGenericMethod(t));
                var taskObj = (Task)closed.Invoke(builder, new object[] { methodName, callArgs })!;
                await taskObj.ConfigureAwait(false);
                return taskObj.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(taskObj);
            }

            return builder.Invoke(methodName, callArgs);
        }

        /// <summary>InvokeDynamicAsync'in çağıracağı metodu (aynı kuralla) döner - dönüş şeklini önceden bilmek isteyenler için.</summary>
        public static MethodInfo ResolveMethod(this EvokerBuilder builder, string methodName, object?[] args)
        {
            string sig = string.Join(",", args.Select(a => a?.GetType().FullName ?? "null"));
            return ResolvedMethods.GetOrAdd((builder.Type, methodName, sig, builder.IncludeNonPublic),
                _ => builder.FindMethod(methodName, args));
        }

        /// <summary>
        /// Bu assembly'nin tiplerine (generic argüman olarak bile - ör. Task&lt;List&lt;Point&gt;&gt;) dokunan tüm
        /// cache girdilerini bırakır. Plugin'in AssemblyLoadContext'i unload edilmeden önce ŞART.
        /// </summary>
        public static void ForgetAssembly(Assembly assembly)
        {
            foreach (var key in ResolvedMethods.Keys)
                if (TypeInvolves(key.Type, assembly)) ResolvedMethods.TryRemove(key, out _);
            foreach (var key in ClosedInvokeAsync.Keys)
                if (TypeInvolves(key, assembly)) ClosedInvokeAsync.TryRemove(key, out _);
        }

        internal static bool TypeInvolves(Type t, Assembly a)
        {
            if (t.Assembly == a) return true;
            if (t.HasElementType && TypeInvolves(t.GetElementType()!, a)) return true;
            return t.IsGenericType && t.GetGenericArguments().Any(g => TypeInvolves(g, a));
        }
    }
}