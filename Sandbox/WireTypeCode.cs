namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// WireValue'nun taşıdığı değerin tipi. Listedeki her şey "yaprak" (leaf) primitive'dir ve
    /// sabit/basit bir binary encoding ile doğrudan yazılır (boxing'siz, DynamicEntityAccessor'daki
    /// generic disiplinle aynı ruhta).
    ///
    /// KURAL: Runtime tipi bu listedeki bir primitive'e TAM eşleşmiyorsa (class, struct, record,
    /// collection, generic T'nin gerçek tipi ne olursa olsun) -> Complex. İç yapısına bakılmaz,
    /// tamamı JSON/SchemaBinarySerializer'a devredilen tek bir opak blob'tur (bkz. ComplexValueFormat).
    /// </summary>
    public enum WireTypeCode : byte
    {
        Null = 0,
        Boolean = 1,
        Byte = 2,
        SByte = 3,
        Int16 = 4,
        UInt16 = 5,
        Int32 = 6,
        UInt32 = 7,
        Int64 = 8,
        UInt64 = 9,
        Single = 10,
        Double = 11,
        Decimal = 12,
        Char = 13,
        String = 14,
        Guid = 15,
        DateTime = 16,
        DateTimeOffset = 17,
        TimeSpan = 18,
        Binary = 19,

        /// <summary>Yukarıdaki listede olmayan HER ŞEY. Bkz. bu dosyanın üstündeki KURAL notu.</summary>
        Complex = 255
    }
}
