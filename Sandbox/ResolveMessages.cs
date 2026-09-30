using System.Collections.Generic;

namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// Bir (TypeName, MethodName, argüman şekli) üçlüsünü BİR KEZ çözüp geriye ucuz bir
    /// int handle almak için. Sonraki milyonlarca Invoke bu handle'ı taşır, string taşımaz.
    /// </summary>
    public sealed class ResolveRequest
    {
        public string TypeName { get; init; } = "";
        public string MethodName { get; init; } = "";

        /// <summary>Overload seçimi için argüman tip kodları (EvokerBuilder.GetMethodInfo'ya yardımcı).</summary>
        public IReadOnlyList<WireTypeCode> ArgTypeCodes { get; init; } = new List<WireTypeCode>();
    }

    public sealed class ResolveReply
    {
        public bool Success { get; init; }

        /// <summary>Success ise geçerli - sonraki InvokeRequest.MethodHandle burada kullanılır.</summary>
        public int MethodHandle { get; init; }

        /// <summary>Success değilse hata açıklaması (örn. metot bulunamadı, overload belirsiz).</summary>
        public string? Error { get; init; }
    }
}
