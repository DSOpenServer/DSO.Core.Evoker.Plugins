using System;

namespace DSO.Core.Evoker.Plugins
{
    /// <summary>
    /// IPluginBuilder.GetFunc / GetAction'ın GENEL (her implementasyonda çalışan) hali: object[] tabanlı
    /// bir çağrı (GetFunc/GetAction ya da Invoke) üzerine tipli bir kabuk. Sandbox'ta zaten her çağrı IPC olduğundan boxing maliyeti önemsiz;
    /// in-process tarafı (InProcessPluginBuilder) mümkün olduğunda bunun yerine EvokerBuilder'ın boxing'siz
    /// derlenmiş delegate'ini kullanır, olmazsa (ör. host DTO'su ↔ plugin tipi, Task dönüşü) buraya düşer.
    /// </summary>
    internal static class PluginTypedDelegates
    {
        public static Func<T1, TResult?> Func<T1, TResult>(Func<object?[], TResult?> f)
        { return a => f(new object?[] { a }); }

        public static Func<T1, T2, TResult?> Func<T1, T2, TResult>(Func<object?[], TResult?> f)
        { return (a, c) => f(new object?[] { a, c }); }

        public static Func<T1, T2, T3, TResult?> Func<T1, T2, T3, TResult>(Func<object?[], TResult?> f)
        { return (a, c, d) => f(new object?[] { a, c, d }); }

        public static Func<T1, T2, T3, T4, TResult?> Func<T1, T2, T3, T4, TResult>(Func<object?[], TResult?> f)
        { return (a, c, d, e) => f(new object?[] { a, c, d, e }); }

        public static Action<T1> Action<T1>(Action<object?[]> f)
        { return a => f(new object?[] { a }); }

        public static Action<T1, T2> Action<T1, T2>(Action<object?[]> f)
        { return (a, c) => f(new object?[] { a, c }); }

        public static Action<T1, T2, T3> Action<T1, T2, T3>(Action<object?[]> f)
        { return (a, c, d) => f(new object?[] { a, c, d }); }

        public static Action<T1, T2, T3, T4> Action<T1, T2, T3, T4>(Action<object?[]> f)
        { return (a, c, d, e) => f(new object?[] { a, c, d, e }); }
    }
}