using System;
using System.Text.Json;
using System.Threading.Tasks;
using DSO.Core.Evoker.Plugins.Sandbox;

namespace DSO.Core.Evoker.Plugins.Scanning
{
    /// <summary>
    /// PluginDescriptor'daki field/property'lere çalışan instance'ın O ANKİ değerlerini yazar - IPluginBuilder
    /// üzerinden, yani sandbox ve in-process'te AYNI kod. Her üye ayrı okunur; okunamayan üye (getter exception
    /// fırlattı, private ama IncludeNonPublic kapalı, static...) sadece kendi ValueError'ını alır, gerisi devam eder.
    /// </summary>
    public static class PluginValueSnapshot
    {
        /// <summary>Bir değerin JSON'u bundan uzunsa kırpılır (ValueTruncated=true) - ör. milyon elemanlı bir liste.</summary>
        public const int DefaultMaxValueJsonLength = 4096;

        public static async Task CaptureAsync(PluginDescriptor descriptor, IPluginBuilder builder, int maxValueJsonLength = DefaultMaxValueJsonLength)
        {
            int failed = 0;
            foreach (var p in descriptor.Type.Properties ?? new())
            {
                if (p.IsStatic == true) { p.ValueError = "static üye - değeri okunmuyor (instance değil)."; continue; }
                if (p.IndexerParameters != null) continue; // indexer: parametresiz bir "değeri" yok
                if (p.Getter == null) { p.ValueError = "get erişimi yok (sadece set)."; continue; }
                if (p.DeclaredIn != null) { p.ValueError = $"temel sınıfta ({p.DeclaredIn}) tanımlı - değer okuma sadece tipin kendi üyeleri için."; continue; }
                if (p.Getter != MemberVisibility.Public && !builder.IncludeNonPublic) { p.ValueError = "public olmayan get - plugin IncludeNonPublic=false ile yüklü."; continue; }
                if (!await TryReadAsync(builder, p.Name, maxValueJsonLength, (v, t, e) => { p.Value = v; p.ValueTruncated = t; p.ValueError = e; }).ConfigureAwait(false)) failed++;
            }
            foreach (var f in descriptor.Type.Fields ?? new())
            {
                if (f.IsConst == true) continue; // değeri zaten ConstValue'da
                if (f.IsStatic == true) { f.ValueError = "static üye - değeri okunmuyor (instance değil)."; continue; }
                if (f.DeclaredIn != null) { f.ValueError = $"temel sınıfta ({f.DeclaredIn}) tanımlı - değer okuma sadece tipin kendi üyeleri için."; continue; }
                if (f.Visibility != MemberVisibility.Public && !builder.IncludeNonPublic) { f.ValueError = "public olmayan field - plugin IncludeNonPublic=false ile yüklü."; continue; }
                if (!await TryReadAsync(builder, f.Name, maxValueJsonLength, (v, t, e) => { f.Value = v; f.ValueTruncated = t; f.ValueError = e; }).ConfigureAwait(false)) failed++;
            }

            descriptor.Values = new PluginValuesInfo
            {
                CapturedUtc = DateTime.UtcNow,
                Source = builder.IsSandboxed ? "Sandbox" : "InProcess",
                Note = "Property değerleri okunurken plugin'in get kodu ÇALIŞTIRILDI." + (failed > 0 ? $" {failed} üye okunamadı (bkz. ValueError)." : "")
            };
        }

        private static async Task<bool> TryReadAsync(IPluginBuilder builder, string name, int maxLen, Action<JsonElement?, bool?, string?> set)
        {
            try
            {
                var raw = await builder.GetValueAsync<object>(name).ConfigureAwait(false);
                if (raw == null) { set(null, null, null); return true; }
                var element = raw is JsonElement je ? je : (JsonElement)WireValueCodec.ConvertTo(raw, typeof(JsonElement))!;
                var text = element.GetRawText();
                if (text.Length > maxLen)
                {
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(text[..maxLen] + "…"));
                    set(doc.RootElement.Clone(), true, null);
                }
                else set(element.Clone(), null, null);
                return true;
            }
            catch (Exception ex)
            {
                var actual = ex is PluginInvocationException pie && pie.InnerException != null ? pie.InnerException : ex;
                set(null, null, $"{actual.GetType().Name}: {actual.Message}");
                return false;
            }
        }
    }
}