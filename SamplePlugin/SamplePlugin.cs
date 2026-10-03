using System;
using System.Threading.Tasks;

namespace TestPlugin
{
    // Basit, "sanki gerçek bir plugin DLL'i" test tipi.
    // PluginScanner + ManagedDotNetPluginLoader'ı hem tarama hem invoke tarafında
    // (void / senkron / Task / Task<T> / optional parametre) test etmek için kullanılıyor.
    public class SamplePlugin
    {
        public int LastVoidCallValue;

        // GetValue<T>/SetValue<T> testleri için gerçek bir { get; set; } property.
        public string DisplayName { get; set; } = "default";

        public void DoSomething(int x) => LastVoidCallValue = x;

        public int Add(int a, int b) => a + b;

        public async Task<int> AddAsync(int a, int b)
        {
            await Task.Delay(5);
            return a + b;
        }

        public async Task DoAsyncWork(int x)
        {
            await Task.Delay(5);
            LastVoidCallValue = x * 10;
        }

        public string Greet(string name, string greeting = "Merhaba") => $"{greeting}, {name}!";

        // includeNonPublic:true testleri için - gerçek plugin senaryosunda 3. parti kodun DIŞARI
        // AÇMADIĞI ama admin/tanılama amaçlı erişmek isteyebileceğiniz üyeler.
        private int _secretCounter = 5;
        private string SecretName { get; set; } = "hidden";
        private int MultiplySecret(int a, int b) => a * b;

        // Sandbox/IPC testleri için - WireValueCodec'in Complex (primitive OLMAYAN) yolunu
        // (WireTypeCode.Complex, JSON öncelikli encode/decode) uçtan uca sınamak amacıyla.
        public Point MakePoint(int x, int y) => new Point { X = x, Y = y };

        // Sandbox/IPC testleri için - plugin metodu exception fırlatınca InvokeReply.Success=false +
        // ExceptionType/ExceptionMessage'ın doğru taşındığını (worker'ın ÇÖKMEDİĞİNİ, sadece bu
        // çağrının başarısız döndüğünü) doğrulamak amacıyla.
        public void Throws() => throw new InvalidOperationException("kasıtlı test hatası");

        // Sandbox testleri: uzun süren bir çağrı - MaxConcurrency (paralellik), çağrı bazlı timeout
        // ve "uzun Invoke heartbeat'i YANLIŞLIKLA tetiklememeli" senaryoları için.
        public async Task<int> SlowAsync(int ms)
        {
            await Task.Delay(ms);
            return ms;
        }

        // Sandbox testleri: plugin'in stdout'a ÇOK yazması worker'ı kilitlememeli (host stdout'u tüketiyor).
        public int SpamConsole(int lines)
        {
            for (int i = 0; i < lines; i++)
                Console.WriteLine($"spam satırı {i} - xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx");
            return lines;
        }

        // Sandbox testleri: plugin'in process'i GERÇEKTEN çökertmesi (catch edilemez) - host process
        // AYAKTA kalmalı, sadece bu worker ölü sayılmalı. İzolasyonun asıl kanıtı budur.
        public void CrashHard() => Environment.FailFast("SamplePlugin.CrashHard - kasıtlı process çökmesi");

        // --- IPluginBuilder parite testleri (sandbox ve in-process AYNI senaryo) ---

        // Enum: tel üzerinde sayı olarak taşınır; host plugin'in enum tipini bilmeden int ile okuyup yazabilmeli.
        public Level CurrentLevel { get; set; } = Level.Low;
        public Level NextLevel(Level l) => l == Level.High ? Level.High : l + 1;

        // Complex property + Complex argüman: host kendi DTO'suyla (aynı şekil) okuyup yazabilmeli.
        public Point Location { get; set; } = new Point { X = 1, Y = 1 };
        public int SumPoint(Point p) => p.X + p.Y;

        // readonly alan: okunabilir, yazma net hata vermeli.
        public readonly int ReadOnlyValue = 10;

        // static metot: instance olmadan da çağrılabilmeli (EvokerBuilder'ın IsStatic yolu).
        public static int StaticTwice(int x) => x * 2;

        // Aynı isim + aynı parametre sayısı, farklı tipler: overload seçimi argüman tiplerine göre.
        public int Combine(int a, int b) => a * 10 + b;
        public string Combine(string a, string b) => a + "+" + b;

        // State: aynı instance üzerinde çağrılar arasında korunmalı.
        public int Counter;
        public int Increment()
        {
            ++Counter;
            CounterChanged?.Invoke(this, Counter);
            return Counter;
        }

        // --- Event testleri (in-process + sandbox) ---
        public event EventHandler<int>? CounterChanged;
        public event Action<string, int>? Progress;
        public int RunWithProgress(int steps)
        {
            for (int i = 1; i <= steps; i++) Progress?.Invoke("adım", i);
            return steps;
        }
        public event EventHandler<Point>? PointMoved;
        public void MoveTo(int x, int y)
        {
            Location = new Point { X = x, Y = y };
            PointMoved?.Invoke(this, Location);
        }

        // --- Toplu çağrı testi: b=0 olan çağrıda DivideByZeroException (BatchIndex doğrulaması) ---
        public int CheckedDivide(int a, int b) => a / b;

        // --- VB.NET Optional parametre testi ---
        public string OptionalDemo(int a, int b = 5, string s = "x") => $"{a}|{b}|{s}";

        // --- Plugin'in KENDİ bağımlılığı (AssemblyLoadContext bağımlılık çözümü + sürüm izolasyonu) ---
        public string UseDependency() => SampleDep.DepHelper.Describe();
    }

    public enum Level { Low = 1, Mid = 2, High = 3 }

    // SamplePlugin.dll'in İÇİNDE tanımlı olması ÖNEMLİ - worker process bu DLL'i Assembly.LoadFrom
    // ile KENDİ İÇİNE yüklüyor, WireValueCodec'in Complex decode'u tipi (embedded TypeName üzerinden)
    // o AppDomain'de çözebilmeli. Ayrı bir "paylaşılan sözleşme" projesi bilinçli olarak YOK -
    // plugin'ler arbitrary DLL'ler, ortak bir tip sözleşmesi zorunluluğu bu tasarımın tam tersi.
    public class Point
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
}