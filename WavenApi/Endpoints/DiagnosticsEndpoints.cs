using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WavenApi.Data;
using WavenApi.Models;

namespace WavenApi.Endpoints;

public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/diagnostics");
        g.MapPost("/heartbeat", PostHeartbeat);
        g.MapPost("/event",     PostEvent);

        // Leitura — alimenta o painel administrativo e qualquer consulta futura
        // autorizada (item 27/28 do pedido). Tudo atrás do mesmo BearerAuthMiddleware.
        g.MapGet("/installations",                     GetInstallations);
        g.MapGet("/installations/{id}/summary",        GetInstallationSummary);
        g.MapGet("/installations/{id}/timeseries",     GetTimeseries);
        g.MapGet("/installations/{id}/snapshots",      GetSnapshots); // legado/compatibilidade
        g.MapGet("/compare",                           CompareInstallations);
        g.MapGet("/incidents",                          GetIncidents);
        g.MapGet("/findings",                           GetFindings);
        g.MapGet("/findings/{id:long}",                 GetFindingDetail);
        g.MapGet("/installations/{id}/investigation",   GetInstallationInvestigation);
        g.MapGet("/summary",                            GetFleetSummary);
    }

    // ── Rate limit server-side (ver comentário original) ────────────────────────
    private static readonly ConcurrentDictionary<string, DateTime> _ultimoHeartbeat = new();
    private static readonly ConcurrentDictionary<string, DateTime> _ultimoIncident   = new();
    private static readonly TimeSpan JanelaOnline = TimeSpan.FromMinutes(10);

    // ── POST /api/diagnostics/heartbeat ───────────────────────────────────────

    private static async Task<IResult> PostHeartbeat(
        DiagnosticHeartbeatRequest req, WavenDbContext db, IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Diagnostics");

        if (string.IsNullOrWhiteSpace(req.InstallationId))
            return Results.BadRequest(new { error = "installationId obrigatório" });

        var minIntervalo = TimeSpan.FromSeconds(opts.Value.Diagnostics.MinIntervaloHeartbeatSegundos);
        if (_ultimoHeartbeat.TryGetValue(req.InstallationId, out var ultimo) && DateTime.UtcNow - ultimo < minIntervalo)
            return Results.Ok(new { ok = true, ignored = true, reason = "rate_limited" });
        _ultimoHeartbeat[req.InstallationId] = DateTime.UtcNow;

        var agora = DateTime.UtcNow;
        var inst = await db.DiagnosticInstallations.FindAsync(req.InstallationId);
        if (inst == null)
        {
            inst = new DiagnosticInstallation { Id = req.InstallationId, FirstSeenUtc = agora };
            db.DiagnosticInstallations.Add(inst);
            logger.LogInformation("DIAGNOSTIC_INSTALLATION_NEW | id={Id} machineId={MachineId} ramal={Ramal}",
                req.InstallationId, req.MachineId, req.Ramal);
        }

        inst.MachineId    = req.MachineId ?? "";
        inst.MachineAlias = req.MachineAlias ?? "";
        inst.Ramal        = req.Ramal ?? "";
        inst.AppVersion   = req.AppVersion ?? "";
        inst.LastSeenUtc  = agora;
        inst.UltimoWorkingSetMb      = req.WorkingSetMb;
        inst.PicoWorkingSetMb        = Math.Max(inst.PicoWorkingSetMb, req.WorkingSetMb);
        inst.UltimoPrivateBytesMb    = req.PrivateBytesMb;
        inst.UltimoManagedHeapMb     = req.ManagedHeapMb;
        inst.UltimoHandleCount       = req.HandleCount;
        inst.UltimoThreadCount       = req.ThreadCount;
        inst.UltimoCpuPercent        = req.CpuPercent;
        inst.UltimoCallsSinceStartup = req.CallsSinceStartup;
        inst.UltimoLastOperation     = req.LastOperation ?? "IDLE";

        db.DiagnosticSnapshots.Add(new DiagnosticSnapshot
        {
            InstallationId = req.InstallationId,
            TimestampUtc   = req.TimestampUtc == default ? agora : req.TimestampUtc,
            UptimeSeconds  = req.UptimeSeconds,
            CallsSinceStartup = req.CallsSinceStartup, CallsIncoming = req.CallsIncoming,
            CallsOutgoing = req.CallsOutgoing, CallsConnected = req.CallsConnected, CallsEnded = req.CallsEnded,
            WorkingSetMb = req.WorkingSetMb, PrivateBytesMb = req.PrivateBytesMb,
            ManagedHeapMb = req.ManagedHeapMb, GcTotalMemoryMb = req.GcTotalMemoryMb,
            Gen0Mb = req.Gen0Mb, Gen1Mb = req.Gen1Mb, Gen2Mb = req.Gen2Mb, LohMb = req.LohMb, PohMb = req.PohMb,
            Gen0Collections = req.Gen0Collections, Gen1Collections = req.Gen1Collections, Gen2Collections = req.Gen2Collections,
            HandleCount = req.HandleCount, ThreadCount = req.ThreadCount,
            GdiObjectCount = req.GdiObjectCount, UserObjectCount = req.UserObjectCount,
            CpuPercent = req.CpuPercent, CpuAvgRecentPercent = req.CpuAvgRecentPercent, CpuPeakRecentPercent = req.CpuPeakRecentPercent,
            ReadBytesPerSecond = req.ReadBytesPerSecond, WriteBytesPerSecond = req.WriteBytesPerSecond,
            ReadOpsPerSecond = req.ReadOpsPerSecond, WriteOpsPerSecond = req.WriteOpsPerSecond,
            LogQueueCount = req.LogQueueCount, HistoricoFileSizeBytes = req.HistoricoFileSizeBytes,
            HistoricoItens = req.HistoricoItens, LogsTotalKb = req.LogsTotalKb,
            AudioNativeDisposeOkCount = req.AudioNativeDisposeOkCount, AudioNativeDisposeFailCount = req.AudioNativeDisposeFailCount,
            AudioDeviceRecoveryCount = req.AudioDeviceRecoveryCount,
            UiFreezeCount = req.UiFreezeCount, UiUltimaFreezeDuracaoMs = req.UiUltimaFreezeDuracaoMs,
            LastOperation = req.LastOperation ?? "IDLE",
            UnobservedExceptionCount = req.UnobservedExceptionCount, CriticalExceptionCount = req.CriticalExceptionCount,
        });

        await db.SaveChangesAsync();
        return Results.Ok(new { ok = true });
    }

    // ── POST /api/diagnostics/event ───────────────────────────────────────────

    private static async Task<IResult> PostEvent(
        DiagnosticEventRequest req, WavenDbContext db, IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Diagnostics");

        if (string.IsNullOrWhiteSpace(req.InstallationId) || string.IsNullOrWhiteSpace(req.Evento))
            return Results.BadRequest(new { error = "installationId e evento são obrigatórios" });

        var chave = $"{req.InstallationId}:{req.Evento}";
        var minIntervalo = TimeSpan.FromSeconds(opts.Value.Diagnostics.MinIntervaloIncidentSegundos);
        if (_ultimoIncident.TryGetValue(chave, out var ultimo) && DateTime.UtcNow - ultimo < minIntervalo)
            return Results.Ok(new { ok = true, ignored = true, reason = "rate_limited" });
        _ultimoIncident[chave] = DateTime.UtcNow;

        db.DiagnosticIncidents.Add(new DiagnosticIncident
        {
            InstallationId = req.InstallationId,
            Ramal          = req.Ramal ?? "",
            AppVersion     = req.AppVersion ?? "",
            TimestampUtc   = req.TimestampUtc == default ? DateTime.UtcNow : req.TimestampUtc,
            Evento         = req.Evento,
            DuracaoMs      = req.DuracaoMs,
            Detalhe        = req.Detalhe ?? "",
            WorkingSetMb   = req.WorkingSetMb,
            PrivateBytesMb = req.PrivateBytesMb,
            HandleCount    = req.HandleCount,
            ThreadCount    = req.ThreadCount,
            CpuPercent     = req.CpuPercent,
            CallsSinceStartup = req.CallsSinceStartup,
            LastOperation  = req.LastOperation ?? "IDLE",
        });

        await db.SaveChangesAsync();
        logger.LogWarning("DIAGNOSTIC_INCIDENT | id={Id} evento={Evento} ramal={Ramal} workingSetMb={Mb} detalhe={Detalhe}",
            req.InstallationId, req.Evento, req.Ramal, req.WorkingSetMb, req.Detalhe);

        return Results.Ok(new { ok = true });
    }

    // ── Núcleo de correlação compartilhado (endpoints de leitura) ──────────────
    // Mesma heurística de "sessão atual" usada pelo DiagnosticAnalysisService: infere o
    // início da sessão a partir do UptimeSeconds do snapshot mais recente, sem precisar
    // de coluna nova. Ver comentário completo em DiagnosticAnalysisService.cs.

    private sealed record ResumoSessao(
        DiagnosticSnapshot Baseline, DiagnosticSnapshot Latest, List<DiagnosticSnapshot> Sessao,
        double PicoWorkingSetMb, double PicoPrivateMb, double PicoManagedHeapMb, int PicoHandles, int PicoLogQueue);

    private static async Task<ResumoSessao?> CarregarResumoSessaoAsync(WavenDbContext db, string installationId, CancellationToken ct = default)
    {
        var recentes = await db.DiagnosticSnapshots
            .Where(s => s.InstallationId == installationId)
            .OrderByDescending(s => s.TimestampUtc)
            .Take(200)
            .ToListAsync(ct);
        if (recentes.Count == 0) return null;
        recentes.Reverse();
        var latest = recentes[^1];

        var inicioSessaoAprox = latest.TimestampUtc - TimeSpan.FromSeconds(latest.UptimeSeconds) - TimeSpan.FromMinutes(2);
        var sessao = recentes.Where(s => s.TimestampUtc >= inicioSessaoAprox).ToList();
        if (sessao.Count < 2) sessao = recentes;
        var baseline = sessao[0];

        return new ResumoSessao(
            baseline, latest, sessao,
            sessao.Max(s => s.WorkingSetMb), sessao.Max(s => s.PrivateBytesMb),
            sessao.Max(s => s.ManagedHeapMb), sessao.Max(s => s.HandleCount), sessao.Max(s => s.LogQueueCount));
    }

    private static string ClassificarStatus(double workingSetMb, bool online)
    {
        if (!online) return "OFFLINE";
        if (workingSetMb >= 1500) return "EMERGENCIA";
        if (workingSetMb >= 1000) return "CRITICO";
        if (workingSetMb >= 500) return "ATENCAO";
        return "NORMAL";
    }

    // record (não anonymous object) — evita "dynamic" nos consumidores (OrderBy, cálculo do
    // texto de resumo) e deixa os nomes de campo do JSON explícitos/estáveis para o painel.
    private sealed record InstallationSummary(
        string id, string machineId, string machineAlias, string ramal, string appVersion,
        DateTime firstSeenUtc, DateTime lastSeenUtc, bool online, string status, long uptimeSeconds,
        double workingSetInicialMb, double workingSetAtualMb, double workingSetPicoMb,
        double privateInicialMb, double privateAtualMb, double privatePicoMb,
        double managedHeapAtualMb, double managedHeapPicoMb,
        int handlesInicial, int handlesAtual, int handlesPico,
        int threads, int gdiObjects, int userObjects, double cpuPercent, double readMBs, double writeMBs,
        int logQueueAtual, int logQueuePico, long callsSinceStartup,
        double crescimentoRamMb, double crescimentoPrivateMb, int crescimentoHandles,
        double? mbPor100Chamadas, double? handlesPor100Chamadas,
        int incidentCount, string lastOperation);

    private static InstallationSummary ProjetarResumo(DiagnosticInstallation inst, ResumoSessao? r, int incidentCount, bool online)
    {
        var baseline = r?.Baseline; var latest = r?.Latest;
        double wsIni = baseline?.WorkingSetMb ?? inst.UltimoWorkingSetMb;
        double wsAtual = latest?.WorkingSetMb ?? inst.UltimoWorkingSetMb;
        double privIni = baseline?.PrivateBytesMb ?? inst.UltimoPrivateBytesMb;
        double privAtual = latest?.PrivateBytesMb ?? inst.UltimoPrivateBytesMb;
        int handlesIni = baseline?.HandleCount ?? inst.UltimoHandleCount;
        int handlesAtual = latest?.HandleCount ?? inst.UltimoHandleCount;
        long callsIni = baseline?.CallsSinceStartup ?? 0;
        long callsAtual = latest?.CallsSinceStartup ?? inst.UltimoCallsSinceStartup;
        long deltaCalls = callsAtual - callsIni;

        double crescimentoRam = wsAtual - wsIni;
        double crescimentoPrivate = privAtual - privIni;
        int crescimentoHandles = handlesAtual - handlesIni;

        double? mbPor100 = deltaCalls > 0 ? Math.Round(crescimentoRam / deltaCalls * 100, 1) : null;
        double? handlesPor100 = deltaCalls > 0 ? Math.Round(crescimentoHandles / (double)deltaCalls * 100, 1) : null;

        return new InstallationSummary(
            id: inst.Id, machineId: inst.MachineId, machineAlias: inst.MachineAlias, ramal: inst.Ramal,
            appVersion: inst.AppVersion, firstSeenUtc: inst.FirstSeenUtc, lastSeenUtc: inst.LastSeenUtc,
            online: online, status: ClassificarStatus(wsAtual, online), uptimeSeconds: latest?.UptimeSeconds ?? 0,
            workingSetInicialMb: Math.Round(wsIni, 1), workingSetAtualMb: Math.Round(wsAtual, 1),
            workingSetPicoMb: Math.Round(Math.Max(r?.PicoWorkingSetMb ?? 0, inst.PicoWorkingSetMb), 1),
            privateInicialMb: Math.Round(privIni, 1), privateAtualMb: Math.Round(privAtual, 1),
            privatePicoMb: Math.Round(r?.PicoPrivateMb ?? privAtual, 1),
            managedHeapAtualMb: Math.Round(latest?.ManagedHeapMb ?? inst.UltimoManagedHeapMb, 1),
            managedHeapPicoMb: Math.Round(r?.PicoManagedHeapMb ?? 0, 1),
            handlesInicial: handlesIni, handlesAtual: handlesAtual, handlesPico: Math.Max(r?.PicoHandles ?? 0, inst.UltimoHandleCount),
            threads: latest?.ThreadCount ?? inst.UltimoThreadCount, gdiObjects: latest?.GdiObjectCount ?? 0,
            userObjects: latest?.UserObjectCount ?? 0, cpuPercent: latest?.CpuPercent ?? inst.UltimoCpuPercent,
            readMBs: Math.Round((latest?.ReadBytesPerSecond ?? 0) / 1024.0 / 1024.0, 2),
            writeMBs: Math.Round((latest?.WriteBytesPerSecond ?? 0) / 1024.0 / 1024.0, 2),
            logQueueAtual: latest?.LogQueueCount ?? 0, logQueuePico: r?.PicoLogQueue ?? 0,
            callsSinceStartup: callsAtual,
            crescimentoRamMb: Math.Round(crescimentoRam, 1), crescimentoPrivateMb: Math.Round(crescimentoPrivate, 1),
            crescimentoHandles: crescimentoHandles, mbPor100Chamadas: mbPor100, handlesPor100Chamadas: handlesPor100,
            incidentCount: incidentCount, lastOperation: inst.UltimoLastOperation);
    }

    // ── GET /api/diagnostics/installations ────────────────────────────────────

    private static async Task<IResult> GetInstallations(WavenDbContext db)
    {
        var instalacoes = await db.DiagnosticInstallations.ToListAsync();
        var agora = DateTime.UtcNow;

        var incidentCounts = await db.DiagnosticIncidents
            .GroupBy(i => i.InstallationId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var resultado = new List<InstallationSummary>();
        foreach (var inst in instalacoes)
        {
            var resumo = await CarregarResumoSessaoAsync(db, inst.Id);
            var online = agora - inst.LastSeenUtc <= JanelaOnline;
            var count = incidentCounts.TryGetValue(inst.Id, out var c) ? c : 0;
            resultado.Add(ProjetarResumo(inst, resumo, count, online));
        }

        return Results.Ok(resultado.OrderByDescending(r => r.workingSetAtualMb));
    }

    // ── GET /api/diagnostics/installations/{id}/summary ───────────────────────

    private static async Task<IResult> GetInstallationSummary(string id, WavenDbContext db)
    {
        var inst = await db.DiagnosticInstallations.FindAsync(id);
        if (inst == null) return Results.NotFound();

        var resumo = await CarregarResumoSessaoAsync(db, id);
        var agora = DateTime.UtcNow;
        var online = agora - inst.LastSeenUtc <= JanelaOnline;

        var incidentes = await db.DiagnosticIncidents
            .Where(i => i.InstallationId == id)
            .OrderByDescending(i => i.TimestampUtc)
            .Take(50)
            .ToListAsync();

        var findings = await db.DiagnosticFindings
            .Where(f => f.InstallationId == id)
            .OrderByDescending(f => f.StartedUtc)
            .Take(20)
            .ToListAsync();

        var projecao = ProjetarResumo(inst, resumo, incidentes.Count, online);

        string resumoTexto;
        if (resumo == null || resumo.Sessao.Count < 2)
        {
            resumoTexto = "Dados insuficientes para gerar resumo ainda.";
        }
        else
        {
            var crescRam = projecao.crescimentoRamMb;
            var crescPriv = projecao.crescimentoPrivateMb;
            var crescHandles = projecao.crescimentoHandles;
            var deltaHeap = (resumo.Latest.ManagedHeapMb - resumo.Baseline.ManagedHeapMb);
            resumoTexto = $"Desde a inicialização: {projecao.callsSinceStartup} chamadas, " +
                $"Private Bytes {(crescPriv >= 0 ? "+" : "")}{crescPriv:0} MB, " +
                $"Managed Heap {(deltaHeap >= 0 ? "+" : "")}{deltaHeap:0} MB, " +
                $"Handles {(crescHandles >= 0 ? "+" : "")}{crescHandles}.";
        }

        return Results.Ok(new
        {
            installation = projecao,
            resumo = resumoTexto,
            incidentesRecentes = incidentes,
            diagnosticos = findings,
        });
    }

    // ── GET /api/diagnostics/installations/{id}/timeseries?periodo=1h|4h|hoje|24h|7d ──

    private static async Task<IResult> GetTimeseries(string id, string? periodo, WavenDbContext db)
    {
        var (desde, maxPontos) = periodo switch
        {
            "1h"    => (DateTime.UtcNow.AddHours(-1), 300),
            "4h"     => (DateTime.UtcNow.AddHours(-4), 300),
            "hoje"   => (DateTime.UtcNow.Date, 400),
            "7d"     => (DateTime.UtcNow.AddDays(-7), 500),
            _        => (DateTime.UtcNow.AddHours(-24), 400), // default: 24h
        };

        var pontos = await db.DiagnosticSnapshots
            .Where(s => s.InstallationId == id && s.TimestampUtc >= desde)
            .OrderBy(s => s.TimestampUtc)
            .ToListAsync();

        if (pontos.Count <= maxPontos)
            return Results.Ok(new { agregado = false, pontos });

        // Downsample preservando picos (item 6 do pedido): agrupa em N baldes e mantém,
        // de cada balde, o ponto com o MAIOR Working Set — nunca a média, para não
        // "engolir" um pico real de memória.
        var baldeTamanho = (int)Math.Ceiling(pontos.Count / (double)maxPontos);
        var agregados = pontos
            .Select((s, idx) => new { s, bucket = idx / baldeTamanho })
            .GroupBy(x => x.bucket)
            .Select(g => g.OrderByDescending(x => x.s.WorkingSetMb).First().s)
            .OrderBy(s => s.TimestampUtc)
            .ToList();

        return Results.Ok(new { agregado = true, pontosOriginais = pontos.Count, pontos = agregados });
    }

    // ── GET /api/diagnostics/installations/{id}/snapshots?horas=24 (legado) ───

    private static async Task<IResult> GetSnapshots(string id, int? horas, WavenDbContext db)
    {
        var desde = DateTime.UtcNow.AddHours(-(horas is > 0 and <= 168 ? horas.Value : 24));
        var lista = await db.DiagnosticSnapshots
            .Where(s => s.InstallationId == id && s.TimestampUtc >= desde)
            .OrderBy(s => s.TimestampUtc)
            .ToListAsync();
        return Results.Ok(lista);
    }

    // ── GET /api/diagnostics/compare?a=<id>&b=<id> ────────────────────────────

    private static async Task<IResult> CompareInstallations(string a, string b, WavenDbContext db)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return Results.BadRequest(new { error = "parâmetros a e b (installationId) são obrigatórios" });

        var instA = await db.DiagnosticInstallations.FindAsync(a);
        var instB = await db.DiagnosticInstallations.FindAsync(b);
        if (instA == null || instB == null) return Results.NotFound();

        var resumoA = await CarregarResumoSessaoAsync(db, a);
        var resumoB = await CarregarResumoSessaoAsync(db, b);
        var agora = DateTime.UtcNow;

        object Projetar(DiagnosticInstallation inst, ResumoSessao? r)
            => ProjetarResumo(inst, r, 0, agora - inst.LastSeenUtc <= JanelaOnline);

        return Results.Ok(new { a = Projetar(instA, resumoA), b = Projetar(instB, resumoB) });
    }

    // ── GET /api/diagnostics/incidents?dias=1&evento=... ──────────────────────

    private static async Task<IResult> GetIncidents(int? dias, string? evento, WavenDbContext db)
    {
        var desde = DateTime.UtcNow.AddDays(-(dias is > 0 and <= 90 ? dias.Value : 7));
        var q = db.DiagnosticIncidents.Where(i => i.TimestampUtc >= desde);
        if (!string.IsNullOrWhiteSpace(evento))
            q = q.Where(i => i.Evento == evento);
        var lista = await q.OrderByDescending(i => i.TimestampUtc).Take(500).ToListAsync();
        return Results.Ok(lista);
    }

    // ── GET /api/diagnostics/findings?status=&severidade= ─────────────────────

    private static async Task<IResult> GetFindings(string? status, string? severidade, bool? apenasAbertos, WavenDbContext db)
    {
        var q = db.DiagnosticFindings.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(f => f.Status == status);
        if (!string.IsNullOrWhiteSpace(severidade)) q = q.Where(f => f.Severity == severidade);
        if (apenasAbertos == true) q = q.Where(f => f.NormalizedUtc == null);

        var lista = await q.OrderByDescending(f => f.UpdatedUtc).Take(200).ToListAsync();
        return Results.Ok(lista);
    }

    // ── GET /api/diagnostics/findings/{id} — com janela de snapshots ao redor do pico
    //    e "resumo para investigação de código" (item 10/25 do pedido original) ─────

    private static async Task<IResult> GetFindingDetail(long id, WavenDbContext db)
    {
        var finding = await db.DiagnosticFindings.FindAsync(id);
        if (finding == null) return Results.NotFound();
        return Results.Ok(await MontarDetalheFindingAsync(finding, db));
    }

    // ── GET /api/diagnostics/installations/{id}/investigation — atalho para o
    //    finding aberto (ou mais recente) desta instalação, mesmo formato acima ──

    private static async Task<IResult> GetInstallationInvestigation(string id, WavenDbContext db)
    {
        var finding = await db.DiagnosticFindings
            .Where(f => f.InstallationId == id)
            .OrderByDescending(f => f.NormalizedUtc == null) // abertos primeiro
            .ThenByDescending(f => f.UpdatedUtc)
            .FirstOrDefaultAsync();
        if (finding == null)
            return Results.Ok(new { mensagem = "Nenhum diagnóstico (finding) registrado para esta instalação ainda.", finding = (object?)null });
        return Results.Ok(await MontarDetalheFindingAsync(finding, db));
    }

    private static async Task<object> MontarDetalheFindingAsync(DiagnosticFinding finding, WavenDbContext db)
    {
        List<DiagnosticSnapshot> janela = new();
        if (finding.PeakSnapshotId.HasValue)
        {
            var pico = await db.DiagnosticSnapshots.FindAsync(finding.PeakSnapshotId.Value);
            if (pico != null)
            {
                var antes = await db.DiagnosticSnapshots
                    .Where(s => s.InstallationId == finding.InstallationId && s.TimestampUtc < pico.TimestampUtc)
                    .OrderByDescending(s => s.TimestampUtc).Take(10).ToListAsync();
                var depois = await db.DiagnosticSnapshots
                    .Where(s => s.InstallationId == finding.InstallationId && s.TimestampUtc > pico.TimestampUtc)
                    .OrderBy(s => s.TimestampUtc).Take(10).ToListAsync();
                antes.Reverse();
                janela.AddRange(antes);
                janela.Add(pico);
                janela.AddRange(depois);
            }
        }

        // Sessão inteira (início → pico ou agora) para as análises de tendência/frequência —
        // a janela de ±10 acima é só para inspeção visual pontual do pico.
        var fimSessao = finding.NormalizedUtc ?? finding.PeakUtc ?? DateTime.UtcNow;
        var sessaoCompleta = await db.DiagnosticSnapshots
            .Where(s => s.InstallationId == finding.InstallationId && s.TimestampUtc >= finding.StartedUtc && s.TimestampUtc <= fimSessao)
            .OrderBy(s => s.TimestampUtc)
            .ToListAsync();

        return new
        {
            finding,
            janelaDoIncidente = janela,
            resumoInvestigacao = MontarResumoInvestigacao(finding, sessaoCompleta),
        };
    }

    // Traduz os números crus em algo pronto pra colar numa investigação de código — item 10/25
    // do pedido: frequência de lastOperation na janela de crescimento, tendência do LogQueue,
    // e um texto de interpretação. Tudo aqui é inferência, nunca causa confirmada.
    private static object MontarResumoInvestigacao(DiagnosticFinding f, List<DiagnosticSnapshot> sessao)
    {
        var frequenciaOperacoes = sessao
            .GroupBy(s => string.IsNullOrWhiteSpace(s.LastOperation) ? "IDLE" : s.LastOperation)
            .Select(g => new { operacao = g.Key, ocorrencias = g.Count() })
            .OrderByDescending(x => x.ocorrencias)
            .ToList();

        string tendenciaLogQueue = "sem dados suficientes";
        double logQueueCorrelacaoComHeap = 0;
        if (sessao.Count >= 4)
        {
            var terco = Math.Max(1, sessao.Count / 3);
            var inicio = sessao.Take(terco).Average(s => s.LogQueueCount);
            var fim = sessao.TakeLast(terco).Average(s => s.LogQueueCount);
            tendenciaLogQueue = fim > inicio * 1.2 ? "crescendo"
                : fim < inicio * 0.8 ? "diminuindo"
                : "estável";

            // Correlação simples (Pearson) entre LogQueueCount e ManagedHeapMb na sessão —
            // quanto mais perto de 1, mais os dois sobem/descem juntos.
            logQueueCorrelacaoComHeap = Math.Round(CorrelacaoPearson(
                sessao.Select(s => (double)s.LogQueueCount).ToList(),
                sessao.Select(s => s.ManagedHeapMb).ToList()), 2);
        }

        var deltaPrivate = f.PrivatePeakMb - f.PrivateStartMb;
        var deltaHeap = f.ManagedHeapPeakMb - f.ManagedHeapStartMb;
        var participacaoHeapNoPrivate = deltaPrivate > 0 ? Math.Round(deltaHeap / deltaPrivate * 100, 0) : (double?)null;

        var tags = (f.Classifications ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var proximosPassos = new List<string>();
        if (tags.Contains("MANAGED_GROWTH_SUSPECT") && tags.Contains("LOG_QUEUE_GROWTH_SUSPECT"))
        {
            proximosPassos.Add("Verificar LogHelper._queue.Count ao vivo nesta instalação (campo logQueueAtual já reflete isso).");
            proximosPassos.Add("Auditar IssabelCdrService/HistoricoStorageService.MesclarCdr por Log()/LogDetalhado() não condicionados que possam rodar por item de um dataset de CDR grande.");
            proximosPassos.Add("Confirmar se 'Logs detalhados' (LogDetailedEnabled) está ligado nesta instalação — reativaria o log verboso por linha já documentado como problema em v2.3.6/v2.4.0.");
            proximosPassos.Add("Verificar tamanho da tabela/consulta de CDR desta instalação — volume de dados anormal explicaria produção de log desproporcional mesmo com 0 chamadas.");
        }
        if (tags.Contains("NATIVE_GROWTH_SUSPECT"))
            proximosPassos.Add("Investigar objetos nativos: SIPSorcery/NAudio/WindowsAudioEndPoint, buffers RTP/áudio não liberados.");
        if (tags.Contains("HANDLE_GROWTH_SUSPECT"))
            proximosPassos.Add("Levantar handle leak (arquivo/Win32) correlacionado ao ciclo de chamadas.");
        if (proximosPassos.Count == 0)
            proximosPassos.Add("Aguardar mais snapshots — dados atuais ainda não sugerem um componente específico.");

        string interpretacao;
        if (participacaoHeapNoPrivate is double pct)
        {
            interpretacao = pct >= 70
                ? $"Managed Heap explica ~{pct:0}% do crescimento de Private Bytes — crescimento predominantemente GERENCIADO."
                : pct <= 25
                    ? $"Managed Heap explica só ~{pct:0}% do crescimento de Private Bytes — crescimento predominantemente NATIVO (fora do heap .NET)."
                    : $"Managed Heap explica ~{pct:0}% do crescimento de Private Bytes — crescimento misto, sem componente único dominante.";
        }
        else interpretacao = "Dados insuficientes para separar crescimento gerenciado de nativo ainda.";

        return new
        {
            frequenciaLastOperation = frequenciaOperacoes,
            tendenciaLogQueue,
            logQueueCorrelacaoComManagedHeap = logQueueCorrelacaoComHeap,
            participacaoHeapNoCrescimentoPrivatePercent = participacaoHeapNoPrivate,
            interpretacaoPreliminar = interpretacao,
            proximaInvestigacaoRecomendada = proximosPassos,
            avisoImportante = "Isto é inferência estatística sobre telemetria — NÃO é causa confirmada. Confirmação exige leitura do código-fonte.",
        };
    }

    private static double CorrelacaoPearson(List<double> x, List<double> y)
    {
        if (x.Count != y.Count || x.Count < 2) return 0;
        var n = x.Count;
        var mx = x.Average(); var my = y.Average();
        double cov = 0, vx = 0, vy = 0;
        for (int i = 0; i < n; i++)
        {
            var dx = x[i] - mx; var dy = y[i] - my;
            cov += dx * dy; vx += dx * dx; vy += dy * dy;
        }
        if (vx <= 0 || vy <= 0) return 0;
        return cov / Math.Sqrt(vx * vy);
    }

    // ── GET /api/diagnostics/summary — visão administrativa da frota ──────────

    private static async Task<IResult> GetFleetSummary(WavenDbContext db)
    {
        var agora = DateTime.UtcNow;
        var instalacoes = await db.DiagnosticInstallations.ToListAsync();
        var online = instalacoes.Where(i => agora - i.LastSeenUtc <= JanelaOnline).ToList();
        var offline = instalacoes.Except(online).ToList();

        var incidentes24h = await db.DiagnosticIncidents
            .Where(i => i.TimestampUtc >= agora.AddHours(-24))
            .OrderByDescending(i => i.TimestampUtc)
            .Take(100)
            .ToListAsync();

        var findingsAbertos = await db.DiagnosticFindings
            .Where(f => f.NormalizedUtc == null)
            .OrderByDescending(f => f.Severity == "EMERGENCIA" ? 3 : f.Severity == "CRITICO" ? 2 : 1)
            .ThenByDescending(f => f.UpdatedUtc)
            .ToListAsync();

        var findingsAguardando = findingsAbertos
            .Where(f => f.Status is DiagnosticInvestigationStatus.EvidenciaSuficiente or DiagnosticInvestigationStatus.CausaProvavel)
            .ToList();

        object[] Suspeitas(string tag) => findingsAbertos
            .Where(f => f.Classifications.Contains(tag))
            .Select(f => (object)new { f.Id, f.InstallationId, f.Ramal, f.Classifications, f.Status })
            .ToArray();

        return Results.Ok(new
        {
            geradoEmUtc = agora,
            instalacoesOnline = online.Count,
            instalacoesOffline = offline.Count,
            versoes = instalacoes.GroupBy(i => i.AppVersion).Select(g => new { versao = g.Key, quantidade = g.Count() }),
            alertasAtivos = findingsAbertos.Select(f => new { f.Id, f.InstallationId, f.Ramal, f.Severity, f.Status, f.Classifications, f.ProbableCause }),
            incidentesRecentes = incidentes24h,
            topRam = instalacoes.OrderByDescending(i => i.UltimoWorkingSetMb).Take(5)
                .Select(i => new { i.Id, i.Ramal, i.UltimoWorkingSetMb }),
            topPicoRam = instalacoes.OrderByDescending(i => i.PicoWorkingSetMb).Take(5)
                .Select(i => new { i.Id, i.Ramal, i.PicoWorkingSetMb }),
            topChamadas = instalacoes.OrderByDescending(i => i.UltimoCallsSinceStartup).Take(5)
                .Select(i => new { i.Id, i.Ramal, i.UltimoCallsSinceStartup }),
            suspeitasManaged = Suspeitas("MANAGED_GROWTH_SUSPECT"),
            suspeitasNative = Suspeitas("NATIVE_GROWTH_SUSPECT"),
            suspeitasHandle = Suspeitas("HANDLE_GROWTH_SUSPECT"),
            suspeitasThread = Suspeitas("THREAD_GROWTH_SUSPECT"),
            suspeitasGdiUser = findingsAbertos.Where(f => f.Classifications.Contains("GDI_GROWTH_SUSPECT") || f.Classifications.Contains("USER_OBJECT_GROWTH_SUSPECT"))
                .Select(f => (object)new { f.Id, f.InstallationId, f.Ramal, f.Classifications }).ToArray(),
            suspeitasIo = Suspeitas("HIGH_DISK_IO"),
            diagnosticosAguardandoInvestigacao = findingsAguardando.Select(f => new { f.Id, f.InstallationId, f.Ramal, f.Status, f.ProbableCause }),
        });
    }
}
