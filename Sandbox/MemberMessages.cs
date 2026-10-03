namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>MemberRequest'in yaptığı iş.</summary>
    public enum MemberOperation : byte
    {
        /// <summary>Property (yoksa aynı isimli field) değerini oku - worker'daki builder.GetValue.</summary>
        Get = 1,

        /// <summary>Property (yoksa aynı isimli field) değerini yaz - worker'daki builder.SetValue.</summary>
        Set = 2,

        /// <summary>Worker'daki builder.ForgetCache() (DynamicEntityAccessor cache'i). MemberName/Value kullanılmaz.</summary>
        ForgetCache = 3,

        /// <summary>MemberName = event adı, Value = Int32 abonelik id'si (host belirler). Sonrasında worker EventRaised gönderir.</summary>
        Subscribe = 4,

        /// <summary>Value = Int32 abonelik id'si.</summary>
        Unsubscribe = 5
    }

    /// <summary>
    /// Host -> worker: worker içindeki EvokerBuilder'ın property/field/cache yüzeyine erişim. Metot
    /// çağrısından (Invoke) farklı olarak Resolve adımı YOK - üye adı her istekte taşınır; worker tarafı
    /// üye bazında (isim -> tip-özel getter/setter) kendi cache'ini tutar. Cevap: InvokeReply.
    /// </summary>
    public sealed class MemberRequest
    {
        public long CorrelationId { get; init; }
        public MemberOperation Operation { get; init; }
        public string MemberName { get; init; } = "";

        /// <summary>Sadece Set için anlamlı - diğerlerinde WireValue.Null.</summary>
        public WireValue Value { get; init; } = WireValue.Null;
    }
}