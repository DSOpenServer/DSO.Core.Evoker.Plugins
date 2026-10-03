using System;
using System.Collections.Generic;
using DSO.Core.Evoker.Plugins.Sandbox;

namespace DSO.Core.Evoker.Plugins
{
    /// <summary>
    /// Bir plugin event'i tetiklendiğinde handler'a gelen argümanlar (event delegate'inin parametreleri,
    /// sırasıyla). Sandbox ve in-process'te AYNI kullanım:
    ///
    ///   b.Subscribe("CounterChanged", e => Console.WriteLine(e.Get&lt;int&gt;(1)));   // EventHandler&lt;int&gt;: [0]=sender, [1]=değer
    ///   b.Subscribe("PointMoved",    e => Show(e.Get&lt;PointDto&gt;(1)));            // plugin'in Point'i -> host DTO'su
    ///
    /// Get&lt;T&gt;, Invoke&lt;T&gt; ile aynı dönüşüm kurallarını kullanır (sayısal, enum, JSON şekil eşlemesi) - ham
    /// değer (this[i]) ise sandbox'ta JsonElement/primitive, in-process'te plugin'in gerçek nesnesi olabilir;
    /// iki modda aynı davranan kod için Get&lt;T&gt; kullanın.
    ///
    /// Plugin'in KENDİSİ olan argüman (tipik "sender") iki modda da null gelir - sandbox'ta process sınırını
    /// geçemez, in-process'te de aynı davranış için null'lanır.
    /// </summary>
    public sealed class PluginEventArgs
    {
        public string EventName { get; }
        public IReadOnlyList<object?> Args { get; }

        public PluginEventArgs(string eventName, IReadOnlyList<object?> args)
        {
            EventName = eventName;
            Args = args ?? Array.Empty<object?>();
        }

        public int Count => Args.Count;
        public object? this[int index] => Args[index];
        public T? Get<T>(int index) => (T?)WireValueCodec.ConvertTo(Args[index], typeof(T));
    }
}