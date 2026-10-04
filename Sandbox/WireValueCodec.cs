using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// TODO 8 (WireValue kısmı): bir CLR nesnesini WireValue'ya (ve geri) çevirir. Primitive'ler
    /// (bkz. WireTypeCode) SABİT boyutlu, elle yazılmış byte encoding kullanır - hiçbir genel amaçlı
    /// serializer'a uğramaz. Listede olmayan HER ŞEY (class/struct/record/collection/generic...)
    /// Complex'e düşer ve önce System.Text.Json, olmazsa DSO.Core.SchemaBinarySerializer ile
    /// serialize edilir (bkz. ComplexValueFormat) - kullanıcıyla konuşulan sıra budur.
    ///
    /// NOT: Bu, IPC (process sınırı) taşıma katmanıdır - "milyonlarca çağrı" gereksinimi GÜVENİLİR/
    /// in-process plugin'ler için EvokerBuilder'ın kendi delegate cache'ine dayanıyor (bkz.
    /// EvokerBuilderDynamicInvokeExtensions). Sandbox/IPC yolu doğası gereği process sınırı geçtiği
    /// için (context switch, pipe I/O) o seviyede bir throughput hedeflemiyor - burada öncelik
    /// DOĞRULUK ve netlik, ham hız değil.
    /// </summary>
    public static class WireValueCodec
    {
        // System.Text.Json, serialize ettiği her tipin metadata'sını KULLANDIĞI options nesnesinde cache'ler.
        // Varsayılan (global) options'ı kullansaydık, plugin'in tipleri (ör. Point) oraya kalıcı olarak
        // yerleşir ve plugin'in AssemblyLoadContext'i ASLA unload edilemezdi. Bu yüzden kendi options
        // nesnemizi kullanıyoruz ve unload'da (bkz. ForgetAssembly) yenisiyle değiştiriyoruz.
        private static JsonSerializerOptions _jsonOptions = new();
        private static JsonSerializerOptions JsonOptions => Volatile.Read(ref _jsonOptions);

        /// <summary>
        /// Bu assembly'nin tiplerine dokunan tüm cache girdilerini (SchemaBinarySerializer köprüleri + JSON metadata) bırakır.
        /// Plugin unload'u için - bkz. ManagedDotNetPluginLoader.UnloadAsync. JSON cache'i tip bazında
        /// temizlenemediği için options nesnesi komple yenilenir (diğer tipler ilk kullanımda yeniden ısınır).
        /// </summary>
        public static void ForgetAssembly(Assembly assembly)
        {
            foreach (var t in SchemaSerializers.Keys)
                if (Involves(t, assembly)) SchemaSerializers.TryRemove(t, out _);
            foreach (var t in SchemaDeserializers.Keys)
                if (Involves(t, assembly)) SchemaDeserializers.TryRemove(t, out _);
            CompiledShapeMapper.ForgetAssembly(assembly, Involves);
            Volatile.Write(ref _jsonOptions, new JsonSerializerOptions());
            ClearSystemTextJsonGlobalCaches();
        }

        // .NET 7+ System.Text.Json, aynı ayarlı options nesneleri arasında PAYLAŞILAN global statik cache'ler
        // tutar (tip metadata'sı + derlenmiş üye erişimcileri). Yeni bir options nesnesi yaratmak bunları
        // temizlemez - plugin tipleri orada kalır ve AssemblyLoadContext unload OLAMAZ (testte yakalandı).
        // System.Text.Json bu cache'leri temizlemek için Hot Reload'a bir kanca sunar: assembly üzerindeki
        // [MetadataUpdateHandler] tipinin static ClearCache(Type[]?) metodu. Aynı kancayı çağırıyoruz; bulunamazsa
        // (farklı runtime sürümü) sessizce geçilir.
        private static void ClearSystemTextJsonGlobalCaches()
        {
            try
            {
                var stj = typeof(JsonSerializer).Assembly;
                foreach (var attr in stj.GetCustomAttributes<System.Reflection.Metadata.MetadataUpdateHandlerAttribute>())
                {
                    attr.HandlerType
                        .GetMethod("ClearCache", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        ?.Invoke(null, new object?[] { null });
                }
            }
            catch
            {
                // Temizlik en iyi çaba ile yapılır; başarısızlık unload'u durdurmamalı (UnloadAsync false döner).
            }
        }

        private static bool Involves(Type t, Assembly a) =>
            t.Assembly == a
            || (t.HasElementType && Involves(t.GetElementType()!, a))
            || (t.IsGenericType && t.GetGenericArguments().Any(g => Involves(g, a)));

        public static WireValue FromObject(object? value)
        {
            if (value is null) return WireValue.Null;

            switch (value)
            {
                case bool b: return Fixed(WireTypeCode.Boolean, new[] { (byte)(b ? 1 : 0) });
                case byte by: return Fixed(WireTypeCode.Byte, new[] { by });
                case sbyte sb: return Fixed(WireTypeCode.SByte, new[] { unchecked((byte)sb) });
                case short s16: return Fixed(WireTypeCode.Int16, BitConverter.GetBytes(s16));
                case ushort u16: return Fixed(WireTypeCode.UInt16, BitConverter.GetBytes(u16));
                case int i32: return Fixed(WireTypeCode.Int32, BitConverter.GetBytes(i32));
                case uint u32: return Fixed(WireTypeCode.UInt32, BitConverter.GetBytes(u32));
                case long i64: return Fixed(WireTypeCode.Int64, BitConverter.GetBytes(i64));
                case ulong u64: return Fixed(WireTypeCode.UInt64, BitConverter.GetBytes(u64));
                case float f32: return Fixed(WireTypeCode.Single, BitConverter.GetBytes(f32));
                case double f64: return Fixed(WireTypeCode.Double, BitConverter.GetBytes(f64));
                case decimal dec: return Fixed(WireTypeCode.Decimal, EncodeDecimal(dec));
                case char ch: return Fixed(WireTypeCode.Char, BitConverter.GetBytes(ch));
                case Guid guid: return Fixed(WireTypeCode.Guid, guid.ToByteArray());
                case DateTime dt: return Fixed(WireTypeCode.DateTime, EncodeDateTime(dt));
                case DateTimeOffset dto: return Fixed(WireTypeCode.DateTimeOffset, EncodeDateTimeOffset(dto));
                case TimeSpan ts: return Fixed(WireTypeCode.TimeSpan, BitConverter.GetBytes(ts.Ticks));
                case string str: return new WireValue { TypeCode = WireTypeCode.String, Raw = Encoding.UTF8.GetBytes(str) };
                case byte[] bin: return new WireValue { TypeCode = WireTypeCode.Binary, Raw = bin };
                case Enum en:
                    // Enum'lar tip adı (plugin'e özgü, karşı tarafta olmayabilir) yerine alttaki sayısal
                    // değer olarak taşınır; karşı taraf param/dönüş tipi ipucuyla (ConvertTo) enum'a çevirir.
                    return FromObject(Convert.ChangeType(en, Enum.GetUnderlyingType(en.GetType()), System.Globalization.CultureInfo.InvariantCulture));
                default: return new WireValue { TypeCode = WireTypeCode.Complex, Raw = EncodeComplex(value, value.GetType()) };
            }
        }

        /// <summary>
        /// <paramref name="typeHint"/>: çağıranın bildiği "beklenen" tip (ör. resolve edilmiş metodun
        /// parametre/dönüş tipi). Complex için embedded TypeName ÖNCELİKLİDİR (polimorfik durumları -
        /// ör. dönüş tipi object ama gerçek tip somut bir sınıf - doğru yansıtır); embedded tip bu
        /// AppDomain'de çözülemezse (bkz. Type.GetType sınırları) typeHint'e düşülür.
        ///
        /// ÖNEMLİ (gerçek plugin senaryosunun DOĞAL sonucu): HOST tarafı, plugin'in KENDİ tanımladığı
        /// bir tipi (ör. plugin'in döndürdüğü özel bir "SonucModeli" sınıfı) genellikle YÜKLEMEMİŞTİR -
        /// sandbox'ın tüm amacı bu (host, plugin'in assembly'sini in-process yüklemek ZORUNDA değil).
        /// Bu durumda (embedded tip host'ta çözülemez VE typeHint verilmemişse):
        ///   - Json formatındaysa (yaygın durum) somut bir CLR tipine DEĞİL, bir
        ///     System.Text.Json.JsonElement'e decode edilir - çağıran GetProperty/EnumerateObject ile
        ///     alanlara dinamik olarak erişebilir (tıpkı bir REST API cevabını okur gibi).
        ///   - SchemaBinarySerializer formatındaysa (JSON'ın desteklemediği ör. döngüsel bir tip) dinamik
        ///     decode YOLU YOK (schema, bir CLR Type olmadan yorumlanamıyor) - InvalidOperationException.
        /// Worker İÇİNDE (plugin'in kendi assembly'si zaten yüklüyken) her iki formatta da tip her zaman
        /// çözülür, bu fallback sadece HOST tarafında pratikte devreye girer.
        /// </summary>
        public static object? ToObject(WireValue value, Type? typeHint = null)
        {
            switch (value.TypeCode)
            {
                case WireTypeCode.Null: return null;
                case WireTypeCode.Boolean: return value.Raw[0] != 0;
                case WireTypeCode.Byte: return value.Raw[0];
                case WireTypeCode.SByte: return unchecked((sbyte)value.Raw[0]);
                case WireTypeCode.Int16: return BitConverter.ToInt16(value.Raw);
                case WireTypeCode.UInt16: return BitConverter.ToUInt16(value.Raw);
                case WireTypeCode.Int32: return BitConverter.ToInt32(value.Raw);
                case WireTypeCode.UInt32: return BitConverter.ToUInt32(value.Raw);
                case WireTypeCode.Int64: return BitConverter.ToInt64(value.Raw);
                case WireTypeCode.UInt64: return BitConverter.ToUInt64(value.Raw);
                case WireTypeCode.Single: return BitConverter.ToSingle(value.Raw);
                case WireTypeCode.Double: return BitConverter.ToDouble(value.Raw);
                case WireTypeCode.Decimal: return DecodeDecimal(value.Raw);
                case WireTypeCode.Char: return BitConverter.ToChar(value.Raw);
                case WireTypeCode.Guid: return new Guid(value.Raw);
                case WireTypeCode.DateTime: return DecodeDateTime(value.Raw);
                case WireTypeCode.DateTimeOffset: return DecodeDateTimeOffset(value.Raw);
                case WireTypeCode.TimeSpan: return new TimeSpan(BitConverter.ToInt64(value.Raw));
                case WireTypeCode.String: return Encoding.UTF8.GetString(value.Raw);
                case WireTypeCode.Binary: return value.Raw;
                case WireTypeCode.Complex: return DecodeComplex(value.Raw, typeHint);
                default: throw new NotSupportedException($"[WireValueCodec] Bilinmeyen WireTypeCode: {value.TypeCode}");
            }
        }

        /// <summary>WireTypeCode listesinde karşılığı olan (Complex OLMAYAN) bir tip mi - enum dahil (sayı olarak taşınır).</summary>
        public static bool IsLeafType(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid)
                || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t == typeof(byte[]);
        }

        /// <summary>
        /// Tipli decode: Invoke&lt;T&gt;/GetValue&lt;T&gt; gibi çağıranın beklediği tipe çevirir.
        /// Complex'te T, JSON'ın hedef tipi olur (host tarafı aynı şekilde bir DTO olabilir, JsonElement
        /// de olabilir). Primitive'lerde sayısal genişletme/daraltma (ör. worker int, çağıran long istiyor),
        /// enum ve Nullable&lt;T&gt; desteklenir. Null + value type (Nullable olmayan) -> default(T).
        /// </summary>
        public static T? ToObject<T>(WireValue value) => (T?)ConvertTo(ToObject(value, typeof(T)), typeof(T));

        /// <summary>Bkz. ToObject&lt;T&gt; - tipi runtime'da bilinen çağıranlar için.</summary>
        public static object? ConvertTo(object? value, Type targetType)
        {
            if (targetType == typeof(object) || targetType == typeof(void)) return value;
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (value == null)
                return targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null
                    ? Activator.CreateInstance(targetType)
                    : null;

            if (targetType.IsInstanceOfType(value) || underlying.IsInstanceOfType(value)) return value;

            if (value is JsonElement je)
                return je.Deserialize(targetType, JsonOptions);

            if (underlying.IsEnum)
                return value is string es ? Enum.Parse(underlying, es, ignoreCase: true) : Enum.ToObject(underlying, value);

            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(underlying))
                return Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture);

            // Aynı şekle sahip farklı tipler (ör. plugin Point ↔ host PointDto): derlenmiş kopyalayıcı - JSON ile
            // aynı sonucu ~20-50 kat hızlı üretir; şekil emin olunamayacak kadar karmaşıksa null döner ve JSON'a düşülür.
            var mapper = CompiledShapeMapper.Get(value.GetType(), targetType);
            if (mapper != null) return mapper(value);

            // Son çare: JSON üzerinden şekil eşlemesi (ör. worker'ın gerçek Point'i host'ta yüklüyse ama
            // çağıran kendi PointDto'sunu istiyorsa).
            var json = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), JsonOptions);
            return JsonSerializer.Deserialize(json, targetType, JsonOptions);
        }

        private static WireValue Fixed(WireTypeCode code, byte[] raw) => new WireValue { TypeCode = code, Raw = raw };

        // --- Sabit boyutlu özel encode/decode'lar ---

        private static byte[] EncodeDecimal(decimal d)
        {
            var bits = decimal.GetBits(d); // 4 x int = 16 byte, sabit
            var raw = new byte[16];
            for (int i = 0; i < 4; i++)
                BitConverter.GetBytes(bits[i]).CopyTo(raw, i * 4);
            return raw;
        }

        private static decimal DecodeDecimal(byte[] raw)
        {
            var bits = new int[4];
            for (int i = 0; i < 4; i++)
                bits[i] = BitConverter.ToInt32(raw, i * 4);
            return new decimal(bits);
        }

        // DateTime.Kind (Utc/Local/Unspecified) round-trip için ayrıca taşınıyor - Ticks tek başına
        // bunu kaybeder (ör. bir plugin'in yerel saat mi UTC mi döndürdüğü karışabilir).
        private static byte[] EncodeDateTime(DateTime dt)
        {
            var raw = new byte[9];
            BitConverter.GetBytes(dt.Ticks).CopyTo(raw, 0);
            raw[8] = (byte)dt.Kind;
            return raw;
        }

        private static DateTime DecodeDateTime(byte[] raw)
        {
            long ticks = BitConverter.ToInt64(raw, 0);
            var kind = (DateTimeKind)raw[8];
            return new DateTime(ticks, kind);
        }

        private static byte[] EncodeDateTimeOffset(DateTimeOffset dto)
        {
            var raw = new byte[10];
            BitConverter.GetBytes(dto.Ticks).CopyTo(raw, 0);
            BitConverter.GetBytes((short)dto.Offset.TotalMinutes).CopyTo(raw, 8);
            return raw;
        }

        private static DateTimeOffset DecodeDateTimeOffset(byte[] raw)
        {
            long ticks = BitConverter.ToInt64(raw, 0);
            short offsetMinutes = BitConverter.ToInt16(raw, 8);
            return new DateTimeOffset(ticks, TimeSpan.FromMinutes(offsetMinutes));
        }

        // --- Complex: [FormatId byte][TypeNameLen int][TypeName UTF8][PayloadLen int][Payload] ---

        private static byte[] EncodeComplex(object value, Type runtimeType)
        {
            ComplexValueFormat format;
            byte[] payload;
            try
            {
                payload = JsonSerializer.SerializeToUtf8Bytes(value, runtimeType, JsonOptions);
                format = ComplexValueFormat.Json;
            }
            catch (Exception)
            {
                // JSON desteklemiyor (döngüsel referans, desteklenmeyen üye tipi vb.) -
                // SchemaBinarySerializer'a düş (kullanıcıyla konuşulan sıra: önce JSON, olmazsa bu).
                payload = GetSchemaSerializer(runtimeType)(value);
                format = ComplexValueFormat.SchemaBinarySerializer;
            }

            string typeName = runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name;
            byte[] typeNameBytes = Encoding.UTF8.GetBytes(typeName);

            var raw = new byte[1 + 4 + typeNameBytes.Length + 4 + payload.Length];
            int offset = 0;
            raw[offset++] = (byte)format;
            BitConverter.GetBytes(typeNameBytes.Length).CopyTo(raw, offset); offset += 4;
            typeNameBytes.CopyTo(raw, offset); offset += typeNameBytes.Length;
            BitConverter.GetBytes(payload.Length).CopyTo(raw, offset); offset += 4;
            payload.CopyTo(raw, offset);
            return raw;
        }

        private static object? DecodeComplex(byte[] raw, Type? typeHint)
        {
            int offset = 0;
            var format = (ComplexValueFormat)raw[offset++];
            int typeNameLen = BitConverter.ToInt32(raw, offset); offset += 4;
            string typeName = Encoding.UTF8.GetString(raw, offset, typeNameLen); offset += typeNameLen;
            int payloadLen = BitConverter.ToInt32(raw, offset); offset += 4;
            var payload = new byte[payloadLen];
            Array.Copy(raw, offset, payload, 0, payloadLen);

            // Embedded TypeName ÖNCELİKLİ (bkz. ToObject'in üstündeki açıklama) - farklı bir yükleme
            // bağlamından geliyorsa (ör. plugin'in kendi assembly'si host'ta hiç yüklü değilse)
            // Type.GetType başarısız olabilir, bu durumda typeHint'e düşülüyor.
            // ...AMA çağıran belirli bir tip İSTİYORSA (typeHint, ör. Invoke<MyDto>) ve embedded tip ona
            // atanamıyorsa (ör. worker plugin'in kendi "Point"ini, host kendi "PointDto"sunu istiyor), istenen
            // tip kazanır - JSON aynı şekle sahip host tarafı bir DTO'ya doğrudan deserialize edilir.
            Type? embeddedType = SafeResolveType(typeName);
            Type? resolvedType = embeddedType != null && (typeHint == null || typeHint.IsAssignableFrom(embeddedType))
                ? embeddedType
                : typeHint;
            if (resolvedType == typeof(object)) resolvedType = embeddedType; // object "istek" değil, ipucu yok demek

            if (resolvedType == typeof(JsonElement) && format == ComplexValueFormat.Json)
                return JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);

            if (resolvedType == null)
            {
                // Somut bir CLR tipi YOK - bkz. ToObject'in üstündeki "ÖNEMLİ" notu. Json için dinamik
                // (JsonElement) decode'a düşüyoruz; SchemaBinarySerializer bir Type olmadan yorumlanamaz.
                if (format == ComplexValueFormat.Json)
                    return JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);

                throw new InvalidOperationException(
                    $"[WireValueCodec] Complex (SchemaBinarySerializer formatlı) değer için tip çözülemedi " +
                    $"(embedded '{typeName}' bu tarafta yüklü değil, typeHint verilmemiş). Bu format bir CLR " +
                    "Type olmadan dinamik decode edilemez - typeHint geçin ya da bu tarafta da plugin " +
                    "assembly'sini yükleyin.");
            }

            return format switch
            {
                ComplexValueFormat.Json => JsonSerializer.Deserialize(payload, resolvedType, JsonOptions),
                ComplexValueFormat.SchemaBinarySerializer => GetSchemaDeserializer(resolvedType)(payload),
                _ => throw new NotSupportedException($"[WireValueCodec] Bilinmeyen ComplexValueFormat: {format}")
            };
        }

        private static Type? SafeResolveType(string assemblyQualifiedName)
        {
            try { return Type.GetType(assemblyQualifiedName, throwOnError: false); }
            catch { return null; }
        }

        // --- SchemaBinarySerializer köprüsü ---
        // Serialize<T>(T value, options) - T normal bir generic parametre (Span DEĞİL), bu yüzden
        // sıradan reflection Invoke ile çağrılabiliyor.
        private static readonly ConcurrentDictionary<Type, Func<object, byte[]>> SchemaSerializers = new();

        private static Func<object, byte[]> GetSchemaSerializer(Type type) =>
            SchemaSerializers.GetOrAdd(type, BuildSchemaSerializer);

        private static Func<object, byte[]> BuildSchemaSerializer(Type type)
        {
            var method = typeof(global::DSO.Core.SchemaBinarySerializer.SchemaBinarySerializer)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(global::DSO.Core.SchemaBinarySerializer.SchemaBinarySerializer.Serialize)
                    && m.IsGenericMethodDefinition
                    && m.ReturnType == typeof(byte[]))
                .MakeGenericMethod(type);

            return value => (byte[])method.Invoke(null, new object?[] { value, null })!;
        }

        // Deserialize<T>(ReadOnlySpan<byte> data, options) - ReadOnlySpan<byte> bir "ref struct",
        // object[] içine BOXLANAMAZ, dolayısıyla MethodInfo.Invoke ile ÇAĞRILAMAZ (derleme zamanı
        // hatası/desteklenmiyor). Çözüm: Expression Tree ile TEK SEFERLİK derlenmiş bir delegate -
        // ReadOnlySpan<byte>, byte[]'den ARA DEĞİŞKEN olmadan (doğrudan Expression.New ile) çağrı
        // argümanı olarak inşa ediliyor; bu, LINQ Expression derleyicisinin izin verdiği bir kalıp
        // (bir Parameter/Variable olarak Span DEĞİL, sadece bir çağrı argümanı ifadesi olarak kullanılıyor).
        private static readonly ConcurrentDictionary<Type, Func<byte[], object?>> SchemaDeserializers = new();

        private static Func<byte[], object?> GetSchemaDeserializer(Type type) =>
            SchemaDeserializers.GetOrAdd(type, BuildSchemaDeserializer);

        private static Func<byte[], object?> BuildSchemaDeserializer(Type type)
        {
            var method = typeof(global::DSO.Core.SchemaBinarySerializer.SchemaBinarySerializer)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(global::DSO.Core.SchemaBinarySerializer.SchemaBinarySerializer.Deserialize) && m.IsGenericMethodDefinition)
                .MakeGenericMethod(type);

            var dataParam = Expression.Parameter(typeof(byte[]), "data");
            var spanCtor = typeof(ReadOnlySpan<byte>).GetConstructor(new[] { typeof(byte[]) })!;
            var spanExpr = Expression.New(spanCtor, dataParam);
            var optionsExpr = Expression.Constant(null, typeof(global::DSO.Core.SchemaBinarySerializer.SchemaBinarySerializerOptions));
            var call = Expression.Call(method, spanExpr, optionsExpr);
            var body = Expression.Convert(call, typeof(object));

            return Expression.Lambda<Func<byte[], object?>>(body, dataParam).Compile();
        }
    }
}