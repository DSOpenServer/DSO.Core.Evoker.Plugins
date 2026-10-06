using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Rapor
{
    /// <summary>
    /// Örnek plugin: rapor servisi - izolasyonun ve hata yönetiminin gösterimi:
    ///   - Ortam(): hangi process'te / hangi AssemblyLoadContext'te çalıştığını söyler (sandbox: ayrı pid; in-process: host'un pid'i),
    ///   - YavasAsync / Bekle: timeout senaryoları (komutta "timeoutMs"),
    ///   - Cokert(): worker process'ini anında öldürür (sandbox'ta host ayakta kalır; in-process modda GÜVENLİK İÇİN reddedilir),
    ///   - HataFirlat(): plugin içi exception → TargetException (500) ve özel exception tipi,
    ///   - OlusturAsync(RaporIstegi): iç içe istek nesnesi, birden çok adımlı async iş, ilerleme event'i.
    /// </summary>
    public class RaporServisi
    {
        private int _uretilen;
        private readonly int _varsayilanGecikmeMs;

        public RaporServisi(int varsayilanGecikmeMs = 0) => _varsayilanGecikmeMs = varsayilanGecikmeMs;

        public int UretilenRaporSayisi => _uretilen;

        /// <summary>Rapor üretimi sırasında her adımda (0-100).</summary>
        public event EventHandler<int>? Ilerleme;

        public OrtamBilgisi Ortam()
        {
            var p = Process.GetCurrentProcess();
            var asm = typeof(RaporServisi).Assembly;
            return new OrtamBilgisi
            {
                ProcessId = Environment.ProcessId,
                ProcessAdi = p.ProcessName,
                KomutSatiri = Environment.GetCommandLineArgs().FirstOrDefault() ?? "",
                SandboxWorkerIcinde = CalisiyorWorkerda(),
                LoadContext = AssemblyLoadContext.GetLoadContext(asm)?.Name ?? "?",
                Framework = RuntimeInformation.FrameworkDescription,
                BellekMb = Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 1),
                ThreadSayisi = p.Threads.Count,
                CalismaSuresiSn = Math.Round((DateTime.Now - p.StartTime).TotalSeconds, 1)
            };
        }

        /// <summary>Async bekleme - komuttaki timeoutMs'den uzunsa Timeout (504) döner.</summary>
        public async Task<string> YavasAsync(int ms)
        {
            await Task.Delay(ms).ConfigureAwait(false);
            return $"{ms} ms sonra bitti";
        }

        /// <summary>SYNC bekleme (thread'i bloklar).</summary>
        public string Bekle(int ms)
        {
            Thread.Sleep(ms);
            return $"{ms} ms (sync) sonra bitti";
        }

        /// <summary>Plugin içi hata - API'de TargetException (500) + exceptionType.</summary>
        public void HataFirlat(string mesaj = "Rapor şablonu bozuk")
        {
            throw new RaporHatasi(mesaj) { HataKodu = "RPR-042" };
        }

        /// <summary>
        /// Process'i ANINDA öldürür (Environment.FailFast). Sandbox'ta sadece worker ölür: host ayakta kalır, çağrı hata
        /// döner, plugin Crashed olur ve bir sonraki çağrıda taze worker açılır. In-process modda host'u da öldüreceği
        /// için reddedilir.
        /// </summary>
        public void Cokert()
        {
            if (!CalisiyorWorkerda())
                throw new InvalidOperationException("Cokert() sadece sandbox modunda çalışır - in-process modda host'u da öldürürdü.");
            Environment.FailFast("Rapor plugin'i bilerek çökertildi (demo).");
        }

        /// <summary>Çok adımlı async rapor: veri topla (paralel kaynaklar) → hesapla → biçimlendir.</summary>
        public async Task<RaporSonucu> OlusturAsync(RaporIstegi istek)
        {
            if (istek == null) throw new ArgumentNullException(nameof(istek));
            if (istek.Baslangic > istek.Bitis) throw new ArgumentException("Başlangıç tarihi bitişten sonra olamaz.");
            var sw = Stopwatch.StartNew();
            Ilerleme?.Invoke(this, 0);

            // 1) kaynakları paralel topla
            var kaynaklar = (istek.Kaynaklar is { Count: > 0 } k ? k : new List<string> { "satis", "iade", "stok" });
            var veriler = await Task.WhenAll(kaynaklar.Select(async ad =>
            {
                await Task.Delay(_varsayilanGecikmeMs + 40 + ad.Length * 10).ConfigureAwait(false);
                int gun = Math.Max(1, (int)(istek.Bitis - istek.Baslangic).TotalDays + 1);
                return new RaporSatiri { Kaynak = ad, KayitSayisi = gun * (ad.Length + 3), Tutar = Math.Round(gun * ad.Length * 137.5m, 2) };
            })).ConfigureAwait(false);
            Ilerleme?.Invoke(this, 60);

            // 2) hesapla
            await Task.Yield();
            var sonuc = new RaporSonucu
            {
                Baslik = string.IsNullOrWhiteSpace(istek.Baslik) ? "Rapor" : istek.Baslik!,
                Donem = $"{istek.Baslangic:yyyy-MM-dd} - {istek.Bitis:yyyy-MM-dd}",
                Satirlar = veriler.OrderByDescending(v => v.Tutar).ToList(),
                ToplamTutar = veriler.Sum(v => v.Tutar),
                Bicim = istek.Bicim
            };
            Ilerleme?.Invoke(this, 90);

            // 3) biçimlendir
            sonuc.Metin = istek.Bicim switch
            {
                RaporBicimi.Csv => "kaynak;kayit;tutar\n" + string.Join("\n", sonuc.Satirlar.Select(s => $"{s.Kaynak};{s.KayitSayisi};{s.Tutar}")),
                RaporBicimi.Ozet => $"{sonuc.Baslik} ({sonuc.Donem}): {sonuc.Satirlar.Count} kaynak, toplam {sonuc.ToplamTutar:N2}",
                _ => null
            };
            sonuc.SureMs = sw.ElapsedMilliseconds;
            Interlocked.Increment(ref _uretilen);
            Ilerleme?.Invoke(this, 100);
            return sonuc;
        }

        private static bool CalisiyorWorkerda() =>
            (Environment.GetCommandLineArgs().FirstOrDefault() ?? "").IndexOf("PluginHost", StringComparison.OrdinalIgnoreCase) >= 0
            || Process.GetCurrentProcess().ProcessName.IndexOf("PluginHost", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public enum RaporBicimi { Json, Csv, Ozet }

    public class RaporIstegi
    {
        public string? Baslik { get; set; }
        public DateTime Baslangic { get; set; } = DateTime.Today.AddDays(-7);
        public DateTime Bitis { get; set; } = DateTime.Today;
        public List<string>? Kaynaklar { get; set; }
        public RaporBicimi Bicim { get; set; } = RaporBicimi.Json;
    }

    public class RaporSatiri
    {
        public string Kaynak { get; set; } = "";
        public int KayitSayisi { get; set; }
        public decimal Tutar { get; set; }
    }

    public class RaporSonucu
    {
        public string Baslik { get; set; } = "";
        public string Donem { get; set; } = "";
        public List<RaporSatiri> Satirlar { get; set; } = new();
        public decimal ToplamTutar { get; set; }
        public RaporBicimi Bicim { get; set; }
        public string? Metin { get; set; }
        public long SureMs { get; set; }
    }

    public class OrtamBilgisi
    {
        public int ProcessId { get; set; }
        public string ProcessAdi { get; set; } = "";
        public string KomutSatiri { get; set; } = "";
        public bool SandboxWorkerIcinde { get; set; }
        public string LoadContext { get; set; } = "";
        public string Framework { get; set; } = "";
        public double BellekMb { get; set; }
        public int ThreadSayisi { get; set; }
        public double CalismaSuresiSn { get; set; }
    }

    public class RaporHatasi : Exception
    {
        public RaporHatasi(string mesaj) : base(mesaj) { }
        public string HataKodu { get; set; } = "";
    }
}
