using System;

namespace DSO.Core.Evoker.Plugins.Sandbox
{
    public sealed class PluginWorkerCrashedEventArgs : EventArgs
    {
        public string PluginFilePath { get; }
        public string TypeFullName { get; }
        /// <summary>Sebep: pipe kopması (process öldü), heartbeat zaman aşımı (kilitlendi) ya da restart hatası.</summary>
        public Exception Reason { get; }
        /// <summary>Çöken worker'ın nesli (1 = ilk başlatma).</summary>
        public int Generation { get; }
        /// <summary>AutoRestartOnCrash nedeniyle yeniden başlatma denenecek mi.</summary>
        public bool WillRestart { get; }
        public DateTime UtcTime { get; } = DateTime.UtcNow;

        public PluginWorkerCrashedEventArgs(string pluginFilePath, string typeFullName, Exception reason, int generation, bool willRestart)
        {
            PluginFilePath = pluginFilePath;
            TypeFullName = typeFullName;
            Reason = reason;
            Generation = generation;
            WillRestart = willRestart;
        }
    }

    public sealed class PluginWorkerRestartedEventArgs : EventArgs
    {
        public string PluginFilePath { get; }
        public string TypeFullName { get; }
        /// <summary>Yeni worker'ın nesli.</summary>
        public int Generation { get; }
        public int? ProcessId { get; }

        public PluginWorkerRestartedEventArgs(string pluginFilePath, string typeFullName, int generation, int? processId)
        {
            PluginFilePath = pluginFilePath;
            TypeFullName = typeFullName;
            Generation = generation;
            ProcessId = processId;
        }
    }
}