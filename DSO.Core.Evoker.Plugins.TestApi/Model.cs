using DSO.Core.Evoker.Plugins.Loading;
using DSO.Core.Evoker.Plugins.Sandbox;
using DSO.Core.Evoker.Plugins.Scanning;
using System.Diagnostics;
using System.Text.Json;

namespace DSO.Core.Evoker.Plugins.TestApi
{
    public static class PluginsTest
    {
        static string PluginsFolderPath = Path.Combine(AppContext.BaseDirectory, "Plugins");
        static string HostFolderPath = Path.Combine(AppContext.BaseDirectory, "Host");

        public static void Test1()
        {
            string dllPath = Path.Combine(PluginsFolderPath, "SamplePlugin.dll");

            Console.WriteLine("=== TODO 1: DetectKind ===");
            var kind = PluginScanner.DetectKind(dllPath);
            Console.WriteLine($"Kind: {kind}");
            if (kind != PluginKind.ManagedDotNet)
            {
                Console.WriteLine("BEKLENMEYEN: ManagedDotNet olmalıydı!");
                Console.ReadKey();
            }

            Console.WriteLine();
            Console.WriteLine("=== TODO 2/3: Scan + ToLogLines ===");
            var result = PluginScanner.Scan(dllPath);
            foreach (var line in PluginScanner.ToLogLines(result))
            {
                Console.WriteLine(line);
            }

            if (result.Errors.Count > 0)
            {
                Console.WriteLine("BEKLENMEYEN: Errors olmamalıydı!");
                Console.ReadKey();
            }
            if (result.Types.Count == 0)
            {
                Console.WriteLine("BEKLENMEYEN: Types boş!");

                Console.ReadKey();
            }

            Console.WriteLine();
            Console.WriteLine("=== TODO 4/5 (yeni API): ManagedDotNetPluginLoader + public Builder ===");
            var loader = new ManagedDotNetPluginLoader();
            loader.LoadInProcessAsync(dllPath, "TestPlugin.SamplePlugin").GetAwaiter().GetResult();
            Console.WriteLine($"Instance tipi: {loader.Instance!.GetType().FullName}");
            Console.WriteLine($"Builder.Type : {loader.Builder!.Type.FullName}");

            // A) IPluginLoader köprüsü üzerinden (loader-türü bilinmeyen genel çağırıcı senaryosu)
            var addResult = loader.InvokeAsync("Add", new object?[] { 3, 4 }).GetAwaiter().GetResult();
            Console.WriteLine($"[köprü] Add(3,4) = {addResult}  (beklenen 7)");
            if (!Equals(addResult, 7)) Console.ReadKey();

            var voidResult = loader.InvokeAsync("DoSomething", new object?[] { 42 }).GetAwaiter().GetResult();
            int LastVoidCallValue = loader.Builder.GetValue<int>("LastVoidCallValue");
            Console.WriteLine($"[köprü] DoSomething(42) -> {voidResult ?? (object)"null"}  (beklenen null)");
            Console.WriteLine($"[köprü] DoSomething(42) -> LastVoidCallValue:{LastVoidCallValue} (beklenen 42)");
            if (voidResult != null) Console.ReadKey();

            var addAsyncResult = loader.InvokeAsync("AddAsync", new object?[] { 10, 20 }).GetAwaiter().GetResult();
            Console.WriteLine($"[köprü] AddAsync(10,20) = {addAsyncResult}  (beklenen 30)");
            if (!Equals(addAsyncResult, 30)) Console.ReadKey();

            var doAsyncResult = loader.InvokeAsync("DoAsyncWork", new object?[] { 5 }).GetAwaiter().GetResult();
            LastVoidCallValue = loader.Builder.GetValue<int>("LastVoidCallValue");

            Console.WriteLine($"[köprü] DoAsyncWork(5) -> {doAsyncResult ?? (object)"null"}  (beklenen null)");
            Console.WriteLine($"[köprü] DoAsyncWork(5) -> LastVoidCallValue:{LastVoidCallValue}   (beklenen 50)");
            if (doAsyncResult != null) Console.ReadKey();

            // B) Doğrudan Builder üzerinden - artık PUBLIC, tip bilerek EvokerBuilder'ın tüm yüzeyi kullanılabiliyor
            var directAdd = loader.Builder.Invoke<int>("Add", 100, 200);
            Console.WriteLine($"[Builder.Invoke<int> DOĞRUDAN] Add(100,200) = {directAdd}  (beklenen 300)");
            if (directAdd != 300) Console.ReadKey();

            var directAsync = loader.Builder.InvokeAsync<int>("AddAsync", 7, 8).GetAwaiter().GetResult();
            Console.WriteLine($"[Builder.InvokeAsync<int> DOĞRUDAN] AddAsync(7,8) = {directAsync}  (beklenen 15)");
            if (directAsync != 15) Console.ReadKey();

            // C) İkinci bir loader ile AYNI Type -> ResolvedMethods cache'inin paylaşıldığını (crash etmeden çalıştığını) doğrula
            var loader2 = new ManagedDotNetPluginLoader();
            loader2.LoadInProcessAsync(dllPath, "TestPlugin.SamplePlugin");
            var secondInstanceAdd = loader2.InvokeAsync("Add", new object?[] { 1, 2 }).GetAwaiter().GetResult();

            Console.WriteLine($"[2. loader, aynı Type] Add(1,2) = {secondInstanceAdd}  (beklenen 3)");

            if (!Equals(secondInstanceAdd, 3)) Console.ReadKey();

            // D) Bir loader'ı iki kez LoadInProcessAsync ile çağırmak artık reddedilmeli
            try
            {
                loader.LoadInProcessAsync(dllPath, "TestPlugin.SamplePlugin").GetAwaiter().GetResult();
                Console.WriteLine("BEKLENMEYEN: ikinci LoadInProcessAsync hata vermedi!");
                Console.ReadKey();
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine("[OK] Aynı loader'da ikinci LoadInProcessAsync reddedildi.");
            }

            // E) GetValue<T>/SetValue<T> - loader.Builder üzerinden (SetInstance ile bağlı, sabit instance)
            Console.WriteLine();
            Console.WriteLine("=== TODO 5-ek: EvokerBuilderPropertyExtensions.GetValue<T>/SetValue<T> ===");

            var initialName = loader.Builder.GetValue<string>("DisplayName");
            Console.WriteLine($"[GetValue<string>] DisplayName (ilk) = \"{initialName}\"  (beklenen \"default\")");

            if (initialName != "default") Console.ReadKey();

            loader.Builder.SetValue("DisplayName", "DSO Plugin");
            var updatedName = loader.Builder.GetValue<string>("DisplayName");

            Console.WriteLine($"[SetValue + GetValue<string>] DisplayName (güncel) = \"{updatedName}\"  (beklenen \"DSO Plugin\")");

            if (updatedName != "DSO Plugin") Console.ReadKey();

            // Aynı instance üzerinde SetValue'nun gerçekten kalıcı olduğunu instance'ı doğrudan (düz reflection ile,
            // SmokeTest plugin DLL'ine derleme-zamanı referans vermiyor - gerçek senaryodaki gibi) okuyarak da doğrula.
            var directRead = loader.Builder.Type.GetProperty("DisplayName")!.GetValue(loader.Instance);
            Console.WriteLine($"[Instance doğrudan okuma] DisplayName = \"{directRead}\"  (beklenen \"DSO Plugin\")");
            if ((string?)directRead != "DSO Plugin") Console.ReadKey();

            // F) SetConstructor modunda (sabit instance YOK) GetValue net bir hata vermeli
            var throwawayBuilder = new DSO.Core.Evoker.EvokerBuilder(loader.Builder.Type).SetConstructor();
            try
            {
                throwawayBuilder.GetValue<string>("DisplayName");
                Console.WriteLine("BEKLENMEYEN: SetConstructor modunda GetValue hata vermedi!");
                Console.ReadKey();
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine("[OK] Sabit instance'ı olmayan builder'da GetValue InvalidOperationException fırlattı.");
            }

            // G) ForgetCache - crash etmeden çalışmalı, sonraki GetValue çağrısı yeniden derleyip aynı sonucu vermeli
            loader.Builder.ForgetCache();
            var afterForget = loader.Builder.GetValue<string>("DisplayName");
            Console.WriteLine($"[ForgetCache sonrası GetValue<string>] DisplayName = \"{afterForget}\"  (beklenen \"DSO Plugin\")");
            if (afterForget != "DSO Plugin") Console.ReadKey();

            // H) includeNonPublic:true - private field/property/method erişimi
            Console.WriteLine();
            Console.WriteLine("=== TODO 5-ek 2: includeNonPublic - private field/property/method ===");

            // H1) Varsayılan (includeNonPublic:false) loader private üyeleri GÖREMEMELİ - eski davranış korunuyor
            try
            {
                loader.Builder.GetValue<int>("_secretCounter");
                Console.WriteLine("BEKLENMEYEN: varsayılan builder private field'ı okuyabildi!");
                Console.ReadKey();
            }
            catch (MissingMemberException)
            {
                Console.WriteLine("[OK] includeNonPublic:false (varsayılan) builder private field'ı GÖREMEDİ (beklenen).");
            }

            // H2) includeNonPublic:true ile YENİ bir loader - private field/property/method hepsi erişilebilir olmalı
            var privilegedLoader = new ManagedDotNetPluginLoader();
            privilegedLoader.LoadInProcessAsync(dllPath, "TestPlugin.SamplePlugin", includeNonPublic: true).GetAwaiter().GetResult();

            var secretCounter = privilegedLoader.Builder!.GetValue<int>("_secretCounter");
            Console.WriteLine($"[private FIELD] GetValue<int>(\"_secretCounter\") = {secretCounter}  (beklenen 5)");
            if (secretCounter != 5) Console.ReadKey();

            privilegedLoader.Builder.SetValue("_secretCounter", 99);
            Console.WriteLine($"[private FIELD] SetValue sonrası = {privilegedLoader.Builder.GetValue<int>("_secretCounter")}  (beklenen 99)");
            if (privilegedLoader.Builder.GetValue<int>("_secretCounter") != 99) Console.ReadKey();

            var secretName = privilegedLoader.Builder.GetValue<string>("SecretName");
            Console.WriteLine($"[private PROPERTY] GetValue<string>(\"SecretName\") (ilk) = \"{secretName}\"  (beklenen \"hidden\")");
            if (secretName != "hidden") Console.ReadKey();

            privilegedLoader.Builder.SetValue("SecretName", "artık gizli değil");
            Console.WriteLine($"[private PROPERTY] SetValue sonrası = \"{privilegedLoader.Builder.GetValue<string>("SecretName")}\"  (beklenen \"artık gizli değil\")");
            if (privilegedLoader.Builder.GetValue<string>("SecretName") != "artık gizli değil") Console.ReadKey();

            var multiplyResult = privilegedLoader.InvokeAsync("MultiplySecret", new object?[] { 6, 7 }).GetAwaiter().GetResult();
            Console.WriteLine($"[private METOT, köprü üzerinden] MultiplySecret(6,7) = {multiplyResult}  (beklenen 42)");
            if (!Equals(multiplyResult, 42)) Console.ReadKey();

            var multiplyDirect = privilegedLoader.Builder.Invoke<int>("MultiplySecret", 3, 3);
            Console.WriteLine($"[private METOT, Builder.Invoke<int> DOĞRUDAN] MultiplySecret(3,3) = {multiplyDirect}  (beklenen 9)");
            if (multiplyDirect != 9) Console.ReadKey();

            Console.WriteLine("[OK] includeNonPublic:true - private field/property/method hepsi çalıştı.");

            Console.WriteLine();
            Console.WriteLine("TÜM TESTLER GEÇTİ.");
        }

        public static async Task Test2Sandbox()
        {
            string dllPath = Path.Combine(PluginsFolderPath, "SamplePlugin.dll"); //args.Length > 0 ? args[0] : throw new ArgumentException("SamplePlugin.dll yolu gerekli.");
            string hostDllPath = Path.Combine(HostFolderPath, "DSO.Core.Evoker.PluginHost.dll"); //args.Length > 1 ? args[1] : throw new ArgumentException("PluginHost.dll yolu gerekli.");
            string typeFullName = "TestPlugin.SamplePlugin";
            string tmpCrashLog = Path.Combine(Path.GetTempPath(), "sandbox-smoketest-crashes.log");
            if (File.Exists(tmpCrashLog)) File.Delete(tmpCrashLog);

            PluginWorkerOptions MakeOptions(int maxConcurrency = 2, int heartbeatMs = 1000, bool autoRestart = false) => new()
            {
                HostPath = hostDllPath,
                HostIsDotnetDll = true,
                MaxConcurrency = maxConcurrency,
                HeartbeatIntervalMs = heartbeatMs,
                MissedHeartbeatsBeforeKill = 3,
                AutoRestartOnCrash = autoRestart,
                NotifyOnCrash = true,
                CrashLogFilePath = tmpCrashLog,
                StartupTimeoutMs = 15000
            };

            Console.WriteLine("=== TODO 15: StartAsync (Process.Start + pipe + Hello handshake) ===");
            var handle = new PluginWorkerHandle(dllPath, typeFullName, MakeOptions());
            await handle.StartAsync();
            Console.WriteLine("[OK] Worker başladı, Hello handshake tamamlandı.");

            Console.WriteLine();
            Console.WriteLine("=== TODO 16: ResolveAsync + TODO 17: InvokeAsync (senkron) ===");
            int addHandle = await handle.ResolveAsync(typeFullName, "Add", new[] { WireTypeCode.Int32, WireTypeCode.Int32 });
            var addReply = await handle.InvokeAsync(addHandle, new[] { WireValueCodec.FromObject(3), WireValueCodec.FromObject(4) });
            Console.WriteLine($"Add(3,4) -> Success={addReply.Success}, Result={WireValueCodec.ToObject(addReply.Result!.Value)}  (beklenen 7)");
            if (!addReply.Success || (int)WireValueCodec.ToObject(addReply.Result!.Value)! != 7) Console.ReadKey();

            Console.WriteLine();
            Console.WriteLine("=== Task<T> dönüşü: AddAsync ===");
            int addAsyncHandle = await handle.ResolveAsync(typeFullName, "AddAsync", new[] { WireTypeCode.Int32, WireTypeCode.Int32 });
            var addAsyncReply = await handle.InvokeAsync(addAsyncHandle, new[] { WireValueCodec.FromObject(10), WireValueCodec.FromObject(20) });
            Console.WriteLine($"AddAsync(10,20) -> Success={addAsyncReply.Success}, Result={WireValueCodec.ToObject(addAsyncReply.Result!.Value)}  (beklenen 30)");
            if (!addAsyncReply.Success || (int)WireValueCodec.ToObject(addAsyncReply.Result!.Value)! != 30) Console.ReadKey();

            Console.WriteLine();
            Console.WriteLine("=== void dönüşü: DoSomething ===");
            int doSomethingHandle = await handle.ResolveAsync(typeFullName, "DoSomething", new[] { WireTypeCode.Int32 });
            var doSomethingReply = await handle.InvokeAsync(doSomethingHandle, new[] { WireValueCodec.FromObject(42) });
            int LastVoidCallValue = handle.Builder.GetValue<int>("LastVoidCallValue");

            Console.WriteLine($"DoSomething(42) -> Success={doSomethingReply.Success}, ResultTypeCode={doSomethingReply.Result?.TypeCode}  (beklenen Null)");
            Console.WriteLine($"DoSomething(42) -> LastVoidCallValue:{LastVoidCallValue} (beklenen 42)");
            if (!doSomethingReply.Success || doSomethingReply.Result!.Value.TypeCode != WireTypeCode.Null) Console.ReadKey();


            Console.WriteLine();
            Console.WriteLine("=== Tasl dönüşü: DoAsyncWork ===");
            int doAsyncWorkHandle = await handle.ResolveAsync(typeFullName, "DoAsyncWork", new[] { WireTypeCode.Int32 });
            var doAsyncWorkReply = await handle.InvokeAsync(doAsyncWorkHandle, new[] { WireValueCodec.FromObject(5) });
            LastVoidCallValue = handle.Builder.GetValue<int>("LastVoidCallValue");

            Console.WriteLine($"DoAsyncWork(5) -> Success={doAsyncWorkReply.Success}, ResultTypeCode={doAsyncWorkReply.Result?.TypeCode}  (beklenen Null)");
            Console.WriteLine($"DoAsyncWork(5) -> LastVoidCallValue:{LastVoidCallValue} (beklenen 50)");
            if (!doAsyncWorkReply.Success || doAsyncWorkReply.Result!.Value.TypeCode != WireTypeCode.Null) Console.ReadKey();


            Console.WriteLine();
            Console.WriteLine("=== string arg/dönüş: Greet ===");
            int greetHandle = await handle.ResolveAsync(typeFullName, "Greet", new[] { WireTypeCode.String, WireTypeCode.String });
            var greetReply = await handle.InvokeAsync(greetHandle, new[] { WireValueCodec.FromObject("Dünya"), WireValueCodec.FromObject("Selam") });
            var greetText = (string)WireValueCodec.ToObject(greetReply.Result!.Value)!;
            Console.WriteLine($"Greet(\"Dünya\",\"Selam\") -> \"{greetText}\"  (beklenen \"Selam, Dünya!\")");
            if (greetText != "Selam, Dünya!") Console.ReadKey();

            Console.WriteLine();
            Console.WriteLine("=== WireTypeCode.Complex: MakePoint (JSON round-trip) ===");
            int pointHandle = await handle.ResolveAsync(typeFullName, "MakePoint", new[] { WireTypeCode.Int32, WireTypeCode.Int32 });
            var pointReply = await handle.InvokeAsync(pointHandle, new[] { WireValueCodec.FromObject(7), WireValueCodec.FromObject(9) });
            Console.WriteLine($"MakePoint sonucu TypeCode={pointReply.Result!.Value.TypeCode}  (beklenen Complex)");
            if (pointReply.Result!.Value.TypeCode != WireTypeCode.Complex) Console.ReadKey();
            // SandboxSmokeTest, SamplePlugin.dll'e derleme-zamanı referans VERMİYOR (gerçek plugin senaryosu gibi) -
            // host bu yüzden Point tipini YÜKLEMEMİŞ durumda, WireValueCodec bunu JsonElement'e (dinamik) decode
            // ediyor (bkz. WireValueCodec.ToObject'in "ÖNEMLİ" notu) - gerçek bir host'un plugin'in kendi tipini
            // bilmediği durumda karşılaşacağı ile AYNI senaryo.
            var pointObj = WireValueCodec.ToObject(pointReply.Result!.Value)!;
            Console.WriteLine($"Point sonucu .NET tipi = {pointObj.GetType().FullName}  (beklenen JsonElement)");
            var pointJson = (System.Text.Json.JsonElement)pointObj;
            int px = pointJson.GetProperty("X").GetInt32();
            int py = pointJson.GetProperty("Y").GetInt32();
            Console.WriteLine($"Point.X={px}, Point.Y={py}  (beklenen X=7, Y=9)");
            if (px != 7 || py != 9) Console.ReadKey();

            Console.WriteLine();
            Console.WriteLine("=== Bilinmeyen MethodHandle -> temiz InvokeReply.Success=false (worker ÇÖKMEMELİ) ===");
            var badHandleReply = await handle.InvokeAsync(999999, Array.Empty<WireValue>());
            Console.WriteLine($"Bilinmeyen handle -> Success={badHandleReply.Success}, ExceptionType={badHandleReply.ExceptionType}");
            if (badHandleReply.Success) Console.ReadKey();

            Console.WriteLine();
            Console.WriteLine("=== Plugin metodu exception fırlatınca -> temiz InvokeReply.Success=false ===");
            int throwsHandle = await handle.ResolveAsync(typeFullName, "Throws", Array.Empty<WireTypeCode>());
            var throwsReply = await handle.InvokeAsync(throwsHandle, Array.Empty<WireValue>());
            Console.WriteLine($"Throws() -> Success={throwsReply.Success}, ExceptionType={throwsReply.ExceptionType}, Message={throwsReply.ExceptionMessage}  (beklenen InvalidOperationException)");
            if (throwsReply.Success || throwsReply.ExceptionType != typeof(InvalidOperationException).FullName) Console.ReadKey();

            Console.WriteLine();
            Console.WriteLine("=== Worker hâlâ hayatta mı (bir önceki hata onu ÇÖKERTMEMİŞ olmalı) ===");
            var addAfterThrowReply = await handle.InvokeAsync(addHandle, new[] { WireValueCodec.FromObject(100), WireValueCodec.FromObject(200) });
            Console.WriteLine($"Add(100,200) (Throws'tan SONRA) = {WireValueCodec.ToObject(addAfterThrowReply.Result!.Value)}  (beklenen 300)");
            if ((int)WireValueCodec.ToObject(addAfterThrowReply.Result!.Value)! != 300) Console.ReadKey();

            Console.WriteLine();
            Console.WriteLine("=== TODO 19: heartbeat - bir kaç Ping/Pong turu boyunca worker canlı kalmalı ===");
            await Task.Delay(2500); // HeartbeatIntervalMs=1000 -> en az 2 Ping/Pong turu geçmeli
            var addAfterHeartbeats = await handle.InvokeAsync(addHandle, new[] { WireValueCodec.FromObject(1), WireValueCodec.FromObject(1) });
            Console.WriteLine($"2.5sn beklemeden SONRA Add(1,1) = {WireValueCodec.ToObject(addAfterHeartbeats.Result!.Value)}  (beklenen 2, worker heartbeat'lerle canlı kalmış olmalı)");
            if (!addAfterHeartbeats.Success || (int)WireValueCodec.ToObject(addAfterHeartbeats.Result!.Value)! != 2) Console.ReadKey();
            if (File.Exists(tmpCrashLog)) { Console.WriteLine("BEKLENMEYEN: heartbeat'ler sorunsuz giderken crash log'a yazılmış!"); Console.ReadKey(); }
            Console.WriteLine("[OK] Crash log'a hiçbir şey yazılmadı (beklenen - worker sorunsuz).");

            Console.WriteLine();
            Console.WriteLine("=== includeNonPublic:true - private metot Resolve+Invoke ===");
            var privilegedHandle = new PluginWorkerHandle(dllPath, typeFullName, MakeOptions(), includeNonPublic: true);
            await privilegedHandle.StartAsync();
            int multiplyHandle = await privilegedHandle.ResolveAsync(typeFullName, "MultiplySecret", new[] { WireTypeCode.Int32, WireTypeCode.Int32 });
            var multiplyReply = await privilegedHandle.InvokeAsync(multiplyHandle, new[] { WireValueCodec.FromObject(6), WireValueCodec.FromObject(7) });
            Console.WriteLine($"[private, includeNonPublic:true] MultiplySecret(6,7) = {WireValueCodec.ToObject(multiplyReply.Result!.Value)}  (beklenen 42)");
            if ((int)WireValueCodec.ToObject(multiplyReply.Result!.Value)! != 42) Console.ReadKey();

            // Varsayılan (includeNonPublic:false) worker AYNI private metodu göremiyor olmalı.
            try
            {
                await handle.ResolveAsync(typeFullName, "MultiplySecret", new[] { WireTypeCode.Int32, WireTypeCode.Int32 });
                Console.WriteLine("BEKLENMEYEN: includeNonPublic:false worker private metodu resolve edebildi!");
                Console.ReadKey();
            }
            catch (MissingMethodException)
            {
                Console.WriteLine("[OK] includeNonPublic:false worker private metodu GÖREMEDİ (beklenen).");
            }

            await privilegedHandle.DisposeAsync();

            Console.WriteLine();
            Console.WriteLine("=== TODO 18: PromoteToInProcessAsync - state kaybı bilinen davranış, ama Builder çalışır olmalı ===");
            var promoteHandle = new PluginWorkerHandle(dllPath, typeFullName, MakeOptions());
            await promoteHandle.StartAsync();
            int promoteAddHandle = await promoteHandle.ResolveAsync(typeFullName, "Add", new[] { WireTypeCode.Int32, WireTypeCode.Int32 });
            _ = await promoteHandle.InvokeAsync(promoteAddHandle, new[] { WireValueCodec.FromObject(1), WireValueCodec.FromObject(1) }); // sandbox üzerinden en az bir çağrı
            var loader = await promoteHandle.PromoteToInProcessAsync();
            Console.WriteLine($"[promote] Instance tipi = {loader.Instance!.GetType().FullName}");
            var inProcessAdd = loader.Builder!.Invoke<int>("Add", 5, 6);
            Console.WriteLine($"[promote sonrası, IN-PROCESS Builder.Invoke<int>] Add(5,6) = {inProcessAdd}  (beklenen 11)");
            if (inProcessAdd != 11) Console.ReadKey();
            try
            {
                await promoteHandle.InvokeAsync(promoteAddHandle, new[] { WireValueCodec.FromObject(1), WireValueCodec.FromObject(1) });
                Console.WriteLine("BEKLENMEYEN: promote sonrası handle hâlâ kullanılabildi!");
                Console.ReadKey();
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine("[OK] Promote sonrası handle artık kullanılamaz hale geldi (beklenen).");
            }

            Console.WriteLine();
            Console.WriteLine("=== TODO 20/21: PluginWorkerPool - GetOrStartAsync aynı handle'ı paylaşmalı, StopAsync temiz kapatmalı ===");
            await using var pool = new PluginWorkerPool();
            var pooled1 = await pool.GetOrStartAsync(dllPath, typeFullName, MakeOptions());
            var pooled2 = await pool.GetOrStartAsync(dllPath, typeFullName, MakeOptions());
            Console.WriteLine($"[pool] pooled1 == pooled2 (referans eşitliği) = {ReferenceEquals(pooled1, pooled2)}  (beklenen true)");
            if (!ReferenceEquals(pooled1, pooled2)) Console.ReadKey();

            int pooledAddHandle = await pooled1.ResolveAsync(typeFullName, "Add", new[] { WireTypeCode.Int32, WireTypeCode.Int32 });
            var pooledAddReply = await pooled1.InvokeAsync(pooledAddHandle, new[] { WireValueCodec.FromObject(2), WireValueCodec.FromObject(2) });
            Console.WriteLine($"[pool] Add(2,2) = {WireValueCodec.ToObject(pooledAddReply.Result!.Value)}  (beklenen 4)");
            if ((int)WireValueCodec.ToObject(pooledAddReply.Result!.Value)! != 4) Console.ReadKey();

            await pool.StopAsync(dllPath, typeFullName);
            var pooled3 = await pool.GetOrStartAsync(dllPath, typeFullName, MakeOptions());
            Console.WriteLine($"[pool] StopAsync sonrası pooled3 == pooled1 = {ReferenceEquals(pooled3, pooled1)}  (beklenen false - TAZE bir worker)");
            if (ReferenceEquals(pooled3, pooled1)) Console.ReadKey();


            Console.WriteLine();
            Console.WriteLine("=== CallAsync (isimle çağırma kolaylığı - otomatik resolve/encode/decode) ===");
            var callSum = await handle.CallAsync("Add", new object?[] { 20, 22 });
            Console.WriteLine($"CallAsync Add(20,22) = {callSum}  (beklenen 42)");
            if (!Equals(callSum, 42)) Console.ReadKey();
            var callGreet = await handle.CallAsync("Greet", new object?[] { "Ali", "Hoş geldin" });
            Console.WriteLine($"CallAsync Greet = \"{callGreet}\"  (beklenen \"Hoş geldin, Ali!\")");
            if (!Equals(callGreet, "Hoş geldin, Ali!")) Console.ReadKey();
            var callVoid = await handle.CallAsync("DoSomething", new object?[] { 5 });
            Console.WriteLine($"CallAsync DoSomething(5) = {callVoid ?? "null"}  (beklenen null)");
            if (callVoid != null) Console.ReadKey();
            // Host plugin tipini yüklemediyse JsonElement, yüklediyse (ör. yukarıdaki promote testi DLL'i bu
            // process'e yükledi) GERÇEK TestPlugin.Point gelir - her iki durumu da JSON üzerinden doğruluyoruz.
            var callPointObj = await handle.CallAsync("MakePoint", new object?[] { 1, 2 });
            Console.WriteLine($"CallAsync MakePoint .NET tipi = {callPointObj!.GetType().FullName}");
            var callPoint = callPointObj is System.Text.Json.JsonElement je ? je : System.Text.Json.JsonSerializer.SerializeToElement(callPointObj, callPointObj.GetType());
            Console.WriteLine($"CallAsync MakePoint(1,2) -> X={callPoint.GetProperty("X").GetInt32()}, Y={callPoint.GetProperty("Y").GetInt32()}");
            if (callPoint.GetProperty("X").GetInt32() != 1 || callPoint.GetProperty("Y").GetInt32() != 2) Console.ReadKey();
            try
            {
                await handle.CallAsync("Throws", Array.Empty<object?>());
                Console.WriteLine("BEKLENMEYEN: Throws hata vermedi!"); Console.ReadKey();
            }
            catch (PluginInvocationException pie)
            {
                Console.WriteLine($"[OK] CallAsync Throws -> PluginInvocationException (RemoteExceptionType={pie.RemoteExceptionType})");
                if (pie.RemoteExceptionType != typeof(InvalidOperationException).FullName) Console.ReadKey();
            }

            Console.WriteLine();
            Console.WriteLine("=== stdout seli: plugin 200.000 satır Console.WriteLine yapınca worker KİLİTLENMEMELİ ===");
            var spamSw = System.Diagnostics.Stopwatch.StartNew();
            var spamResult = await handle.CallAsync("SpamConsole", new object?[] { 200_000 }, timeoutMs: 30_000);
            Console.WriteLine($"SpamConsole(200000) = {spamResult}, süre {spamSw.ElapsedMilliseconds}ms  (beklenen 200000, takılmadan)");
            if (!Equals(spamResult, 200_000)) Console.ReadKey();

            Console.WriteLine();
            Console.WriteLine("=== MaxConcurrency: 2 -> iki 700ms'lik çağrı PARALEL, 1 -> SIRALI ===");
            async Task<long> TwoSlowCalls(PluginWorkerHandle h)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await Task.WhenAll(h.CallAsync("SlowAsync", new object?[] { 700 }), h.CallAsync("SlowAsync", new object?[] { 700 }));
                return sw.ElapsedMilliseconds;
            }
            long parallelMs = await TwoSlowCalls(handle); // handle MaxConcurrency=2
            var serialHandle = new PluginWorkerHandle(dllPath, typeFullName, MakeOptions(maxConcurrency: 1));
            await serialHandle.StartAsync();
            long serialMs = await TwoSlowCalls(serialHandle);
            await serialHandle.DisposeAsync();
            Console.WriteLine($"MaxConcurrency=2: {parallelMs}ms (beklenen ~700-1200)  |  MaxConcurrency=1: {serialMs}ms (beklenen >=1400)");
            if (parallelMs >= 1300 || serialMs < 1350) Console.ReadKey();

            Console.WriteLine();
            Console.WriteLine("=== Uzun çağrı heartbeat'i YANLIŞLIKLA tetiklememeli (heartbeat 200ms x 3, çağrı 2000ms) ===");
            var hbHandle = new PluginWorkerHandle(dllPath, typeFullName, MakeOptions(heartbeatMs: 200));
            await hbHandle.StartAsync();
            var longResult = await hbHandle.CallAsync("SlowAsync", new object?[] { 2000 });
            Console.WriteLine($"SlowAsync(2000) = {longResult}, IsDead={hbHandle.IsDead}  (beklenen 2000, False)");
            if (!Equals(longResult, 2000) || hbHandle.IsDead) Console.ReadKey();
            await hbHandle.DisposeAsync();

            Console.WriteLine();
            Console.WriteLine("=== Çağrı bazlı timeout: SADECE o çağrı düşer, worker ayakta kalır ===");
            var timeoutSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await handle.CallAsync("SlowAsync", new object?[] { 1500 }, timeoutMs: 200);
                Console.WriteLine("BEKLENMEYEN: timeout olmadı!"); Console.ReadKey();
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"[OK] {timeoutSw.ElapsedMilliseconds}ms sonra TimeoutException (beklenen ~200ms)");
            }
            var afterTimeout = await handle.CallAsync("Add", new object?[] { 2, 3 });
            Console.WriteLine($"Timeout SONRASI Add(2,3) = {afterTimeout}, IsDead={handle.IsDead}  (beklenen 5, False)");
            if (!Equals(afterTimeout, 5) || handle.IsDead) Console.ReadKey();

            Console.WriteLine();
            Console.WriteLine("=== GERÇEK İZOLASYON: plugin Environment.FailFast ile process'i çökertir -> host AYAKTA kalmalı ===");
            var failFastHandle = new PluginWorkerHandle(dllPath, typeFullName, MakeOptions(autoRestart: false));
            await failFastHandle.StartAsync();
            try
            {
                await failFastHandle.CallAsync("CrashHard", Array.Empty<object?>());
                Console.WriteLine("BEKLENMEYEN: CrashHard dönüş yaptı!"); Console.ReadKey();
            }
            catch (Exception ex) when (ex is not PluginInvocationException)
            {
                Console.WriteLine($"[OK] Host çağrıda {ex.GetType().Name} aldı, host process ÇÖKMEDİ. IsDead={failFastHandle.IsDead}  (beklenen True)");
                if (!failFastHandle.IsDead) Console.ReadKey();
            }
            try
            {
                await failFastHandle.CallAsync("Add", new object?[] { 1, 1 });
                Console.WriteLine("BEKLENMEYEN: ölü worker'a çağrı kabul edildi!"); Console.ReadKey();
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine("[OK] Ölü worker'a (AutoRestart kapalı) sonraki çağrı net hata veriyor.");
            }
            await failFastHandle.DisposeAsync();
            var addStillOk = await handle.CallAsync("Add", new object?[] { 7, 7 });
            Console.WriteLine($"Diğer worker hâlâ çalışıyor: Add(7,7) = {addStillOk}  (beklenen 14)");
            if (!Equals(addStillOk, 14)) Console.ReadKey();

            Console.WriteLine();
            Console.WriteLine("=== Yanlış HostPath: worker hemen ölürse StartAsync HIZLI hata vermeli (15sn timeout beklemeden) ===");
            var badHostSw = System.Diagnostics.Stopwatch.StartNew();
            var badHost = new PluginWorkerHandle(dllPath, typeFullName, new PluginWorkerOptions { HostPath = "/yok/boyle/bir/host.dll", StartupTimeoutMs = 15000 });
            try
            {
                await badHost.StartAsync();
                Console.WriteLine("BEKLENMEYEN: yanlış host ile başladı!"); Console.ReadKey();
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"[OK] {badHostSw.ElapsedMilliseconds}ms içinde hata: {ex.Message.Split('.')[0]}...");
                if (badHostSw.ElapsedMilliseconds > 8000) Console.ReadKey();
            }

            Console.WriteLine();
            Console.WriteLine("=== TODO 19 (devamı): worker DIŞARIDAN öldürülürse AutoRestartOnCrash otomatik toparlamalı ===");
            var crashHandle = new PluginWorkerHandle(dllPath, typeFullName, MakeOptions(heartbeatMs: 300, autoRestart: true));
            await crashHandle.StartAsync();
            int crashAddHandle = await crashHandle.ResolveAsync(typeFullName, "Add", new[] { WireTypeCode.Int32, WireTypeCode.Int32 });
            var beforeCrashReply = await crashHandle.InvokeAsync(crashAddHandle, new[] { WireValueCodec.FromObject(1), WireValueCodec.FromObject(2) });
            Console.WriteLine($"Kill öncesi Add(1,2) = {WireValueCodec.ToObject(beforeCrashReply.Result!.Value)}  (beklenen 3)");
            if ((int)WireValueCodec.ToObject(beforeCrashReply.Result!.Value)! != 3) Console.ReadKey();

            _ = await crashHandle.CallAsync("Add", new object?[] { 0, 0 }); // CallAsync cache'ini restart ÖNCESİ doldur
            int? pidBeforeKill = crashHandle.ProcessId;
            Console.WriteLine($"Worker PID (kill öncesi) = {pidBeforeKill}");
            System.Diagnostics.Process.GetProcessById(pidBeforeKill!.Value).Kill(entireProcessTree: true);
            Console.WriteLine("[test] Worker process DIŞARIDAN kill edildi - okuma döngüsünün bunu yakalayıp restart etmesi bekleniyor...");

            // Restart, read-loop'un pipe kopmasını yakalamasıyla ASENKRON olur - kısa bir bekleme ile devam etmesini bekle.
            await Task.Delay(3000);

            // EnsureAlive'ın restart sonrası eski _deadFlag'i sıfırlamış olması + eski Resolve handle'ının
            // YENİ worker instance'ında da GEÇERLİ olması gerekiyor (Resolve, restart sonrası TEKRAR
            // yapılmadı - taze process kendi handle sayacını 1'den başlatır, ama biz aynı "Add" ismini,
            // aynı sırayla ilk resolve ettiğimiz için aynı numarayı (1) alacaktır - GERÇEK bir sistemde
            // admin restart sonrası metotları YENİDEN resolve etmelidir, burada sadece worker'ın GENEL
            // olarak tekrar çalışır durumda olduğunu doğruluyoruz).
            // Restart ÖNCESİ alınmış handle artık reddedilmeli (yeni worker'da başka metoda denk gelebilirdi).
            try
            {
                await crashHandle.InvokeAsync(crashAddHandle, new[] { WireValueCodec.FromObject(1), WireValueCodec.FromObject(1) });
                Console.WriteLine("BEKLENMEYEN: eski nesil handle kabul edildi!"); Console.ReadKey();
            }
            catch (StaleMethodHandleException ex)
            {
                Console.WriteLine($"[OK] Eski handle -> StaleMethodHandleException (nesil {ex.IssuedGeneration} -> {ex.CurrentGeneration})");
            }
            // CallAsync ise restart'ı çağırana hissettirmeden kendi kendine yeniden resolve etmeli.
            var callAfterRestart = await crashHandle.CallAsync("Add", new object?[] { 4, 4 });
            Console.WriteLine($"Restart SONRASI CallAsync Add(4,4) = {callAfterRestart}, Generation={crashHandle.Generation}  (beklenen 8, 2)");
            if (!Equals(callAfterRestart, 8) || crashHandle.Generation != 2) Console.ReadKey();

            int crashAddHandleAfterRestart = await crashHandle.ResolveAsync(typeFullName, "Add", new[] { WireTypeCode.Int32, WireTypeCode.Int32 });
            var afterCrashReply = await crashHandle.InvokeAsync(crashAddHandleAfterRestart, new[] { WireValueCodec.FromObject(10), WireValueCodec.FromObject(20) });
            Console.WriteLine($"Restart SONRASI Add(10,20) = {WireValueCodec.ToObject(afterCrashReply.Result!.Value)}  (beklenen 30)");
            if (!afterCrashReply.Success || (int)WireValueCodec.ToObject(afterCrashReply.Result!.Value)! != 30) Console.ReadKey();

            int? pidAfterRestart = crashHandle.ProcessId;
            Console.WriteLine($"Worker PID (restart sonrası) = {pidAfterRestart}  (beklenen eskisinden FARKLI: {pidBeforeKill})");
            if (pidAfterRestart == pidBeforeKill) Console.ReadKey();

            bool crashLogged = File.Exists(tmpCrashLog) && File.ReadAllText(tmpCrashLog).Contains(dllPath);
            Console.WriteLine($"[OK] Crash log'a yazıldı mı = {crashLogged}  (beklenen true)");
            if (!crashLogged) Console.ReadKey();

            await crashHandle.DisposeAsync();
            await handle.DisposeAsync();
            await promoteHandle.DisposeAsync(); // promote edilmiş handle için no-op olmalı, hata fırlatmamalı

            Console.WriteLine();
            Console.WriteLine("TÜM SANDBOX TESTLERİ GEÇTİ.");

        }

        public static async Task Test3BuilderParityTest()
        {
            // IPluginBuilder parite testi: AYNI senaryo gövdesi (RunScenarioAsync) hem sandbox (ayrı worker process,
            // IPC) hem in-process (doğrudan EvokerBuilder) implementasyonuna karşı çalıştırılır. İkisi aynı sonucu
            // vermeli - uygulama kodu IPluginBuilder'a bir kez yazılır, plugin nerede çalışırsa çalışsın değişmez.
            //
            // Kullanım: dotnet run -- <SamplePlugin.dll yolu> <DSO.Core.Evoker.PluginHost.dll yolu>
            // NOT: Bu proje SamplePlugin'e derleme zamanı referans VERMİYOR - host plugin tiplerini (Point, Level)
            // bilmiyor; kendi PointDto'sunu ve int'i kullanıyor (gerçek senaryo).

            string dllPath = Path.Combine(PluginsFolderPath, "SamplePlugin.dll"); //args.Length > 0 ? args[0] : throw new ArgumentException("SamplePlugin.dll yolu gerekli.");
            string hostDllPath = Path.Combine(HostFolderPath, "DSO.Core.Evoker.PluginHost.dll"); //args.Length > 1 ? args[1] : throw new ArgumentException("PluginHost.dll yolu gerekli.");
            const string TypeName = "TestPlugin.SamplePlugin";

            int failures = 0;
            void Check(string label, bool ok, string detail = "")
            {
                Console.WriteLine($"  [{(ok ? "OK" : "HATA")}] {label}{(detail.Length > 0 ? "  -> " + detail : "")}");
                if (!ok) failures++;
            }

            async Task ExpectAsync<TEx>(string label, Func<Task> action, Func<TEx, bool>? extra = null) where TEx : Exception
            {
                try
                {
                    await action();
                    Check(label, false, "exception bekleniyordu, gelmedi");
                }
                catch (TEx ex)
                {
                    Check(label, extra == null || extra(ex), $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                }
                catch (Exception ex)
                {
                    Check(label, false, $"beklenen {typeof(TEx).Name}, gelen {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                }
            }

            PluginWorkerOptions Options(bool autoRestart = false) => new()
            {
                HostPath = hostDllPath,
                MaxConcurrency = 1,
                HeartbeatIntervalMs = 1000,
                AutoRestartOnCrash = autoRestart,
                NotifyOnCrash = false
            };

            async Task RunScenarioAsync(IPluginBuilder b)
            {
                Console.WriteLine($"\n===== {(b.IsSandboxed ? "SANDBOX" : "IN-PROCESS")} (IncludeNonPublic={b.IncludeNonPublic}) =====");

                Console.WriteLine("-- Metot çağırma (sync/async, void/değer/Task/Task<T>) --");
                Check("Invoke<int> Add(3,4)=7", b.Invoke<int>("Add", 3, 4) == 7);
                Check("Invoke (object) Add(1,1)=2", Equals(b.Invoke("Add", 1, 1), 2));
                Check("InvokeAsync<int> AddAsync(10,20)=30", await b.InvokeAsync<int>("AddAsync", 10, 20) == 30);
                Check("Invoke<int> SENKRON ama Task<int> metot: AddAsync(1,2)=3", b.Invoke<int>("AddAsync", 1, 2) == 3);
                b.Execute("DoSomething", 42);
                Check("Execute DoSomething(42) -> field LastVoidCallValue=42", b.GetValue<int>("LastVoidCallValue") == 42);
                await b.ExecuteAsync("DoAsyncWork", 5);
                Check("ExecuteAsync DoAsyncWork(5) -> field=50 (Task beklendi)", await b.GetValueAsync<int>("LastVoidCallValue") == 50);
                Check("Invoke<long> (int dönen metot, genişletme) Add(2,2)=4L", b.Invoke<long>("Add", 2, 2) == 4L);
                Check("Invoke<string> Greet", b.Invoke<string>("Greet", "Ali", "Selam") == "Selam, Ali!");
                Check("static metot StaticTwice(21)=42", b.Invoke<int>("StaticTwice", 21) == 42);
                Check("overload Combine(int,int)=12", b.Invoke<int>("Combine", 1, 2) == 12);
                Check("overload Combine(string,string)=\"a+b\"", b.Invoke<string>("Combine", "a", "b") == "a+b");
                int c1 = b.Invoke<int>("Increment"), c2 = b.Invoke<int>("Increment");
                Check("state korunuyor: Increment iki kez -> 1,2", c1 == 1 && c2 == 2, $"{c1},{c2}");

                Console.WriteLine("-- Property / field --");
                Check("GetValue<string> DisplayName = default", b.GetValue<string>("DisplayName") == "default");
                b.SetValue("DisplayName", "yeni ad");
                Check("SetValue + GetValue DisplayName", b.GetValue<string>("DisplayName") == "yeni ad");
                await b.SetValueAsync("Counter", 100);
                Check("SetValueAsync field Counter=100, sonra Increment=101", b.Invoke<int>("Increment") == 101);
                Check("readonly field okunuyor ReadOnlyValue=10", b.GetValue<int>("ReadOnlyValue") == 10);
                await ExpectAsync<MissingMemberException>("readonly field'a yazma -> MissingMember/MethodException",
                    () => b.SetValueAsync("ReadOnlyValue", 1));

                Console.WriteLine("-- Enum (host plugin'in enum tipini bilmiyor, int kullanıyor) --");
                Check("GetValue<int> CurrentLevel = 1 (Low)", b.GetValue<int>("CurrentLevel") == 1);
                b.SetValue("CurrentLevel", 3);
                Check("SetValue(int 3) -> CurrentLevel = 3 (High)", b.GetValue<int>("CurrentLevel") == 3);
                Check("enum parametre+dönüş: NextLevel(1)=2", b.Invoke<int>("NextLevel", 1) == 2);

                Console.WriteLine("-- Complex (host kendi PointDto'sunu kullanıyor) --");
                var loc = b.GetValue<PointDto>("Location");
                Check("GetValue<PointDto> Location = (1,1)", loc is { X: 1, Y: 1 }, $"{loc?.X},{loc?.Y}");
                b.SetValue("Location", new PointDto { X = 5, Y = 6 });
                var loc2 = b.GetValue<PointDto>("Location");
                Check("SetValue(PointDto) -> Location = (5,6)", loc2 is { X: 5, Y: 6 }, $"{loc2?.X},{loc2?.Y}");
                var locJson = b.GetValue<JsonElement>("Location");
                Check("GetValue<JsonElement> Location.X = 5", locJson.GetProperty("X").GetInt32() == 5);
                Check("Complex ARGÜMAN: SumPoint(PointDto{2,3}) = 5", b.Invoke<int>("SumPoint", new PointDto { X = 2, Y = 3 }) == 5);
                var mp = b.Invoke<PointDto>("MakePoint", 7, 9);
                Check("Complex DÖNÜŞ: Invoke<PointDto> MakePoint(7,9)", mp is { X: 7, Y: 9 });

                Console.WriteLine("-- private üyeler --");
                if (b.IncludeNonPublic)
                {
                    Check("private field _secretCounter = 5", b.GetValue<int>("_secretCounter") == 5);
                    b.SetValue("_secretCounter", 99);
                    Check("private field SetValue -> 99", b.GetValue<int>("_secretCounter") == 99);
                    Check("private property SecretName = hidden", b.GetValue<string>("SecretName") == "hidden");
                    Check("private metot MultiplySecret(6,7)=42", b.Invoke<int>("MultiplySecret", 6, 7) == 42);
                }
                else
                {
                    await ExpectAsync<MissingMemberException>("private field görünmüyor", () => b.GetValueAsync<int>("_secretCounter"));
                    await ExpectAsync<MissingMethodException>("private metot görünmüyor", () => b.InvokeAsync<int>("MultiplySecret", 6, 7));
                }

                Console.WriteLine("-- GetFunc / GetAction (bir kez çözülür, çok kez çağrılır) --");
                var add = b.GetFunc<int>("Add", new object?[] { 0, 0 });
                long sum = 0;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 2000; i++) sum += add(new object?[] { i, 1 });
                Check("GetFunc<int> Add x2000", sum == Enumerable.Range(0, 2000).Sum(i => (long)i + 1), $"{sw.ElapsedMilliseconds}ms");
                var addAsync = b.GetFuncAsync<int>("AddAsync", new object?[] { 0, 0 });
                Check("GetFuncAsync<int> AddAsync(4,5)=9", await addAsync(new object?[] { 4, 5 }) == 9);
                var getFuncTask = b.GetFunc<int>("AddAsync", new object?[] { 0, 0 });
                Check("GetFunc<int> Task<int> dönen metot (senkron beklenir)=11", getFuncTask(new object?[] { 5, 6 }) == 11);
                var doSomething = b.GetAction("DoSomething", new object?[] { 0 });
                doSomething(new object?[] { 7 });
                Check("GetAction DoSomething(7) -> field=7", b.GetValue<int>("LastVoidCallValue") == 7);
                var doAsyncAction = b.GetAction("DoAsyncWork", new object?[] { 0 });
                doAsyncAction(new object?[] { 3 });
                Check("GetAction Task dönen metot BEKLENİYOR (fire-and-forget değil) -> field=30", b.GetValue<int>("LastVoidCallValue") == 30);
                var doAsyncFn = b.GetActionAsync("DoAsyncWork", new object?[] { 0 });
                await doAsyncFn(new object?[] { 4 });
                Check("GetActionAsync DoAsyncWork(4) -> field=40", b.GetValue<int>("LastVoidCallValue") == 40);

                Console.WriteLine("-- Hatalar (iki modda AYNI exception tipleri) --");
                await ExpectAsync<PluginInvocationException>("plugin exception -> PluginInvocationException",
                    () => b.InvokeAsync("Throws"), ex => ex.RemoteExceptionType == typeof(InvalidOperationException).FullName);
                await ExpectAsync<MissingMethodException>("olmayan metot -> MissingMethodException", () => b.InvokeAsync("YokBoyleMetot"));
                await ExpectAsync<MissingMemberException>("olmayan üye -> MissingMemberException", () => b.GetValueAsync<int>("YokBoyleUye"));
                Check("hatalardan sonra instance hâlâ çalışıyor", b.Invoke<int>("Add", 1, 2) == 3);

                Console.WriteLine("-- Timeout (sadece bekleme kesilir) --");
                b.DefaultTimeoutMs = 150;
                await ExpectAsync<TimeoutException>("DefaultTimeoutMs=150, SlowAsync(1000) -> TimeoutException", () => b.InvokeAsync("SlowAsync", 1000));
                b.DefaultTimeoutMs = null;
                Check("timeout sonrası çalışmaya devam", b.Invoke<int>("Add", 2, 2) == 4);

                Console.WriteLine("-- ForgetCache --");
                b.ForgetCache();
                Check("ForgetCache sonrası GetValue hâlâ doğru", b.GetValue<string>("DisplayName") == "yeni ad");
            }

            // 1) Sandbox, varsayılan görünürlük
            await using (var h = new PluginWorkerHandle(dllPath, TypeName, Options()))
            {
                await h.StartAsync();
                await RunScenarioAsync(h.Builder);
            }

            // 2) Sandbox, includeNonPublic
            await using (var h = new PluginWorkerHandle(dllPath, TypeName, Options(), includeNonPublic: true))
            {
                await h.StartAsync();
                await RunScenarioAsync(h.Builder);
            }

            // 3) In-process, varsayılan görünürlük
            {
                var loader = new ManagedDotNetPluginLoader();
                await loader.LoadInProcessAsync(dllPath, TypeName);
                await RunScenarioAsync(loader.Builder!.AsPluginBuilder());
            }

            // 4) In-process, includeNonPublic
            {
                var loader = new ManagedDotNetPluginLoader();
                await loader.LoadInProcessAsync(dllPath, TypeName, includeNonPublic: true);
                await RunScenarioAsync(loader.Builder!.AsPluginBuilder());
            }

            // 5) Sandbox'a özgü: GetFunc delegate'i ve builder worker restart'ından SONRA da çalışmalı
            Console.WriteLine("\n===== SANDBOX: restart sonrası builder + önceden alınmış delegate =====");
            await using (var h = new PluginWorkerHandle(dllPath, TypeName, Options(autoRestart: true)))
            {
                await h.StartAsync();
                IPluginBuilder b = h.Builder;
                var add = b.GetFunc<int>("Add", new object?[] { 0, 0 });
                Check("restart öncesi add(1,2)=3", add(new object?[] { 1, 2 }) == 3);
                b.SetValue("DisplayName", "restart öncesi");
                int oldPid = h.ProcessId!.Value;
                Process.GetProcessById(oldPid).Kill(entireProcessTree: true);
                var until = DateTime.UtcNow.AddSeconds(10);
                while ((h.Generation < 2 || h.IsDead) && DateTime.UtcNow < until) await Task.Delay(100);
                Check("worker yeniden başladı (Generation=2)", h.Generation == 2 && !h.IsDead, $"gen={h.Generation}");
                Check("AYNI delegate restart sonrası çalışıyor add(10,20)=30", add(new object?[] { 10, 20 }) == 30);
                Check("builder restart sonrası çalışıyor", b.Invoke<int>("Add", 5, 5) == 10);
                Check("state yeni process'te SIFIRDAN (bilinen davranış): DisplayName=default", b.GetValue<string>("DisplayName") == "default");
            }

            // 6) Promote: IPluginBuilder'a yazılmış kod sandbox -> in-process geçişinde DEĞİŞMEDEN çalışmalı
            Console.WriteLine("\n===== PROMOTE: aynı kod, sandbox'tan in-process'e =====");
            static int BusinessLogic(IPluginBuilder b) => b.Invoke<int>("Add", 20, 22) + b.GetValue<int>("ReadOnlyValue");
            var ph = new PluginWorkerHandle(dllPath, TypeName, Options());
            await ph.StartAsync();
            IPluginBuilder current = ph.Builder;
            Check("iş kodu sandbox'ta = 52", BusinessLogic(current) == 52, current.IsSandboxed ? "sandbox" : "in-process");
            var promotedLoader = await ph.PromoteToInProcessAsync();
            current = promotedLoader.Builder!.AsPluginBuilder();
            Check("AYNI iş kodu promote sonrası = 52", BusinessLogic(current) == 52, current.IsSandboxed ? "sandbox" : "in-process");
            await ph.DisposeAsync();

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "TÜM PARİTE TESTLERİ GEÇTİ." : $"{failures} TEST BAŞARISIZ.");
            //return failures == 0 ? 0 : 1;


        }
    }

    public sealed class PointDto
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
}

