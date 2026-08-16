using System;
using System.IO;
using System.Text;

namespace EditableEncyclopedia
{
    // Shared categorized logger for the EE-* module family.
    // Each peer module gets its own log file under:
    //   Documents\Mount and Blade II Bannerlord\Configs\ModSettings\Global\EE-Core\Logs\<moduleId>.log
    //
    // Usage:
    //     private static readonly IEELogger Log = EECoreLogger.For("EE-WebAPI");
    //     Log.Info("server started on :8080");
    //     Log.Error("failed to bind port", ex);
    //
    // Existing EditableEncyclopedia debug.log is unaffected — peers opt in to this logger.
    public static class EECoreLogger
    {
        private static readonly object _writeLock = new object();
        private static string _logRoot;

        public static bool IsEnabled { get; set; } = true;

        public static IEELogger For(string moduleId)
        {
            return new EELoggerImpl(moduleId ?? "unknown");
        }

        internal static string GetLogPath(string moduleId)
        {
            if (_logRoot == null)
            {
                try
                {
                    string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    _logRoot = Path.Combine(docs,
                        "Mount and Blade II Bannerlord",
                        "Configs", "ModSettings", "Global", "EE-Core", "Logs");
                    Directory.CreateDirectory(_logRoot);
                }
                catch
                {
                    _logRoot = Path.GetTempPath();
                }
            }
            string safe = moduleId;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(_logRoot, safe + ".log");
        }

        internal static void Write(string moduleId, string level, string message, Exception ex)
        {
            if (!IsEnabled) return;
            try
            {
                var sb = new StringBuilder(256);
                sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
                sb.Append('[').Append(level).Append("] ");
                sb.Append(message ?? string.Empty);
                if (ex != null)
                {
                    sb.AppendLine();
                    sb.Append("  ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                    if (ex.StackTrace != null) sb.AppendLine().Append(ex.StackTrace);
                }
                sb.AppendLine();

                string path = GetLogPath(moduleId);
                lock (_writeLock)
                {
                    File.AppendAllText(path, sb.ToString());
                }
            }
            catch
            {
                // Logger must never throw into game code.
            }
        }
    }

    public interface IEELogger
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message, Exception ex = null);
    }

    internal sealed class EELoggerImpl : IEELogger
    {
        private readonly string _moduleId;
        public EELoggerImpl(string moduleId) { _moduleId = moduleId; }
        public void Debug(string message) => EECoreLogger.Write(_moduleId, "DEBUG", message, null);
        public void Info(string message) => EECoreLogger.Write(_moduleId, "INFO", message, null);
        public void Warn(string message) => EECoreLogger.Write(_moduleId, "WARN", message, null);
        public void Error(string message, Exception ex = null) => EECoreLogger.Write(_moduleId, "ERROR", message, ex);
    }
}
