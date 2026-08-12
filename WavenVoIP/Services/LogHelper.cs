using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

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

        // 3 MB era pequeno demais para diagnosticar chamadas em fila: um único ciclo de
        // sincronização de CDR (a cada poucos segundos) já gera milhares de linhas de diagnóstico
        // por grupo/linkedid, girando o arquivo em questão de segundos e apagando evidência de
        // testes reais antes de dar tempo de analisar. 50 MB dá margem para uma sessão de teste
        // completa (vários minutos) sobreviver até a leitura.
        private const long MaxBytes = 50 * 1024 * 1024; // 50 MB per file before rotation

        // Escrita em disco roda numa thread dedicada — chamadas a Append() (feitas direto
        // na UI thread em vários pontos: handlers de clique, StatusChanged, etc.) so
        // enfileiram a linha e retornam na hora, sem bloquear em I/O de disco.
        private static readonly BlockingCollection<(string Channel, string Line)> _queue = new();
        private static readonly Thread _writerThread;

        static LogHelper()
        {
            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "LogHelperWriter" };
            _writerThread.Start();
        }

        private static void WriterLoop()
        {
            foreach (var (channel, line) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    Directory.CreateDirectory(_logDir);
                    var path = Path.Combine(_logDir, $"{channel}.log");
                    RotateIfNeeded(path);
                    File.AppendAllText(path, line);
                }
                catch { /* logging must never crash the app */ }
            }
        }

        internal static bool IsEnabled         { get; private set; } = true;
        internal static bool IsDetailedEnabled { get; private set; } = false;

        internal static void ConfigurarDeSettings(SipConfig cfg)
        {
            IsEnabled         = cfg.LogEnabled;
            IsDetailedEnabled = cfg.LogDetailedEnabled;
        }

        internal static string LogDir => _logDir;

        // v2.4.0 — diagnóstico de UI freeze: última linha de log gravada (canal:caller), usado
        // apenas como aproximação barata de "última operação" quando o watchdog de UI detecta
        // atraso — todo LogHelper.Info/Sip/Ami/Cdr/etc. já passa [CallerMemberName], então isso
        // não exige nenhum novo ponto de instrumentação espalhado pelo app.
        internal static string UltimaOperacao { get; private set; } = "(nenhuma)";

        /// Espera a fila de escrita esvaziar (usado no encerramento do app, para nao perder as ultimas linhas).
        internal static void Flush(TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (_queue.Count > 0 && sw.Elapsed < timeout)
                Thread.Sleep(10);
        }

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

        // Canal dedicado para o log de deduplicação de contatos (CONTACT_DUPLICATE_*/
        // CONTACT_MATCH_*): esse processamento roda sobre milhares de contatos e podia gerar,
        // sozinho, dezenas de milhares de linhas por sincronização — inundando "ui_flow" e
        // apagando evidência de diagnóstico de chamadas/fila antes de dar tempo de analisar.
        internal static void Contacts(string msg, LogLevel level = LogLevel.INFO, [CallerMemberName] string caller = "")
            => Append("contacts_sync", level, caller, msg);

        internal static void WhatsApp(string msg, LogLevel level = LogLevel.INFO, [CallerMemberName] string caller = "")
            => Append("whatsapp", level, caller, msg);

        // ── Internal ─────────────────────────────────────────────────────────────

        private static void Append(string channel, LogLevel level, string caller, string msg)
        {
            // ERRORs always written regardless of enable flags
            if (!IsEnabled && level != LogLevel.ERROR) return;

            var ts = DateTime.Now;
            UltimaOperacao = $"{channel}:{caller}";
            try
            {
                var line = $"{ts:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] [{caller}] {msg}{Environment.NewLine}";
                _queue.Add((channel, line));
            }
            catch { /* logging must never crash the app */ }

            // Subscriber roda no thread chamador — deve ser rapido e nao lancar
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
