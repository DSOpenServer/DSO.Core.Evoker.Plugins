using System.Diagnostics;

namespace Siparis
{
    /// <summary>
    /// Örnek plugin: mağaza sipariş servisi. Bilerek "gerçek hayattaki gibi" karmaşık:
    ///   - parametreli constructor (metin + optional decimal + optional iç içe ayar nesnesi),
    ///   - iç içe DTO'lar (Musteri → Adres, Siparis → List&lt;SiparisSatiri&gt;), enum, nullable parametreler,
    ///   - sync ve async metotlar (Task&lt;T&gt;, ValueTask&lt;T&gt;), Task.WhenAll ile paralel işler,
    ///   - aşırı yüklenmiş (overload) metotlar, event, doğrulama hataları (exception), thread-safe state.
    /// Evoker'a referansı yoktur.
    /// </summary>
    public class SiparisServisi
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, Siparis> _siparisler = new();
        private int _sonNo = 1000;
        private decimal _kdvOrani;

        public SiparisServisi(string magazaAdi, decimal kdvOrani = 0.20m, SiparisAyarlari? ayarlar = null)
        {
            if (string.IsNullOrWhiteSpace(magazaAdi))
                throw new ArgumentException("Mağaza adı boş olamaz.", nameof(magazaAdi));
            MagazaAdi = magazaAdi;
            KdvOrani = kdvOrani;
            Ayarlar = ayarlar ?? new SiparisAyarlari();
            AcilisZamani = DateTime.Now;
        }

        // ============================ Property'ler ============================

        public static string Surum => "1.0.0";

        public string MagazaAdi { get; }

        public SiparisAyarlari Ayarlar { get; }

        public DateTime AcilisZamani { get; }

        /// <summary>0 ile 1 arası (0.20 = %20).</summary>
        public decimal KdvOrani
        {
            get => _kdvOrani;
            set
            {
                if (value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value), "KDV oranı 0 ile 1 arasında olmalı (0.20 = %20).");
                _kdvOrani = value;
            }
        }

        public int SiparisSayisi { get { lock (_lock) return _siparisler.Count; } }

        public decimal ToplamCiro
        {
            get { lock (_lock) return _siparisler.Values.Where(s => s.Durum != SiparisDurumu.Iptal).Sum(s => s.GenelToplam); }
        }

        public string? SonIslem { get; private set; }

        /// <summary>Sipariş durumu her değiştiğinde.</summary>
        public event EventHandler<DurumDegistiEventArgs>? DurumDegisti;

        // ============================ Sync metotlar ============================

        /// <summary>Yeni sipariş (taslak).</summary>
        public Siparis Olustur(Musteri musteri, List<SiparisSatiri> satirlar)
        {
            if (musteri == null || string.IsNullOrWhiteSpace(musteri.Kod))
                throw new ArgumentException("Müşteri kodu zorunlu.", nameof(musteri));
            if (satirlar == null || satirlar.Count == 0)
                throw new ArgumentException("Sipariş en az bir satır içermeli.", nameof(satirlar));
            if (satirlar.Count > Ayarlar.MaxSatir)
                throw new InvalidOperationException($"Bir siparişte en fazla {Ayarlar.MaxSatir} satır olabilir (gelen: {satirlar.Count}).");
            foreach (var s in satirlar)
            {
                if (s.Adet <= 0) throw new ArgumentException($"'{s.UrunKodu}' adedi pozitif olmalı.");
                if (s.BirimFiyat < 0) throw new ArgumentException($"'{s.UrunKodu}' fiyatı negatif olamaz.");
            }

            lock (_lock)
            {
                var siparis = new Siparis
                {
                    No = ++_sonNo,
                    Musteri = musteri,
                    Satirlar = satirlar.Select(s => new SiparisSatiri { UrunKodu = s.UrunKodu, Adet = s.Adet, BirimFiyat = s.BirimFiyat }).ToList(),
                    Durum = SiparisDurumu.Taslak,
                    OlusturmaZamani = DateTime.Now
                };
                Hesapla(siparis);
                siparis.Gecmis.Add($"{DateTime.Now:HH:mm:ss} oluşturuldu");
                _siparisler[siparis.No] = siparis;
                SonIslem = $"#{siparis.No} oluşturuldu";
                return siparis;
            }
        }

        /// <summary>Kısa yol (overload): tek satırlı sipariş.</summary>
        public Siparis Olustur(string musteriKodu, string urunKodu, int adet, decimal birimFiyat) =>
            Olustur(new Musteri { Kod = musteriKodu, Ad = musteriKodu }, new List<SiparisSatiri>
            {
                new() { UrunKodu = urunKodu, Adet = adet, BirimFiyat = birimFiyat }
            });

        public Siparis Getir(int no)
        {
            lock (_lock)
                return _siparisler.TryGetValue(no, out var s) ? s : throw new KeyNotFoundException($"#{no} numaralı sipariş yok.");
        }

        public List<Siparis> Listele(SiparisDurumu? durum = null, string? musteriKodu = null)
        {
            lock (_lock)
                return _siparisler.Values
                    .Where(s => durum == null || s.Durum == durum)
                    .Where(s => musteriKodu == null || string.Equals(s.Musteri.Kod, musteriKodu, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.No)
                    .ToList();
        }

        public void IptalEt(int no, string neden)
        {
            var s = Getir(no);
            if (s.Durum is SiparisDurumu.Kargoda or SiparisDurumu.TeslimEdildi)
                throw new InvalidOperationException($"#{no} {s.Durum} durumunda - iptal edilemez.");
            DurumDegistir(s, SiparisDurumu.Iptal, "iptal: " + neden);
        }

        /// <summary>Satırların tutarı (sipariş oluşturmadan): ara toplam, indirim, KDV, genel toplam.</summary>
        public TutarOzeti Hesapla(List<SiparisSatiri> satirlar, decimal? indirimOrani = null)
        {
            decimal ara = satirlar.Sum(s => s.Adet * s.BirimFiyat);
            decimal indirim = Math.Round(ara * (indirimOrani ?? Ayarlar.IndirimOrani), 2);
            decimal kdv = Math.Round((ara - indirim) * KdvOrani, 2);
            return new TutarOzeti { AraToplam = ara, Indirim = indirim, Kdv = kdv, GenelToplam = ara - indirim + kdv };
        }

        /// <summary>Müşteri bazında ciro (iptaller hariç).</summary>
        public Dictionary<string, decimal> CiroRaporu()
        {
            lock (_lock)
                return _siparisler.Values
                    .Where(s => s.Durum != SiparisDurumu.Iptal)
                    .GroupBy(s => s.Musteri.Kod)
                    .ToDictionary(g => g.Key, g => g.Sum(s => s.GenelToplam));
        }

        // ============================ Async metotlar ============================

        /// <summary>Siparişi onaylar (ödeme kontrolü taklidi - Ayarlar.OnayGecikmesiMs kadar sürer).</summary>
        public async Task<Siparis> OnaylaAsync(int no)
        {
            var s = Getir(no);
            if (s.Durum != SiparisDurumu.Taslak)
                throw new InvalidOperationException($"#{no} zaten {s.Durum}.");
            await Task.Delay(Ayarlar.OnayGecikmesiMs).ConfigureAwait(false);
            DurumDegistir(s, SiparisDurumu.Onaylandi, "ödeme onaylandı");
            return s;
        }

        public async Task<Siparis> KargoyaVerAsync(int no, string kargoFirmasi = "Yurtiçi")
        {
            var s = Getir(no);
            if (s.Durum != SiparisDurumu.Onaylandi)
                throw new InvalidOperationException($"#{no} kargoya verilemez - önce onaylanmalı (şu an {s.Durum}).");
            await Task.Delay(50).ConfigureAwait(false);
            s.KargoTakipNo = $"{kargoFirmasi.ToUpperInvariant()[..Math.Min(3, kargoFirmasi.Length)]}-{no}-{Random.Shared.Next(1000, 9999)}";
            DurumDegistir(s, SiparisDurumu.Kargoda, $"kargoya verildi ({kargoFirmasi}, {s.KargoTakipNo})");
            return s;
        }

        /// <summary>
        /// Ürünlerin fiyatlarını 3 tedarikçiden PARALEL ister (Task.WhenAll), her ürün için en ucuzu seçer.
        /// Seri olsaydı ~ürün×3×gecikme sürerdi; paralel olduğu için ~en yavaş tedarikçi kadar sürer.
        /// </summary>
        public async Task<FiyatOzeti> FiyatTeklifiAlAsync(List<string> urunKodlari)
        {
            if (urunKodlari == null || urunKodlari.Count == 0) throw new ArgumentException("En az bir ürün kodu verin.");
            var sw = Stopwatch.StartNew();
            string[] tedarikciler = { "Alfa", "Beta", "Gama" };

            var istekler = urunKodlari.SelectMany(u => tedarikciler.Select(t => TeklifAsync(u, t))).ToList();
            var teklifler = await Task.WhenAll(istekler).ConfigureAwait(false);

            var enIyi = teklifler.GroupBy(t => t.UrunKodu).Select(g => g.OrderBy(t => t.Fiyat).First()).ToList();
            return new FiyatOzeti
            {
                Teklifler = enIyi,
                TumTeklifSayisi = teklifler.Length,
                Toplam = enIyi.Sum(t => t.Fiyat),
                ToplamSureMs = sw.ElapsedMilliseconds,
                SeriOlsaydiMs = teklifler.Sum(t => t.SureMs)
            };
        }

        private static async Task<FiyatTeklifi> TeklifAsync(string urunKodu, string tedarikci)
        {
            int gecikme = 80 + Math.Abs((urunKodu + tedarikci).GetHashCode() % 120);
            await Task.Delay(gecikme).ConfigureAwait(false);
            decimal fiyat = 50 + Math.Abs((tedarikci + urunKodu).GetHashCode() % 5000) / 10m;
            return new FiyatTeklifi { UrunKodu = urunKodu, Tedarikci = tedarikci, Fiyat = fiyat, SureMs = gecikme };
        }

        /// <summary>Aynı anda <paramref name="adet"/> sipariş oluşturur (paralel görevler; state thread-safe).</summary>
        public async Task<TopluSonuc> TopluOlusturAsync(int adet, string musteriKodu = "TOPLU")
        {
            if (adet <= 0 || adet > 1000) throw new ArgumentOutOfRangeException(nameof(adet), "1-1000 arası.");
            var sw = Stopwatch.StartNew();
            var gorevler = Enumerable.Range(1, adet).Select(async i =>
            {
                await Task.Delay(Random.Shared.Next(5, 30)).ConfigureAwait(false);
                return Olustur(musteriKodu, "URN-" + (i % 7), i % 5 + 1, 10m * (i % 9 + 1)).No;
            });
            var numaralar = await Task.WhenAll(gorevler).ConfigureAwait(false);
            return new TopluSonuc { Adet = numaralar.Length, IlkNo = numaralar.Min(), SonNo = numaralar.Max(), SureMs = sw.ElapsedMilliseconds };
        }

        /// <summary>ValueTask örneği: önbellekten (sync) ya da hesaplayarak (async) döner.</summary>
        public ValueTask<int> SayAsync(SiparisDurumu durum)
        {
            lock (_lock)
                return new ValueTask<int>(_siparisler.Values.Count(s => s.Durum == durum));
        }

        // ============================ İç işler ============================

        private void Hesapla(Siparis s)
        {
            var t = Hesapla(s.Satirlar);
            s.AraToplam = t.AraToplam;
            s.Indirim = t.Indirim;
            s.Kdv = t.Kdv;
            s.GenelToplam = t.GenelToplam;
        }

        private void DurumDegistir(Siparis s, SiparisDurumu yeni, string not)
        {
            SiparisDurumu eski;
            lock (_lock)
            {
                eski = s.Durum;
                s.Durum = yeni;
                s.Gecmis.Add($"{DateTime.Now:HH:mm:ss} {not}");
                SonIslem = $"#{s.No} {eski} → {yeni}";
            }
            DurumDegisti?.Invoke(this, new DurumDegistiEventArgs { No = s.No, Eski = eski, Yeni = yeni, Not = not });
        }
    }

    // ============================ Modeller ============================

    public enum SiparisDurumu { Taslak, Onaylandi, Kargoda, TeslimEdildi, Iptal }

    public class SiparisAyarlari
    {
        public int MaxSatir { get; set; } = 20;
        public int OnayGecikmesiMs { get; set; } = 150;
        /// <summary>Varsayılan indirim oranı (0.05 = %5).</summary>
        public decimal IndirimOrani { get; set; }
    }

    public class Adres
    {
        public string Il { get; set; } = "";
        public string? Ilce { get; set; }
        public string? AcikAdres { get; set; }
    }

    public class Musteri
    {
        public string Kod { get; set; } = "";
        public string Ad { get; set; } = "";
        public string? Eposta { get; set; }
        public Adres? Adres { get; set; }
    }

    public class SiparisSatiri
    {
        public string UrunKodu { get; set; } = "";
        public int Adet { get; set; }
        public decimal BirimFiyat { get; set; }
        public decimal Tutar => Adet * BirimFiyat;
    }

    public class Siparis
    {
        public int No { get; set; }
        public Musteri Musteri { get; set; } = new();
        public List<SiparisSatiri> Satirlar { get; set; } = new();
        public SiparisDurumu Durum { get; set; }
        public decimal AraToplam { get; set; }
        public decimal Indirim { get; set; }
        public decimal Kdv { get; set; }
        public decimal GenelToplam { get; set; }
        public string? KargoTakipNo { get; set; }
        public DateTime OlusturmaZamani { get; set; }
        public List<string> Gecmis { get; set; } = new();
    }

    public class TutarOzeti
    {
        public decimal AraToplam { get; set; }
        public decimal Indirim { get; set; }
        public decimal Kdv { get; set; }
        public decimal GenelToplam { get; set; }
    }

    public class FiyatTeklifi
    {
        public string UrunKodu { get; set; } = "";
        public string Tedarikci { get; set; } = "";
        public decimal Fiyat { get; set; }
        public int SureMs { get; set; }
    }

    public class FiyatOzeti
    {
        public List<FiyatTeklifi> Teklifler { get; set; } = new();
        public int TumTeklifSayisi { get; set; }
        public decimal Toplam { get; set; }
        public long ToplamSureMs { get; set; }
        public long SeriOlsaydiMs { get; set; }
    }

    public class TopluSonuc
    {
        public int Adet { get; set; }
        public int IlkNo { get; set; }
        public int SonNo { get; set; }
        public long SureMs { get; set; }
    }

    public class DurumDegistiEventArgs : EventArgs
    {
        public int No { get; set; }
        public SiparisDurumu Eski { get; set; }
        public SiparisDurumu Yeni { get; set; }
        public string Not { get; set; } = "";
    }
}
