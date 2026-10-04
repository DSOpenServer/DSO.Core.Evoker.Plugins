using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// TODO 7/8 (mesaj kısmı): Hello/Resolve/Invoke/Fault/... mesajlarının payload'ını encode/decode eder.
    /// WireValue'nun kendi encode/decode'u için bkz. WireValueCodec.
    ///
    /// TEL FORMATI DEĞİŞMEDİ (BinaryWriter/BinaryReader uyumlu: little-endian sayılar, 7-bit uzunluk önekli UTF-8
    /// string'ler) - eski/yeni host ve worker birbirini okur.
    ///
    /// PERFORMANS (optimizasyon turu):
    ///   - Encode: her mesaj için yeni MemoryStream + BinaryWriter + ToArray yerine, IpcWriter thread başına tek bir
    ///     tamponu yeniden kullanır ve frame başlığını da AYNI tampona yazar (bkz. IpcWriter.WriteEncodedAsync).
    ///     Burada her mesaj tipinin "BinaryWriter'a yaz" hali (WriteX) var; EncodeX (byte[] döner) geriye uyumluluk için.
    ///   - Decode: MemoryStream + BinaryReader yerine allocation'sız bir span okuyucu (SpanReader).
    /// </summary>
    public static class IpcMessageCodec
    {
        // ======================= Encode (BinaryWriter'a) =======================

        internal static void WriteHello(BinaryWriter bw, bool success, string? typeFullName, string? error, int protocolVersion)
        {
            bw.Write(success);
            bw.Write(success ? (typeFullName ?? "") : (error ?? ""));
            bw.Write(protocolVersion);
        }

        internal static void WriteFault(BinaryWriter bw, string message) => bw.Write(message ?? "");

        internal static void WriteResolveRequest(BinaryWriter bw, ResolveRequest request)
        {
            bw.Write(request.TypeName);
            bw.Write(request.MethodName);
            bw.Write(request.ArgTypeCodes.Count);
            foreach (var code in request.ArgTypeCodes)
                bw.Write((byte)code);
        }

        internal static void WriteResolveReply(BinaryWriter bw, ResolveReply reply)
        {
            bw.Write(reply.Success);
            if (reply.Success) bw.Write(reply.MethodHandle);
            else bw.Write(reply.Error ?? "");
        }

        // --- InvokeRequest: [CorrelationId long][MethodHandle int][HasTimeout bool][TimeoutMs int?]
        //                    [ArgCount int]{[TypeCode byte][RawLen int][Raw]}*ArgCount ---
        internal static void WriteInvokeRequest(BinaryWriter bw, long correlationId, int methodHandle, int? timeoutMs, WireValue[] args)
        {
            bw.Write(correlationId);
            bw.Write(methodHandle);
            bw.Write(timeoutMs.HasValue);
            if (timeoutMs.HasValue) bw.Write(timeoutMs.Value);
            bw.Write(args.Length);
            foreach (var arg in args) WriteWireValue(bw, arg);
        }

        // --- InvokeReply: [CorrelationId long][Success]
        //                  [Success ise: WireValue (void dönüşler için WireValue.Null)]
        //                  [değilse: ExceptionType][ExceptionMessage] ---
        internal static void WriteInvokeReply(BinaryWriter bw, InvokeReply reply)
        {
            bw.Write(reply.CorrelationId);
            bw.Write(reply.Success);
            if (reply.Success)
            {
                WriteWireValue(bw, reply.Result ?? WireValue.Null);
            }
            else
            {
                bw.Write(reply.ExceptionType ?? "");
                bw.Write(reply.ExceptionMessage ?? "");
            }
        }

        // --- MemberRequest: [CorrelationId long][Operation byte][MemberName][WireValue] ---
        internal static void WriteMemberRequest(BinaryWriter bw, long correlationId, MemberOperation operation, string memberName, WireValue value)
        {
            bw.Write(correlationId);
            bw.Write((byte)operation);
            bw.Write(memberName ?? "");
            WriteWireValue(bw, value.Raw == null ? WireValue.Null : value);
        }

        // --- InvokeBatchRequest: [CorrelationId long][MethodHandle int][Count int]{[ArgCount int][WireValue]*}* ---
        internal static void WriteInvokeBatchRequest(BinaryWriter bw, InvokeBatchRequest request)
        {
            bw.Write(request.CorrelationId);
            bw.Write(request.MethodHandle);
            bw.Write(request.ArgsList.Length);
            foreach (var args in request.ArgsList)
            {
                bw.Write(args.Length);
                foreach (var a in args) WriteWireValue(bw, a);
            }
        }

        // --- InvokeBatchReply: [CorrelationId long][Success]{[Count int][WireValue]* | [FailedIndex int][ExType][ExMsg]} ---
        internal static void WriteInvokeBatchReply(BinaryWriter bw, InvokeBatchReply reply)
        {
            bw.Write(reply.CorrelationId);
            bw.Write(reply.Success);
            if (reply.Success)
            {
                bw.Write(reply.Results.Length);
                foreach (var r in reply.Results) WriteWireValue(bw, r);
            }
            else
            {
                bw.Write(reply.FailedIndex);
                bw.Write(reply.ExceptionType ?? "");
                bw.Write(reply.ExceptionMessage ?? "");
            }
        }

        // --- EventRaised: [SubscriptionId int][ArgCount int][WireValue]*ArgCount ---
        internal static void WriteEventRaised(BinaryWriter bw, int subscriptionId, WireValue[] args)
        {
            bw.Write(subscriptionId);
            bw.Write(args.Length);
            foreach (var a in args) WriteWireValue(bw, a);
        }

        // --- WireValue: [TypeCode byte][RawLen int][Raw bytes] ---
        private static void WriteWireValue(BinaryWriter bw, WireValue value)
        {
            bw.Write((byte)value.TypeCode);
            bw.Write(value.Raw.Length);
            bw.Write(value.Raw);
        }

        // ======================= Encode (byte[] - geriye uyumlu public API) =======================

        private static byte[] ToBytes<TState>(TState state, Action<BinaryWriter, TState> write)
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) write(bw, state);
            return ms.ToArray();
        }

        public static byte[] EncodeHello(bool success, string? typeFullName, string? error, int protocolVersion = IpcProtocol.Version)
            => ToBytes((success, typeFullName, error, protocolVersion), static (bw, s) => WriteHello(bw, s.success, s.typeFullName, s.error, s.protocolVersion));

        public static byte[] EncodeFault(string message) => ToBytes(message, static (bw, m) => WriteFault(bw, m));
        public static byte[] EncodeResolveRequest(ResolveRequest request) => ToBytes(request, WriteResolveRequest);
        public static byte[] EncodeResolveReply(ResolveReply reply) => ToBytes(reply, WriteResolveReply);
        public static byte[] EncodeInvokeRequest(InvokeRequest r)
            => ToBytes(r, static (bw, x) => WriteInvokeRequest(bw, x.CorrelationId, x.MethodHandle, x.TimeoutMs, x.Args));
        public static byte[] EncodeInvokeReply(InvokeReply reply) => ToBytes(reply, WriteInvokeReply);
        public static byte[] EncodeMemberRequest(MemberRequest r)
            => ToBytes(r, static (bw, x) => WriteMemberRequest(bw, x.CorrelationId, x.Operation, x.MemberName, x.Value));
        public static byte[] EncodeInvokeBatchRequest(InvokeBatchRequest request) => ToBytes(request, WriteInvokeBatchRequest);
        public static byte[] EncodeInvokeBatchReply(InvokeBatchReply reply) => ToBytes(reply, WriteInvokeBatchReply);
        public static byte[] EncodeEventRaised(int subscriptionId, WireValue[] args)
            => ToBytes((subscriptionId, args), static (bw, s) => WriteEventRaised(bw, s.subscriptionId, s.args));

        // ======================= Decode (span okuyucu) =======================

        // Hello: ProtocolVersion SONA eklendi - bu alan yokken derlenmiş eski worker'ların Hello'su da okunabilsin
        // (o durumda sürüm 0 sayılır ve host uyumsuzluk hatası verir - bkz. IpcProtocol).
        public static (bool Success, string? TypeFullName, string? Error) DecodeHello(byte[] payload)
        {
            var (success, typeName, error, _) = DecodeHelloWithVersion(payload);
            return (success, typeName, error);
        }

        public static (bool Success, string? TypeFullName, string? Error, int ProtocolVersion) DecodeHelloWithVersion(byte[] payload)
        {
            var r = new SpanReader(payload);
            bool success = r.ReadBoolean();
            string text = r.ReadString();
            int version = r.Remaining >= 4 ? r.ReadInt32() : 0;
            return success ? (true, text, null, version) : (false, null, text, version);
        }

        public static string DecodeFault(byte[] payload) => new SpanReader(payload).ReadString();

        public static ResolveRequest DecodeResolveRequest(byte[] payload)
        {
            var r = new SpanReader(payload);
            string typeName = r.ReadString();
            string methodName = r.ReadString();
            int count = r.ReadInt32();
            var codes = new WireTypeCode[count];
            for (int i = 0; i < count; i++) codes[i] = (WireTypeCode)r.ReadByte();
            return new ResolveRequest { TypeName = typeName, MethodName = methodName, ArgTypeCodes = codes };
        }

        public static ResolveReply DecodeResolveReply(byte[] payload)
        {
            var r = new SpanReader(payload);
            if (r.ReadBoolean()) return new ResolveReply { Success = true, MethodHandle = r.ReadInt32() };
            return new ResolveReply { Success = false, Error = r.ReadString() };
        }

        public static InvokeRequest DecodeInvokeRequest(byte[] payload)
        {
            var r = new SpanReader(payload);
            long correlationId = r.ReadInt64();
            int methodHandle = r.ReadInt32();
            int? timeoutMs = r.ReadBoolean() ? r.ReadInt32() : null;
            int argCount = r.ReadInt32();
            var args = new WireValue[argCount];
            for (int i = 0; i < argCount; i++) args[i] = r.ReadWireValue();
            return new InvokeRequest { CorrelationId = correlationId, MethodHandle = methodHandle, TimeoutMs = timeoutMs, Args = args };
        }

        public static InvokeReply DecodeInvokeReply(byte[] payload)
        {
            var r = new SpanReader(payload);
            long correlationId = r.ReadInt64();
            if (r.ReadBoolean())
                return new InvokeReply { CorrelationId = correlationId, Success = true, Result = r.ReadWireValue() };
            string exceptionType = r.ReadString();
            string exceptionMessage = r.ReadString();
            return new InvokeReply { CorrelationId = correlationId, Success = false, ExceptionType = exceptionType, ExceptionMessage = exceptionMessage };
        }

        public static MemberRequest DecodeMemberRequest(byte[] payload)
        {
            var r = new SpanReader(payload);
            long correlationId = r.ReadInt64();
            var operation = (MemberOperation)r.ReadByte();
            string memberName = r.ReadString();
            var value = r.ReadWireValue();
            return new MemberRequest { CorrelationId = correlationId, Operation = operation, MemberName = memberName, Value = value };
        }

        public static InvokeBatchRequest DecodeInvokeBatchRequest(byte[] payload)
        {
            var r = new SpanReader(payload);
            long cid = r.ReadInt64();
            int handle = r.ReadInt32();
            int n = r.ReadInt32();
            var list = new WireValue[n][];
            for (int i = 0; i < n; i++)
            {
                int c = r.ReadInt32();
                var args = new WireValue[c];
                for (int j = 0; j < c; j++) args[j] = r.ReadWireValue();
                list[i] = args;
            }
            return new InvokeBatchRequest { CorrelationId = cid, MethodHandle = handle, ArgsList = list };
        }

        public static InvokeBatchReply DecodeInvokeBatchReply(byte[] payload)
        {
            var r = new SpanReader(payload);
            long cid = r.ReadInt64();
            if (r.ReadBoolean())
            {
                int n = r.ReadInt32();
                var results = new WireValue[n];
                for (int i = 0; i < n; i++) results[i] = r.ReadWireValue();
                return new InvokeBatchReply { CorrelationId = cid, Success = true, Results = results };
            }
            int failed = r.ReadInt32();
            string exType = r.ReadString();
            string exMsg = r.ReadString();
            return new InvokeBatchReply { CorrelationId = cid, Success = false, FailedIndex = failed, ExceptionType = exType, ExceptionMessage = exMsg };
        }

        public static (int SubscriptionId, WireValue[] Args) DecodeEventRaised(byte[] payload)
        {
            var r = new SpanReader(payload);
            int id = r.ReadInt32();
            int n = r.ReadInt32();
            var args = new WireValue[n];
            for (int i = 0; i < n; i++) args[i] = r.ReadWireValue();
            return (id, args);
        }

        /// <summary>
        /// BinaryReader ile birebir uyumlu, allocation'sız okuyucu (sadece okunan string/byte[] değerleri ayrılır).
        /// Veri biterse EndOfStreamException (BinaryReader ile aynı).
        /// </summary>
        private ref struct SpanReader
        {
            private readonly ReadOnlySpan<byte> _data;
            private int _pos;

            public SpanReader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }

            public int Remaining => _data.Length - _pos;

            private ReadOnlySpan<byte> Take(int n)
            {
                if (n < 0 || _pos + n > _data.Length) throw new EndOfStreamException("[IpcMessageCodec] Mesaj beklenenden kısa (bozuk frame).");
                var s = _data.Slice(_pos, n);
                _pos += n;
                return s;
            }

            public byte ReadByte() => Take(1)[0];
            public bool ReadBoolean() => Take(1)[0] != 0;
            public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
            public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

            // BinaryWriter.Write(string): 7-bit kodlanmış byte uzunluğu + UTF-8
            public string ReadString()
            {
                int len = Read7BitEncodedInt();
                return len == 0 ? "" : Encoding.UTF8.GetString(Take(len));
            }

            private int Read7BitEncodedInt()
            {
                int result = 0, shift = 0;
                while (true)
                {
                    if (shift >= 35) throw new FormatException("[IpcMessageCodec] Bozuk 7-bit uzunluk.");
                    byte b = ReadByte();
                    result |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) return result;
                    shift += 7;
                }
            }

            public WireValue ReadWireValue()
            {
                var code = (WireTypeCode)ReadByte();
                int len = ReadInt32();
                byte[] raw = len == 0 ? Array.Empty<byte>() : Take(len).ToArray();
                return new WireValue { TypeCode = code, Raw = raw };
            }
        }
    }
}