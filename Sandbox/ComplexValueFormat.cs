namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// WireTypeCode.Complex payload'ının hangi serializer ile üretildiği.
    /// Karşı taraf bu ID sayesinde deneme-yanılma yapmadan doğru deserializer'ı çağırır.
    /// Kural: önce System.Text.Json dene, desteklenmeyen/döngüsel bir tip ise SchemaBinarySerializer'a düş.
    /// </summary>
    public enum ComplexValueFormat : byte
    {
        Json = 0,
        SchemaBinarySerializer = 1
    }
}
