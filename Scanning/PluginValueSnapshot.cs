using System;
using System.Threading.Tasks;
using DSO.Core.Evoker.Description;

namespace DSO.Core.Evoker.Plugins.Scanning
{
    /// <summary>
    /// PluginDescriptor'daki field/property'lere çalışan instance'ın O ANKİ değerlerini yazar - IPluginBuilder üzerinden,
    /// yani sandbox ve in-process'te AYNI kod. Asıl iş çekirdekte (EvokerDescriber.CaptureValuesAsync) - plugin olmayan
    /// hedefler de aynı kuralları kullanır.
    /// </summary>
    public static class PluginValueSnapshot
    {
        /// <summary>Bir değerin JSON'u bundan uzunsa kırpılır (ValueTruncated=true) - ör. milyon elemanlı bir liste.</summary>
        public const int DefaultMaxValueJsonLength = 4096;

        public static async Task CaptureAsync(PluginDescriptor descriptor, IPluginBuilder builder, int maxValueJsonLength = DefaultMaxValueJsonLength)
        {
            await EvokerDescriber.CaptureValuesAsync(descriptor.Type,
                async (name, _) => await builder.GetValueAsync<object>(name).ConfigureAwait(false),
                builder.IncludeNonPublic, builder.IsSandboxed ? "Sandbox" : "InProcess", maxValueJsonLength).ConfigureAwait(false);
            descriptor.Values = descriptor.Type.Values;
        }
    }
}