using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// TODO 7/8 (mesaj kısmı): Hello/Resolve/Invoke/Fault mesajlarının payload'ını encode/decode eder.
    /// WireValue'nun kendi encode/decode'u için bkz. WireValueCodec. BinaryWriter/BinaryReader
    /// (bir MemoryStream üzerinde) kullanılıyor - elle offset takibi yerine netlik tercih edildi;
    /// bu, kontrol-düzlemi (Resolve, bir kez) ve invoke-başına (her çağrıda) mesajlar için DOĞRU ve
    /// anlaşılır bir başlangıç noktası - IPC throughput bir darboğaz olursa (bkz. WireValueCodec'in
    /// üstündeki not) burası havuzlanmış buffer'larla optimize edilebilir.
    /// </summary>
    public static class IpcMessageCodec
    {
        // --- Hello: [1 byte Success][Success ise: TypeFullName][değilse: Error][ProtocolVersion int] ---
        // ProtocolVersion SONA eklendi: bu alan yokken derlenmiş eski worker'ların Hello'su da okunabilsin
        // (o durumda sürüm 0 sayılır ve host uyumsuzluk hatası verir - bkz. IpcProtocol).

        public static byte[] EncodeHello(bool success, string? typeFullName, string? error, int protocolVersion = IpcProtocol.Version)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(success);
            bw.Write(success ? (typeFullName ?? "") : (error ?? ""));
            bw.Write(protocolVersion);
            return ms.ToArray();
        }

        public static (bool Success, string? TypeFullName, string? Error) DecodeHello(byte[] payload)
        {
            var (success, typeName, error, _) = DecodeHelloWithVersion(payload);
            return (success, typeName, error);
        }

        public static (bool Success, string? TypeFullName, string? Error, int ProtocolVersion) DecodeHelloWithVersion(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            bool success = br.ReadBoolean();
            string text = br.ReadString();
            int version = ms.Length - ms.Position >= 4 ? br.ReadInt32() : 0;
            return success ? (true, text, null, version) : (false, null, text, version);
        }

        // --- Fault: [Message] ---

        public static byte[] EncodeFault(string message)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(message ?? "");
            return ms.ToArray();
        }

        public static string DecodeFault(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            return br.ReadString();
        }

        // --- ResolveRequest: [TypeName][MethodName][ArgCount int][ArgTypeCode byte]*ArgCount ---

        public static byte[] EncodeResolveRequest(ResolveRequest request)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(request.TypeName);
            bw.Write(request.MethodName);
            bw.Write(request.ArgTypeCodes.Count);
            foreach (var code in request.ArgTypeCodes)
                bw.Write((byte)code);
            return ms.ToArray();
        }

        public static ResolveRequest DecodeResolveRequest(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            string typeName = br.ReadString();
            string methodName = br.ReadString();
            int count = br.ReadInt32();
            var codes = new WireTypeCode[count];
            for (int i = 0; i < count; i++)
                codes[i] = (WireTypeCode)br.ReadByte();

            return new ResolveRequest { TypeName = typeName, MethodName = methodName, ArgTypeCodes = codes };
        }

        // --- ResolveReply: [Success][Success ise: MethodHandle int][değilse: Error] ---

        public static byte[] EncodeResolveReply(ResolveReply reply)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(reply.Success);
            if (reply.Success)
                bw.Write(reply.MethodHandle);
            else
                bw.Write(reply.Error ?? "");
            return ms.ToArray();
        }

        public static ResolveReply DecodeResolveReply(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            bool success = br.ReadBoolean();
            if (success)
                return new ResolveReply { Success = true, MethodHandle = br.ReadInt32() };
            return new ResolveReply { Success = false, Error = br.ReadString() };
        }

        // --- InvokeRequest: [CorrelationId long][MethodHandle int][HasTimeout bool][TimeoutMs int?]
        //                    [ArgCount int]{[TypeCode byte][RawLen int][Raw]}*ArgCount ---

        public static byte[] EncodeInvokeRequest(InvokeRequest request)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(request.CorrelationId);
            bw.Write(request.MethodHandle);
            bw.Write(request.TimeoutMs.HasValue);
            if (request.TimeoutMs.HasValue)
                bw.Write(request.TimeoutMs.Value);

            bw.Write(request.Args.Length);
            foreach (var arg in request.Args)
                WriteWireValue(bw, arg);

            return ms.ToArray();
        }

        public static InvokeRequest DecodeInvokeRequest(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            long correlationId = br.ReadInt64();
            int methodHandle = br.ReadInt32();
            bool hasTimeout = br.ReadBoolean();
            int? timeoutMs = hasTimeout ? br.ReadInt32() : null;

            int argCount = br.ReadInt32();
            var args = new WireValue[argCount];
            for (int i = 0; i < argCount; i++)
                args[i] = ReadWireValue(br);

            return new InvokeRequest
            {
                CorrelationId = correlationId,
                MethodHandle = methodHandle,
                TimeoutMs = timeoutMs,
                Args = args
            };
        }

        // --- InvokeReply: [CorrelationId long][Success]
        //                  [Success ise: WireValue (void dönüşler için WireValue.Null - "sonuç yok"
        //                    ile "sonuç null" arasında AYRIM YAPMIYORUZ, ikisi de aynı şekilde temsil
        //                    ediliyor; çağıran taraf zaten TReturn'ü biliyor, bu ayrım pratikte gerekmiyor)]
        //                  [değilse: ExceptionType][ExceptionMessage] ---

        public static byte[] EncodeInvokeReply(InvokeReply reply)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
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

            return ms.ToArray();
        }

        public static InvokeReply DecodeInvokeReply(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            long correlationId = br.ReadInt64();
            bool success = br.ReadBoolean();

            if (success)
            {
                WireValue result = ReadWireValue(br);
                return new InvokeReply { CorrelationId = correlationId, Success = true, Result = result };
            }

            string exceptionType = br.ReadString();
            string exceptionMessage = br.ReadString();
            return new InvokeReply
            {
                CorrelationId = correlationId,
                Success = false,
                ExceptionType = exceptionType,
                ExceptionMessage = exceptionMessage
            };
        }

        // --- MemberRequest: [CorrelationId long][Operation byte][MemberName][WireValue] ---

        public static byte[] EncodeMemberRequest(MemberRequest request)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(request.CorrelationId);
            bw.Write((byte)request.Operation);
            bw.Write(request.MemberName ?? "");
            WriteWireValue(bw, request.Value.Raw == null ? WireValue.Null : request.Value);
            return ms.ToArray();
        }

        public static MemberRequest DecodeMemberRequest(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            long correlationId = br.ReadInt64();
            var operation = (MemberOperation)br.ReadByte();
            string memberName = br.ReadString();
            var value = ReadWireValue(br);
            return new MemberRequest { CorrelationId = correlationId, Operation = operation, MemberName = memberName, Value = value };
        }

        // --- InvokeBatchRequest: [CorrelationId long][MethodHandle int][Count int]{[ArgCount int][WireValue]*}* ---

        public static byte[] EncodeInvokeBatchRequest(InvokeBatchRequest request)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(request.CorrelationId);
            bw.Write(request.MethodHandle);
            bw.Write(request.ArgsList.Length);
            foreach (var args in request.ArgsList)
            {
                bw.Write(args.Length);
                foreach (var a in args) WriteWireValue(bw, a);
            }
            return ms.ToArray();
        }

        public static InvokeBatchRequest DecodeInvokeBatchRequest(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            long cid = br.ReadInt64();
            int handle = br.ReadInt32();
            int n = br.ReadInt32();
            var list = new WireValue[n][];
            for (int i = 0; i < n; i++)
            {
                int c = br.ReadInt32();
                var args = new WireValue[c];
                for (int j = 0; j < c; j++) args[j] = ReadWireValue(br);
                list[i] = args;
            }
            return new InvokeBatchRequest { CorrelationId = cid, MethodHandle = handle, ArgsList = list };
        }

        // --- InvokeBatchReply: [CorrelationId long][Success]{[Count int][WireValue]* | [FailedIndex int][ExType][ExMsg]} ---

        public static byte[] EncodeInvokeBatchReply(InvokeBatchReply reply)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
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
            return ms.ToArray();
        }

        public static InvokeBatchReply DecodeInvokeBatchReply(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            long cid = br.ReadInt64();
            if (br.ReadBoolean())
            {
                int n = br.ReadInt32();
                var results = new WireValue[n];
                for (int i = 0; i < n; i++) results[i] = ReadWireValue(br);
                return new InvokeBatchReply { CorrelationId = cid, Success = true, Results = results };
            }
            return new InvokeBatchReply
            {
                CorrelationId = cid,
                Success = false,
                FailedIndex = br.ReadInt32(),
                ExceptionType = br.ReadString(),
                ExceptionMessage = br.ReadString()
            };
        }

        // --- EventRaised: [SubscriptionId int][ArgCount int][WireValue]*ArgCount ---

        public static byte[] EncodeEventRaised(int subscriptionId, WireValue[] args)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(subscriptionId);
            bw.Write(args.Length);
            foreach (var a in args) WriteWireValue(bw, a);
            return ms.ToArray();
        }

        public static (int SubscriptionId, WireValue[] Args) DecodeEventRaised(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            int id = br.ReadInt32();
            int n = br.ReadInt32();
            var args = new WireValue[n];
            for (int i = 0; i < n; i++) args[i] = ReadWireValue(br);
            return (id, args);
        }

        // --- WireValue: [TypeCode byte][RawLen int][Raw bytes] ---
        // NOT: primitive'ler için RawLen aslında TypeCode'dan zaten çıkarılabilir (sabit boyut),
        // ama tek tip bir framing (her zaman uzunluk öneki) hem yazan hem okuyan tarafı BASİT ve
        // hatasız tutuyor - v1 için bilinçli bir sadeleştirme (bkz. WireValueCodec'in üstündeki not).

        private static void WriteWireValue(BinaryWriter bw, WireValue value)
        {
            bw.Write((byte)value.TypeCode);
            bw.Write(value.Raw.Length);
            bw.Write(value.Raw);
        }

        private static WireValue ReadWireValue(BinaryReader br)
        {
            var code = (WireTypeCode)br.ReadByte();
            int len = br.ReadInt32();
            byte[] raw = len == 0 ? Array.Empty<byte>() : br.ReadBytes(len);
            return new WireValue { TypeCode = code, Raw = raw };
        }
    }
}