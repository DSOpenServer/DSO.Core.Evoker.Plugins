namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// Host ile worker (DSO.Core.Evoker.PluginHost) arasındaki protokolün sürümü. Worker bunu Hello mesajında
    /// bildirir; host kendi sürümüyle uyuşmazsa worker'ı kapatıp açık bir hata verir.
    ///
    /// NEDEN: 'const' olduğu için değer DERLEME ANINDA hem host'a hem worker exe'sine gömülür. HostPath eski bir
    /// PluginHost build'ini gösterirse (proje yeniden derlenmemiş, yol eski bir bin klasörüne bakıyor...) bu
    /// fark burada yakalanır. Önceden eski worker "çalışıyor" görünüp, sonradan eklenen özelliklerde (ör.
    /// büyük/küçük harf duyarsız metot arama) anlaşılmaz "metot bulunamadı" hataları veriyordu - testte
    /// birebir yaşandı.
    ///
    /// KURAL: protokolde (mesaj tipi, payload düzeni, worker davranışı) her değişiklikte bu sayı artırılır.
    ///   0 = sürüm bildirmeyen eski worker'lar (bu kontrol eklenmeden önceki tüm build'ler)
    ///   1 = Member/EventRaised/InvokeBatch mesajları, FindMethod tabanlı resolve, CurrentUserOnly pipe
    /// </summary>
    public static class IpcProtocol
    {
        public const int Version = 1;
    }
}