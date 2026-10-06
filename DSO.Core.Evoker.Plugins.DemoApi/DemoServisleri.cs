namespace DSO.Core.Evoker.Plugins.DemoApi.Services
{
    /// <summary>
    /// Plugin OLMAYAN örnek hedefler - host'un kendi sınıfları, EvokerCatalog'a kodda kaydedilir (bkz. Program.cs) ve
    /// api/evoker/targets/{key} uçlarından aynı JSON komutlarla kullanılır. Nesne ömürleri DI'daki gibi:
    ///   Sayac        Singleton - tüm istekler aynı nesne (state kalıcı)
    ///   Sepet        Scoped    - komut başına bir nesne (çok adımlı komutun adımları aynı nesneyi paylaşır)
    ///   Hesap        Transient - her ADIM yeni nesne
    ///   SunucuSaati  Static    - nesne yok (static sınıf)
    /// </summary>
    public static class DemoAnahtarlar
    {
        public static readonly Guid Sayac = Guid.Parse("5a1ac000-0000-4000-8000-000000000001");
        public static readonly Guid Sepet = Guid.Parse("5a1ac000-0000-4000-8000-000000000002");
        public static readonly Guid Hesap = Guid.Parse("5a1ac000-0000-4000-8000-000000000003");
        public static readonly Guid SunucuSaati = Guid.Parse("5a1ac000-0000-4000-8000-000000000004");
    }

    public class Sayac
    {
        private long _deger;
        public string Ad { get; set; } = "genel";
        public long Deger => Interlocked.Read(ref _deger);
        public long Arttir(int adim = 1) => Interlocked.Add(ref _deger, adim);
        public void Sifirla() => Interlocked.Exchange(ref _deger, 0);
    }

    public class Sepet
    {
        private readonly List<SepetKalemi> _kalemler = new();
        public Guid SepetNo { get; } = Guid.NewGuid();
        public int KalemSayisi => _kalemler.Count;

        public int Ekle(string urun, int adet = 1, decimal fiyat = 0)
        {
            _kalemler.Add(new SepetKalemi { Urun = urun, Adet = adet, Fiyat = fiyat });
            return _kalemler.Count;
        }

        public List<SepetKalemi> Kalemler() => _kalemler.ToList();
        public decimal Toplam() => _kalemler.Sum(k => k.Adet * k.Fiyat);
    }

    public class SepetKalemi
    {
        public string Urun { get; set; } = "";
        public int Adet { get; set; }
        public decimal Fiyat { get; set; }
    }

    public class Hesap
    {
        public Guid NesneNo { get; } = Guid.NewGuid();
        public decimal Topla(decimal a, decimal b) => a + b;
        public decimal Bol(decimal a, decimal b) => a / b; // b=0 → DivideByZeroException → TargetException (500)
        public double Ortalama(params double[] sayilar) => sayilar.Length == 0 ? 0 : sayilar.Average();
        public async Task<double> KarekokAsync(double x)
        {
            await Task.Delay(10);
            if (x < 0) throw new ArgumentOutOfRangeException(nameof(x), "Negatif sayının karekökü yok.");
            return Math.Sqrt(x);
        }
    }

    public static class SunucuSaati
    {
        private static readonly DateTime Baslangic = DateTime.Now;
        public static DateTime Simdi() => DateTime.Now;
        public static string CalismaSuresi() => (DateTime.Now - Baslangic).ToString(@"hh\:mm\:ss");
        public static string Bicimle(DateTime tarih, string bicim = "dd.MM.yyyy HH:mm") => tarih.ToString(bicim);
    }
}