using DSO.Core.Evoker;
using DSO.Core.Evoker.Plugins;
using DSO.Core.Evoker.Plugins.Loading;
using DSO.Core.Evoker.Plugins.Management;
using DSO.Core.Evoker.Plugins.Sandbox;
using DSO.Core.Evoker.Plugins.Scanning;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

namespace DSO.Core.Evoker.Plugins.TestApi
{
    public static class PluginsTest
    {
        static string PluginsFolderPath = Path.Combine(AppContext.BaseDirectory, "Plugins");
        static string HostFolderPath = Path.Combine(AppContext.BaseDirectory, "Host");
        static string dllPath = Path.Combine(PluginsFolderPath, "SamplePlugin.dll"); //args.Length > 0 ? args[0] : throw new ArgumentException("SamplePlugin.dll yolu gerekli.");
        static string hostDllPath = Path.Combine(HostFolderPath, "DSO.Core.Evoker.PluginHost.dll"); //args.Length > 1 ? args[1] : throw new ArgumentException("PluginHost.dll yolu gerekli.");

        public static void Test1()
        {
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

            const string TypeName = "TestPlugin.SamplePlugin";

            if (!PreflightSamplePlugin(dllPath))
            {
                Console.WriteLine(2);
                return;
            }

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
                try { await RunScenarioCoreAsync(b); }
                catch (Exception ex)
                {
                    // Bir kontrolün içindeki çağrı beklenmedik bir exception fırlatırsa test programı KAPANMASIN:
                    // bu modun kalan kontrolleri atlanır, hata raporlanır, diğer modlara devam edilir.
                    Check($"senaryo beklenmedik bir hatayla KESİLDİ ({(b.IsSandboxed ? "sandbox" : "in-process")})", false, $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            async Task RunScenarioCoreAsync(IPluginBuilder b)
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

                Console.WriteLine("-- Optional parametre + büyük/küçük harf (VB.NET) --");
                Check("Greet(\"Ali\") - optional greeting varsayılanı", b.Invoke<string>("Greet", "Ali") == "Merhaba, Ali!");
                Check("optionaldemo(1) küçük harf + 2 optional", b.Invoke<string>("optionaldemo", 1) == "1|5|x");
                Check("OptionalDemo(1,2)", b.Invoke<string>("OptionalDemo", 1, 2) == "1|2|x");
                Check("property küçük harf: displayname", b.GetValue<string>("displayname") == "yeni ad");
                Check("GetFunc optional ile: sampleArgs 1 eleman", b.GetFunc<string>("OptionalDemo", new object?[] { 0 })(new object?[] { 7 }) == "7|5|x");

                Console.WriteLine("-- Event'ler --");
                var names = b.GetEventNames();
                Check("GetEventNames", new[] { "CounterChanged", "PointMoved", "Progress" }.All(names.Contains), string.Join(",", names));
                var counterValues = new System.Collections.Concurrent.ConcurrentQueue<int>();
                object? senderSeen = "set edilmedi";
                var counterSub = b.Subscribe("CounterChanged", e => { senderSeen = e[0]; counterValues.Enqueue(e.Get<int>(1)); });
                int before = b.Invoke<int>("Increment");
                int after = b.Invoke<int>("Increment");
                await WaitUntil(() => counterValues.Count >= 2);
                Check("EventHandler<int>: iki Increment -> iki event, doğru değerler", counterValues.SequenceEqual(new[] { before, after }), string.Join(",", counterValues));
                Check("sender (plugin'in kendisi) null geliyor", senderSeen == null);
                counterSub.Dispose();
                await Task.Delay(100);
                b.Invoke<int>("Increment");
                await Task.Delay(200);
                Check("Dispose sonrası event gelmiyor", counterValues.Count == 2, counterValues.Count.ToString());

                var progress = new System.Collections.Concurrent.ConcurrentQueue<int>();
                using (await b.SubscribeAsync("progress", e => progress.Enqueue(e.Get<int>(1))))   // küçük harf isim
                {
                    b.Invoke<int>("RunWithProgress", 200);
                    await WaitUntil(() => progress.Count >= 200);
                }
                Check("Action<string,int> event, 200 bildirim, SIRA korunuyor", progress.SequenceEqual(Enumerable.Range(1, 200)), progress.Count.ToString());

                PointDto? moved = null;
                using (b.Subscribe("PointMoved", e => moved = e.Get<PointDto>(1)))
                {
                    b.Execute("MoveTo", 11, 12);
                    await WaitUntil(() => moved != null);
                }
                Check("Complex event argümanı -> host DTO'su: PointMoved(11,12)", moved is { X: 11, Y: 12 }, $"{moved?.X},{moved?.Y}");

                int okAfterThrow = 0;
                using (b.Subscribe("CounterChanged", e => { okAfterThrow++; throw new Exception("handler hatası (kasıtlı)"); }))
                {
                    int c = b.Invoke<int>("Increment");
                    await WaitUntil(() => okAfterThrow >= 1);
                    Check("handler exception fırlatsa da plugin çağrısı başarılı döndü", c > 0);
                }
                Check("handler hatasından sonra plugin çalışıyor", b.Invoke<int>("Add", 1, 1) == 2);
                await ExpectAsync<MissingMemberException>("olmayan event -> MissingMemberException", () => b.SubscribeAsync("YokBoyleEvent", _ => { }));

                Console.WriteLine("-- Toplu çağrı (InvokeBatchAsync) --");
                var batchArgs = Enumerable.Range(0, 5000).Select(i => new object?[] { i, 1 }).ToList();
                var swb = Stopwatch.StartNew();
                var batch = await b.InvokeBatchAsync<int>("Add", batchArgs);
                long batchMs = swb.ElapsedMilliseconds;
                Check("InvokeBatchAsync Add x5000 - sonuçlar sırayla doğru", batch.Length == 5000 && batch.Select((v, i) => v == i + 1).All(x => x), $"{batchMs}ms");
                var getFuncAdd = b.GetFunc<int>("Add", new object?[] { 0, 0 });
                swb.Restart();
                for (int i = 0; i < 5000; i++) getFuncAdd(new object?[] { i, 1 });
                long singleMs = swb.ElapsedMilliseconds;
                Console.WriteLine($"     (5000 çağrı: tek tek GetFunc {singleMs}ms, toplu {batchMs}ms)");
                var asyncBatch = await b.InvokeBatchAsync<int>("AddAsync", new[] { new object?[] { 1, 2 }, new object?[] { 3, 4 } });
                Check("Task<int> dönen metotla toplu çağrı", asyncBatch.SequenceEqual(new[] { 3, 7 }));
                await ExpectAsync<PluginInvocationException>("toplu çağrıda 3. eleman (index 2) hata -> BatchIndex=2",
                    () => b.InvokeBatchAsync<int>("CheckedDivide", new[] { new object?[] { 10, 2 }, new object?[] { 9, 3 }, new object?[] { 1, 0 }, new object?[] { 8, 4 } }),
                    ex => ex.BatchIndex == 2 && ex.RemoteExceptionType == typeof(DivideByZeroException).FullName);
                await b.ExecuteBatchAsync("DoSomething", new[] { new object?[] { 1 }, new object?[] { 2 }, new object?[] { 3 } });
                Check("ExecuteBatchAsync - son çağrının etkisi görünüyor (field=3)", b.GetValue<int>("LastVoidCallValue") == 3);

                Console.WriteLine("-- ForgetCache --");
                b.ForgetCache();
                Check("ForgetCache sonrası GetValue hâlâ doğru", b.GetValue<string>("DisplayName") == "yeni ad");
            }

            static async Task WaitUntil(Func<bool> cond, int timeoutMs = 5000)
            {
                var sw = Stopwatch.StartNew();
                while (!cond() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
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
                Check("restart ÖNCESİ kurulan event aboneliği restart SONRASI da çalışıyor", await EventAfterRestart());

                async Task<bool> EventAfterRestart()
                {
                    // Abonelik restart öncesi kurulmuş olmalıydı - bunun için ikinci bir handle ile aynı akışı kısaca tekrar ediyoruz.
                    await using var h2 = new PluginWorkerHandle(dllPath, TypeName, Options(autoRestart: true));
                    await h2.StartAsync();
                    int got = 0;
                    using var sub = h2.Builder.Subscribe("CounterChanged", e => got = e.Get<int>(1));
                    Process.GetProcessById(h2.ProcessId!.Value).Kill(entireProcessTree: true);
                    var until2 = DateTime.UtcNow.AddSeconds(10);
                    while ((h2.Generation < 2 || h2.IsDead) && DateTime.UtcNow < until2) await Task.Delay(100);
                    await Task.Delay(300); // yeniden abonelik mesajının gitmesi için
                    h2.Builder.Invoke<int>("Increment");
                    await WaitUntil(() => got == 1);
                    return got == 1;
                }
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

            // --- Ön kontrol: verilen SamplePlugin.dll bu test kitiyle aynı sürüm mü? ---
            // (Eski bir build verilirse testler anlaşılmaz "metot bulunamadı" hatalarıyla kesiliyordu.)
            static bool PreflightSamplePlugin(string dll)
            {
                var scan = DSO.Core.Evoker.Plugins.Scanning.PluginScanner.Scan(dll);
                var t = scan.Types.FirstOrDefault(x => x.FullName == "TestPlugin.SamplePlugin");
                var methods = t?.Methods.Select(m => m.Name).ToHashSet() ?? new HashSet<string>();
                var events = t?.Events.Select(e => e.Name).ToHashSet() ?? new HashSet<string>();
                var missing = new[] { "Add", "AddAsync", "MakePoint", "SlowAsync", "OptionalDemo", "UseDependency", "CheckedDivide", "RunWithProgress", "MoveTo", "Increment" }
                    .Where(m => !methods.Contains(m))
                    .Concat(new[] { "CounterChanged", "Progress", "PointMoved" }.Where(e => !events.Contains(e)).Select(e => "event " + e))
                    .ToList();
                if (missing.Count == 0) return true;
                Console.WriteLine($"[ÖN KONTROL HATASI] Verilen SamplePlugin.dll bu test kitinden ESKİ: {dll}");
                Console.WriteLine($"  Dosya tarihi: {File.GetLastWriteTime(dll):yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"  Eksik üyeler: {string.Join(", ", missing)}");
                Console.WriteLine("  TestKit/SamplePlugin'i güncel SamplePlugin.cs + SamplePlugin.csproj (SampleDep referansı) ile yeniden derleyip");
                Console.WriteLine("  testi o build çıktısındaki SamplePlugin.dll ile çalıştırın.");
                return false;
            }

        }
    }

    public static class PariteTesti
    {
        static string PluginsFolderPath = Path.Combine(AppContext.BaseDirectory, "Plugins");
        static string HostFolderPath = Path.Combine(AppContext.BaseDirectory, "Host");
        static string dllPath = Path.Combine(PluginsFolderPath, "SamplePlugin.dll"); //args.Length > 0 ? args[0] : throw new ArgumentException("SamplePlugin.dll yolu gerekli.");
        static string hostDllPath = Path.Combine(HostFolderPath, "DSO.Core.Evoker.PluginHost.dll"); //args.Length > 1 ? args[1] : throw new ArgumentException("PluginHost.dll yolu gerekli.");
        static string TypeName = "TestPlugin.SamplePlugin";

        // IPluginBuilder parite testi: AYNI senaryo gövdesi (RunScenarioAsync) hem sandbox (ayrı worker process,
        // IPC) hem in-process (doğrudan EvokerBuilder) implementasyonuna karşı çalıştırılır. İkisi aynı sonucu
        // vermeli - uygulama kodu IPluginBuilder'a bir kez yazılır, plugin nerede çalışırsa çalışsın değişmez.
        //
        // Kullanım: dotnet run -- <SamplePlugin.dll yolu> <DSO.Core.Evoker.PluginHost.dll yolu>
        // NOT: Bu proje SamplePlugin'e derleme zamanı referans VERMİYOR - host plugin tiplerini (Point, Level)
        // bilmiyor; kendi PointDto'sunu ve int'i kullanıyor (gerçek senaryo).

        public static async Task TestParite()
        {
            if (!PreflightSamplePlugin(dllPath))
            {
                Console.WriteLine(2);
                return;
            }

            int failures = 0;
            var DescribeStructures = new Dictionary<bool, string>();
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
                try { await RunScenarioCoreAsync(b); }
                catch (Exception ex)
                {
                    // Bir kontrolün içindeki çağrı beklenmedik bir exception fırlatırsa test programı KAPANMASIN:
                    // bu modun kalan kontrolleri atlanır, hata raporlanır, diğer modlara devam edilir.
                    Check($"senaryo beklenmedik bir hatayla KESİLDİ ({(b.IsSandboxed ? "sandbox" : "in-process")})", false, $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            async Task RunScenarioCoreAsync(IPluginBuilder b)
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

                Console.WriteLine("-- Tipli delegate'ler (GetTypedFunc / GetTypedAction) --");
                var tAdd = b.GetTypedFunc<int, int, int>("Add");
                long tSum = 0;
                for (int i = 0; i < 2000; i++) tSum += tAdd(i, 1);
                Check("GetTypedFunc<int,int,int> Add x2000", tSum == Enumerable.Range(0, 2000).Sum(i => (long)i + 1));
                Check("dönüş genişletme: GetTypedFunc<int,int,long> Add(2,3)=5L", b.GetTypedFunc<int, int, long>("Add")(2, 3) == 5L);
                Check("Task<int> dönen metot bekleniyor: GetTypedFunc<int,int,int> AddAsync(4,5)=9", b.GetTypedFunc<int, int, int>("AddAsync")(4, 5) == 9);
                Check("Complex argüman (host DTO): GetTypedFunc<PointDto,int> SumPoint", b.GetTypedFunc<PointDto, int>("SumPoint")(new PointDto { X = 2, Y = 3 }) == 5);
                var tmp = b.GetTypedFunc<int, int, PointDto>("MakePoint")(7, 9);
                Check("Complex dönüş (host DTO): GetTypedFunc<int,int,PointDto> MakePoint", tmp is { X: 7, Y: 9 });
                Check("overload tipe göre: GetTypedFunc<string,string,string> Combine", b.GetTypedFunc<string, string, string>("Combine")("a", "b") == "a+b");
                Check("static: GetTypedFunc<int,int> StaticTwice", b.GetTypedFunc<int, int>("StaticTwice")(21) == 42);
                b.GetTypedAction<int>("DoSomething")(8);
                Check("GetTypedAction<int> DoSomething(8) -> field=8", b.GetValue<int>("LastVoidCallValue") == 8);
                b.GetTypedAction<int>("DoAsyncWork")(2);
                Check("GetTypedAction Task dönen metot BEKLENİYOR -> field=20", b.GetValue<int>("LastVoidCallValue") == 20);
                var tDiv = b.GetTypedFunc<int, int, int>("CheckedDivide");
                await ExpectAsync<PluginInvocationException>("tipli delegate'te plugin hatası -> PluginInvocationException",
                    () => { tDiv(1, 0); return Task.CompletedTask; }, ex => ex.RemoteExceptionType == typeof(DivideByZeroException).FullName);
                Check("hatadan sonra aynı tipli delegate çalışıyor", tDiv(10, 2) == 5);
                if (b.IncludeNonPublic)
                    Check("private: GetTypedFunc<int,int,int> MultiplySecret(6,7)=42", b.GetTypedFunc<int, int, int>("MultiplySecret")(6, 7) == 42);

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

                Console.WriteLine("-- Optional parametre + büyük/küçük harf (VB.NET) --");
                Check("Greet(\"Ali\") - optional greeting varsayılanı", b.Invoke<string>("Greet", "Ali") == "Merhaba, Ali!");
                Check("optionaldemo(1) küçük harf + 2 optional", b.Invoke<string>("optionaldemo", 1) == "1|5|x");
                Check("OptionalDemo(1,2)", b.Invoke<string>("OptionalDemo", 1, 2) == "1|2|x");
                Check("property küçük harf: displayname", b.GetValue<string>("displayname") == "yeni ad");
                Check("GetFunc optional ile: sampleArgs 1 eleman", b.GetFunc<string>("OptionalDemo", new object?[] { 0 })(new object?[] { 7 }) == "7|5|x");

                Console.WriteLine("-- Event'ler --");
                var names = b.GetEventNames();
                Check("GetEventNames", new[] { "CounterChanged", "PointMoved", "Progress" }.All(names.Contains), string.Join(",", names));
                var counterValues = new System.Collections.Concurrent.ConcurrentQueue<int>();
                object? senderSeen = "set edilmedi";
                var counterSub = b.Subscribe("CounterChanged", e => { senderSeen = e[0]; counterValues.Enqueue(e.Get<int>(1)); });
                int before = b.Invoke<int>("Increment");
                int after = b.Invoke<int>("Increment");
                await WaitUntil(() => counterValues.Count >= 2);
                Check("EventHandler<int>: iki Increment -> iki event, doğru değerler", counterValues.SequenceEqual(new[] { before, after }), string.Join(",", counterValues));
                Check("sender (plugin'in kendisi) null geliyor", senderSeen == null);
                counterSub.Dispose();
                await Task.Delay(100);
                b.Invoke<int>("Increment");
                await Task.Delay(200);
                Check("Dispose sonrası event gelmiyor", counterValues.Count == 2, counterValues.Count.ToString());

                var progress = new System.Collections.Concurrent.ConcurrentQueue<int>();
                using (await b.SubscribeAsync("progress", e => progress.Enqueue(e.Get<int>(1))))   // küçük harf isim
                {
                    b.Invoke<int>("RunWithProgress", 200);
                    await WaitUntil(() => progress.Count >= 200);
                }
                Check("Action<string,int> event, 200 bildirim, SIRA korunuyor", progress.SequenceEqual(Enumerable.Range(1, 200)), progress.Count.ToString());

                PointDto? moved = null;
                using (b.Subscribe("PointMoved", e => moved = e.Get<PointDto>(1)))
                {
                    b.Execute("MoveTo", 11, 12);
                    await WaitUntil(() => moved != null);
                }
                Check("Complex event argümanı -> host DTO'su: PointMoved(11,12)", moved is { X: 11, Y: 12 }, $"{moved?.X},{moved?.Y}");

                int okAfterThrow = 0;
                using (b.Subscribe("CounterChanged", e => { okAfterThrow++; throw new Exception("handler hatası (kasıtlı)"); }))
                {
                    int c = b.Invoke<int>("Increment");
                    await WaitUntil(() => okAfterThrow >= 1);
                    Check("handler exception fırlatsa da plugin çağrısı başarılı döndü", c > 0);
                }
                Check("handler hatasından sonra plugin çalışıyor", b.Invoke<int>("Add", 1, 1) == 2);
                await ExpectAsync<MissingMemberException>("olmayan event -> MissingMemberException", () => b.SubscribeAsync("YokBoyleEvent", _ => { }));

                Console.WriteLine("-- Toplu çağrı (InvokeBatchAsync) --");
                var batchArgs = Enumerable.Range(0, 5000).Select(i => new object?[] { i, 1 }).ToList();
                var swb = Stopwatch.StartNew();
                var batch = await b.InvokeBatchAsync<int>("Add", batchArgs);
                long batchMs = swb.ElapsedMilliseconds;
                Check("InvokeBatchAsync Add x5000 - sonuçlar sırayla doğru", batch.Length == 5000 && batch.Select((v, i) => v == i + 1).All(x => x), $"{batchMs}ms");
                var getFuncAdd = b.GetFunc<int>("Add", new object?[] { 0, 0 });
                swb.Restart();
                for (int i = 0; i < 5000; i++) getFuncAdd(new object?[] { i, 1 });
                long singleMs = swb.ElapsedMilliseconds;
                Console.WriteLine($"     (5000 çağrı: tek tek GetFunc {singleMs}ms, toplu {batchMs}ms)");
                var asyncBatch = await b.InvokeBatchAsync<int>("AddAsync", new[] { new object?[] { 1, 2 }, new object?[] { 3, 4 } });
                Check("Task<int> dönen metotla toplu çağrı", asyncBatch.SequenceEqual(new[] { 3, 7 }));
                await ExpectAsync<PluginInvocationException>("toplu çağrıda 3. eleman (index 2) hata -> BatchIndex=2",
                    () => b.InvokeBatchAsync<int>("CheckedDivide", new[] { new object?[] { 10, 2 }, new object?[] { 9, 3 }, new object?[] { 1, 0 }, new object?[] { 8, 4 } }),
                    ex => ex.BatchIndex == 2 && ex.RemoteExceptionType == typeof(DivideByZeroException).FullName);
                await b.ExecuteBatchAsync("DoSomething", new[] { new object?[] { 1 }, new object?[] { 2 }, new object?[] { 3 } });
                Check("ExecuteBatchAsync - son çağrının etkisi görünüyor (field=3)", b.GetValue<int>("LastVoidCallValue") == 3);

                Console.WriteLine("-- Plugin tanımı (DescribeAsync -> JSON) --");
                var desc = await b.DescribeAsync();
                var json = desc.ToJson();
                string jsonFile = Path.Combine(Path.GetTempPath(), $"plugin-describe-{(b.IsSandboxed ? "sandbox" : "inprocess")}-{(b.IncludeNonPublic ? "nonpublic" : "public")}.json");
                File.WriteAllText(jsonFile, json);
                Console.WriteLine($"     (JSON: {jsonFile}, {json.Length} karakter)");
                var dm = desc.Type.Methods ?? new();
                var dp = desc.Type.Properties ?? new();
                var df = desc.Type.Fields ?? new();
                var opt = dm.FirstOrDefault(x => x.Name == "OptionalDemo");
                Check("OptionalDemo: 3 parametre, b optional varsayılan 5, s varsayılan \"x\"",
                    opt?.Parameters?.Count == 3 && opt.Parameters[1].HasDefaultValue == true && Convert.ToInt32(opt.Parameters[1].DefaultValue) == 5
                    && (string?)opt.Parameters[2].DefaultValue == "x");
                Check("private metot listede: MultiplySecret (Private)", dm.Any(x => x.Name == "MultiplySecret" && x.Visibility == MemberVisibility.Private));
                Check("private field ve property listede: _secretCounter, SecretName",
                    df.Any(x => x.Name == "_secretCounter" && x.Visibility == MemberVisibility.Private) && dp.Any(x => x.Name == "SecretName" && x.Getter == MemberVisibility.Private));
                Check("gürültü yok: GetType/ToString/Equals, get_/set_/add_, k__BackingField, event alanı",
                    !dm.Any(x => x.Name is "GetType" or "ToString" or "Equals" or "GetHashCode" || x.Name.StartsWith("get_") || x.Name.StartsWith("add_"))
                    && !json.Contains("k__BackingField") && !df.Any(x => x.Name is "CounterChanged" or "Progress" or "PointMoved"));
                Check("PascalCase alan adları + enum metin", json.Contains("\"ReturnType\"") && !json.Contains("\"returnType\"") && json.Contains("\"Visibility\": \"Private\""));
                Check("okunur tip adları: AddAsync -> Task<int>, IsAsync",
                    dm.Any(x => x.Name == "AddAsync" && x.ReturnType == "Task<int>" && x.IsAsync == true));
                Check("static metot işaretli: StaticTwice", dm.Any(x => x.Name == "StaticTwice" && x.IsStatic == true));
                Check("readonly field: ReadOnlyValue IsReadOnly", df.Any(x => x.Name == "ReadOnlyValue" && x.IsReadOnly == true));
                Check("event argümanları: CounterChanged(object, int)",
                    desc.Type.Events?.FirstOrDefault(e => e.Name == "CounterChanged")?.Arguments?.Select(a => a.Type).SequenceEqual(new[] { "object", "int" }) == true);
                Check("assembly bilgisi: Sha256, sürüm, hedef framework", desc.Assembly.Sha256?.Length == 64 && desc.Assembly.Version != null && desc.Assembly.TargetFramework != null,
                    $"{desc.Assembly.Name} {desc.Assembly.Version} {desc.Assembly.TargetFramework}");
                Check("değer: DisplayName = \"yeni ad\"", dp.First(x => x.Name == "DisplayName").Value?.GetString() == "yeni ad");
                var locVal = dp.First(x => x.Name == "Location").Value;
                Check("değer: Location (Complex) -> {X,Y} nesnesi", locVal?.ValueKind == System.Text.Json.JsonValueKind.Object && locVal.Value.TryGetProperty("X", out _));
                Check("değer: field Counter sayı", df.First(x => x.Name == "Counter").Value?.ValueKind == System.Text.Json.JsonValueKind.Number);
                var secret = df.First(x => x.Name == "_secretCounter");
                Check(b.IncludeNonPublic ? "değer: private _secretCounter okunuyor" : "private _secretCounter: değer yerine açıklayıcı ValueError",
                    b.IncludeNonPublic ? secret.Value?.ValueKind == System.Text.Json.JsonValueKind.Number : secret.Value == null && secret.ValueError?.Contains("IncludeNonPublic") == true,
                    secret.Value?.GetRawText() ?? secret.ValueError ?? "");
                Check("Values bilgisi: kaynak doğru", desc.Values?.Source == (b.IsSandboxed ? "Sandbox" : "InProcess"));
                // Yapı (değerler hariç) sandbox ve in-process'te BİREBİR aynı olmalı
                var structure = (await b.DescribeAsync(includeValues: false)).Type;
                var structJson = System.Text.Json.JsonSerializer.Serialize(structure, DSO.Core.Evoker.Plugins.Scanning.PluginDescriptor.JsonOptions);
                if (DescribeStructures.TryGetValue(b.IncludeNonPublic, out var other))
                    Check("yapı tanımı sandbox ve in-process'te BİREBİR aynı", other == structJson);
                else DescribeStructures[b.IncludeNonPublic] = structJson;

                Console.WriteLine("-- ForgetCache --");
                b.ForgetCache();
                Check("ForgetCache sonrası GetValue hâlâ doğru", b.GetValue<string>("DisplayName") == "yeni ad");
            }

            static async Task WaitUntil(Func<bool> cond, int timeoutMs = 5000)
            {
                var sw = Stopwatch.StartNew();
                while (!cond() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
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
                Check("restart ÖNCESİ kurulan event aboneliği restart SONRASI da çalışıyor", await EventAfterRestart());

                async Task<bool> EventAfterRestart()
                {
                    // Abonelik restart öncesi kurulmuş olmalıydı - bunun için ikinci bir handle ile aynı akışı kısaca tekrar ediyoruz.
                    await using var h2 = new PluginWorkerHandle(dllPath, TypeName, Options(autoRestart: true));
                    await h2.StartAsync();
                    int got = 0;
                    using var sub = h2.Builder.Subscribe("CounterChanged", e => got = e.Get<int>(1));
                    Process.GetProcessById(h2.ProcessId!.Value).Kill(entireProcessTree: true);
                    var until2 = DateTime.UtcNow.AddSeconds(10);
                    while ((h2.Generation < 2 || h2.IsDead) && DateTime.UtcNow < until2) await Task.Delay(100);
                    await Task.Delay(300); // yeniden abonelik mesajının gitmesi için
                    h2.Builder.Invoke<int>("Increment");
                    await WaitUntil(() => got == 1);
                    return got == 1;
                }
            }

            // 5b) Derlenmiş şekil eşleyici (plugin tipi <-> host DTO): sonuç JSON yoluyla BİREBİR aynı olmalı
            Console.WriteLine("\n===== ŞEKİL EŞLEME: derlenmiş eşleyici == JSON =====");
            {
                static string J(object? o) => JsonSerializer.Serialize(o);
                static object? ViaJson(object v, Type t) => JsonSerializer.Deserialize(JsonSerializer.Serialize(v, v.GetType()), t);
                void Same(string label, object src, Type target)
                {
                    object? fast = null, slow = null; string? fe = null, se = null;
                    try { fast = WireValueCodec.ConvertTo(src, target); } catch (Exception ex) { fe = ex.GetType().Name; }
                    try { slow = ViaJson(src, target); } catch (Exception ex) { se = ex.GetType().Name; }
                    Check(label, fe == se && J(fast) == J(slow) && (fast == null || fast.GetType() == target), fe ?? J(fast));
                }
                Same("basit: X,Y", new MapSrcA { X = 1, Y = 2, Name = "a" }, typeof(MapDstA));
                Same("eksik/fazla property + null string", new MapSrcA { X = 3, Name = null }, typeof(MapDstB));
                Same("iç içe nesne", new MapSrcN { Id = 7, Inner = new MapSrcA { X = 4, Y = 5, Name = "i" } }, typeof(MapDstN));
                Same("iç içe null", new MapSrcN { Id = 8, Inner = null }, typeof(MapDstN));
                Same("tip uyuşmazlığı int->long (JSON'a düşer)", new MapSrcA { X = 9 }, typeof(MapDstLong));
                Same("[JsonPropertyName] (JSON'a düşer)", new MapSrcA { X = 1, Y = 2 }, typeof(MapDstAttr));
                Same("init-only hedef (JSON'a düşer)", new MapSrcA { X = 1, Y = 2 }, typeof(MapDstInit));
                Same("liste property (JSON'a düşer)", new MapSrcList { Items = new() { 1, 2, 3 } }, typeof(MapDstList));
                Same("struct hedef", new MapSrcA { X = 5, Y = 6 }, typeof(MapDstStruct));
                Same("DateTime/decimal/Guid/enum aynı tip", new MapSrcMix { D = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), M = 1.25m, G = Guid.Parse("11111111-2222-3333-4444-555555555555"), L = DayOfWeek.Friday }, typeof(MapDstMix));
                Same("yazılamaz hedef property atlanıyor", new MapSrcA { X = 1, Y = 2 }, typeof(MapDstReadOnly));
                Same("büyük/küçük harf farklı ad (JSON eşlemez)", new MapSrcA { X = 1, Y = 2 }, typeof(MapDstLower));
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

            // --- Ön kontrol: verilen SamplePlugin.dll bu test kitiyle aynı sürüm mü? ---
            // (Eski bir build verilirse testler anlaşılmaz "metot bulunamadı" hatalarıyla kesiliyordu.)
            static bool PreflightSamplePlugin(string dll)
            {
                var scan = DSO.Core.Evoker.Plugins.Scanning.PluginScanner.Scan(dll);
                var t = scan.Types.FirstOrDefault(x => x.FullName == "TestPlugin.SamplePlugin");
                var methods = t?.Methods.Select(m => m.Name).ToHashSet() ?? new HashSet<string>();
                var events = t?.Events.Select(e => e.Name).ToHashSet() ?? new HashSet<string>();
                var missing = new[] { "Add", "AddAsync", "MakePoint", "SlowAsync", "OptionalDemo", "UseDependency", "CheckedDivide", "RunWithProgress", "MoveTo", "Increment" }
                    .Where(m => !methods.Contains(m))
                    .Concat(new[] { "CounterChanged", "Progress", "PointMoved" }.Where(e => !events.Contains(e)).Select(e => "event " + e))
                    .ToList();
                if (missing.Count == 0) return true;
                Console.WriteLine($"[ÖN KONTROL HATASI] Verilen SamplePlugin.dll bu test kitinden ESKİ: {dll}");
                Console.WriteLine($"  Dosya tarihi: {File.GetLastWriteTime(dll):yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"  Eksik üyeler: {string.Join(", ", missing)}");
                Console.WriteLine("  TestKit/SamplePlugin'i güncel SamplePlugin.cs + SamplePlugin.csproj (SampleDep referansı) ile yeniden derleyip");
                Console.WriteLine("  testi o build çıktısındaki SamplePlugin.dll ile çalıştırın.");
                return false;
            }

        }

    }

    public static class PluginManagerTesti
    {
        static string PluginsFolderPath = Path.Combine(AppContext.BaseDirectory, "Plugins");
        static string HostFolderPath = Path.Combine(AppContext.BaseDirectory, "Host");
        static string dllPath = Path.Combine(PluginsFolderPath, "SamplePlugin.dll"); //args.Length > 0 ? args[0] : throw new ArgumentException("SamplePlugin.dll yolu gerekli.");
        static string hostDllPath = Path.Combine(HostFolderPath, "DSO.Core.Evoker.PluginHost.dll"); //args.Length > 1 ? args[1] : throw new ArgumentException("PluginHost.dll yolu gerekli.");
        static string TypeName = "TestPlugin.SamplePlugin";

        // PluginManager testi: kayıt + kalıcılık (JSON), uygulamanın elindeki TEK IPluginBuilder'ın canlı mod
        // geçişlerinde (sandbox <-> in-process) çalışmaya devam etmesi, event/GetFunc'ın geçişlerden sağ çıkması,
        // in-process'ten çıkarken belleğin gerçekten boşaltılması, çökme bildirimi, geçersiz kayıt reddi, pool ayar kontrolü.
        //
        // Kullanım: dotnet run -- <SamplePlugin.dll yolu> <DSO.Core.Evoker.PluginHost.dll yolu>

        public static async Task PluginManagerTest()
        {
            if (!PreflightSamplePlugin(dllPath))
            {
                Console.WriteLine(2);
                return;
            }

            int failures = 0;
            void Check(string label, bool ok, string detail = "")
            {
                Console.WriteLine($"  [{(ok ? "OK" : "HATA")}] {label}{(detail.Length > 0 ? "  -> " + detail : "")}");
                if (!ok) failures++;
            }
            async Task Expect<TEx>(string label, Func<Task> a) where TEx : Exception
            {
                try { await a(); Check(label, false, "exception bekleniyordu"); }
                catch (TEx ex) { Check(label, true, ex.GetType().Name); }
                catch (Exception ex) { Check(label, false, $"beklenen {typeof(TEx).Name}, gelen {ex.GetType().Name}: {ex.Message}"); }
            }
            static async Task WaitUntil(Func<bool> cond, int timeoutMs = 10000)
            {
                var sw = Stopwatch.StartNew();
                while (!cond() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(20);
            }

            string configPath = Path.Combine(Path.GetTempPath(), "dso-plugins-" + Guid.NewGuid().ToString("N")[..6] + ".json");
            var managerOptions = new PluginManagerOptions { HostPath = hostDllPath, NotifyOnCrash = false };

            Console.WriteLine("=== 1) Kayıt, doğrulama, kalıcılık ===");
            var mgr = new PluginManager(new JsonFilePluginConfigStore(configPath), managerOptions);
            await mgr.InitializeAsync();
            var r1 = await mgr.RegisterAsync(new PluginRegistration { Id = "sample", FilePath = dllPath, TypeFullName = TypeName, MaxConcurrency = 2 });
            Check("kayıt başarılı", r1.Success && r1.Id == "sample", r1.Message);
            Check("kayıt JSON dosyasına yazıldı", File.Exists(configPath) && File.ReadAllText(configPath).Contains("\"Mode\": \"Sandbox\""));
            var badType = await mgr.RegisterAsync(new PluginRegistration { Id = "x", FilePath = dllPath, TypeFullName = "Yok.BoyleBirTip" });
            Check("olmayan tip: exception YOK, mesaj var, eklenmedi", !badType.Success && !mgr.Registrations.Any(r => r.Id == "x"), badType.Message);
            var badFile = await mgr.RegisterAsync(new PluginRegistration { Id = "y", FilePath = "/yok/x.dll", TypeFullName = TypeName });
            Check("olmayan dosya: exception YOK, mesaj var, eklenmedi", !badFile.Success && !mgr.Registrations.Any(r => r.Id == "y"), badFile.Message);
            var dup = await mgr.RegisterAsync(new PluginRegistration { Id = "SAMPLE", FilePath = dllPath, TypeFullName = TypeName });
            Check("aynı ad (büyük/küçük harf farklı) ikinci kez: exception YOK, mesaj var, eklenmedi",
                !dup.Success && mgr.Registrations.Count(r => r.Id.Equals("sample", StringComparison.OrdinalIgnoreCase)) == 1, dup.Message);

            Console.WriteLine("\n=== 1b) Aynı plugin'in birden çok örneği (isimli + isimsiz) ===");
            var named = await mgr.RegisterAsync(new PluginRegistration { Id = "sample-sirketB", FilePath = dllPath, TypeFullName = TypeName, Mode = PluginExecutionMode.InProcess });
            var unnamed = await mgr.RegisterAsync(new PluginRegistration { FilePath = dllPath, TypeFullName = TypeName });
            Check("isimli ikinci örnek eklendi", named.Success, named.Message);
            Check("isimsiz örnek: GUID üretildi", unnamed.Success && Guid.TryParse(unnamed.Id, out _), $"{unnamed.Id} - {unnamed.Message}");
            Check("GUID kalıcı: JSON'a yazıldı", File.ReadAllText(configPath).Contains(unnamed.Id!));
            Check("DisplayName tip + ad içeriyor", mgr.GetRegistrationCopy(named.Id!).DisplayName == $"{TypeName} [sample-sirketB]");
            var instA = mgr.Get(named.Id!);
            var instB = mgr.Get(unnamed.Id!);
            instA.SetValue("DisplayName", "B şirketi");
            instB.SetValue("DisplayName", "isimsiz");
            Check("örneklerin state'i ayrı", instA.GetValue<string>("DisplayName") == "B şirketi" && instB.GetValue<string>("DisplayName") == "isimsiz");
            Check("biri in-process, diğeri sandbox çalışıyor", !instA.IsSandboxed && instB.IsSandboxed && mgr.GetStatus(unnamed.Id!).ProcessId != null);
            var mgrDesc = await mgr.DescribeAsync(named.Id!);
            Check("manager.DescribeAsync: çalışan örneğin değerleri dahil",
                mgrDesc.Values != null && mgrDesc.Type.Properties!.First(p => p.Name == "DisplayName").Value?.GetString() == "B şirketi");
            await mgr.StopAsync(named.Id!);
            var stoppedDesc = await mgr.DescribeAsync(named.Id!);
            Check("çalışmayan plugin: sadece yapı + uyarı (plugin başlatılmadı)",
                stoppedDesc.Values == null && stoppedDesc.Warnings?.Count > 0 && !mgr.GetStatus(named.Id!).IsRunning);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "plugin-describe-manager.json"), mgrDesc.ToJson());
            await mgr.UnregisterAsync(named.Id!);
            await mgr.UnregisterAsync(unnamed.Id!);
            Check("ek örnekler silindi", mgr.Registrations.Count == 1);

            var scan = mgr.Scan("sample");
            Check("Scan: property/field/event listesi", scan.Types.Any(t => t.Events.Count >= 3 && t.Properties.Count >= 3), $"{scan.Types[0].Methods.Count} metot, {scan.Types[0].Properties.Count} property, {scan.Types[0].Events.Count} event");

            Console.WriteLine("\n=== 2) Uygulamanın elindeki builder - lazy başlatma (sandbox) ===");
            IPluginBuilder app = mgr.Get("sample");
            Check("Get her seferinde AYNI nesne", ReferenceEquals(app, mgr.Get("sample")));
            Check("henüz başlatılmadı (lazy)", !mgr.GetStatus("sample").IsRunning);
            Check("ilk çağrı: Add(2,3)=5", app.Invoke<int>("Add", 2, 3) == 5);
            var st = mgr.GetStatus("sample");
            Check("şimdi sandbox'ta çalışıyor", st.IsRunning && st.Mode == PluginExecutionMode.Sandbox && st.ProcessId != null && app.IsSandboxed, $"pid={st.ProcessId}");

            var counterEvents = new System.Collections.Concurrent.ConcurrentQueue<int>();
            var sub = app.Subscribe("CounterChanged", e => counterEvents.Enqueue(e.Get<int>(1)));
            var add = app.GetFunc<int>("Add", new object?[] { 0, 0 });
            app.Invoke("Increment");
            await WaitUntil(() => counterEvents.Count == 1);
            Check("event (sandbox)", counterEvents.Count == 1);
            Check("GetFunc (sandbox)", add(new object?[] { 1, 1 }) == 2);

            var tAdd = app.GetTypedFunc<int, int, int>("Add");
            Check("tipli delegate (sandbox): tAdd(2,3)=5", tAdd(2, 3) == 5);

            Console.WriteLine("\n=== 3) Admin: Sandbox -> InProcess (canlı geçiş) ===");
            var modeChanges = new List<PluginExecutionMode>();
            mgr.ModeChanged += (_, m) => modeChanges.Add(m);
            await mgr.SetModeAsync("sample", PluginExecutionMode.InProcess);
            st = mgr.GetStatus("sample");
            Check("mod InProcess, çalışıyor, worker yok", st.Mode == PluginExecutionMode.InProcess && st.IsRunning && st.ProcessId == null && !app.IsSandboxed);
            Check("AYNI builder nesnesi çalışıyor: Add(20,22)=42", app.Invoke<int>("Add", 20, 22) == 42);
            Check("AYNI GetFunc delegate'i yeni tarafa geçti", add(new object?[] { 5, 5 }) == 10);
            Check("AYNI tipli delegate yeni tarafa (in-process, boxing'siz) geçti: tAdd(5,6)=11", tAdd(5, 6) == 11);
            Check("state sıfırlandı (yeni instance): Counter=0", app.GetValue<int>("Counter") == 0);
            app.Invoke("Increment");
            await WaitUntil(() => counterEvents.Count == 2);
            Check("event aboneliği yeni tarafta otomatik yeniden kuruldu", counterEvents.Count == 2, string.Join(",", counterEvents));
            Check("ModeChanged bildirimi", modeChanges.SequenceEqual(new[] { PluginExecutionMode.InProcess }));
            Check("karar kalıcı: JSON'da InProcess", File.ReadAllText(configPath).Contains("\"Mode\": \"InProcess\""));

            Console.WriteLine("\n=== 4) Admin: InProcess -> Sandbox (kesintisiz, eski kopya bellekten atılır) ===");
            await mgr.SetModeAsync("sample", PluginExecutionMode.Sandbox);
            st = mgr.GetStatus("sample");
            Check("mod Sandbox, worker var", st.Mode == PluginExecutionMode.Sandbox && st.ProcessId != null && app.IsSandboxed);
            Check("eski in-process kopya bellekten GERÇEKTEN boşaltıldı", st.LastUnloadReleasedMemory == true, st.LastUnloadReleasedMemory?.ToString() ?? "null");
            Check("builder çalışıyor", app.Invoke<int>("Add", 1, 2) == 3);
            Check("GetFunc çalışıyor", add(new object?[] { 3, 3 }) == 6);
            Check("tipli delegate tekrar sandbox'ta çalışıyor: tAdd(7,8)=15", tAdd(7, 8) == 15);
            app.Invoke("Increment");
            await WaitUntil(() => counterEvents.Count == 3);
            Check("event aboneliği tekrar sandbox tarafında", counterEvents.Count == 3);
            sub.Dispose();
            app.Invoke("Increment");
            await Task.Delay(300);
            Check("abonelikten çıkınca event gelmiyor", counterEvents.Count == 3);

            Console.WriteLine("\n=== 5) Geçiş SIRASINDA süren çağrılar (eşzamanlı yük altında mod değişimi) ===");
            var cts = new CancellationTokenSource();
            int okCalls = 0, failedCalls = 0;
            string? firstError = null;
            var load = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try { if (await app.InvokeAsync<int>("Add", 1, 1) == 2) Interlocked.Increment(ref okCalls); }
                    catch (Exception ex) { Interlocked.Increment(ref failedCalls); firstError ??= $"{ex.GetType().Name}: {ex.Message}"; }
                }
            });
            await Task.Delay(300);
            await mgr.SetModeAsync("sample", PluginExecutionMode.InProcess);
            await Task.Delay(300);
            await mgr.SetModeAsync("sample", PluginExecutionMode.Sandbox);
            await Task.Delay(300);
            cts.Cancel();
            await load;
            Check("iki canlı geçiş boyunca çağrılar kesintisiz (0 hata)", failedCalls == 0 && okCalls > 100, $"başarılı {okCalls}, hatalı {failedCalls}{(firstError != null ? " - ilk hata: " + firstError : "")}");

            Console.WriteLine("\n=== 6) Kalıcılık: yeni bir PluginManager aynı dosyadan kararları okur ===");
            await mgr.UpdateAsync("sample", r => { r.MaxConcurrency = 4; r.Notes = "admin notu"; });
            var mgr2 = new PluginManager(new JsonFilePluginConfigStore(configPath), managerOptions);
            await mgr2.InitializeAsync();
            var reg2 = mgr2.GetRegistrationCopy("sample");
            Check("mod, MaxConcurrency, not okundu", reg2.Mode == PluginExecutionMode.Sandbox && reg2.MaxConcurrency == 4 && reg2.Notes == "admin notu");
            Check("UpdateAsync sonrası (yeniden başlatıldı) builder çalışıyor", app.Invoke<int>("Add", 4, 4) == 8);

            Console.WriteLine("\n=== 7) Çökme bildirimi + kendini toparlama ===");
            PluginWorkerCrashedEventArgs? crash = null;
            string? crashedId = null;
            mgr.PluginCrashed += (id, e) => { crashedId = id; crash = e; };
            await Expect<Exception>("CrashHard çağrısı hata verdi (host ayakta)", () => app.ExecuteAsync("CrashHard"));
            await WaitUntil(() => crash != null);
            Check("PluginCrashed tetiklendi", crash != null && crashedId == "sample", crash?.Reason.GetType().Name ?? "yok");
            Check("durum ekranında son çökme görünüyor", mgr.GetStatus("sample").LastCrashUtc != null);
            Check("sonraki çağrıda taze worker ile devam: Add(5,5)=10", app.Invoke<int>("Add", 5, 5) == 10);

            Console.WriteLine("\n=== 8) Devre dışı bırakma, durdurma, kayıt silme ===");
            await mgr.StopAsync("sample");
            Check("StopAsync: çalışmıyor", !mgr.GetStatus("sample").IsRunning);
            Check("durdurulmuşken çağrı -> yeniden başlar", app.Invoke<int>("Add", 1, 1) == 2);
            await mgr.UpdateAsync("sample", r => r.Enabled = false);
            await Expect<InvalidOperationException>("Enabled=false iken çağrı reddedilir", () => app.InvokeAsync("Add", 1, 1));
            await mgr.UpdateAsync("sample", r => r.Enabled = true);
            Check("tekrar etkin", app.Invoke<int>("Add", 2, 2) == 4);
            await mgr.UnregisterAsync("sample");
            Check("kayıt silindi, JSON'da yok", !mgr.Registrations.Any() && !File.ReadAllText(configPath).Contains("sample"));
            await Expect<KeyNotFoundException>("kayıt silindikten sonra eski builder (async) -> KeyNotFoundException", () => app.InvokeAsync<int>("Add", 1, 1));
            await Expect<KeyNotFoundException>("kayıt silindikten sonra eski builder (senkron) -> KeyNotFoundException", () => Task.FromResult(app.Invoke<int>("Add", 1, 1)));

            Console.WriteLine("\n=== 9) Pool: aynı plugin farklı ayarla istenirse net hata ===");
            await using (var pool = new PluginWorkerPool())
            {
                var o1 = new PluginWorkerOptions { HostPath = hostDllPath, MaxConcurrency = 1, NotifyOnCrash = false };
                var h1 = await pool.GetOrStartAsync(dllPath, TypeName, o1);
                var h1b = await pool.GetOrStartAsync(dllPath, TypeName, new PluginWorkerOptions { HostPath = hostDllPath, MaxConcurrency = 1, NotifyOnCrash = false });
                Check("aynı ayarlar (farklı nesne) -> aynı worker", ReferenceEquals(h1, h1b));
                await Expect<InvalidOperationException>("farklı MaxConcurrency -> InvalidOperationException",
                    () => pool.GetOrStartAsync(dllPath, TypeName, new PluginWorkerOptions { HostPath = hostDllPath, MaxConcurrency = 4 }));
                await Expect<InvalidOperationException>("farklı includeNonPublic -> InvalidOperationException",
                    () => pool.GetOrStartAsync(dllPath, TypeName, o1, includeNonPublic: true));
            }

            Console.WriteLine("\n=== 10) WarmStart: plugin ilk çağrıdan ÖNCE arka planda başlatılır ===");
            string warmConfig = Path.Combine(Path.GetTempPath(), "dso-plugins-warm-" + Guid.NewGuid().ToString("N")[..6] + ".json");
            await using (var warm = new PluginManager(new JsonFilePluginConfigStore(warmConfig), new PluginManagerOptions { HostPath = hostDllPath, NotifyOnCrash = false, WarmStart = true }))
            {
                await warm.InitializeAsync();
                var wr = await warm.RegisterAsync(new PluginRegistration { Id = "warm", FilePath = dllPath, TypeFullName = TypeName });
                Check("kayıt", wr.Success, wr.Message);
                await WaitUntil(() => warm.GetStatus("warm").IsRunning);
                Check("hiç çağrı yapılmadan worker çalışıyor (arka planda başladı)", warm.GetStatus("warm").IsRunning && warm.GetStatus("warm").ProcessId != null);
                var sw1 = Stopwatch.StartNew();
                int r = warm.Get("warm").Invoke<int>("Add", 1, 2);
                Check("ilk çağrı başlatma beklemeden döndü", r == 3 && sw1.ElapsedMilliseconds < 250, $"{sw1.ElapsedMilliseconds} ms");
                Check("WarmUpAsync (zaten çalışıyor) -> true", await warm.WarmUpAsync("warm"));
                await warm.RegisterAsync(new PluginRegistration { Id = "kapali", FilePath = dllPath, TypeFullName = TypeName, Enabled = false });
                await Task.Delay(300);
                Check("Enabled=false kayıt ısıtılmıyor", !warm.GetStatus("kapali").IsRunning && !await warm.WarmUpAsync("kapali"));
            }
            // Yeni bir manager AYNI dosyayla açılınca (uygulama yeniden başladı) etkin plugin'ler InitializeAsync'te ısınır.
            await using (var warm2 = new PluginManager(new JsonFilePluginConfigStore(warmConfig), new PluginManagerOptions { HostPath = hostDllPath, NotifyOnCrash = false, WarmStart = true }))
            {
                await warm2.InitializeAsync();
                await WaitUntil(() => warm2.GetStatus("warm").IsRunning);
                Check("yeniden açılışta InitializeAsync etkin plugin'i ısıttı", warm2.GetStatus("warm").IsRunning);
            }
            File.Delete(warmConfig);

            await mgr.DisposeAsync();
            await mgr2.DisposeAsync();
            File.Delete(configPath);

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "TÜM MANAGER TESTLERİ GEÇTİ." : $"{failures} TEST BAŞARISIZ.");
            //return failures == 0 ? 0 : 1;

            // --- Ön kontrol: verilen SamplePlugin.dll bu test kitiyle aynı sürüm mü? ---
            // (Eski bir build verilirse testler anlaşılmaz "metot bulunamadı" hatalarıyla kesiliyordu.)
            static bool PreflightSamplePlugin(string dll)
            {
                var scan = DSO.Core.Evoker.Plugins.Scanning.PluginScanner.Scan(dll);
                var t = scan.Types.FirstOrDefault(x => x.FullName == "TestPlugin.SamplePlugin");
                var methods = t?.Methods.Select(m => m.Name).ToHashSet() ?? new HashSet<string>();
                var events = t?.Events.Select(e => e.Name).ToHashSet() ?? new HashSet<string>();
                var missing = new[] { "Add", "AddAsync", "MakePoint", "SlowAsync", "OptionalDemo", "UseDependency", "CheckedDivide", "RunWithProgress", "MoveTo", "Increment" }
                    .Where(m => !methods.Contains(m))
                    .Concat(new[] { "CounterChanged", "Progress", "PointMoved" }.Where(e => !events.Contains(e)).Select(e => "event " + e))
                    .ToList();
                if (missing.Count == 0) return true;
                Console.WriteLine($"[ÖN KONTROL HATASI] Verilen SamplePlugin.dll bu test kitinden ESKİ: {dll}");
                Console.WriteLine($"  Dosya tarihi: {File.GetLastWriteTime(dll):yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"  Eksik üyeler: {string.Join(", ", missing)}");
                Console.WriteLine("  TestKit/SamplePlugin'i güncel SamplePlugin.cs + SamplePlugin.csproj (SampleDep referansı) ile yeniden derleyip");
                Console.WriteLine("  testi o build çıktısındaki SamplePlugin.dll ile çalıştırın.");
                return false;
            }
        }
    }

    public static class InProcessPluginTesti
    {
        static string PluginsFolderPath = Path.Combine(AppContext.BaseDirectory, "Plugins");
        static string HostFolderPath = Path.Combine(AppContext.BaseDirectory, "Host");
        static string dllPath = Path.Combine(PluginsFolderPath, "SamplePlugin.dll"); //args.Length > 0 ? args[0] : throw new ArgumentException("SamplePlugin.dll yolu gerekli.");
        static string depV2 = Path.Combine(PluginsFolderPath, "depV2", "SampleDep.dll");
        static string hostDllPath = Path.Combine(HostFolderPath, "DSO.Core.Evoker.PluginHost.dll"); //args.Length > 1 ? args[1] : throw new ArgumentException("PluginHost.dll yolu gerekli.");
        static string TypeName = "TestPlugin.SamplePlugin";

        // In-process plugin'in kendi AssemblyLoadContext'ine yüklenip GERÇEKTEN boşaltılabildiğini doğrular.
        //
        // Kullanım: dotnet run -- <SamplePlugin.dll yolu> [SampleDep v2 dll yolu (opsiyonel)]
        //   SampleDep v2 üretmek için:  dotnet build SampleDep -c Release -p:DefineConstants=V2 -o depv2
        //   (verilmezse sürüm-izolasyonu alt testi atlanır, diğerleri çalışır)

        public static async Task TestRun()
        {
            string sourceDll = dllPath;
            if (!PreflightSamplePlugin(sourceDll))
            {
                Console.WriteLine(2);
                return;
            }

            int failures = 0;
            void Check(string label, bool ok, string detail = "")
            {
                Console.WriteLine($"  [{(ok ? "OK" : "HATA")}] {label}{(detail.Length > 0 ? "  -> " + detail : "")}");
                if (!ok) failures++;
            }

            // Plugin klasörünü geçici bir yere kopyala: unload sonrası DLL'in silinip yenisiyle değiştirilebildiğini
            // gösterebilmek için orijinal build çıktısına dokunmuyoruz.
            string CopyPluginDir(string name)
            {
                var dir = Path.Combine(Path.GetTempPath(), "dso-unloadtest-" + name + "-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(dir);
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(sourceDll)!))
                    File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
                return Path.Combine(dir, Path.GetFileName(sourceDll));
            }

            int PluginContextCount() => AssemblyLoadContext.All.Count(c => c.Name?.StartsWith("Plugin:SamplePlugin.dll") == true);

            // Plugin'i yükleyip "her şeyi" kullanan yardımcı: metot, async, property, Complex (JSON cache'ine
            // plugin tipi girer), GetFunc, event, optional parametre, kendi bağımlılığı. Hepsi unload'dan önce
            // temizlenmesi gereken bir cache/referans iz bırakır - unload yine de başarılı olmalı.
            [MethodImpl(MethodImplOptions.NoInlining)]
            static async Task<ManagedDotNetPluginLoader> LoadAndUseAsync(string path, Action<string, bool, string> check)
            {
                var loader = new ManagedDotNetPluginLoader();
                await loader.LoadInProcessAsync(path, "TestPlugin.SamplePlugin");
                IPluginBuilder b = loader.Builder!.AsPluginBuilder();

                check("Add(1,2)=3", b.Invoke<int>("Add", 1, 2) == 3, "");
                check("AddAsync(2,3)=5", await b.InvokeAsync<int>("AddAsync", 2, 3) == 5, "");
                b.SetValue("DisplayName", "x");
                check("Get/SetValue", b.GetValue<string>("DisplayName") == "x", "");
                var p = b.Invoke<PointDto>("MakePoint", 3, 4);
                check("Complex dönüş (JSON cache'ine plugin tipi girer)", p is { X: 3, Y: 4 }, "");
                check("kendi bağımlılığı: UseDependency", b.Invoke<string>("UseDependency") == "SampleDep v1", b.Invoke<string>("UseDependency") ?? "");
                check("optional + küçük harf: optionaldemo(1)", b.Invoke<string>("optionaldemo", 1) == "1|5|x", "");
                var add = b.GetFunc<int>("Add", new object?[] { 0, 0 });
                check("GetFunc", add(new object?[] { 5, 5 }) == 10, "");
                // Optimizasyon turunda eklenen cache'ler de plugin tiplerine referans tutar - unload'u ENGELLEMEMELİ:
                check("tipli delegate (EvokerBuilder tipli cache'i)", b.GetTypedFunc<int, int, int>("Add")(2, 2) == 4, "");
                check("Complex ARGÜMAN (derlenmiş şekil eşleyici host DTO -> plugin Point)", b.Invoke<int>("SumPoint", new PointDto { X = 1, Y = 2 }) == 3, "");
                check("tipli Complex dönüş (eşleyici plugin Point -> host DTO)", b.GetTypedFunc<int, int, PointDto>("MakePoint")(5, 6) is { X: 5, Y: 6 }, "");
                check("InvokeDynamicAsync Task<T> planı (derlenmiş sonuç okuyucu)", (int)(await loader.Builder!.InvokeDynamicAsync("AddAsync", new object?[] { 1, 1 }))! == 2, "");
                int evt = 0;
                using (loader.Builder!.AddEventHandler("CounterChanged", a => evt = (int)a[1]!))
                    b.Invoke("Increment");
                check("event (abonelik unload'dan önce kapatıldı)", evt == 1, "");
                return loader;
            }

            Console.WriteLine("=== 1) Yükle, her şeyi kullan, boşalt -> context bellekten GİTMELİ ===");
            string pathA = CopyPluginDir("A");
            var loaderA = await LoadAndUseAsync(pathA, Check);
            Check("yüklü plugin context sayısı = 1", PluginContextCount() == 1, PluginContextCount().ToString());
            bool unloaded = await loaderA.UnloadAsync();
            Check("UnloadAsync -> true (context GC tarafından toplandı)", unloaded && loaderA.IsMemoryReleased);
            Check("AssemblyLoadContext.All içinde artık yok", PluginContextCount() == 0, PluginContextCount().ToString());
            try
            {
                File.Delete(pathA);
                File.Copy(sourceDll, pathA);
                Check("DLL dosyası silinip yerine yenisi konabildi (kilit yok)", true);
            }
            catch (Exception ex) { Check("DLL dosyası serbest", false, ex.Message); }
            try { await loaderA.InvokeAsync("Add", new object?[] { 1, 1 }); Check("boşaltılmış loader'a çağrı reddedilmeli", false); }
            catch (InvalidOperationException) { Check("boşaltılmış loader'a çağrı -> InvalidOperationException", true); }

            Console.WriteLine("\n=== 2) Aynı dosyadan YENİDEN yükle (güncelleme senaryosu) ===");
            var reloaded = await LoadAndUseAsync(pathA, Check);
            Check("yeniden yüklenen plugin çalışıyor", reloaded.Builder!.Invoke<int>("Add", 20, 22) == 42);
            Check("yeniden yükleme sonrası boşaltma", await reloaded.UnloadAsync());

            Console.WriteLine("\n=== 3) Çağıran referans tutarken unload -> false; referans bırakılınca bellek serbest ===");
            var holder = new Holder();
            var heldLoader = await LoadAndHoldAsync(pathA, holder);
            bool heldResult = await heldLoader.UnloadAsync(timeoutMs: 1500);
            Check("builder referansı tutulurken UnloadAsync -> false", !heldResult);
            holder.Builder = null;
            for (int i = 0; i < 20 && !heldLoader.IsMemoryReleased; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(50); }
            Check("referans bırakılınca context toplandı (IsMemoryReleased)", heldLoader.IsMemoryReleased);

            Console.WriteLine("\n=== 4) İzolasyon: iki kopya yan yana, farklı bağımlılık sürümleriyle ===");
            string pathB = CopyPluginDir("B");
            if (depV2 != null) File.Copy(depV2, Path.Combine(Path.GetDirectoryName(pathB)!, "SampleDep.dll"), overwrite: true);
            var l1 = new ManagedDotNetPluginLoader(); await l1.LoadInProcessAsync(pathA, TypeName);
            var l2 = new ManagedDotNetPluginLoader(); await l2.LoadInProcessAsync(pathB, TypeName);
            Check("iki kopyanın Type nesneleri FARKLI (ayrı context'ler)", l1.Builder!.Type != l2.Builder!.Type);
            l1.Builder.SetValue("Counter", 100);
            Check("state paylaşılmıyor", l2.Builder.GetValue<int>("Counter") == 0);
            Check("aynı AssemblyQualifiedName'e rağmen EvokerBuilder cache'i karışmıyor",
                l1.Builder.Invoke<int>("Increment") == 101 && l2.Builder.Invoke<int>("Increment") == 1);
            if (depV2 != null)
            {
                string d1 = l1.Builder.Invoke<string>("UseDependency")!, d2 = l2.Builder.Invoke<string>("UseDependency")!;
                Check("kopya A kendi SampleDep v1'ini, kopya B kendi v2'sini kullanıyor", d1 == "SampleDep v1" && d2 == "SampleDep v2", $"{d1} / {d2}");
            }
            else Console.WriteLine("  [ATLANDI] sürüm izolasyonu (SampleDep v2 yolu verilmedi)");
            Check("A boşaltıldı", await l1.UnloadAsync());
            Check("A boşaldıktan sonra B hâlâ çalışıyor", l2.Builder!.Invoke<int>("Add", 1, 1) == 2);
            Check("B boşaltıldı", await l2.UnloadAsync());

            Console.WriteLine("\n=== 5) 20 kez yükle/kullan/boşalt -> sızıntı yok ===");
            int okCount = 0;
            for (int i = 0; i < 20; i++)
            {
                var l = await LoadAndUseAsync(pathA, (_, ok, _) => { if (!ok) failures++; });
                if (await l.UnloadAsync()) okCount++;
            }
            Check("20/20 boşaltma başarılı", okCount == 20, $"{okCount}/20");
            Check("döngü sonunda bellekte plugin context'i kalmadı", PluginContextCount() == 0, PluginContextCount().ToString());

            Console.WriteLine("\n=== 6) EvokerEngine.ResolveType ve plugin tipleri ===");
            var rl = new ManagedDotNetPluginLoader(); await rl.LoadInProcessAsync(pathA, TypeName);
            Check("ResolveType plugin tipini buluyor", EvokerEngine.ResolveType(TypeName) == rl.Builder!.Type);
            var rl2 = new ManagedDotNetPluginLoader(); await rl2.LoadInProcessAsync(pathB, TypeName);
            try { EvokerEngine.ResolveType("SamplePlugin"); Check("iki kopya yüklüyken kısa ad -> AmbiguousMatchException", false); }
            catch (System.Reflection.AmbiguousMatchException) { Check("iki kopya yüklüyken kısa ad -> AmbiguousMatchException (rastgele seçmiyor)", true); }
            Check("ResolveType cache'i ikinci kopyanın unload'unu ENGELLEMİYOR", await rl2.UnloadAsync());
            Check("ResolveType cache'i birinci kopyanın unload'unu ENGELLEMİYOR", await rl.UnloadAsync());
            var rl3 = new ManagedDotNetPluginLoader(); await rl3.LoadInProcessAsync(pathA, TypeName);
            Check("reload sonrası ResolveType YENİ tipi döndürüyor (eski cache'ten değil)", EvokerEngine.ResolveType(TypeName) == rl3.Builder!.Type);
            Check("son kopya da boşaltıldı", await rl3.UnloadAsync());

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "TÜM UNLOAD TESTLERİ GEÇTİ." : $"{failures} TEST BAŞARISIZ.");
            //return failures == 0 ? 0 : 1;

            [MethodImpl(MethodImplOptions.NoInlining)]
            static async Task<ManagedDotNetPluginLoader> LoadAndHoldAsync(string path, Holder holder)
            {
                var loader = new ManagedDotNetPluginLoader();
                await loader.LoadInProcessAsync(path, "TestPlugin.SamplePlugin");
                holder.Builder = loader.Builder!.AsPluginBuilder();
                holder.Builder.Invoke<int>("Add", 1, 1);
                return loader;
            }

            // --- Ön kontrol: verilen SamplePlugin.dll bu test kitiyle aynı sürüm mü? ---
            // (Eski bir build verilirse testler anlaşılmaz "metot bulunamadı" hatalarıyla kesiliyordu.)
            static bool PreflightSamplePlugin(string dll)
            {
                var scan = DSO.Core.Evoker.Plugins.Scanning.PluginScanner.Scan(dll);
                var t = scan.Types.FirstOrDefault(x => x.FullName == "TestPlugin.SamplePlugin");
                var methods = t?.Methods.Select(m => m.Name).ToHashSet() ?? new HashSet<string>();
                var events = t?.Events.Select(e => e.Name).ToHashSet() ?? new HashSet<string>();
                var missing = new[] { "Add", "AddAsync", "MakePoint", "SlowAsync", "OptionalDemo", "UseDependency", "CheckedDivide", "RunWithProgress", "MoveTo", "Increment" }
                    .Where(m => !methods.Contains(m))
                    .Concat(new[] { "CounterChanged", "Progress", "PointMoved" }.Where(e => !events.Contains(e)).Select(e => "event " + e))
                    .ToList();
                if (missing.Count == 0) return true;
                Console.WriteLine($"[ÖN KONTROL HATASI] Verilen SamplePlugin.dll bu test kitinden ESKİ: {dll}");
                Console.WriteLine($"  Dosya tarihi: {File.GetLastWriteTime(dll):yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"  Eksik üyeler: {string.Join(", ", missing)}");
                Console.WriteLine("  TestKit/SamplePlugin'i güncel SamplePlugin.cs + SamplePlugin.csproj (SampleDep referansı) ile yeniden derleyip");
                Console.WriteLine("  testi o build çıktısındaki SamplePlugin.dll ile çalıştırın.");
                return false;
            }

        }
    }

    public static class PerformansTesti
    {
        static string PluginsFolderPath = Path.Combine(AppContext.BaseDirectory, "Plugins");
        static string HostFolderPath = Path.Combine(AppContext.BaseDirectory, "Host");
        static string dllPath = Path.Combine(PluginsFolderPath, "SamplePlugin.dll"); //args.Length > 0 ? args[0] : throw new ArgumentException("SamplePlugin.dll yolu gerekli.");
        static string hostDllPath = Path.Combine(HostFolderPath, "DSO.Core.Evoker.PluginHost.dll"); //args.Length > 1 ? args[1] : throw new ArgumentException("PluginHost.dll yolu gerekli.");

        // Performans karşılaştırması: aynı işlem (SamplePlugin.Add(int,int)) her katmandan çağrılır; çağrı başına süre
        // (ns/op) ve çağrı başına bellek ayırma (B/op) ölçülür. Ayrıca sandbox'a özgü senaryolar (paralellik, toplu
        // çağrı, büyük metin, complex nesne) ve başlatma maliyetleri.
        //
        // Kullanım (Release ile!): dotnet run -c Release -- <SamplePlugin.dll yolu> <DSO.Core.Evoker.PluginHost.dll yolu>
        // NOT: Mutlak sayılar makineye/işletim sistemine bağlı (özellikle sandbox: Windows named pipe ile Linux Unix
        // soket farklı). Karşılaştırma için önemli olan katmanlar ARASINDAKİ oranlar.

        public static async Task TestRun()
        {
            string dll = dllPath; //Path.GetFullPath(args.Length > 0 ? args[0] : throw new ArgumentException("SamplePlugin.dll yolu gerekli."));
            string host = hostDllPath; //Path.GetFullPath(args.Length > 1 ? args[1] : throw new ArgumentException("PluginHost.dll yolu gerekli."));

            const string T = "TestPlugin.SamplePlugin";
            var results = new List<(string Group, string Name, double Ns, double Bytes, string Note)>();

#if DEBUG
            Console.WriteLine("UYARI: DEBUG build - sonuçlar anlamsız olur, -c Release ile çalıştırın.");
#endif

            // ---------------- ölçüm yardımcıları ----------------
            (double ns, double bytes) Measure(int iterations, Action body)
            {
                for (int i = 0; i < Math.Max(200, iterations / 10); i++) body(); // ısınma (JIT + cache'ler)
                var samples = new List<(double, double)>();
                for (int round = 0; round < 3; round++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long a0 = GC.GetTotalAllocatedBytes(true);
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < iterations; i++) body();
                    sw.Stop();
                    long a1 = GC.GetTotalAllocatedBytes(true);
                    samples.Add((sw.Elapsed.TotalMilliseconds * 1e6 / iterations, (a1 - a0) / (double)iterations));
                }
                return samples.OrderBy(s => s.Item1).ElementAt(1); // 3 turun medyanı
            }

            async Task<(double ns, double bytes)> MeasureAsync(int iterations, Func<Task> body)
            {
                for (int i = 0; i < Math.Max(100, iterations / 10); i++) await body();
                var samples = new List<(double, double)>();
                for (int round = 0; round < 3; round++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long a0 = GC.GetTotalAllocatedBytes(true);
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < iterations; i++) await body();
                    sw.Stop();
                    long a1 = GC.GetTotalAllocatedBytes(true);
                    samples.Add((sw.Elapsed.TotalMilliseconds * 1e6 / iterations, (a1 - a0) / (double)iterations));
                }
                return samples.OrderBy(s => s.Item1).ElementAt(1);
            }

            void Add(string group, string name, (double ns, double bytes) r, string note = "")
            {
                results.Add((group, name, r.ns, r.bytes, note));
                Console.WriteLine($"  {name,-62} {Fmt(r.ns),12}  {r.bytes,8:0} B/op  {note}");
            }

            static string Fmt(double ns) => ns < 1_000 ? $"{ns:0.0} ns" : ns < 1_000_000 ? $"{ns / 1000:0.00} µs" : $"{ns / 1_000_000:0.00} ms";

            async Task<double> TimeMs(Func<Task> a)
            {
                var sw = Stopwatch.StartNew();
                await a();
                return sw.Elapsed.TotalMilliseconds;
            }

            // =====================================================================================
            Console.WriteLine("=== A) In-process çağrı yolları: Add(int,int) ===");
            var loader = new ManagedDotNetPluginLoader();
            await loader.LoadInProcessAsync(dll, T);
            var eb = loader.Builder!;
            var inst = loader.Instance!;
            var addMi = eb.Type.GetMethod("Add")!;
            var typed = (Func<int, int, int>)addMi.CreateDelegate(typeof(Func<int, int, int>), inst);
            int x = 0;
            const int N = 1_000_000;

            Add("A", "A1 Derlenmiş tipli delegate (en hızlı referans noktası)", Measure(N * 5, () => x = typed(x & 1023, 1)));
            Add("A", "A2 MethodInfo.Invoke (düz reflection)", Measure(N, () => x = (int)addMi.Invoke(inst, new object[] { x & 1023, 1 })!));
            Add("A", "A3 EvokerBuilder.Invoke<int>(\"Add\", a, b)", Measure(N, () => x = eb.Invoke<int>("Add", x & 1023, 1)));
            var ebFunc = eb.GetFunc<int>("Add", new object[] { 0, 0 });
            Add("A", "A4 EvokerBuilder.GetFunc<int> (önceden çözülmüş)", Measure(N, () => x = ebFunc(new object[] { x & 1023, 1 })));
            Add("A", "A5 InvokeDynamicAsync (dönüş şekli bilinmeyen köprü)", Measure(N / 2, () => x = (int)eb.InvokeDynamicAsync("Add", new object?[] { x & 1023, 1 }).GetAwaiter().GetResult()!));
            IPluginBuilder ipb = eb.AsPluginBuilder();
            Add("A", "A6 IPluginBuilder (in-process).Invoke<int>", Measure(N / 2, () => x = ipb.Invoke<int>("Add", x & 1023, 1)));
            Add("A", "A7 IPluginBuilder (in-process).InvokeAsync<int> (await)", await MeasureAsync(N / 2, async () => x = await ipb.InvokeAsync<int>("Add", x & 1023, 1)));
            var ipbFunc = ipb.GetFunc<int>("Add", new object?[] { 0, 0 });
            Add("A", "A8 IPluginBuilder (in-process).GetFunc<int>", Measure(N, () => x = ipbFunc(new object?[] { x & 1023, 1 })));
            var ebTyped = eb.GetTypedFunc<int, int, int>("Add");
            Add("A", "A10 EvokerBuilder.GetTypedFunc<int,int,int> (boxing yok)", Measure(N * 5, () => x = ebTyped(x & 1023, 1)));
            var ipbTyped = ipb.GetTypedFunc<int, int, int>("Add");
            Add("A", "A11 IPluginBuilder (in-process).GetTypedFunc<int,int,int>", Measure(N * 5, () => x = ipbTyped(x & 1023, 1)));
            var batchArgs = Enumerable.Range(0, 10_000).Select(i => new object?[] { i, 1 }).ToList();
            var rb = await MeasureAsync(20, async () => await ipb.InvokeBatchAsync<int>("Add", batchArgs));
            Add("A", "A9 IPluginBuilder (in-process).InvokeBatchAsync (çağrı başına)", (rb.ns / batchArgs.Count, rb.bytes / batchArgs.Count));

            Console.WriteLine("\n=== B) In-process property/field: Counter (int field) ===");
            var fieldInfo = eb.Type.GetField("Counter")!;
            Add("B", "B1 FieldInfo.GetValue (düz reflection)", Measure(N, () => x = (int)fieldInfo.GetValue(inst)!));
            var getter = DynamicEntityAccessor.GetGetter<int>(eb.Type, "Counter");
            Add("B", "B2 DynamicEntityAccessor getter delegate (önceden alınmış)", Measure(N * 5, () => x = getter(inst)));
            Add("B", "B3 EvokerBuilder.GetValue<int>(\"Counter\")", Measure(N, () => x = eb.GetValue<int>("Counter")));
            Add("B", "B4 IPluginBuilder (in-process).GetValue<int>", Measure(N, () => x = ipb.GetValue<int>("Counter")));
            Add("B", "B5 EvokerBuilder.SetValue<int>(\"Counter\", v)", Measure(N, () => eb.SetValue("Counter", x & 1023)));
            Add("B", "B6 IPluginBuilder (in-process).SetValue<int>", Measure(N, () => ipb.SetValue("Counter", x & 1023)));

            Console.WriteLine("\n=== C) Complex nesne (Point) - in-process ===");
            Add("C", "C1 Invoke<PointDto>(\"MakePoint\") (plugin Point -> host DTO, şekil eşleme)", Measure(N / 10, () => ipb.Invoke<PointDto>("MakePoint", 1, 2)));
            var dto = new PointDto { X = 2, Y = 3 };
            Add("C", "C2 Invoke<int>(\"SumPoint\", PointDto) (host DTO -> plugin Point)", Measure(N / 10, () => x = ipb.Invoke<int>("SumPoint", dto)));

            Console.WriteLine("\n=== D) PluginManager proxy (SwitchablePluginBuilder) - in-process modda ===");
            string cfg = Path.Combine(Path.GetTempPath(), "perf-plugins-" + Guid.NewGuid().ToString("N")[..6] + ".json");
            await using var mgr = new PluginManager(new JsonFilePluginConfigStore(cfg), new PluginManagerOptions { HostPath = host, NotifyOnCrash = false });
            await mgr.InitializeAsync();
            await mgr.RegisterAsync(new PluginRegistration { Id = "perf-in", FilePath = dll, TypeFullName = T, Mode = PluginExecutionMode.InProcess });
            var proxy = mgr.Get("perf-in");
            proxy.Invoke<int>("Add", 1, 1);
            Add("D", "D1 proxy.Invoke<int>", Measure(N / 2, () => x = proxy.Invoke<int>("Add", x & 1023, 1)));
            var proxyFunc = proxy.GetFunc<int>("Add", new object?[] { 0, 0 });
            Add("D", "D2 proxy.GetFunc<int>", Measure(N, () => x = proxyFunc(new object?[] { x & 1023, 1 })));
            Add("D", "D3 proxy.GetValue<int>", Measure(N / 2, () => x = proxy.GetValue<int>("Counter")));
            var proxyTyped = proxy.GetTypedFunc<int, int, int>("Add");
            Add("D", "D4 proxy.GetTypedFunc<int,int,int>", Measure(N * 2, () => x = proxyTyped(x & 1023, 1)));

            // =====================================================================================
            Console.WriteLine("\n=== E) Sandbox (ayrı worker process, named pipe) ===");
            var opts = new PluginWorkerOptions { HostPath = host, MaxConcurrency = 1, NotifyOnCrash = false, HeartbeatIntervalMs = 5000 };
            await using var h = new PluginWorkerHandle(dll, T, opts);
            await h.StartAsync();
            IPluginBuilder sb = h.Builder;
            const int S = 5_000;
            Add("E", "E1 sandbox Invoke<int> (senkron, tek tek)", Measure(S, () => x = sb.Invoke<int>("Add", x & 1023, 1)));
            Add("E", "E2 sandbox InvokeAsync<int> (await, tek tek)", await MeasureAsync(S, async () => x = await sb.InvokeAsync<int>("Add", x & 1023, 1)));
            var sbFunc = sb.GetFunc<int>("Add", new object?[] { 0, 0 });
            Add("E", "E3 sandbox GetFunc<int>", Measure(S, () => x = sbFunc(new object?[] { x & 1023, 1 })));
            int rawHandle = await h.ResolveAsync(T, "Add", new[] { WireTypeCode.Int32, WireTypeCode.Int32 });
            var w1 = WireValueCodec.FromObject(1);
            Add("E", "E4 en alt seviye: handle.InvokeAsync(handle, WireValue[])", await MeasureAsync(S, async () => await h.InvokeAsync(rawHandle, new[] { w1, w1 })));
            var sbBatch = await MeasureAsync(10, async () => await sb.InvokeBatchAsync<int>("Add", batchArgs));
            Add("E", "E5 sandbox InvokeBatchAsync (çağrı başına, 10.000'lik)", (sbBatch.ns / batchArgs.Count, sbBatch.bytes / batchArgs.Count));
            Add("E", "E6 sandbox GetValue<int>(\"Counter\")", Measure(S, () => x = sb.GetValue<int>("Counter")));
            Add("E", "E7 sandbox SetValue<int>", Measure(S, () => sb.SetValue("Counter", x & 1023)));
            Add("E", "E8 sandbox Invoke<PointDto>(\"MakePoint\") (Complex dönüş)", Measure(S / 2, () => sb.Invoke<PointDto>("MakePoint", 1, 2)));
            Add("E", "E9 sandbox Invoke<int>(\"SumPoint\", PointDto) (Complex argüman)", Measure(S / 2, () => x = sb.Invoke<int>("SumPoint", dto)));
            var sbTyped = sb.GetTypedFunc<int, int, int>("Add");
            Add("E", "E11 sandbox GetTypedFunc<int,int,int>", Measure(S, () => x = sbTyped(x & 1023, 1)));
            string big = new string('x', 100_000);
            var rBig = Measure(300, () => sb.Invoke<string>("Greet", big, "Merhaba"));
            Add("E", "E10 sandbox Greet(100 KB metin) - gidiş + dönüş", rBig, $"≈ {200_000 / (rBig.ns / 1e9) / 1_048_576:0} MB/s");

            Console.WriteLine("\n=== F) Sandbox paralellik: 4 eşzamanlı çağıran, toplam 8.000 Add ===");
            async Task<double> Parallel(int maxConcurrency)
            {
                await using var ph = new PluginWorkerHandle(dll, T, new PluginWorkerOptions { HostPath = host, MaxConcurrency = maxConcurrency, NotifyOnCrash = false });
                await ph.StartAsync();
                var f = ph.Builder.GetFuncAsync<int>("Add");
                for (int i = 0; i < 500; i++) await f(new object?[] { i, 1 });
                var sw = Stopwatch.StartNew();
                await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () => { for (int i = 0; i < 2000; i++) await f(new object?[] { i, 1 }); })));
                return 8000 / sw.Elapsed.TotalSeconds;
            }
            double p1 = await Parallel(1), p4 = await Parallel(4);
            Console.WriteLine($"  MaxConcurrency=1: {p1:0} çağrı/sn   |   MaxConcurrency=4: {p4:0} çağrı/sn");
            results.Add(("F", "F1 4 çağıran, MaxConcurrency=1 (çağrı/sn)", p1, 0, ""));
            results.Add(("F", "F2 4 çağıran, MaxConcurrency=4 (çağrı/sn)", p4, 0, ""));

            // =====================================================================================
            Console.WriteLine("\n=== G) Başlatma / yönetim maliyetleri (ortalama) ===");
            double loadMs = 0, unloadMs = 0;
            for (int i = 0; i < 5; i++)
            {
                var l = new ManagedDotNetPluginLoader();
                loadMs += await TimeMs(() => l.LoadInProcessAsync(dll, T));
                l.Builder!.Invoke<int>("Add", 1, 1);
                unloadMs += await TimeMs(async () => await l.UnloadAsync());
            }
            Console.WriteLine($"  In-process yükleme (context + ctor): {loadMs / 5:0.0} ms   |   UnloadAsync (cache temizliği + GC doğrulama): {unloadMs / 5:0.0} ms");
            double startMs = 0, stopMs = 0;
            for (int i = 0; i < 3; i++)
            {
                var sh = new PluginWorkerHandle(dll, T, opts);
                startMs += await TimeMs(() => sh.StartAsync());
                stopMs += await TimeMs(async () => await sh.DisposeAsync());
            }
            Console.WriteLine($"  Sandbox worker başlatma (process + pipe + Hello): {startMs / 3:0} ms   |   kapatma: {stopMs / 3:0} ms");
            double scanMs = await TimeMs(() => { PluginScanner.Scan(dll); return Task.CompletedTask; });
            double descMs = await TimeMs(() => { PluginInspector.Describe(dll, T); return Task.CompletedTask; });
            double descValMs = await TimeMs(async () => await ipb.DescribeAsync(true));
            double descSbMs = await TimeMs(async () => await sb.DescribeAsync(true));
            Console.WriteLine($"  PluginScanner.Scan: {scanMs:0.0} ms | PluginInspector.Describe (yapı): {descMs:0.0} ms | DescribeAsync+değerler: in-process {descValMs:0.0} ms, sandbox {descSbMs:0.0} ms");
            // Not: plugin şu an iki context'te yüklü (loader + manager) -> kısa/tam ad belirsiz (doğru davranış, AmbiguousMatchException).
            // Cache isabetini tekil bir host tipiyle ölçüyoruz.
            var rt = Measure(N, () => EvokerEngine.ResolveType("PointDto"));
            Console.WriteLine($"  EvokerEngine.ResolveType cache isabeti: {Fmt(rt.ns)} ({rt.bytes:0} B/op)");
            string missName = "OlmayanTip_" + Guid.NewGuid().ToString("N");
            var missSw = Stopwatch.StartNew();
            try { EvokerEngine.ResolveType(missName); } catch (TypeLoadException) { }
            double firstMiss = missSw.Elapsed.TotalMilliseconds;
            missSw.Restart();
            try { EvokerEngine.ResolveType(missName); } catch (TypeLoadException) { }
            Console.WriteLine($"  EvokerEngine.ResolveType ıska: ilk {firstMiss:0.0} ms (tüm assembly'leri tarar), aynı ad tekrar {missSw.Elapsed.TotalMilliseconds * 1000:0} µs (bulunamayan ad cache'i)");

            await loader.UnloadAsync();
            File.Delete(cfg);

            Console.WriteLine("\n=== ÖZET (A1'e göre oran) ===");
            double baseNs = results.First(r => r.Name.StartsWith("A1")).Ns;
            foreach (var r in results.Where(r => r.Group != "F"))
                Console.WriteLine($"  {r.Name,-62} {Fmt(r.Ns),12} {r.Bytes,8:0} B   x{r.Ns / baseNs,10:0.0}");

        }
    }

    public sealed class PointDto
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    public sealed class Holder { public IPluginBuilder? Builder; }
    public class MapSrcA { public int X { get; set; } public int Y { get; set; } public string? Name { get; set; } }
    public class MapDstA { public int X { get; set; } public int Y { get; set; } public string? Name { get; set; } }
    public class MapDstB { public int X { get; set; } public string? Name { get; set; } = "varsayilan"; public int Extra { get; set; } = 42; }
    public class MapSrcN { public int Id { get; set; } public MapSrcA? Inner { get; set; } }
    public class MapDstN { public int Id { get; set; } public MapDstA? Inner { get; set; } }
    public class MapDstLong { public long X { get; set; } }
    public class MapDstAttr { [System.Text.Json.Serialization.JsonPropertyName("Y")] public int X { get; set; } }
    public class MapDstInit { public int X { get; init; } public int Y { get; set; } }
    public class MapSrcList { public List<int> Items { get; set; } = new(); }
    public class MapDstList { public List<int> Items { get; set; } = new(); }
    public struct MapDstStruct { public int X { get; set; } public int Y { get; set; } }
    public class MapSrcMix { public DateTime D { get; set; } public decimal M { get; set; } public Guid G { get; set; } public DayOfWeek L { get; set; } }
    public class MapDstMix { public DateTime D { get; set; } public decimal M { get; set; } public Guid G { get; set; } public DayOfWeek L { get; set; } }
    public class MapDstReadOnly { public int X { get; } = 77; public int Y { get; set; } }
    public class MapDstLower { public int x { get; set; } public int Y { get; set; } }
}





