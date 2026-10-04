using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
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
    /// Metot SEÇİMİ EvokerBuilder.FindMethod ile yapılıyor - optional parametreler (VB.NET), büyük/küçük harf
    /// duyarsızlığı ve argüman tipine göre overload seçimi Invoke ile BİREBİR aynı kuralla işliyor.
    ///
    /// PERFORMANS: her (tip, metot, argüman tip imzası) için BİR KEZ bir "plan" çıkarılır: dönüş şekli ve
    /// Task&lt;T&gt; ise sonucu okuyan DERLENMİŞ erişimci. Eskiden her çağrıda imza string'i üretiliyor,
    /// argüman dizisi LINQ ile kopyalanıyor, Task&lt;T&gt; yolu MethodInfo.Invoke + GetProperty("Result")
    /// reflection'ı ile yürüyordu. Senkron metotlar artık async state machine'e de girmiyor.
    /// </summary>
    public static class EvokerBuilderDynamicInvokeExtensions
    {
        private enum ReturnShape { Void, Sync, Task, TaskOfT }

        private sealed class DynPlan
        {
            public readonly MethodInfo Method;
            public readonly ReturnShape Shape;
            public readonly Func<Task, object?>? TaskResult;
            public DynPlan(MethodInfo method, ReturnShape shape, Func<Task, object?>? taskResult)
            {
                Method = method; Shape = shape; TaskResult = taskResult;
            }
        }

        // (Type, metot adı, argüman TİPLERİ, NonPublic) -> plan. Aynı plugin tipinden birden fazla
        // loader/instance varsa PAYLAŞILIR. Plugin unload'unda ForgetAssembly ile temizlenmeli.
        private static readonly ConcurrentDictionary<(Type Type, string Method, ArgTypeKey Args, bool NonPublic), DynPlan> Plans = new();

        // Task<X> -> derlenmiş "((Task<X>)t).Result" (boxing ile object). Reflection'sız sonuç okuma.
        private static readonly ConcurrentDictionary<Type, Func<Task, object?>> TaskResultReaders = new();

        private static readonly Task<object?> NullResult = Task.FromResult<object?>(null);

        /// <summary>
        /// methodName'i çağırır; metodun void/Task/Task&lt;T&gt;/senkron olduğunu (bir kere, sonrası cache'ten)
        /// tespit edip uygun şekilde çağırır. Senkron metotlarda dönen Task zaten tamamlanmıştır.
        /// Hatalar her zaman dönen Task üzerinden gelir (senkron fırlatmaz).
        /// </summary>
        public static Task<object?> InvokeDynamicAsync(this EvokerBuilder builder, string methodName, object?[] args)
        {
            try
            {
                if (builder == null) throw new ArgumentNullException(nameof(builder));
                if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("methodName boş olamaz.", nameof(methodName));

                // object?[] ile object[] çalışma zamanında AYNI tip - kopya gerekmiyor (EvokerBuilder diziyi değiştirmez).
                var callArgs = (object[])(args ?? Array.Empty<object?>());
                var plan = GetPlan(builder, methodName, callArgs);

                switch (plan.Shape)
                {
                    case ReturnShape.Void:
                        builder.Execute(methodName, callArgs);
                        return NullResult;
                    case ReturnShape.Sync:
                        return Task.FromResult(builder.Invoke(methodName, callArgs));
                    case ReturnShape.Task:
                        return AwaitTask(builder.Invoke<Task>(methodName, callArgs), null);
                    default:
                        return AwaitTask(builder.Invoke<Task>(methodName, callArgs), plan.TaskResult);
                }
            }
            catch (Exception ex)
            {
                return Task.FromException<object?>(ex);
            }
        }

        private static async Task<object?> AwaitTask(Task? task, Func<Task, object?>? reader)
        {
            if (task == null) return null; // metot null Task döndürdü (eski davranış: sonuç null)
            await task.ConfigureAwait(false);
            return reader?.Invoke(task);
        }

        /// <summary>InvokeDynamicAsync'in çağıracağı metodu (aynı kuralla) döner - dönüş şeklini önceden bilmek isteyenler için.</summary>
        public static MethodInfo ResolveMethod(this EvokerBuilder builder, string methodName, object?[] args)
            => GetPlan(builder, methodName, args).Method;

        private static DynPlan GetPlan(EvokerBuilder builder, string methodName, object?[] args)
        {
            var key = (builder.Type, methodName, ArgTypeKey.From(args), builder.IncludeNonPublic);
            if (Plans.TryGetValue(key, out var plan)) return plan;
            return Plans.GetOrAdd(key, BuildPlan(builder.FindMethod(methodName, args)));
        }

        private static DynPlan BuildPlan(MethodInfo method)
        {
            var rt = method.ReturnType;
            if (rt == typeof(void)) return new DynPlan(method, ReturnShape.Void, null);
            if (rt == typeof(Task)) return new DynPlan(method, ReturnShape.Task, null);
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
                return new DynPlan(method, ReturnShape.TaskOfT, TaskResultReaders.GetOrAdd(rt, BuildTaskResultReader));
            // Task'tan türeyen başka bir tip (nadir) ya da ValueTask vb.: değer olarak döner (eski davranış).
            return new DynPlan(method, ReturnShape.Sync, null);
        }

        private static Func<Task, object?> BuildTaskResultReader(Type taskOfT)
        {
            var p = Expression.Parameter(typeof(Task), "t");
            var result = Expression.Property(Expression.Convert(p, taskOfT), nameof(Task<object>.Result));
            return Expression.Lambda<Func<Task, object?>>(Expression.Convert(result, typeof(object)), p).Compile();
        }

        /// <summary>
        /// Bu assembly'nin tiplerine (generic argüman olarak bile - ör. Task&lt;List&lt;Point&gt;&gt;, ya da argüman
        /// tipi olarak) dokunan tüm cache girdilerini bırakır. Plugin'in AssemblyLoadContext'i unload edilmeden önce ŞART.
        /// </summary>
        public static void ForgetAssembly(Assembly assembly)
        {
            foreach (var key in Plans.Keys)
                if (TypeInvolves(key.Type, assembly) || key.Args.Involves(t => TypeInvolves(t, assembly))
                    || (Plans.TryGetValue(key, out var p) && TypeInvolves(p.Method.ReturnType, assembly)))
                    Plans.TryRemove(key, out _);
            foreach (var key in TaskResultReaders.Keys)
                if (TypeInvolves(key, assembly)) TaskResultReaders.TryRemove(key, out _);
        }

        internal static bool TypeInvolves(Type t, Assembly a)
        {
            if (t.Assembly == a) return true;
            if (t.HasElementType && TypeInvolves(t.GetElementType()!, a)) return true;
            return t.IsGenericType && t.GetGenericArguments().Any(g => TypeInvolves(g, a));
        }
    }
}