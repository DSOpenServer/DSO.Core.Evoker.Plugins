# DSO.Core.Evoker.Plugins

> **Herhangi bir .NET DLL'i, tek satır değiştirmeden plugin olarak çalıştırın. Güvenmiyorsanız ayrı process'te, güveniyorsanız içeride; ikisi arasında canlı, kesintisiz geçiş.**

![.NET](https://img.shields.io/badge/.NET-6.0%20%7C%208.0-512BD4) ![Testler](https://img.shields.io/badge/test-500%2B%20kontrol-success) ![Unload](https://img.shields.io/badge/unload-do%C4%9Fruland%C4%B1-brightgreen)

`DSO.Core.Evoker.Plugins`, [DSO.Core.Evoker](../DSO.Core.Evoker/README.md) çekirdeğinin üzerine kurulu, üretim için
tasarlanmış bir plugin altyapısıdır. Plugin'leriniz özel bir arayüz uygulamak, attribute taşımak ya da bu kütüphaneye
referans vermek **zorunda değildir**. Sıradan bir sınıf kütüphanesi yeterlidir: C#, VB.NET, eski ya da yeni, sizin ya da
üçüncü partinin.

| Mod | Ne zaman | Ne kazandırır |
|---|---|---|
| **Sandbox** | DLL'e güvenmiyorsanız, 3. parti kodsa | Ayrı worker process. Plugin çökse, sonsuz döngüye girse ya da belleği tüketse bile **host ayakta kalır**. Heartbeat, otomatik yeniden başlatma |
| **InProcess** | DLL'e güveniyorsanız | Host'un içinde, kendi **collectible AssemblyLoadContext**'inde. Nanosaniye seviyesinde çağrı; **gerçekten** bellekten atılabilir |

İki mod da **aynı arayüzü** (`IPluginBuilder`) kullanır. Admin bir plugin'i çalışırken sandbox'tan içeriye ya da tersine
aldığında uygulamanın elindeki nesne çalışmaya devam eder.

---

## İçindekiler

- [Neden Evoker.Plugins?](#neden-evokerplugins)
- [Mimari](#mimari)
- [Rakamlar](#rakamlar)
- [Kurulum](#kurulum)
- [60 saniyede Plugins](#60-saniyede-plugins)
- [Plugin yazmak](#plugin-yazmak)
- [API Rehberi](#api-rehberi)
  - [IPluginBuilder — tek arayüz, iki mod](#ipluginbuilder--tek-arayüz-iki-mod)
  - [PluginManager — yönetim servisi](#pluginmanager--yönetim-servisi)
  - [Tarama: PluginScanner / PluginInspector](#tarama-pluginscanner--plugininspector)
  - [In-process: ManagedDotNetPluginLoader / PluginLoadContext](#in-process-manageddotnetpluginloader--pluginloadcontext)
  - [Sandbox: PluginWorkerHandle / PluginWorkerPool](#sandbox-pluginworkerhandle--pluginworkerpool)
  - [Değerlerin process sınırını geçmesi](#değerlerin-process-sınırını-geçmesi)
  - [Hatalar](#hatalar)
- [PluginHost (worker) dağıtımı](#pluginhost-worker-dağıtımı)
- [Testler ve örnekler](#testler-ve-örnekler)
- [Bilinen sınırlar](#bilinen-sınırlar)

---

## Neden Evoker.Plugins?

| Tipik plugin çatısı | Bedeli | Evoker.Plugins |
|---|---|---|
| **Sözleşme / attribute tabanlı keşif** | Plugin'in ortak bir arayüz DLL'ine bağlı olması ve onu uygulaması gerekir; 3. parti DLL'ler kapsam dışı kalır | **Sözleşme yok.** Herhangi bir public sınıf plugin'dir; metotlar isimle ya da JSON komutla çağrılır |
| **Sadece AssemblyLoadContext yükleyicileri** | İzolasyon yok: plugin çökerse host da çöker. Unload çoğu zaman sızıntılıdır | **İki mod:** ayrı process (çökme izolasyonu) ya da collectible context (unload **testle doğrulanmış**) |
| **Process izolasyonu sunan ağır çözümler** | Kod üretimi, proxy arayüzleri, özel serializer'lar, mod başına farklı API | **Tek API**, iki modda birebir aynı sonuç (368 kontrollük parite testi) |
| **Hepsinde** | Mod değiştirmek uygulamayı yeniden yazmak demektir | **Canlı mod geçişi:** önce yeni taraf hazırlanır, sonra eski kapatılır; elinizdeki referans geçerli kalır |

Bunların üstüne:

- **Sürüm izolasyonu:** plugin, host'ta farklı sürümü olan bir kütüphaneyi (ör. Newtonsoft.Json 12 ve 13) kendi sürümüyle
  kullanır. Çakışma olmaz.
- **Aynı DLL'den çok örnek:** aynı plugin farklı constructor argümanlarıyla, farklı modlarda, aynı anda çalışabilir.
  Aynı assembly'nin v1 ve v2 sürümleri de yan yana çalışabilir.
- **Host DTO'ları:** host plugin'in tiplerini **hiç yüklemeden** kendi DTO'suyla konuşur. `Point` gider, `PointDto` gelir;
  şekil eşlemesi derlenmiş kopyalayıcıyla yapılır.
- **Event'ler process sınırını geçer:** sandbox'taki bir plugin'in event'ine host'tan abone olunur; worker yeniden
  başlarsa abonelik kendiliğinden yenilenir.
- **Toplu çağrı:** sandbox'ta 10.000 çağrı tek mesajda gider. Çağrı başına maliyet **~95 µs'den ~0.7 µs'ye** iner.
- **JSON komutlar worker'ın içinde çalışır:** sandbox ve in-process modlar aynı JSON sonucunu üretir. Web'e açmak için
  [DSO.Core.Evoker.Plugins.Api](../DSO.Core.Evoker.Plugins.Api/README.md) yeterlidir.
- **DLL çalıştırılmadan tarama:** tipler, imzalar, varsayılan değerler ve hazır komut şablonları DLL yüklenmeden
  MetadataLoadContext ile okunur. Güvenmediğiniz bir DLL'i incelemek de güvenlidir.

---

## Mimari

```
                         ┌──────────────────────────── Host process ─────────────────────────────┐
  uygulama kodu ───────► │ IPluginBuilder (SwitchablePluginBuilder - ömür boyu aynı nesne)        │
  JSON / REST ─────────► │ EvokerCatalog ─► PluginTarget ─┐                                       │
                         │                                ▼                                       │
                         │                       PluginManager (kayıtlar, durumlar, kilitler)     │
                         │                          │                      │                      │
                         │        InProcess ◄───────┘                      └──────► Sandbox       │
                         │   PluginLoadContext (collectible)            PluginWorkerHandle        │
                         │   InProcessPluginBuilder                     SandboxBuilder            │
                         └──────────────────────────────────────────────────────┬────────────────┘
                                                                                │ named pipe (CurrentUserOnly)
                                                                                │ ikili protokol v2, heartbeat
                                                         ┌──────────────────────▼────────────────┐
                                                         │ DSO.Core.Evoker.PluginHost (worker)   │
                                                         │ plugin DLL + EvokerBuilder + komutlar │
                                                         └───────────────────────────────────────┘
```

---

## Rakamlar

`TestKit/PerfBench` ile .NET 8, Release derlemesinde ölçüldü. Sandbox süreleri işletim sistemine ve pipe
implementasyonuna göre değişir; oranlar belirleyicidir.

| Yol | Süre / çağrı | Allocation |
|---|---:|---:|
| In-process `Invoke<int>` | ~95 ns | 112 B |
| **In-process `GetFunc<int,int,int>`** | **~10 ns** | **0 B** |
| In-process `InvokeBatchAsync` (çağrı başına) | ~48 ns | 28 B |
| In-process `GetValue<int>` / `SetValue<int>` | ~70–80 ns | 0 B |
| In-process complex dönüşüm (plugin `Point` ↔ host `PointDto`) | ~390 ns | ~130 B |
| `PluginManager` proxy `Invoke<int>` | ~100 ns | 112 B |
| Proxy `GetFunc<int,int,int>` | ~26 ns | 0 B |
| Sandbox tekil async çağrı | ~50–95 µs | ~1.4 KB |
| **Sandbox `InvokeBatchAsync` (çağrı başına, 10.000'lik)** | **~0.7 µs** | 230 B |
| Sandbox 100 KB metin gidiş-dönüş | ~200–285 MB/s | — |
| Worker başlatma (process + pipe + el sıkışma) | ~90 ms (`WarmStart` ile ilk çağrı beklemez) | — |

Optimizasyon turundaki iyileşmeler:

- in-process `Invoke` **10 kat** hızlandı (950 ns → 95 ns);
- proxy **11 kat** hızlandı;
- sandbox toplu çağrı **2 kat** hızlandı;
- sandbox çağrı başına allocation **%46** azaldı.

---

## Kurulum

```xml
<ProjectReference Include="..\DSO.Core.Evoker.Plugins\DSO.Core.Evoker.Plugins.csproj" />
```

| Bağımlılık | Not |
|---|---|
| [DSO.Core.Evoker](../DSO.Core.Evoker/README.md) | Çekirdek |
| `DSO.Core.SchemaBinarySerializer` | System.Text.Json'ın taşıyamadığı karmaşık değerler için ikinci format |
| `System.Reflection.MetadataLoadContext` (NuGet) | DLL'i çalıştırmadan tarama |
| `DSO.Core.Evoker.PluginHost` | Sandbox worker exe'si; sadece Sandbox modu için ([dağıtım](#pluginhost-worker-dağıtımı)) |

Hedefler: `net6.0`, `net8.0`. Ad alanları: `DSO.Core.Evoker.Plugins`, `.Management`, `.Loading`, `.Sandbox`,
`.Scanning`.

---

## 60 saniyede Plugins

```csharp
using DSO.Core.Evoker.Plugins;
using DSO.Core.Evoker.Plugins.Management;

await using var mgr = new PluginManager(
    new JsonFilePluginConfigStore("App_Data/plugins.json"),
    new PluginManagerOptions { HostPath = @"Host\DSO.Core.Evoker.PluginHost.dll" });
await mgr.InitializeAsync();                                   // kayıtları oku, aktif olanları arka planda yükle

var r = await mgr.RegisterAsync(new PluginRegistration
{
    Name = "Merkez mağaza",
    FilePath = @"Plugins\Siparis\1.0\Siparis.dll",
    TypeFullName = "Siparis.SiparisServisi",
    ConstructorArgs = JsonDocument.Parse("{\"magazaAdi\":\"Merkez\",\"kdvOrani\":0.18}").RootElement,
    Mode = PluginExecutionMode.Sandbox                          // güvenmiyorum: ayrı process
});
Console.WriteLine(r.Message);                                    // "Merkez mağaza (Siparis.SiparisServisi) eklendi ve yüklendi."

IPluginBuilder siparis = mgr.Get(r.Key!.Value);                  // uygulama BUNU saklar, ömür boyu geçerli
var s = await siparis.InvokeAsync<SiparisDto>("OnaylaAsync", 1001);   // host kendi DTO'sunu ister

await mgr.SetModeAsync(r.Key.Value, PluginExecutionMode.InProcess);   // admin: "güveniyorum" - canlı geçiş
int adet = siparis.GetValue<int>("SiparisSayisi");                    // aynı nesne, artık nanosaniyeler

var json = await mgr.Catalog.ExecuteAsync(r.Key.Value,
    "{ \"op\":\"invoke\", \"member\":\"FiyatTeklifiAlAsync\", \"args\":{ \"urunKodlari\":[\"A\",\"B\"] } }");
```

---

## Plugin yazmak

Plugin yazmanın tek kuralı, **public bir sınıf** olmasıdır. Referans, arayüz, attribute ya da taban sınıf gerekmez.

```csharp
public class SiparisServisi
{
    public SiparisServisi(string magazaAdi, decimal kdvOrani = 0.20m, SiparisAyarlari? ayarlar = null) { ... }

    public decimal KdvOrani { get; set; }                                   // GetValue / SetValue
    public event EventHandler<DurumDegistiEventArgs>? DurumDegisti;         // Subscribe
    public Siparis Olustur(Musteri musteri, List<SiparisSatiri> satirlar) { ... }
    public async Task<Siparis> OnaylaAsync(int no) { ... }                  // Task<T>: beklenir
    public ValueTask<int> SayAsync(SiparisDurumu durum) { ... }
}
```

```vb
' VB.NET: Optional parametreler ve büyük/küçük harf duyarsız adlar desteklenir
Public Class StokServisi
    Public Sub New(Optional varsayilanDepo As String = "ANA")
    Public Function Miktar(urunKodu As String, Optional depo As String = Nothing) As Integer
    Public Function ToplamMiktar(ParamArray urunKodlari As String()) As Integer
    Public Async Function SayimAsync(Optional depo As String = Nothing) As Task(Of SayimSonucu)
End Class
```

Desteklenen dönüş şekilleri:

- `void`,
- senkron değer,
- `Task`,
- `Task<T>`,
- `ValueTask<T>` (JSON komutlarda).

Constructor seçimi metot seçimiyle aynı kurallarla yapılır; parametresiz constructor zorunluluğu **yoktur**.

---

## API Rehberi

### IPluginBuilder — tek arayüz, iki mod

| Mod | Nasıl elde edilir |
|---|---|
| `PluginManager` (önerilen) | `mgr.Get(key)` |
| Sandbox, doğrudan | `handle.Builder` (`SandboxBuilder`) |
| In-process, doğrudan | `loader.Builder!.AsPluginBuilder()` (`InProcessPluginBuilder`) |

#### Özellikler

```csharp
string tip   = b.TypeFullName;
bool   gizli = b.IncludeNonPublic;
bool   ayri  = b.IsSandboxed;
b.DefaultTimeoutMs = 5000;        // async çağrıların BEKLEMESİNİ keser (TimeoutException); null = sınırsız
```

#### Metot çağırma

```csharp
object? o = b.Invoke("Hesapla", 3, 4);
int?    i = b.Invoke<int>("Hesapla", 3, 4);
b.Execute("Temizle");

object? oa = await b.InvokeAsync("OnaylaAsync", 1001);
SiparisDto? s = await b.InvokeAsync<SiparisDto>("OnaylaAsync", 1001);   // plugin'in Siparis'i -> host DTO'su
await b.ExecuteAsync("KargoyaVerAsync", 1001, "Aras");
```

Her metot void, değer, `Task` ya da `Task<T>` dönebilir; `Task`'lar beklenir. Senkron metotlar async olanların
bloklayan halidir (`ConfigureAwait(false)` ile; UI thread'inde deadlock yapmaz). Mümkünse async olanları tercih edin.

#### Toplu çağrı

```csharp
var argumanlar = Enumerable.Range(0, 10_000).Select(i => new object?[] { i, 1 }).ToList();
int[] sonuclar = await b.InvokeBatchAsync<int>("Topla", argumanlar);   // sandbox'ta tek mesaj
await b.ExecuteBatchAsync("Kaydet", argumanlar);
```

Hata olursa `PluginInvocationException.BatchIndex` hata veren sırayı gösterir; öncekiler çalışmıştır. Sandbox'ta paket
boyutu `SandboxBuilder.BatchChunkSize` ile ayarlanır (varsayılan 1000).

#### Property ve field

```csharp
decimal kdv = b.GetValue<decimal>("KdvOrani");
b.SetValue("KdvOrani", 0.10m);
int ic = await b.GetValueAsync<int>("_icSayac");     // IncludeNonPublic ise private field
await b.SetValueAsync("DisplayName", "yeni");
```

#### Önceden çözülmüş delegate'ler

```csharp
Func<object?[], int>        f  = b.GetFunc<int>("Hesapla", sampleArgs: new object?[] { 0, 0 });
Func<object?[], Task<int>>  fa = b.GetFuncAsync<int>("HesaplaAsync");
Action<object?[]>           a  = b.GetAction("Temizle");
Func<object?[], Task>       aa = b.GetActionAsync("TemizleAsync");

// Tipli (in-process'te object[]/boxing'siz derlenmiş delegate)
Func<int, int, int>  topla = b.GetFunc<int, int, int>("Topla");           // 1–4 argüman
Action<string>       logla = b.GetAction<string>("Logla");
```

Tipler doğrudan dönüştürülemiyorsa (ör. host DTO'su ↔ plugin tipi) ya da metot `Task` dönüyorsa, tipli delegate'ler
`Invoke` ile aynı kurallarla çalışan genel yola düşer: sonuç aynıdır, sadece daha yavaştır. Sandbox'ta her çağrı yine
IPC'dir.

#### Event'ler

```csharp
using var abonelik = b.Subscribe("DurumDegisti", e =>
{
    var arg = e.Get<DurumDegistiDto>(1);          // [0]=sender (her zaman null), [1]=EventArgs -> host DTO'su
    Console.WriteLine($"#{arg!.No}: {arg.Eski} -> {arg.Yeni}");
});
IDisposable ab2 = await b.SubscribeAsync("KritikSeviyeAltinda", e => Uyar(e.Get<string>(1)));
string[] adlar = b.GetEventNames();
```

| | In-process | Sandbox |
|---|---|---|
| Handler nerede çağrılır | Plugin'in event'i tetiklediği thread'de, **senkron** | Host'ta ayrı bir thread'de, tetiklenme sırasıyla, **asenkron** (plugin beklemez) |
| Handler exception fırlatırsa | Plugin'e yansımaz | Plugin'e yansımaz |
| Worker yeniden başlarsa | — | Abonelik kendiliğinden yenilenir |

Handler hataları `EventHandlerFailed` event'i ile bildirilir. Bu event in-process'te `InProcessPluginBuilder`'da,
sandbox'ta `PluginWorkerHandle`'da bulunur. `PluginEventArgs` üyeleri: `EventName`, `Args`, `Count`,
`this[i]`, `Get<T>(i)`.

#### Tanım, JSON komut, cache

```csharp
PluginDescriptor d = await b.DescribeAsync(includeValues: true);   // yapı + o anki değerler
Console.WriteLine(d.ToJson());

EvokerCommandResult r = await b.ExecuteCommandAsync(EvokerCommand.Parse(json));   // sandbox'ta worker İÇİNDE çalışır
// r.Mode == "Sandbox" | "InProcess"; hata fırlatmaz, r.Error.Code ile döner

b.ForgetCache();            // plugin tipinin accessor cache'ini temizle (sandbox'ta worker içinde)
await b.ForgetCacheAsync();
```

#### Davranış sözleşmesi

İki implementasyon da aynı test senaryolarıyla doğrulanır:

- **Tipli dönüşler** (`Invoke<T>`, `GetValue<T>`, `e.Get<T>`): sayısal genişletme ve daraltma, enum, `Nullable` ve
  karmaşık nesneler için JSON üzerinden şekil eşlemesi. Host plugin'in tipini yüklemek zorunda değildir.
- **`GetValue` / `SetValue`:** önce property, yoksa aynı adlı field; `IncludeNonPublic` ise private olanlar da.
- **Plugin exception'ı:** `PluginInvocationException` fırlatılır. `RemoteExceptionType` orijinal tip adını taşır;
  in-process'te `InnerException` orijinal exception'dır.
- **`ref` / `out`:** desteklenmez (açık hata).

---

### PluginManager — yönetim servisi

Kayıtları tutar, her plugin'i kayıttaki moda göre çalıştırır, uygulamaya ömür boyu değişmeyen bir `IPluginBuilder`
verir ve her kaydı `Catalog`'a (`EvokerCatalog`) aynı Guid anahtarla ekler. Bütün yönetim işlemleri plugin başına
kilitlidir.

#### Oluşturma ve başlatma

```csharp
var mgr = new PluginManager(
    store: new PluginsRootConfigStore(new JsonFilePluginConfigStore("App_Data/plugins.json"), "Plugins"),
    options: new PluginManagerOptions
    {
        HostPath = @"Host\DSO.Core.Evoker.PluginHost.dll",   // ya da .exe (HostIsDotnetDll = false)
        HostIsDotnetDll = true,
        StartupTimeoutMs = 15000,
        NotifyOnCrash = true,
        CrashLogFilePath = "App_Data/crash.log",
        WarmStart = true                                       // aktif plugin'ler açılışta arka planda yüklenir
    },
    catalog: paylasilanKatalog);                               // opsiyonel: API ile aynı katalog

await mgr.InitializeAsync();   // eski kayıt biçimleri (Id/Enabled) otomatik taşınır ve kaydedilir
```

#### Kayıt

```csharp
PluginRegistrationResult r = await mgr.RegisterAsync(new PluginRegistration
{
    Name = "Ana depo stok",                 // sadece açıklama; boş olabilir, tekrar edebilir
    FilePath = @"Plugins\Stok\v1\Stok.dll",
    TypeFullName = "Stok.StokServisi",
    ConstructorArgs = null,                  // dizi (sıralı) ya da nesne (isimli); null = parametresiz/optional ctor
    Mode = PluginExecutionMode.InProcess,
    IsActive = true,                          // true: hemen yüklenir; false: sadece listede
    IncludeNonPublic = false,
    MaxConcurrency = 1,                       // sandbox: aynı anda kaç çağrı (DLL thread-safe ise artırın)
    AutoRestartOnCrash = false,               // sandbox: çökünce bir kez yeniden başlat
    HeartbeatIntervalMs = 5000,
    MissedHeartbeatsBeforeKill = 3,
    DefaultTimeoutMs = 30000,
    Notes = "Muhasebe ekibi istedi"
});

if (!r.Success) Console.WriteLine(r.Message);   // dosya/tip yok, .NET değil, constructor uymuyor... kayıt EKLENMEZ
Guid key = r.Key!.Value;                         // anahtar HER ZAMAN sistemin ürettiği Guid
PluginState? durum = r.State;                    // aktif ama yüklenemediyse Faulted (kayıt yine eklenir)
```

Kayıt sırasında DLL **çalıştırılmadan** doğrulanır:

- dosyanın varlığı;
- .NET assembly'si olup olmadığı;
- tipin varlığı (adın gerçek yazımı da düzeltilir);
- abstract ya da static olmaması;
- verilen argümanlara uyan bir constructor bulunması.

Assembly sürümü DLL'den otomatik okunur.

#### Yaşam döngüsü

```csharp
PluginStatus s1 = await mgr.ActivateAsync(key);     // aktif et + yükle (yüklenemezse Faulted, LastError)
PluginStatus s2 = await mgr.DeactivateAsync(key);   // bellekten at, listede kalsın; çağrılar reddedilir
PluginStatus s3 = await mgr.ReloadAsync(key);       // DLL değiştiyse; state sıfırlanır, sürüm yeniden okunur
await mgr.StopAsync(key);                           // bellekten at ama aktif bırak; ilk çağrıda kendiliğinden yüklenir
bool calisiyor = await mgr.WarmUpAsync(key);         // şimdi yükle (ilk çağrıyı bekletmemek için)
await mgr.UnregisterAsync(key);                     // durdur ve kaydı sil (eski IPluginBuilder bundan sonra hata verir)
```

#### Güncelleme ve canlı mod geçişi

```csharp
await mgr.UpdateAsync(key, r =>
{
    r.FilePath = @"Plugins\Stok\v2\Stok.dll";                              // sürüm değişimi
    r.ConstructorArgs = JsonDocument.Parse("{\"kritikSeviye\":50}").RootElement;
    r.Notes = "v2'ye geçti";
});
// Sadece Name/Notes/DefaultTimeoutMs değiştiyse yeniden başlatılmaz; çalışmayı etkileyen alanlarda yeni ayarlarla
// yeniden başlatılır. Geçersiz değişiklik ArgumentException ile reddedilir; kayıt bozulmaz.

await mgr.SetModeAsync(key, PluginExecutionMode.InProcess);
```

**Canlı geçiş** sırası şöyledir:

- **Sandbox → InProcess:** önce in-process kopya yüklenir ve çağrılar ona yönlendirilir, sonra worker kapatılır.
- **InProcess → Sandbox:** önce yeni worker başlatılır, sonra eski kopya bellekten atılır.

Plugin state'i sıfırlanır; constructor argümanları korunur.

#### Sorgulama

```csharp
IPluginBuilder b = mgr.Get(key);                       // ömür boyu aynı nesne (mod/sürüm/restart değişse de)
PluginStatus st = mgr.GetStatus(key);
IReadOnlyList<PluginStatus> hepsi = mgr.GetStatuses();
IReadOnlyList<PluginRegistration> kayitlar = mgr.Registrations;
PluginRegistration kopya = mgr.GetRegistrationCopy(key);
bool var = mgr.Contains(key);
PluginScanResult tarama = mgr.Scan(key);
PluginDescriptor tanim = await mgr.DescribeAsync(key, includeValues: true, includeSamples: true, includeNonPublic: false);
EvokerCatalog katalog = mgr.Catalog;                   // her plugin bir PluginTarget (Kind = "Plugin")
```

#### Olaylar

```csharp
mgr.PluginCrashed += (key, e) => Log($"{key} çöktü: {e.Reason.Message}, yeniden başlıyor: {e.WillRestart}");
mgr.ModeChanged   += (key, mod) => Log($"{key} artık {mod}");
```

Bilerek yapılan kapatmalar çökme olayı **üretmez**: stop, deactivate, mod geçişi ve silme.

#### Durumlar (`PluginState`)

| Durum | Anlamı |
|---|---|
| `Inactive` | Pasif: sadece listede, bellekte yok, çağrılar reddedilir |
| `Stopped` | Aktif ama yüklü değil; ilk çağrıda yüklenir |
| `Starting` | Yükleniyor |
| `Running` | Yüklü, kullanıma açık |
| `Faulted` | Yüklenemedi (`LastError`); bir sonraki çağrıda ya da aktivasyonda yeniden denenir |
| `Crashed` | Sandbox worker çöktü ve yeniden başlamadı; sonraki çağrı taze worker açar |

`PluginStatus` alanları:

| Grup | Alanlar |
|---|---|
| Kimlik | `Key`, `Name`, `DisplayName` ("Ad (Tip)"), `TypeFullName`, `FilePath`, `AssemblyVersion` |
| Durum | `Mode`, `IsActive`, `State`, `IsRunning` |
| Sandbox | `ProcessId`, `Generation` |
| Hata ve çökme | `LastError`, `LastErrorUtc`, `LastCrashUtc`, `LastCrashReason` |
| Diğer | `LastUnloadReleasedMemory`, `UpdatedUtc`, `Notes` |

#### Kayıt depoları

| Depo | Açıklama |
|---|---|
| `JsonFilePluginConfigStore(path)` | Okunabilir, elle düzenlenebilir JSON dosyası. Yazma atomiktir (geçici dosya + yer değiştirme) |
| `PluginsRootConfigStore(inner, root)` | Yolları kök klasöre **göreli** saklar (`"Stok/v2/Stok.dll"`). Kök taşınsa da kayıtlar geçerli kalır. Yardımcılar: `ToFullPath`, `ToRelativePath`, `RootPath` |
| `IPluginConfigStore` | Kendi deponuz (veritabanı vb.): `LoadAsync()` / `SaveAsync(list)` |

---

### Tarama: PluginScanner / PluginInspector

DLL **hiç çalıştırılmadan** MetadataLoadContext ile okunur. Güvenmediğiniz bir dosyayı incelemek de güvenlidir.

```csharp
PluginKind tur = PluginScanner.DetectKind("x.dll");          // ManagedDotNet | Native | Unknown
PluginScanResult s = PluginScanner.Scan("x.dll");             // Types (Methods/Properties/Fields/Events), Errors, FatalFailure
foreach (var satir in PluginScanner.ToLogLines(s)) Console.WriteLine(satir);

PluginDescriptor d = PluginInspector.Describe("x.dll", "Acme.Servis", new PluginDescribeOptions
{
    IncludeNonPublic = true,     // private üyeler
    IncludeInherited = true,     // kendi assembly'sindeki taban sınıf üyeleri
    IncludeReferences = true,    // referans verdiği assembly'ler (eksikler işaretlenir)
    IncludeSamples = true        // her üye için hazır JSON komut şablonu
});
string json = d.ToJson();

await PluginValueSnapshot.CaptureAsync(d, builder);   // çalışan instance'tan o anki değerler (getter'lar çalışır)
```

`PluginDescriptor` iki parçadan oluşur:

- **`Assembly`:** `Name`, `Version`, `FileVersion`, `InformationalVersion`, `TargetFramework`, `FilePath`, `FileSize`,
  `LastWriteUtc`, `Sha256`, `References`.
- **`Type`:** çekirdeğin `EvokerTypeDescriptor`'ı. Ayrıntılar için
  [Description](../DSO.Core.Evoker/README.md#description--tip-tanımı-ve-şablonlar).

Bunlara `Values` ve `Warnings` eklenir. Örnek çıktı: `TestKit/ornek-plugin-tanimi.json`.

---

### In-process: ManagedDotNetPluginLoader / PluginLoadContext

```csharp
var loader = new ManagedDotNetPluginLoader();
await loader.LoadInProcessAsync(@"Plugins\Rapor\1.0\Rapor.dll", "Rapor.RaporServisi",
    includeNonPublic: false, constructorArgs: ctorJson);

IPluginBuilder b = loader.Builder!.AsPluginBuilder();
object? nesne = loader.Instance;
Assembly asm = loader.PluginAssembly!;
PluginLoadContext ctx = loader.LoadContext!;

bool bosaldi = await loader.UnloadAsync(timeoutMs: 10_000);   // true = DLL bellekten GERÇEKTEN gitti
bool kontrol = loader.IsMemoryReleased;
```

`UnloadAsync` şu adımları izler:

1. Evoker'ın tüm cache'lerinden plugin tiplerini temizler. Bunlar arasında System.Text.Json'ın global cache'leri de var.
2. Context'i boşaltır.
3. GC ile context'in gerçekten toplandığını doğrular.

`false` dönerse bir yerde hâlâ referans tutuluyordur. Olası sebepler:

- çağıranın elinde kalan bir plugin nesnesi;
- `GetFunc` ile alınmış bir delegate;
- kapatılmamış bir event aboneliği;
- plugin'in kendi başlattığı ve bitmeyen bir thread ya da timer.

Bunlar bırakılınca context yine de toplanır.

**`PluginLoadContext` çözümleme kuralları:**

1. **Host ile paylaşılanlar:**
   - .NET'in kendi assembly'leri;
   - `DSO.Core.*`;
   - `PluginLoadContext.SharedAssemblyNames`'e eklenenler.
2. **Plugin'in kendi bağımlılıkları:** önce plugin'in `.deps.json`'u, yoksa plugin klasöründeki `{Ad}.dll` kullanılır.
   Böylece host'ta başka sürümü olan kütüphane çakışmaz.
3. **Native DLL'ler:** aynı sırayla çözülür.

```csharp
PluginLoadContext.SharedAssemblyNames.Add("Acme.Contracts");   // host ile plugin'in BİLEREK paylaştığı sözleşme DLL'i
```

---

### Sandbox: PluginWorkerHandle / PluginWorkerPool

```csharp
var opt = new PluginWorkerOptions
{
    HostPath = @"Host\DSO.Core.Evoker.PluginHost.dll",
    HostIsDotnetDll = true,          // false: self-contained .exe doğrudan çalışır
    MaxConcurrency = 4,              // worker aynı anda kaç isteği işler (varsayılan 1 - güvenli taraf)
    AutoRestartOnCrash = true,       // bir kez denenir; crash-loop yapılmaz
    NotifyOnCrash = true,
    CrashLogFilePath = "App_Data/crash.log",
    HeartbeatIntervalMs = 5000,      // mesaj döngüsü canlı mı (uzun süren bir çağrıdan ETKİLENMEZ)
    MissedHeartbeatsBeforeKill = 3,  // cevapsız kalırsa Process.Kill
    StartupTimeoutMs = 15000
};

await using var h = new PluginWorkerHandle(@"Plugins\Rapor\1.0\Rapor.dll", "Rapor.RaporServisi", opt,
    includeNonPublic: false, constructorArgs: ctorJson);
h.Crashed   += (s, e) => Log($"çöktü (nesil {e.Generation}): {e.Reason.Message}, yeniden: {e.WillRestart}");
h.Restarted += (s, e) => Log($"yeniden başladı: pid {e.ProcessId}");
await h.StartAsync();

SandboxBuilder b = h.Builder;                         // IPluginBuilder - restart sonrası da geçerli
b.BatchChunkSize = 2000;
object? r = await h.CallAsync("Ortam", Array.Empty<object?>());
string sonucJson = await h.ExecuteCommandAsync("{\"member\":\"Ortam\"}");
var abone = await h.SubscribeAsync("Ilerleme", e => Console.WriteLine(e.Get<int>(1)));

int? pid = h.ProcessId; int nesil = h.Generation; bool olu = h.IsDead;
ManagedDotNetPluginLoader icerde = await h.PromoteToInProcessAsync();   // terfi: state kaybolur, handle kapanır
```

**Alt seviye API** (milyonlarca çağrılık özel döngüler için):

- `ResolveAsync(tip, metot, WireTypeCode[])` metot handle'ı verir;
- `InvokeAsync(handle, WireValue[])`, `InvokeBatchAsync(handle, WireValue[][])` ve `MemberAsync(...)` bu handle'la çalışır.

**Güvenlik ve sağlamlık:**

- Pipe `CurrentUserOnly` ile açılır.
- El sıkışmada protokol sürümü kontrol edilir; eski bir worker açık bir hatayla reddedilir.
- Constructor argümanları el sıkışmadan önce `Init` mesajıyla gider.
- Host ölürse worker kendini kapatır.

**`PluginWorkerPool`** (dosya + tip [+ örnek adı] başına uzun ömürlü worker'lar):

```csharp
await using var havuz = new PluginWorkerPool();
havuz.WorkerCrashed   += (s, e) => ...;
havuz.WorkerRestarted += (s, e) => ...;

PluginWorkerHandle w = await havuz.GetOrStartAsync(dll, tip, opt, includeNonPublic: false, instanceName: "kiraci-1");
IReadOnlyList<PluginWorkerHandle> calisanlar = havuz.RunningWorkers;
await havuz.StopAsync(dll, tip, "kiraci-1");
```

Aynı plugin farklı ayarlarla istenirse sessizce eski worker'ı döndürmek yerine `InvalidOperationException` fırlatılır.

---

### Değerlerin process sınırını geçmesi

| Değer | Taşınma |
|---|---|
| Primitive'ler, `string`, `decimal`, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `byte[]` | Kendi ikili kodlarıyla (`WireTypeCode`) |
| Karmaşık nesneler | System.Text.Json; desteklenmeyen ya da döngüsel tiplerde `DSO.Core.SchemaBinarySerializer` (`ComplexValueFormat`) |
| Host tarafında | İstenen tipe çevrilir: host DTO'su, `JsonElement`, sayısal ya da enum dönüşümü |

```csharp
WireValue w = WireValueCodec.FromObject(deger);
object? o = WireValueCodec.ToObject(w, typeof(PointDto));
PointDto? p = WireValueCodec.ToObject<PointDto>(w);
object? c = WireValueCodec.ConvertTo(jsonElement, typeof(int));
```

---

### Hatalar

| Exception | Ne zaman |
|---|---|
| `PluginInvocationException` | Plugin'in kendi kodu fırlattı. Üyeler: `MethodName`, `RemoteExceptionType`, `BatchIndex`. Worker çökmez |
| `TimeoutException` | `DefaultTimeoutMs` / `timeoutMs` aşıldı. Bekleme kesilir; plugin kodu durdurulmaz |
| `PluginWorkerDisconnectedException` | Worker beklenmedik şekilde koptu (çökme / öldürülme) |
| `StaleMethodHandleException` | Alt seviye API'de eski nesle ait metot handle'ı kullanıldı |
| `InvalidOperationException` | Pasif plugin'e çağrı, protokol uyuşmazlığı, ayar çakışması |
| `KeyNotFoundException` | Silinmiş bir kaydın builder'ı kullanıldı |
| `MissingMethodException` | Metot ya da uygun imza yok (mevcut imzalar mesajda listelenir) |
| `MissingMemberException` | `GetValue`/`SetValue` için property ya da field yok |

JSON komutlar exception fırlatmaz; aynı durumlar `EvokerErrorCodes` ile döner
([tablo](../DSO.Core.Evoker/README.md#sonuç-ve-hata-modeli)).

---

## PluginHost (worker) dağıtımı

`DSO.Core.Evoker.PluginHost` küçük bir konsol uygulamasıdır. Host onu şu argümanlarla başlatır:

```
<pipeAdı> <pluginDosyası> <tipTamAdı> <includeNonPublic:true|false> <maxConcurrency>
```

Önerilen düzen ([Plugins.Api](../DSO.Core.Evoker.Plugins.Api/README.md) bunu kendiliğinden kurar):

```
<uygulama klasörü>/
  MyApp.dll ...
  Host/      DSO.Core.Evoker.PluginHost.dll (+ .exe, .runtimeconfig.json, .deps.json, DSO.Core.*.dll)
  Plugins/   Siparis/1.0/Siparis.dll, Stok/v2/Stok.dll ...
```

- **Protokol sürümü** (`IpcProtocol.Version` = 2) derleme anında hem host'a hem worker'a gömülür. Kütüphaneyi
  güncellediğinizde PluginHost'u da yeniden derleyin; eski worker açık bir hatayla reddedilir.
- **Hızlı açılış için:** RID ile publish edildiğinde ReadyToRun etkinleşir. `WarmStart` ile ilk çağrı başlatmayı
  beklemez.

---

## Testler ve örnekler

`TestKit` klasöründeki her proje kendi başına çalışır:

| Proje | Kontrol | Kapsam |
|---|---:|---|
| `SamplePlugin` / `SampleDep` | — | Test plugin'i: void/senkron/Task/`Task<T>`, optional, event'ler, complex tipler, `ConfiguredPlugin` (parametreli ctor), bağımlılık (v1/v2 sürüm izolasyonu için) |
| `SmokeTest` | 4 | `DetectKind`, `Scan` + `ToLogLines`, in-process yükleme ve çağrı, builder üzerinden `GetValue`/`SetValue` |
| `SandboxSmokeTest` | 11 | Worker el sıkışması, resolve/invoke, `Task<T>`/void/complex dönüşler, hatanın worker'ı çökertmemesi, heartbeat, `includeNonPublic`, in-process'e terfi, worker havuzu, 200.000 satır stdout selinde kilitlenmeme, `MaxConcurrency` paralelliği, uzun çağrının heartbeat'i yanlışlıkla tetiklememesi, çağrı bazlı timeout, **`Environment.FailFast` ile çöken plugin'e rağmen host'un ayakta kalması** |
| `BuilderParityTest` | **368** | **Aynı senaryo** hem sandbox hem in-process `IPluginBuilder`'a karşı; sonuçlar birebir aynı olmalı (tipli delegate'ler, şekil eşleyici == JSON, MLC şablonları) |
| `UnloadTest` | 48 | In-process plugin gerçekten bellekten atılıyor mu; cache'ler unload'u engelliyor mu; sürüm izolasyonu (SampleDep v1/v2) |
| `ManagerTest` | **87** | Guid anahtarlar, pasif/aktif, canlı mod geçişi (yük altında), kalıcılık, çökme bildirimi (sahte çökme yok), WarmStart, constructor argümanları, katalog komutları, sandbox == in-process JSON, Faulted → düzeltme, eski kayıt biçiminden taşıma |
| `PerfBench` | — | Bu sayfadaki tüm performans rakamları |

```
dotnet run --project TestKit/BuilderParityTest -- <SamplePlugin.dll> <DSO.Core.Evoker.PluginHost.dll>
dotnet run --project TestKit/ManagerTest       -- <SamplePlugin.dll> <DSO.Core.Evoker.PluginHost.dll>
dotnet run --project TestKit/UnloadTest        -- <SamplePlugin.dll> [SampleDep v2 dll]
dotnet run -c Release --project TestKit/PerfBench -- <SamplePlugin.dll> <DSO.Core.Evoker.PluginHost.dll>
```

Gerçekçi örnek plugin'ler için `DemoPlugins` klasörüne bakın:

- **Siparis (C#):** async, `Task.WhenAll`, iç içe DTO, overload, event.
- **Stok v1 / v2 (VB.NET):** Optional, `ParamArray`, aynı assembly'nin iki sürümü.
- **Rapor (C#):** pid ile izolasyon, timeout, çökme, özel exception.

Bunları çalışır halde görmek için:
[DSO.Core.Evoker.Plugins.Api → DemoApi](../DSO.Core.Evoker.Plugins.Api/README.md#demo-ve-testler).

---

## Bilinen sınırlar

- **Process izolasyonu bir güvenlik sandbox'ı değildir.** Worker, host ile aynı kullanıcı haklarıyla çalışır. Çökme,
  donma ve bellek sorunlarına karşı korur; kötü niyetli koda karşı ek işletim sistemi önlemi gerekir (ayrı kullanıcı,
  konteyner vb.).
- Şu an sadece **managed (.NET)** plugin'ler desteklenir. `NativePluginLoader` ileride kullanılmak üzere yer tutucudur.
- `ref` / `out` parametreli metotlar plugin API'sinden çağrılamaz; açık bir hata verilir.
- Timeout'lar **beklemeyi** keser; plugin'in çalışan kodunu durdurmaz. Gerçekten durdurmak için sandbox worker'ı stop
  ya da reload edin. Heartbeat, worker'ın mesaj döngüsünü izler; uzun süren tek bir çağrıyı "donmuş" saymaz.
- Sandbox'ta **senkron** `Invoke`, async'ten genelde yavaştır. Sıkı döngülerde `InvokeBatchAsync` ya da InProcess +
  `GetFunc` kullanın.
- InProcess modda yüklü bir DLL'in üzerine Windows'ta yazılamaz (dosya kilidi). Önce `StopAsync` ya da
  `DeactivateAsync` çağırın.
