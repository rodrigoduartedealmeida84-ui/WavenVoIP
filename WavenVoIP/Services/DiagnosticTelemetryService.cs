using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    /// <summary>
    /// Telemetria de diagnóstico remoto — v2.4.2, produção (FASE 1: WavenVoIP → Waven API
    /// apenas, sem GitHub). Objetivo: descobrir se o consumo de memória cresce
    /// proporcionalmente ao número de chamadas, acompanhando a frota inteira sem
    /// precisar de AnyDesk em cada máquina.
    ///
    /// Requisitos de design (não violar sem atualizar o relatório de arquitetura):
    /// - Nunca bloquear a UI thread nem SIP/áudio — toda comunicação roda fora da UI thread,
    ///   com timeout curto, e falha nunca propaga.
    /// - Nunca reler historico.json inteiro; usa contadores/tamanho já expostos por
    ///   HistoricoStorageService.
    /// - Nunca envia segredo (senha SIP, token, credenciais Google) — o payload é
    ///   construído campo a campo, nunca serializando SipConfig/objetos de negócio.
    /// - Fila offline local com tamanho máximo — nunca cresce indefinidamente; ao
    ///   reconectar, drena gradualmente (respeitando o rate limit), nunca em rajada.
    /// - Rate limit local por tipo de evento + limite global — nenhum bug de log pode
    ///   virar flood de requests.
    /// - Só observa nesta versão: não altera regra nenhuma de SIP/CDR/histórico/áudio.
    /// </summary>
    internal sealed class DiagnosticTelemetryService : IDisposable
    {
        // ── Identidade da instalação (persistida, gerada uma única vez) ─────────

        private static readonly string IdentityFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP", "diagnostics-identity.json");

        internal static string InstallationId { get; }
        internal static string MachineId      { get; }

        private sealed class IdentityFileModel
        {
            public string InstallationId { get; set; } = "";
            public DateTime CriadoEmUtc  { get; set; }
        }

        static DiagnosticTelemetryService()
        {
            InstallationId = CarregarOuCriarInstallationId();
            MachineId      = ComputarMachineId();
        }

        private static string CarregarOuCriarInstallationId()
        {
            try
            {
                if (File.Exists(IdentityFile))
                {
                    var existente = JsonSerializer.Deserialize<IdentityFileModel>(File.ReadAllText(IdentityFile));
                    if (!string.IsNullOrWhiteSpace(existente?.InstallationId))
                        return existente.InstallationId;
                }
            }
            catch { /* recria abaixo */ }

            var novo = new IdentityFileModel { InstallationId = Guid.NewGuid().ToString("N"), CriadoEmUtc = DateTime.UtcNow };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(IdentityFile)!);
                File.WriteAllText(IdentityFile, JsonSerializer.Serialize(novo));
            }
            catch { /* segue mesmo sem persistir — próximo start gera outro (raro) */ }
            return novo.InstallationId;
        }

        // Identificador estável e não-reversível do computador — nunca o MachineName cru
        // (nada de username/hostname completo saindo da máquina).
        private static string ComputarMachineId()
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(Environment.MachineName + "|WavenVoIP");
                var hash  = SHA256.HashData(bytes);
                return Convert.ToHexString(hash)[..10].ToLowerInvariant();
            }
            catch { return "unknown"; }
        }

        // ── Config / HTTP ────────────────────────────────────────────────────

        // Timeout curto — telemetria nunca pode travar esperando rede (item 7 do pedido).
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        private static (string url, string token, bool ativo) ObterConfig()
        {
            var cfg = SipConfig.CarregarSalva();
            return (cfg?.WavenApiUrl?.TrimEnd('/') ?? string.Empty,
                    cfg?.WavenApiToken ?? string.Empty,
                    cfg?.DiagnosticoRemotoAtivado ?? false);
        }

        // ── Estado da instância ──────────────────────────────────────────────

        private readonly string _ramal;
        private readonly Timer  _timer;
        private readonly CancellationTokenSource _cts = new();
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private int _tickEmAndamento;

        // Amostragem de CPU (delta de TotalProcessorTime entre heartbeats)
        private TimeSpan _cpuAnterior     = Process.GetCurrentProcess().TotalProcessorTime;
        private DateTime _cpuAmostradoEm  = DateTime.UtcNow;
        private readonly Queue<double> _cpuJanela = new(); // últimas ~10 amostras p/ média/pico "recente"

        // Amostragem de I/O
        private (ulong readOps, ulong writeOps, ulong readBytes, ulong writeBytes) _ioAnterior = NativeProcessMetrics.ObterIoCounters();
        private DateTime _ioAmostradoEm = DateTime.UtcNow;

        // Contadores Waven (atualizados via hook em LogHelper.LogWritten — ver OnLogWritten).
        // Todos são só-leitura de eventos JÁ existentes e comprovados — nenhuma mudança de
        // máquina de estados SIP/CDR/Google/AMI.
        private long _callsIncoming, _callsOutgoing, _callsConnected, _callsEnded;
        private long _audioDisposeOk, _audioDisposeFail, _audioDeviceRecovery;
        private long _uiFreezeCount;
        private double _ultimaFreezeDuracaoMs;
        private long _unobservedExceptionCount, _criticalExceptionCount;

        // Última operação conhecida — best effort, deduzida dos mesmos marcadores de log.
        private volatile string _lastOperation = "IDLE";

        // Janela deslizante de erros p/ EXCEPTION_BURST (>=5 erros em 2 min)
        private readonly object _erroWindowLock = new();
        private readonly Queue<DateTime> _errosRecentes = new();
        private const int    BurstMinOcorrencias = 5;
        private static readonly TimeSpan BurstJanela = TimeSpan.FromMinutes(2);

        // ── Limiares (produção v2.4.2 — ver relatório de arquitetura, seção "alertas
        // automáticos": candidatos naturais a vir do backend numa v2) ──────────
        private const double MemoryWarningMb   = 500;
        private const double MemoryCriticalMb  = 1000;
        private const double MemoryEmergencyMb = 1500;
        private const double UiFreezeIncidentMs = 3000; // watchdog local já loga em >=1500ms; só reporta remoto >=3s
        private const int    LogQueueWarning    = 1000; // ver escala 100/500/1000/5000 pedida — alerta a partir de 1000

        // Cooldown por tipo de evento — evita reenviar o mesmo incidente a cada heartbeat
        // enquanto a condição persiste (ex.: memória crítica sustentada por 30 minutos).
        private readonly Dictionary<string, DateTime> _ultimoEnvioIncidente = new();
        private readonly object _cooldownLock = new();
        private static readonly Dictionary<string, TimeSpan> _cooldownPorEvento = new()
        {
            ["MEMORY_WARNING"]   = TimeSpan.FromMinutes(15),
            ["MEMORY_CRITICAL"]  = TimeSpan.FromMinutes(5),
            ["MEMORY_EMERGENCY"] = TimeSpan.FromMinutes(2),
            ["UI_FREEZE_DETECTED"] = TimeSpan.FromSeconds(30),
            ["AUDIO_NATIVE_INPUT_DISPOSE_FAIL"] = TimeSpan.FromSeconds(10),
            ["HANDLE_WARNING"]   = TimeSpan.FromMinutes(15),
            ["LOG_QUEUE_WARNING"] = TimeSpan.FromMinutes(10),
            ["EXCEPTION_BURST"]  = TimeSpan.FromMinutes(10),
            ["UNOBSERVED_EXCEPTION"] = TimeSpan.FromMinutes(5),
        };

        // Rate limit GLOBAL — rede de segurança final contra qualquer bug de log que
        // gere flood: nunca mais que 10 envios (heartbeat+evento somados) por minuto.
        private readonly object _globalRateLock = new();
        private readonly Queue<DateTime> _enviosRecentes = new();
        private const int MaxEnviosPorMinuto = 10;

        // ── Fila offline (bounded) ───────────────────────────────────────────

        private static readonly string QueueFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP", "diagnostics-queue.json");

        private const int MaxOfflineQueueItems = 100; // item 8 do pedido — nunca cresce sem limite

        // ── Ciclo de vida ─────────────────────────────────────────────────────

        internal DiagnosticTelemetryService(string ramal)
        {
            _ramal = ramal ?? "";
            LogHelper.LogWritten += OnLogWritten;
            // primeiro heartbeat após 15s (deixa app estabilizar); depois a cada 60s.
            _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60));
        }

        public void Dispose()
        {
            LogHelper.LogWritten -= OnLogWritten;
            _cts.Cancel();
            _timer.Dispose();
            _cts.Dispose();
        }

        // ── Hook de eventos: reaproveita o log estruturado que já existe, sem nenhuma
        // mudança na máquina de estados SIP/CDR/Google/AMI. Deve ser rápido e nunca lançar. ──

        private void OnLogWritten(LogEntry e)
        {
            try
            {
                switch (e.Message)
                {
                    case var m when m.StartsWith("CALL_INCOMING_NEW", StringComparison.Ordinal):
                        Interlocked.Increment(ref _callsIncoming);
                        _lastOperation = "DIALING"; // ringing local; reaproveita o mesmo rótulo de "chamada em andamento, ainda não conectada"
                        break;

                    case var m when m.StartsWith("CALL_START_CLICK", StringComparison.Ordinal):
                        _lastOperation = "DIALING";
                        break;

                    case var m when m.StartsWith("CALL_INVITE_SENT", StringComparison.Ordinal):
                        Interlocked.Increment(ref _callsOutgoing);
                        break;

                    case var m when m.StartsWith("CALL_CONNECTED", StringComparison.Ordinal):
                        Interlocked.Increment(ref _callsConnected);
                        _lastOperation = "CALL_CONNECTED";
                        break;

                    case var m when m.StartsWith("CALL_ENDED_REASON", StringComparison.Ordinal):
                        Interlocked.Increment(ref _callsEnded);
                        _lastOperation = "CALL_ENDING";
                        break;

                    case var m when m.StartsWith("AUDIO_NATIVE_INPUT_DISPOSE_OK", StringComparison.Ordinal):
                        Interlocked.Increment(ref _audioDisposeOk);
                        break;

                    case var m when m.StartsWith("AUDIO_NATIVE_INPUT_DISPOSE_FAIL", StringComparison.Ordinal):
                        Interlocked.Increment(ref _audioDisposeFail);
                        _ = EnviarIncidenteAsync("AUDIO_NATIVE_INPUT_DISPOSE_FAIL", detalhe: "dispose_fail");
                        break;

                    case var m when m.StartsWith("AUDIO_DEVICE_RECOVERY", StringComparison.Ordinal):
                        Interlocked.Increment(ref _audioDeviceRecovery);
                        break;

                    case var m when m.StartsWith("UI_FREEZE_DETECTED", StringComparison.Ordinal):
                        Interlocked.Increment(ref _uiFreezeCount);
                        var dur = ExtrairDuracaoMs(m);
                        _ultimaFreezeDuracaoMs = dur;
                        if (dur >= UiFreezeIncidentMs)
                            _ = EnviarIncidenteAsync("UI_FREEZE_DETECTED", duracaoMs: dur, detalhe: $"ultima_operacao={LogHelper.UltimaOperacao}");
                        break;

                    case var m when m.StartsWith("CDR_SYNC_START", StringComparison.Ordinal):
                        _lastOperation = "CDR_SYNC";
                        break;

                    case var m when m.Contains("GOOGLE_SYNC_START", StringComparison.Ordinal):
                        _lastOperation = "GOOGLE_SYNC";
                        break;

                    case var m when m.StartsWith("API_AMI_PEERS_REQUEST", StringComparison.Ordinal) ||
                                     m.StartsWith("API_AMI_QUEUES_REQUEST", StringComparison.Ordinal):
                        _lastOperation = "AMI_SYNC";
                        break;

                    case var m when m.StartsWith("API_CONTACT_SYNC_START", StringComparison.Ordinal):
                        _lastOperation = "CONTACT_SYNC";
                        break;
                }

                if (e.Message.StartsWith("TASK_UNOBSERVED_EXCEPTION", StringComparison.Ordinal))
                {
                    // v2.4.0 (App.xaml.cs) → TaskScheduler.UnobservedTaskException. Só dispara
                    // quando o GC coleta a task faltosa — pode ser bem depois da falha real (ver
                    // item 3 do pedido de instrumentação v2.4.4). É a rede de segurança pra
                    // qualquer fire-and-forget que ainda não passou pelo helper FireAndForget.
                    Interlocked.Increment(ref _unobservedExceptionCount);
                    _ = EnviarIncidenteDetalhadoAsync("UNOBSERVED_EXCEPTION", "task_unobserved_exception",
                        origem: "(desconhecida — capturada via TaskScheduler, sem call-site)",
                        captureSource: "UNOBSERVED_TASK", ex: e.Exception);
                }
                else if (e.Message.StartsWith("FIRE_AND_FORGET_FAULTED", StringComparison.Ordinal))
                {
                    // v2.4.4 — FireAndForgetExtensions.FireAndForget() observou a falha no momento
                    // real em que ela ocorreu (não no GC). Extrai a origem embutida na mensagem
                    // pelo próprio helper (ver DiagnosticTelemetryService.ExtrairOrigem).
                    Interlocked.Increment(ref _unobservedExceptionCount);
                    _ = EnviarIncidenteDetalhadoAsync("UNOBSERVED_EXCEPTION", "fire_and_forget_faulted",
                        origem: ExtrairOrigem(e.Message), captureSource: "FIRE_AND_FORGET", ex: e.Exception);
                }

                if (e.Level == LogLevel.ERROR)
                {
                    Interlocked.Increment(ref _criticalExceptionCount);
                    RegistrarErroERoEstourarBurstSeNecessario();
                }
            }
            catch { /* handler de log nunca pode lançar */ }
        }

        private static double ExtrairDuracaoMs(string msg)
        {
            var m = Regex.Match(msg, @"duracao_ms=(\d+(\.\d+)?)");
            return m.Success && double.TryParse(m.Groups[1].Value, out var v) ? v : 0;
        }

        // Extrai o "origem=..." embutido pelo FireAndForgetExtensions na mensagem de log
        // (ex.: "FIRE_AND_FORGET_FAULTED | origem=GoogleSyncTimer_Tick").
        private static string ExtrairOrigem(string msg)
        {
            var m = Regex.Match(msg, @"origem=(.+)$");
            return m.Success ? m.Groups[1].Value.Trim() : "(origem desconhecida)";
        }

        // ── Sanitização de exceção (item 2 do pedido v2.4.4) ────────────────────────
        // Nunca deixa passar: caminho absoluto do usuário (embute o username do Windows),
        // sequência de dígitos no formato de telefone BR (10-13 dígitos — cobre com/sem 9º
        // dígito e com/sem código do país). Mensagens de exceção .NET normalmente já não
        // carregam dado de negócio (nome/endereço/conteúdo de chamada) — isto é defesa em
        // profundidade, não uma suposição de que o código de negócio nunca vá construir uma
        // exceção com uma string interpolada contendo um número.
        private static readonly Regex RegexCaminhoUsuario  = new(@"[A-Za-z]:\\Users\\[^\\]+\\?", RegexOptions.Compiled);
        private static readonly Regex RegexPossivelTelefone = new(@"\b\d{10,13}\b", RegexOptions.Compiled);

        private static string SanitizarTexto(string? texto, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(texto)) return "";
            var s = RegexCaminhoUsuario.Replace(texto, @"C:\Users\<user>\");
            s = RegexPossivelTelefone.Replace(s, "<num>");
            return s.Length > maxChars ? s[..maxChars] : s;
        }

        // Mantém só as primeiras linhas (frames mais relevantes — normalmente onde a falha
        // realmente aconteceu) e troca o caminho absoluto de cada " in C:\...\Arquivo.cs:line N"
        // por só "Arquivo.cs:line N" — preserva Classe.Método (já vem antes do " in ") e a
        // linha, mas nunca o caminho de disco (que embutiria o nome de usuário do Windows).
        private static readonly Regex RegexStackLinhaArquivo = new(@"\s+in\s+.*[\\/]([^\\/]+\.cs:line \d+)", RegexOptions.Compiled);
        private const int MaxFramesStackTrace = 6;

        private static string SanitizarStackTrace(string? stack, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(stack)) return "";
            var linhas = stack
                .Split('\n')
                .Take(MaxFramesStackTrace)
                .Select(l => RegexStackLinhaArquivo.Replace(l.Trim(), " in $1"));
            var resultado = string.Join(" | ", linhas);
            return SanitizarTexto(resultado, maxChars);
        }

        private void RegistrarErroERoEstourarBurstSeNecessario()
        {
            lock (_erroWindowLock)
            {
                var agora = DateTime.UtcNow;
                _errosRecentes.Enqueue(agora);
                while (_errosRecentes.Count > 0 && agora - _errosRecentes.Peek() > BurstJanela)
                    _errosRecentes.Dequeue();

                if (_errosRecentes.Count >= BurstMinOcorrencias)
                    _ = EnviarIncidenteAsync("EXCEPTION_BURST", detalhe: $"count={_errosRecentes.Count} janela_min={BurstJanela.TotalMinutes:F0}");
            }
        }

        // ── Heartbeat ─────────────────────────────────────────────────────────

        private void OnTick(object? _)
        {
            if (_cts.IsCancellationRequested) return;
            if (Interlocked.CompareExchange(ref _tickEmAndamento, 1, 0) != 0) return;
            // v2.4.4 — catalogado como fire-and-forget NÃO PROTEGIDO (try/finally sem catch):
            // se HeartbeatAsync() deixar escapar algo não previsto, agora é observado e logado
            // no instante real da falha, em vez de só aparecer (sem contexto nenhum) quando o
            // GC coletar esta Task. Não muda o comportamento do heartbeat em si.
            Task.Run(async () =>
            {
                try { await HeartbeatAsync().ConfigureAwait(false); }
                finally { Interlocked.Exchange(ref _tickEmAndamento, 0); }
            }, _cts.Token).FireAndForget("DiagnosticTelemetryService.OnTick");
        }

        private async Task HeartbeatAsync()
        {
            var (url, token, ativo) = ObterConfig();
            if (!ativo || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token)) return;

            DiagnosticHeartbeat hb;
            try { hb = MontarHeartbeat(); }
            catch { return; } // coleta de métrica nunca pode derrubar a telemetria

            AvaliarAlertasDeHeartbeat(hb);

            if (!RespeitaRateLimitGlobal()) return;

            var ok = await EnviarAsync(url, token, "/api/diagnostics/heartbeat", hb).ConfigureAwait(false);
            if (!ok)
            {
                EnfileirarOffline("heartbeat", hb);
                return;
            }

            await DrenarFilaOfflineAsync(url, token).ConfigureAwait(false);
        }

        private DiagnosticHeartbeat MontarHeartbeat()
        {
            var proc = Process.GetCurrentProcess();
            var agora = DateTime.UtcNow;

            // CPU%
            var cpuAgora = proc.TotalProcessorTime;
            var deltaCpuMs = (cpuAgora - _cpuAnterior).TotalMilliseconds;
            var deltaWallMs = Math.Max(1, (agora - _cpuAmostradoEm).TotalMilliseconds);
            var cpuPercent = Math.Round(100.0 * deltaCpuMs / deltaWallMs / Environment.ProcessorCount, 1);
            _cpuAnterior = cpuAgora;
            _cpuAmostradoEm = agora;
            _cpuJanela.Enqueue(cpuPercent);
            while (_cpuJanela.Count > 10) _cpuJanela.Dequeue();

            // GC — nada aqui força uma coleta (GetGCMemoryInfo lê o resultado da última GC;
            // GetTotalMemory(false) idem; CollectionCount é só um contador incremental).
            var gcInfo = GC.GetGCMemoryInfo();
            var gcGenArr = gcInfo.GenerationInfo.ToArray();
            double GenMb(int idx) => gcGenArr.Length > idx
                ? Math.Round(gcGenArr[idx].SizeAfterBytes / 1024.0 / 1024.0, 1) : 0;

            // GDI/USER
            var (gdi, user) = NativeProcessMetrics.ObterGdiUserObjects();

            // I/O (taxa desde a última amostra)
            var ioAgora = NativeProcessMetrics.ObterIoCounters();
            var deltaIoSec = Math.Max(0.5, (agora - _ioAmostradoEm).TotalSeconds);
            var io = new
            {
                ReadBps  = Math.Round((ioAgora.readBytes  - _ioAnterior.readBytes)  / deltaIoSec, 1),
                WriteBps = Math.Round((ioAgora.writeBytes - _ioAnterior.writeBytes) / deltaIoSec, 1),
                ReadOps  = Math.Round((ioAgora.readOps  - _ioAnterior.readOps)  / deltaIoSec, 2),
                WriteOps = Math.Round((ioAgora.writeOps - _ioAnterior.writeOps) / deltaIoSec, 2),
            };
            _ioAnterior = ioAgora;
            _ioAmostradoEm = agora;

            var cfg = SipConfig.CarregarSalva();

            // Tamanho dos logs — apenas FileInfo (sem ler conteúdo)
            double logsTotalKb = 0;
            try
            {
                if (Directory.Exists(LogHelper.LogDir))
                    logsTotalKb = Math.Round(new DirectoryInfo(LogHelper.LogDir)
                        .EnumerateFiles("*.log").Sum(f => f.Length) / 1024.0, 1);
            }
            catch { }

            long historicoBytes = 0;
            try { historicoBytes = (long)Math.Round(HistoricoStorageService.TamanhoArquivoKb() * 1024.0); }
            catch { }

            return new DiagnosticHeartbeat
            {
                InstallationId = InstallationId,
                MachineId      = MachineId,
                MachineAlias   = "",
                Ramal          = _ramal,
                AppVersion     = VersionService.Versao,
                TimestampUtc   = agora,
                UptimeSeconds  = (long)_uptime.Elapsed.TotalSeconds,

                CallsSinceStartup = Interlocked.Read(ref _callsEnded),
                CallsIncoming     = Interlocked.Read(ref _callsIncoming),
                CallsOutgoing     = Interlocked.Read(ref _callsOutgoing),
                CallsConnected    = Interlocked.Read(ref _callsConnected),
                CallsEnded        = Interlocked.Read(ref _callsEnded),

                WorkingSetMb    = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 1),
                PrivateBytesMb  = Math.Round(proc.PrivateMemorySize64 / 1024.0 / 1024.0, 1),
                ManagedHeapMb   = Math.Round(gcInfo.HeapSizeBytes / 1024.0 / 1024.0, 1),
                GcTotalMemoryMb = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 1),
                Gen0Mb = GenMb(0), Gen1Mb = GenMb(1), Gen2Mb = GenMb(2), LohMb = GenMb(3), PohMb = GenMb(4),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),

                HandleCount = proc.HandleCount,
                ThreadCount = proc.Threads.Count,
                GdiObjectCount  = gdi,
                UserObjectCount = user,

                CpuPercent           = cpuPercent,
                CpuAvgRecentPercent  = _cpuJanela.Count > 0 ? Math.Round(_cpuJanela.Average(), 1) : 0,
                CpuPeakRecentPercent = _cpuJanela.Count > 0 ? Math.Round(_cpuJanela.Max(), 1) : 0,

                ReadBytesPerSecond  = io.ReadBps,
                WriteBytesPerSecond = io.WriteBps,
                ReadOpsPerSecond    = io.ReadOps,
                WriteOpsPerSecond   = io.WriteOps,

                LogQueueCount          = LogHelper.QueuedCount,
                HistoricoFileSizeBytes = historicoBytes,
                HistoricoItens         = HistoricoStorageService.UltimoTotalConhecido,
                LogsTotalKb            = logsTotalKb,
                UsaCdr    = cfg?.CdrAtivo ?? false,
                UsaAmi    = cfg?.AmiAtivo ?? false,
                UsaGoogle = false,

                AudioNativeDisposeOkCount   = Interlocked.Read(ref _audioDisposeOk),
                AudioNativeDisposeFailCount = Interlocked.Read(ref _audioDisposeFail),
                AudioDeviceRecoveryCount    = Interlocked.Read(ref _audioDeviceRecovery),

                UiFreezeCount           = Interlocked.Read(ref _uiFreezeCount),
                UiUltimaFreezeDuracaoMs = _ultimaFreezeDuracaoMs,
                LastOperation           = _lastOperation,

                UnobservedExceptionCount = Interlocked.Read(ref _unobservedExceptionCount),
                CriticalExceptionCount   = Interlocked.Read(ref _criticalExceptionCount),
            };
        }

        // ── Alertas derivados do heartbeat (memória / handles / fila de log) ───

        private void AvaliarAlertasDeHeartbeat(DiagnosticHeartbeat hb)
        {
            if (hb.WorkingSetMb >= MemoryEmergencyMb)
                _ = EnviarIncidenteAsync("MEMORY_EMERGENCY", detalhe: $"workingSetMb={hb.WorkingSetMb}");
            else if (hb.WorkingSetMb >= MemoryCriticalMb)
                _ = EnviarIncidenteAsync("MEMORY_CRITICAL", detalhe: $"workingSetMb={hb.WorkingSetMb}");
            else if (hb.WorkingSetMb >= MemoryWarningMb)
                _ = EnviarIncidenteAsync("MEMORY_WARNING", detalhe: $"workingSetMb={hb.WorkingSetMb}");

            if (hb.LogQueueCount >= LogQueueWarning)
                _ = EnviarIncidenteAsync("LOG_QUEUE_WARNING", detalhe: $"queueCount={hb.LogQueueCount}");

            // HANDLE_WARNING: limiar absoluto conservador nesta v1 — correlação completa com
            // callsSinceStartup fica para análise offline dos snapshots no painel.
            if (hb.HandleCount >= 5000)
                _ = EnviarIncidenteAsync("HANDLE_WARNING", detalhe: $"handles={hb.HandleCount} calls={hb.CallsSinceStartup}");
        }

        // ── Envio de incidente (imediato, com cooldown por tipo + rate limit global) ──

        private async Task EnviarIncidenteAsync(string evento, double? duracaoMs = null, string detalhe = "")
            => await EnviarIncidenteInternoAsync(evento, duracaoMs, detalhe, origem: null, captureSource: null, ex: null).ConfigureAwait(false);

        // v2.4.4 — mesma infraestrutura (cooldown/rate-limit/fila offline) de EnviarIncidenteAsync,
        // mas anexa o detalhe sanitizado da exceção quando disponível (item 2 do pedido). Só usado
        // hoje para UNOBSERVED_EXCEPTION (via TaskScheduler ou via FireAndForgetExtensions).
        private async Task EnviarIncidenteDetalhadoAsync(string evento, string detalhe, string origem, string captureSource, Exception? ex)
            => await EnviarIncidenteInternoAsync(evento, duracaoMs: null, detalhe, origem, captureSource, ex).ConfigureAwait(false);

        private async Task EnviarIncidenteInternoAsync(string evento, double? duracaoMs, string detalhe,
            string? origem, string? captureSource, Exception? ex)
        {
            try
            {
                lock (_cooldownLock)
                {
                    var cooldown = _cooldownPorEvento.TryGetValue(evento, out var c) ? c : TimeSpan.FromMinutes(5);
                    if (_ultimoEnvioIncidente.TryGetValue(evento, out var ultimo) && DateTime.UtcNow - ultimo < cooldown)
                        return;
                    _ultimoEnvioIncidente[evento] = DateTime.UtcNow;
                }

                if (!RespeitaRateLimitGlobal()) return;

                var (url, token, ativo) = ObterConfig();
                if (!ativo || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token)) return;

                var proc = Process.GetCurrentProcess();
                var payload = new DiagnosticEvent
                {
                    InstallationId = InstallationId,
                    MachineId      = MachineId,
                    AppVersion     = VersionService.Versao,
                    Ramal          = _ramal,
                    TimestampUtc   = DateTime.UtcNow,
                    Evento         = evento,
                    DuracaoMs      = duracaoMs,
                    Detalhe        = detalhe,
                    WorkingSetMb   = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 1),
                    PrivateBytesMb = Math.Round(proc.PrivateMemorySize64 / 1024.0 / 1024.0, 1),
                    HandleCount    = proc.HandleCount,
                    ThreadCount    = proc.Threads.Count,
                    CpuPercent     = _cpuJanela.Count > 0 ? _cpuJanela.Last() : 0,
                    CallsSinceStartup = Interlocked.Read(ref _callsEnded),
                    LastOperation  = _lastOperation,
                };

                if (ex != null)
                {
                    payload.Origem        = SanitizarTexto(origem, 200);
                    payload.CaptureSource = captureSource;
                    payload.ExceptionType = SanitizarTexto(ex.GetType().FullName, 200);
                    payload.ExceptionMessage = SanitizarTexto(ex.Message, 500);
                    if (ex.InnerException != null)
                    {
                        payload.InnerExceptionType    = SanitizarTexto(ex.InnerException.GetType().FullName, 200);
                        payload.InnerExceptionMessage = SanitizarTexto(ex.InnerException.Message, 500);
                    }
                    payload.StackTraceTop     = SanitizarStackTrace(ex.StackTrace, 1500);
                    payload.ExceptionThreadId = Environment.CurrentManagedThreadId;
                    payload.ExceptionTaskId   = Task.CurrentId;
                }

                var ok = await EnviarAsync(url, token, "/api/diagnostics/event", payload).ConfigureAwait(false);
                if (!ok) EnfileirarOffline("event", payload);
            }
            catch { /* telemetria nunca pode propagar erro */ }
        }

        private bool RespeitaRateLimitGlobal()
        {
            lock (_globalRateLock)
            {
                var agora = DateTime.UtcNow;
                while (_enviosRecentes.Count > 0 && agora - _enviosRecentes.Peek() > TimeSpan.FromMinutes(1))
                    _enviosRecentes.Dequeue();
                if (_enviosRecentes.Count >= MaxEnviosPorMinuto) return false;
                _enviosRecentes.Enqueue(agora);
                return true;
            }
        }

        // ── HTTP ─────────────────────────────────────────────────────────────

        private static async Task<bool> EnviarAsync(string url, string token, string path, object body)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{url}{path}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ── Fila offline (bounded — nunca cresce indefinidamente) ──────────────

        private static void EnfileirarOffline(string kind, object payload)
        {
            try
            {
                var fila = CarregarFila();
                fila.Add(new DiagnosticOfflineItem
                {
                    Kind = kind,
                    Json = JsonSerializer.Serialize(payload),
                    EnfileiradoEmUtc = DateTime.UtcNow,
                });
                // Descarta os mais antigos primeiro se estourar o limite — preferimos perder
                // heartbeats velhos a crescer sem limite (telemetria é secundária, nunca deve
                // virar ela mesma um problema de memória/disco).
                while (fila.Count > MaxOfflineQueueItems)
                    fila.RemoveAt(0);
                SalvarFila(fila);
            }
            catch { }
        }

        private async Task DrenarFilaOfflineAsync(string url, string token)
        {
            List<DiagnosticOfflineItem> fila;
            try { fila = CarregarFila(); } catch { return; }
            if (fila.Count == 0) return;

            // Envio gradual, item a item, respeitando o rate limit global — nunca em rajada
            // (item 8 do pedido: "NÃO disparar 100 requisições simultâneas").
            var restantes = new List<DiagnosticOfflineItem>();
            foreach (var item in fila)
            {
                if (!RespeitaRateLimitGlobal()) { restantes.Add(item); continue; }
                var path = item.Kind == "event" ? "/api/diagnostics/event" : "/api/diagnostics/heartbeat";
                var ok = await EnviarRawAsync(url, token, path, item.Json).ConfigureAwait(false);
                if (!ok) restantes.Add(item);
            }
            try { SalvarFila(restantes); } catch { }
        }

        private static async Task<bool> EnviarRawAsync(string url, string token, string path, string json)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{url}{path}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private static List<DiagnosticOfflineItem> CarregarFila()
        {
            try
            {
                if (!File.Exists(QueueFile)) return new List<DiagnosticOfflineItem>();
                return JsonSerializer.Deserialize<List<DiagnosticOfflineItem>>(File.ReadAllText(QueueFile))
                       ?? new List<DiagnosticOfflineItem>();
            }
            catch { return new List<DiagnosticOfflineItem>(); }
        }

        private static void SalvarFila(List<DiagnosticOfflineItem> fila)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(QueueFile)!);
                File.WriteAllText(QueueFile, JsonSerializer.Serialize(fila));
            }
            catch { }
        }
    }
}
