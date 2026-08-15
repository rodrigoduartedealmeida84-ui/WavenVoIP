using Microsoft.EntityFrameworkCore;
using WavenApi.Data;
using WavenApi.Models;

namespace WavenApi.Services;

/// <summary>
/// Motor de correlação/classificação — a peça central da investigação de causa raiz.
/// Roda periodicamente, olha os snapshots de cada instalação "online", calcula deltas
/// desde o início da sessão atual (detectada pelo UptimeSeconds, sem precisar de coluna
/// nova em DiagnosticInstallation/Snapshot), classifica SUSPEITAS (nunca causa
/// confirmada — ver seção 10/24 do pedido) e mantém um DiagnosticFinding persistente por
/// episódio de crescimento, que NUNCA é apagado quando a métrica volta ao normal.
///
/// O que este serviço NUNCA faz: alterar código, reiniciar processos, mudar config,
/// tocar em SIP/Issabel, ou declarar CAUSA_CONFIRMADA/correção automaticamente.
/// </summary>
public sealed class DiagnosticAnalysisService(
    IServiceScopeFactory scopeFactory,
    ILogger<DiagnosticAnalysisService> logger) : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OnlineJanela = TimeSpan.FromMinutes(10);

    // Marcos de memória a registrar como incidente (item 17 do pedido).
    private static readonly double[] MemoryMarksMb = { 500, 750, 1000, 1500, 2000 };

    // Limiares de classificação — ver seção 10 do pedido. Conservadores de propósito:
    // é melhor deixar de classificar do que gerar falso positivo com pouco dado.
    private const double DeltaPrivateSignificativoMb = 100;
    private const double RatioHeapAltoParaManaged     = 0.5;
    private const double RatioHeapBaixoParaNative      = 0.2;
    private const long   DeltaCallsMinParaHandleRatio  = 20;
    private const int    DeltaHandlesSignificativo     = 300;
    private const int    DeltaThreadsSignificativo     = 15;
    private const int    DeltaGdiSignificativo         = 200;
    private const int    DeltaUserSignificativo        = 200;
    private const int    LogQueueAltoSustentado        = 500;
    private const double DiskIoAltoBytesPerSec         = 10 * 1024 * 1024; // 10 MB/s

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // deixa a API subir

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await AnalisarFrotaAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "DIAGNOSTIC_ANALYSIS_ERROR"); }

            try { await Task.Delay(Intervalo, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task AnalisarFrotaAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WavenDbContext>();

        var corte = DateTime.UtcNow - OnlineJanela;
        var instalacoesOnline = await db.DiagnosticInstallations
            .Where(i => i.LastSeenUtc >= corte)
            .Select(i => i.Id)
            .ToListAsync(ct);

        foreach (var installationId in instalacoesOnline)
        {
            try { await AnalisarInstalacaoAsync(db, installationId, ct); }
            catch (Exception ex) { logger.LogError(ex, "DIAGNOSTIC_ANALYSIS_INSTALLATION_ERROR | id={Id}", installationId); }
        }
    }

    private async Task AnalisarInstalacaoAsync(WavenDbContext db, string installationId, CancellationToken ct)
    {
        // Últimos snapshots — suficiente para cobrir uma sessão longa sem carregar histórico
        // inteiro (200 snapshots a cada 60s ~ 3h20; sessões mais longas ainda funcionam
        // porque o baseline é recalculado a cada ciclo com base no uptime mais recente).
        var recentes = await db.DiagnosticSnapshots
            .Where(s => s.InstallationId == installationId)
            .OrderByDescending(s => s.TimestampUtc)
            .Take(200)
            .ToListAsync(ct);
        if (recentes.Count < 2) return;

        recentes.Reverse(); // ordem cronológica
        var latest = recentes[^1];

        // Sessão atual inferida por uptime — evita precisar de coluna/flag de "restart".
        var inicioSessaoAprox = latest.TimestampUtc - TimeSpan.FromSeconds(latest.UptimeSeconds) - TimeSpan.FromMinutes(2);
        var sessao = recentes.Where(s => s.TimestampUtc >= inicioSessaoAprox).ToList();
        if (sessao.Count < 2) sessao = recentes; // fallback: sem sessão clara, usa a janela toda
        var baseline = sessao[0];

        var picoWorkingSet = sessao.Max(s => s.WorkingSetMb);
        var picoSnap = sessao.OrderByDescending(s => s.WorkingSetMb).First();

        double deltaWs      = latest.WorkingSetMb - baseline.WorkingSetMb;
        double deltaPrivate = latest.PrivateBytesMb - baseline.PrivateBytesMb;
        double deltaHeap    = latest.ManagedHeapMb - baseline.ManagedHeapMb;
        int    deltaHandles = latest.HandleCount - baseline.HandleCount;
        int    deltaThreads = latest.ThreadCount - baseline.ThreadCount;
        int    deltaGdi     = latest.GdiObjectCount - baseline.GdiObjectCount;
        int    deltaUser    = latest.UserObjectCount - baseline.UserObjectCount;
        long   deltaCalls   = latest.CallsSinceStartup - baseline.CallsSinceStartup;

        var classificacoes = new List<string>();

        if (Math.Abs(deltaPrivate) >= DeltaPrivateSignificativoMb)
        {
            var ratio = deltaHeap / deltaPrivate; // guiado por sinal — se private caiu, ratio negativo não classifica
            if (deltaPrivate > 0)
            {
                if (ratio >= RatioHeapAltoParaManaged) classificacoes.Add("MANAGED_GROWTH_SUSPECT");
                else if (ratio <= RatioHeapBaixoParaNative) classificacoes.Add("NATIVE_GROWTH_SUSPECT");
            }
        }

        if (deltaCalls >= DeltaCallsMinParaHandleRatio && deltaHandles >= DeltaHandlesSignificativo)
            classificacoes.Add("HANDLE_GROWTH_SUSPECT");

        if (deltaThreads >= DeltaThreadsSignificativo)
            classificacoes.Add("THREAD_GROWTH_SUSPECT");

        if (deltaGdi >= DeltaGdiSignificativo)
            classificacoes.Add("GDI_GROWTH_SUSPECT");

        if (deltaUser >= DeltaUserSignificativo)
            classificacoes.Add("USER_OBJECT_GROWTH_SUSPECT");

        var ultimosLogQueue = sessao.TakeLast(5).Select(s => s.LogQueueCount).ToList();
        if (ultimosLogQueue.Count >= 3 && ultimosLogQueue.Min() > LogQueueAltoSustentado)
            classificacoes.Add("LOG_QUEUE_GROWTH_SUSPECT");

        var ultimosIo = sessao.TakeLast(5).ToList();
        if (ultimosIo.Count >= 3 && ultimosIo.All(s => s.ReadBytesPerSecond + s.WriteBytesPerSecond > DiskIoAltoBytesPerSec))
            classificacoes.Add("HIGH_DISK_IO");

        // ── Marcos de memória (item 17) — um incidente por marco cruzado nesta sessão ──
        foreach (var marco in MemoryMarksMb)
        {
            if (baseline.WorkingSetMb < marco && latest.WorkingSetMb >= marco)
            {
                var evento = $"MEMORY_MARK_{marco:0}MB";
                var jaRegistrado = await db.DiagnosticIncidents.AnyAsync(i =>
                    i.InstallationId == installationId && i.Evento == evento &&
                    i.TimestampUtc >= baseline.TimestampUtc, ct);
                if (!jaRegistrado)
                {
                    db.DiagnosticIncidents.Add(new DiagnosticIncident
                    {
                        InstallationId = installationId,
                        Ramal = "",
                        AppVersion = "",
                        TimestampUtc = latest.TimestampUtc,
                        Evento = evento,
                        Detalhe = $"workingSetMb={latest.WorkingSetMb:0} calls={latest.CallsSinceStartup}",
                        WorkingSetMb = latest.WorkingSetMb,
                        PrivateBytesMb = latest.PrivateBytesMb,
                        HandleCount = latest.HandleCount,
                        ThreadCount = latest.ThreadCount,
                        CpuPercent = latest.CpuPercent,
                        CallsSinceStartup = latest.CallsSinceStartup,
                        LastOperation = latest.LastOperation,
                    });
                    logger.LogWarning("DIAGNOSTIC_MEMORY_MARK | id={Id} marco={Marco}MB workingSetMb={Ws}",
                        installationId, marco, latest.WorkingSetMb);
                }
            }
        }

        // suspeitas de crescimento como incidentes também, para aparecerem na timeline de
        // incidentes junto com os enviados pelo cliente — com cooldown simples via checagem
        // de "já existe suspeita igual nas últimas 2h" para não duplicar a cada ciclo de 5min.
        foreach (var tag in classificacoes)
        {
            var recente = await db.DiagnosticIncidents.AnyAsync(i =>
                i.InstallationId == installationId && i.Evento == tag &&
                i.TimestampUtc >= DateTime.UtcNow.AddHours(-2), ct);
            if (!recente)
            {
                db.DiagnosticIncidents.Add(new DiagnosticIncident
                {
                    InstallationId = installationId,
                    Ramal = "",
                    AppVersion = "",
                    TimestampUtc = DateTime.UtcNow,
                    Evento = tag,
                    Detalhe = $"deltaWs={deltaWs:0} deltaPrivate={deltaPrivate:0} deltaHeap={deltaHeap:0} deltaHandles={deltaHandles} deltaCalls={deltaCalls}",
                    WorkingSetMb = latest.WorkingSetMb,
                    PrivateBytesMb = latest.PrivateBytesMb,
                    HandleCount = latest.HandleCount,
                    ThreadCount = latest.ThreadCount,
                    CpuPercent = latest.CpuPercent,
                    CallsSinceStartup = latest.CallsSinceStartup,
                    LastOperation = latest.LastOperation,
                });
            }
        }

        // ── Ciclo de vida do Finding persistente (item 21) ──────────────────────────
        var aberto = await db.DiagnosticFindings
            .Where(f => f.InstallationId == installationId && f.NormalizedUtc == null)
            .OrderByDescending(f => f.StartedUtc)
            .FirstOrDefaultAsync(ct);

        if (latest.WorkingSetMb >= MemoryMarksMb[0]) // >= 500MB
        {
            if (aberto == null)
            {
                aberto = new DiagnosticFinding
                {
                    InstallationId = installationId,
                    StartedUtc = baseline.TimestampUtc,
                    UptimeSecondsAtStart = baseline.UptimeSeconds,
                    CallsAtStart = baseline.CallsSinceStartup,
                    WorkingSetStartMb = baseline.WorkingSetMb,
                    PrivateStartMb = baseline.PrivateBytesMb,
                    ManagedHeapStartMb = baseline.ManagedHeapMb,
                    HandlesStart = baseline.HandleCount,
                    Status = DiagnosticInvestigationStatus.Novo,
                    FirstSnapshotId = baseline.Id,
                };
                db.DiagnosticFindings.Add(aberto);
            }

            aberto.PeakUtc = picoSnap.TimestampUtc;
            aberto.PeakSnapshotId = picoSnap.Id;
            aberto.CallsAtPeak = Math.Max(aberto.CallsAtPeak, latest.CallsSinceStartup);
            aberto.WorkingSetPeakMb = Math.Max(aberto.WorkingSetPeakMb, picoWorkingSet);
            aberto.PrivatePeakMb = Math.Max(aberto.PrivatePeakMb, sessao.Max(s => s.PrivateBytesMb));
            aberto.ManagedHeapPeakMb = Math.Max(aberto.ManagedHeapPeakMb, sessao.Max(s => s.ManagedHeapMb));
            aberto.GcTotalMemoryPeakMb = Math.Max(aberto.GcTotalMemoryPeakMb, sessao.Max(s => s.GcTotalMemoryMb));
            aberto.LohPeakMb = Math.Max(aberto.LohPeakMb, sessao.Max(s => s.LohMb));
            aberto.PohPeakMb = Math.Max(aberto.PohPeakMb, sessao.Max(s => s.PohMb));
            aberto.HandlesPeak = Math.Max(aberto.HandlesPeak, sessao.Max(s => s.HandleCount));
            aberto.ThreadsPeak = Math.Max(aberto.ThreadsPeak, sessao.Max(s => s.ThreadCount));
            aberto.GdiPeak = Math.Max(aberto.GdiPeak, sessao.Max(s => s.GdiObjectCount));
            aberto.UserPeak = Math.Max(aberto.UserPeak, sessao.Max(s => s.UserObjectCount));
            aberto.CpuAtPeak = latest.CpuPercent;
            aberto.ReadMBsAtPeak = Math.Max(aberto.ReadMBsAtPeak, latest.ReadBytesPerSecond / 1024.0 / 1024.0);
            aberto.WriteMBsAtPeak = Math.Max(aberto.WriteMBsAtPeak, latest.WriteBytesPerSecond / 1024.0 / 1024.0);
            aberto.LogQueuePeak = Math.Max(aberto.LogQueuePeak, sessao.Max(s => s.LogQueueCount));
            aberto.LastOperationAtPeak = latest.LastOperation;
            aberto.UpdatedUtc = DateTime.UtcNow;

            if (classificacoes.Count > 0)
                aberto.Classifications = string.Join(",", classificacoes.Distinct());

            aberto.Severity = latest.WorkingSetMb >= 1500 ? "EMERGENCIA"
                : latest.WorkingSetMb >= 1000 ? "CRITICO"
                : "ATENCAO";

            // Ladder de status — só avança, nunca regride (item 22/24 do pedido).
            var snapCount = sessao.Count;
            AvancarStatusSeAplicavel(aberto, snapCount, deltaPrivate, classificacoes);

            aberto.Evidence = ConstruirEvidencia(deltaCalls, deltaWs, deltaPrivate, deltaHeap, deltaHandles, deltaThreads, deltaGdi, deltaUser);
            aberto.SuspectComponents = MapearComponentesSuspeitos(classificacoes);
            aberto.ProbableCause = MapearCausaProvavel(classificacoes);
            aberto.Hypothesis = classificacoes.Count > 0
                ? $"Padrão de crescimento consistente com: {string.Join(", ", classificacoes.Distinct())}."
                : "Working Set elevado, mas sem padrão claro o suficiente ainda para classificar — aguardando mais dados.";
        }
        else if (aberto != null)
        {
            // Recuperou perto do baseline — marca normalizado, mas NUNCA apaga (item 21/23).
            var limiarRecuperacao = Math.Max(400, aberto.WorkingSetStartMb * 1.2);
            if (latest.WorkingSetMb < limiarRecuperacao)
            {
                aberto.NormalizedUtc = DateTime.UtcNow;
                aberto.UpdatedUtc = DateTime.UtcNow;
            }
        }

        // Preenche Ramal/AppVersion do finding a partir da instalação (não vem do snapshot).
        if (aberto != null && string.IsNullOrEmpty(aberto.Ramal))
        {
            var inst = await db.DiagnosticInstallations.FindAsync(new object[] { installationId }, ct);
            if (inst != null) { aberto.Ramal = inst.Ramal; aberto.AppVersion = inst.AppVersion; }
        }

        await db.SaveChangesAsync(ct);
    }

    private static void AvancarStatusSeAplicavel(DiagnosticFinding f, int snapCount, double deltaPrivate, List<string> classificacoes)
    {
        string[] ordem =
        {
            DiagnosticInvestigationStatus.Novo,
            DiagnosticInvestigationStatus.EmObservacao,
            DiagnosticInvestigationStatus.EvidenciaSuficiente,
            DiagnosticInvestigationStatus.CausaProvavel,
        };
        int atual = Array.IndexOf(ordem, f.Status);
        if (atual < 0) atual = 0; // status manual desconhecido (ex.: já avançado por humano) — não mexe

        int alvo = atual;
        if (snapCount >= 3) alvo = Math.Max(alvo, 1);
        if (snapCount >= 5 && Math.Abs(deltaPrivate) >= 200) alvo = Math.Max(alvo, 2);
        if (snapCount >= 8 && classificacoes.Count > 0) alvo = Math.Max(alvo, 3);

        // Só avança dentro da ladder automática (0-3); nunca mexe se já estiver em
        // CAUSA_CONFIRMADA ou além (index >= ordem.Length, tratado pelo Array.IndexOf == -1 acima
        // quando o status não está nesta lista de 4 — nesse caso "atual" já ficou 0 por segurança,
        // então precisamos checar explicitamente antes de sobrescrever).
        bool statusEhAutomatico = Array.IndexOf(ordem, f.Status) >= 0;
        if (statusEhAutomatico && alvo > atual)
            f.Status = ordem[alvo];
    }

    private static string ConstruirEvidencia(long deltaCalls, double deltaWs, double deltaPrivate, double deltaHeap,
        int deltaHandles, int deltaThreads, int deltaGdi, int deltaUser) =>
        $"Desde o início da sessão: {deltaCalls} chamadas, Working Set {Sinal(deltaWs)}{deltaWs:0}MB, " +
        $"Private Bytes {Sinal(deltaPrivate)}{deltaPrivate:0}MB, Managed Heap {Sinal(deltaHeap)}{deltaHeap:0}MB, " +
        $"Handles {Sinal(deltaHandles)}{deltaHandles}, Threads {Sinal(deltaThreads)}{deltaThreads}, " +
        $"GDI {Sinal(deltaGdi)}{deltaGdi}, USER {Sinal(deltaUser)}{deltaUser}.";

    private static string Sinal(double v) => v >= 0 ? "+" : "";

    private static string MapearCausaProvavel(List<string> tags)
    {
        // Combo mais específico primeiro: Managed Heap crescendo em paralelo com a fila do
        // LogHelper é uma correlação muito mais acionável do que qualquer um dos dois isolado —
        // a própria fila (List<(string,string)> sem limite) É managed heap, então o padrão
        // "os dois crescem juntos" aponta direto pra ela como candidata primária, não só "algum
        // objeto .NET genérico". Ver ConstruirResumoInvestigacao para a correlação numérica real.
        if (tags.Contains("MANAGED_GROWTH_SUSPECT") && tags.Contains("LOG_QUEUE_GROWTH_SUSPECT"))
            return "Possível vazamento gerenciado ligado à fila de escrita de log (LogHelper._queue) — " +
                   "a fila em si é memória gerenciada (lista de tuplas string), então seu crescimento sem " +
                   "escoamento suficiente explicaria diretamente o Managed Heap subindo.";
        if (tags.Contains("NATIVE_GROWTH_SUSPECT") && tags.Contains("HANDLE_GROWTH_SUSPECT"))
            return "Possível vazamento de recurso nativo/handle relacionado ao ciclo de chamadas.";
        if (tags.Contains("NATIVE_GROWTH_SUSPECT"))
            return "Possível vazamento de memória nativa (fora do heap gerenciado do .NET).";
        if (tags.Contains("MANAGED_GROWTH_SUSPECT"))
            return "Possível retenção de objetos .NET (event handlers, coleções, timers, Tasks).";
        if (tags.Contains("HANDLE_GROWTH_SUSPECT"))
            return "Possível handle leak (arquivo/recurso Win32 não liberado).";
        if (tags.Contains("THREAD_GROWTH_SUSPECT"))
            return "Threads não encerradas corretamente.";
        if (tags.Contains("GDI_GROWTH_SUSPECT") || tags.Contains("USER_OBJECT_GROWTH_SUSPECT"))
            return "Recursos gráficos do Windows (GDI/USER) não liberados — provável janela/recurso WPF.";
        if (tags.Contains("LOG_QUEUE_GROWTH_SUSPECT"))
            return "Fila de escrita do LogHelper crescendo sem escoar.";
        if (tags.Contains("HIGH_DISK_IO"))
            return "I/O de disco sustentado elevado.";
        return string.Empty;
    }

    private static string MapearComponentesSuspeitos(List<string> tags)
    {
        var componentes = new List<string>();
        if (tags.Contains("MANAGED_GROWTH_SUSPECT") && tags.Contains("LOG_QUEUE_GROWTH_SUSPECT"))
        {
            // Combo específico primeiro (ver MapearCausaProvavel) — os produtores de log mais
            // prováveis de gerar volume desproporcional vêm antes dos genéricos.
            componentes.AddRange(new[] { "LogHelper (_queue sem limite)", "IssabelCdrService", "HistoricoStorageService.MesclarCdr" });
        }
        if (tags.Contains("NATIVE_GROWTH_SUSPECT") || tags.Contains("HANDLE_GROWTH_SUSPECT"))
            componentes.AddRange(new[] { "SipService", "VoIPMediaSession", "WindowsAudioEndPoint", "NAudio", "SIPSorcery" });
        if (tags.Contains("MANAGED_GROWTH_SUSPECT"))
            componentes.AddRange(new[] { "CallWindow", "HistoricoStorageService", "ContatoStorageService", "FavoritesStorageService", "LogHelper" });
        if (tags.Contains("GDI_GROWTH_SUSPECT") || tags.Contains("USER_OBJECT_GROWTH_SUSPECT"))
            componentes.AddRange(new[] { "DialerShellWindow", "CallWindow", "recursos WPF/ícones" });
        if (tags.Contains("LOG_QUEUE_GROWTH_SUSPECT"))
            componentes.Add("LogHelper");
        if (tags.Contains("HIGH_DISK_IO"))
            componentes.AddRange(new[] { "IssabelCdrService", "HistoricoStorageService", "LogHelper" });
        return string.Join(", ", componentes.Distinct());
    }
}
