using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        //
        // v2.4.3 — incidente real de produção (ramal 103, ver DiagnosticFinding #1 na Waven
        // API): esta fila SEM boundedCapacity acumulou ~8,7 milhões de itens e ~4,2 GB de
        // Managed Heap (matemática bate: ~500 bytes/item, condizente com a tupla+string de
        // cada linha formatada) quando o produtor (MesclarCdr, ver HistoricoStorageService)
        // passou a gerar linhas muito mais rápido do que esta única thread conseguia escrever
        // (File.AppendAllText linha-a-linha é lento sob volume alto). Duas camadas de defesa
        // agora, nenhuma delas dependendo de identificar corretamente TODO produtor futuro:
        //   1) capacidade máxima — nunca mais cresce sem limite, não importa o que a produza;
        //   2) TryAdd não-bloqueante — nunca trava a thread chamadora (UI/SIP/CDR/etc.) mesmo
        //      com a fila cheia; ERROR/WARN ganham uma folga curta pra tentar entrar, INFO
        //      desiste na hora. Descartes são contados, nunca geram log por item (só um
        //      resumo periódico, ver RegistrarDescarteSeNecessario).
        private const int MaxQueueSize = 20_000; // ~10 MB no pior caso (linha média ~500B) — nunca mais GBs
        private static readonly BlockingCollection<(string Channel, string Line)> _queue =
            new(new ConcurrentQueue<(string, string)>(), MaxQueueSize);
        private static readonly Thread _writerThread;

        private static long _droppedCount;
        // Total de linhas descartadas por fila cheia desde o início do processo — exposto pra
        // telemetria/diagnóstico futuro, mesma filosofia de QueuedCount abaixo.
        internal static long DroppedCount => Interlocked.Read(ref _droppedCount);

        static LogHelper()
        {
            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "LogHelperWriter" };
            _writerThread.Start();
        }

        // Drena em lote (até 500 por vez) e agrupa por canal antes de escrever — evita abrir/
        // escrever/fechar o arquivo linha a linha sob volume alto (gargalo real que ajudou a
        // fila a crescer no incidente). Comportamento idêntico ao anterior no caso comum
        // (poucas linhas por vez): cada lote de 1 item vira exatamente 1 File.AppendAllText,
        // mesmo conteúdo, mesma rotação.
        private static void WriterLoop()
        {
            var buffer = new List<(string Channel, string Line)>(64);
            foreach (var primeiro in _queue.GetConsumingEnumerable())
            {
                buffer.Add(primeiro);
                while (buffer.Count < 500 && _queue.TryTake(out var proximo))
                    buffer.Add(proximo);

                foreach (var grupo in buffer.GroupBy(x => x.Channel, x => x.Line))
                {
                    try
                    {
                        Directory.CreateDirectory(_logDir);
                        var path = Path.Combine(_logDir, $"{grupo.Key}.log");
                        RotateIfNeeded(path);
                        File.AppendAllText(path, string.Concat(grupo));
                    }
                    catch { /* logging must never crash the app */ }
                }
                buffer.Clear();
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

        // Usado pela telemetria de diagnóstico (DiagnosticTelemetryService) para monitorar
        // se o produtor está ultrapassando o consumidor (ver comentário sobre _queue acima
        // não ter boundedCapacity) — leitura O(1), sem custo extra.
        internal static int QueuedCount => _queue.Count;

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

                // Nunca bloqueia a thread chamadora. WARN/ERROR ganham uma folga curtíssima
                // (ainda não-bloqueante de verdade pra UI — só reduz a chance de perder um
                // erro num pico passageiro); INFO desiste na hora se a fila estiver cheia.
                var entrou = level == LogLevel.INFO
                    ? _queue.TryAdd((channel, line))
                    : _queue.TryAdd((channel, line), TimeSpan.FromMilliseconds(20));

                if (!entrou)
                    RegistrarDescarteSeNecessario();
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

        // Conta o descarte e, a cada 1000, tenta registrar UM resumo (nunca um log por item
        // descartado — geraria o mesmo tipo de flood que estamos protegendo contra). O log do
        // resumo passa pelo mesmo Append() normal; na pior hipótese (fila permanentemente
        // cheia) a recursão pára sozinha em 1 nível, porque o contador já foi incrementado
        // antes da checagem de módulo.
        private static void RegistrarDescarteSeNecessario()
        {
            var total = Interlocked.Increment(ref _droppedCount);
            if (total % 1000 == 0)
                Append("ui_flow", LogLevel.WARN, nameof(RegistrarDescarteSeNecessario),
                    $"LOG_QUEUE_OVERFLOW dropped_total={total} queue_size={_queue.Count}");
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
