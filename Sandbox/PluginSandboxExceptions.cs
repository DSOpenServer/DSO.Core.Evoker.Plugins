using System;

namespace DSO.Core.Evoker.Plugins.Sandbox
{
    /// <summary>
    /// Plugin kodunun KENDİSİ exception fırlattı (worker ÇÖKMEDİ, protokol hatası değil - sadece bu
    /// çağrı başarısız). Sandbox'ta orijinal exception process sınırını geçemediği için tipi ve mesajı
    /// string olarak taşınır; in-process'te (InProcessPluginBuilder) aynı exception fırlatılır ve
    /// orijinali InnerException'dadır - böylece IPluginBuilder kullanan kod iki modda da AYNI catch'i yazar.
    /// </summary>
    public sealed class PluginInvocationException : Exception
    {
        public string MethodName { get; }
        public string? RemoteExceptionType { get; }

        public PluginInvocationException(string methodName, string? remoteExceptionType, string? remoteMessage, Exception? inner = null)
            : base($"[Plugin] '{methodName}' çağrısı plugin içinde hata verdi: {remoteExceptionType}: {remoteMessage}", inner)
        {
            MethodName = methodName;
            RemoteExceptionType = remoteExceptionType;
        }

        /// <summary>
        /// Worker'dan gelen hata bilgisini, in-process ile AYNI exception tipine çevirir: "bulunamadı"
        /// hataları (MissingMethodException / MissingMemberException - yanlış isim, uygun overload yok)
        /// çağıran hatasıdır ve olduğu gibi fırlatılır; geri kalan her şey PluginInvocationException.
        /// </summary>
        public static Exception FromRemote(string name, string? remoteExceptionType, string? remoteMessage)
        {
            if (remoteExceptionType == typeof(MissingMethodException).FullName || remoteExceptionType == nameof(MissingMethodException))
                return new MissingMethodException(remoteMessage);
            if (remoteExceptionType == typeof(MissingMemberException).FullName || remoteExceptionType == nameof(MissingMemberException))
                return new MissingMemberException(remoteMessage);
            return new PluginInvocationException(name, remoteExceptionType, remoteMessage);
        }
    }

    /// <summary>
    /// Worker yeniden başlatıldıktan SONRA, önceki nesilde (restart öncesi) alınmış bir MethodHandle
    /// ile InvokeAsync çağrıldı. Yeni worker'ın handle tablosu sıfırdan başladığı için bu numara başka
    /// bir metoda karşılık gelebilir - sessizce yanlış metodu çağırmak yerine durduruyoruz.
    /// Çözüm: ResolveAsync ile yeniden resolve edin (ya da bunu otomatik yapan CallAsync'i kullanın).
    /// </summary>
    public sealed class StaleMethodHandleException : InvalidOperationException
    {
        public int MethodHandle { get; }
        public int IssuedGeneration { get; }
        public int CurrentGeneration { get; }

        public StaleMethodHandleException(int methodHandle, int issuedGeneration, int currentGeneration)
            : base($"[PluginWorkerHandle] MethodHandle {methodHandle} worker'ın {issuedGeneration}. neslinde verilmişti, " +
                   $"worker yeniden başlatıldı (şu an {currentGeneration}. nesil) - metodu yeniden ResolveAsync ile çözün " +
                   "ya da CallAsync kullanın.")
        {
            MethodHandle = methodHandle;
            IssuedGeneration = issuedGeneration;
            CurrentGeneration = currentGeneration;
        }
    }
}