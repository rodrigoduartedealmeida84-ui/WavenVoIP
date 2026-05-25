using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace WavenVoIP.Services
{
    public enum LogLevel { INFO, WARN, ERROR }

    internal sealed class LogEntry
    {
        public DateTime Timestamp { get; init; }
        public LogLevel Level     { get; init; }
        public string   Channel   { get; init; } = "";
        public string   Caller    { get; init; } = "";
        public string   Message   { get; init; } = "";
    }

    /// <summary>
    /// Thread-safe, rotating file logger with per-channel output.
    /// Channels: UI (default), SIP, AMI, CDR, UPDATE, GOOGLE, WHATSAPP.
    /// </summary>
    internal static class LogHelper
    {
        private static readonly string _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP", "Logs");

        private const long MaxBytes = 3 * 1024 * 1024; // 3 MB per file before rotation

        private static readonly object _lock = new object();

        internal static bool IsEnabled         { get; private set; } = true;
        internal static bool IsDetailedEnabled { get; private set; } = false;

        internal static void ConfigurarDeSettings(SipConfig cfg)
        {
            IsEnabled         = cfg.LogEnabled;
            IsDetailedEnabled = cfg.LogDetailedEnabled;
        }

        internal static string LogDir => _logDir;

        /// Fired on every log call — subscribers must be fast and non-throwing.
        internal static event Action<LogEntry>? LogWritten;

        // ── Public API ──────────────────────────────────────────────────────────

        internal static void Write(string msg,
            LogLevel level = LogLevel.INFO,
            [CallerMemberName] string caller = "")
            => Append("ui_flow", level, caller, msg);

        internal static void Info(string msg, [CallerMemberName] string caller = "")
            => Append("ui_flow", LogLevel.INFO, caller, msg);

        internal static void Warn(string msg, [CallerMemberName] string caller = "")
            => Append("ui_flow", LogLevel.WARN, caller, msg);

        internal static void Error(string msg, Exception? ex = null, [CallerMemberName] string caller = "")
            => Append("ui_flow", LogLevel.ERROR, caller, ex == null ? msg : $"{msg} | {ex.GetType().Name}: {ex.Message}");

        internal static void Sip(string msg, LogLevel level = LogLevel.INFO, [CallerMemberName] string caller = "")
            => Append("sip_signal", level, caller, msg);

        internal static void Ami(string msg, LogLevel level = LogLevel.INFO, [CallerMemberName] string caller = "")
            => Append("ami_sync", level, caller, msg);

        internal static void Cdr(string msg, LogLevel level = LogLevel.INFO, [CallerMemberName] string caller = "")
            => Append("cdr_sync", level, caller, msg);

        internal static void Update(string msg, LogLevel level = LogLevel.INFO, [CallerMemberName] string caller = "")
            => Append("update", level, caller, msg);

        internal static void Google(string msg, LogLevel level = LogLevel.INFO, [CallerMemberName] string caller = "")
            => Append("google", level, caller, msg);

        internal static void WhatsApp(string msg, LogLevel level = LogLevel.INFO, [CallerMemberName] string caller = "")
            => Append("whatsapp", level, caller, msg);

        // ── Internal ─────────────────────────────────────────────────────────────

        private static void Append(string channel, LogLevel level, string caller, string msg)
        {
            // ERRORs always written regardless of enable flags
            if (!IsEnabled && level != LogLevel.ERROR) return;

            var ts = DateTime.Now;
            try
            {
                var line = $"{ts:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] [{caller}] {msg}{Environment.NewLine}";
                lock (_lock)
                {
                    Directory.CreateDirectory(_logDir);
                    var path = Path.Combine(_logDir, $"{channel}.log");
                    RotateIfNeeded(path);
                    File.AppendAllText(path, line);
                }
            }
            catch { /* logging must never crash the app */ }

            // Fire outside lock so a slow/throwing subscriber never stalls other threads
            try
            {
                LogWritten?.Invoke(new LogEntry
                {
                    Timestamp = ts,
                    Level     = level,
                    Channel   = channel,
                    Caller    = caller,
                    Message   = msg.TrimEnd()
                });
            }
            catch { }
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length <= MaxBytes) return;

                var old = path.Replace(".log", ".old.log");
                try
                {
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
                catch
                {
                    // If we can't rotate (AV lock, etc.), truncate to avoid disk fill
                    try { File.WriteAllText(path, string.Empty); } catch { }
                }
            }
            catch { }
        }
    }
}
