namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// Tek bir argüman ya da dönüş değerinin tel (wire) üzerindeki temsili.
    /// Primitive tipler için Raw doğrudan ham byte'ları taşır (fixed-size, TypeCode'a göre encode/decode edilir).
    /// Complex için Raw, [FormatId byte][TypeName][payload] şeklinde iç yapıya sahiptir
    /// (bkz. TODO 8 - IpcWriter/IpcReader.WriteComplex/ReadComplex).
    /// </summary>
    public readonly struct WireValue
    {
        public WireTypeCode TypeCode { get; init; }

        /// <summary>Encode edilmiş ham veri. Null (WireTypeCode.Null) için boş dizi.</summary>
        public byte[] Raw { get; init; }

        public static readonly WireValue Null = new WireValue
        {
            TypeCode = WireTypeCode.Null,
            Raw = System.Array.Empty<byte>()
        };
    }
}
