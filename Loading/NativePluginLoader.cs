using System;
using System.Threading.Tasks;
using DSO.Core.Evoker.Plugins.Scanning;

namespace DSO.Core.Evoker.Plugins.Loading
{
    /// <summary>
    /// PLACEHOLDER - şimdilik implemente edilmiyor.
    ///
    /// Native (C/C++ extern "C" export, COM) veya Python gibi CLR-dışı runtime'lar için.
    /// Bu problem sınıfı yönetilen (.NET) yüklemeden temelden farklı: native ikili dosyalarda
    /// CLR metadata yok, imzalar dışarıdan bildirilmek zorunda (reflection ile keşfedilemez).
    ///
    /// Muhtemel yön: ayrı bir proje (örn. DSO.Core.Evoker.Native), bu projeye (DSO.Core.Evoker.Plugins)
    /// bağımlı olacak ve IPluginLoader arayüzünün burada bir implementasyonunu sağlayacak.
    /// Kullanıcı bu kısmın tasarımını ayrıca anlatacak - şimdilik sadece yer tutucu.
    /// </summary>
    public sealed class NativePluginLoader : IPluginLoader
    {
        public PluginKind SupportedKind => PluginKind.Native;

        public object? Instance => null;

        public Task LoadInProcessAsync(string filePath, string typeFullName, bool includeNonPublic = false)
            => throw new NotSupportedException(
                "Native/başka-runtime plugin yükleme henüz implemente edilmedi. " +
                "Bkz. NativePluginLoader.cs üstündeki not.");

        public Task<object?> InvokeAsync(string methodName, object?[] args)
            => throw new NotSupportedException(
                "Native/başka-runtime plugin çağrısı henüz implemente edilmedi. " +
                "Bkz. NativePluginLoader.cs üstündeki not.");
    }
}