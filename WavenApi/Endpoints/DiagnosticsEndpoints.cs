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

        // Leitura — alimenta o painel administrativo.
        g.MapGet("/installations",                GetInstallations);
        g.MapGet("/installations/{id}/snapshots",  GetSnapshots);
        g.MapGet("/incidents",                     GetIncidents);
    }

    // Rate limit server-side por installationId — rede de segurança final,
    // independente do rate limit já feito no cliente (defende contra cliente com bug
    // ou versão antiga sem o limite). Estado em memória: aceitável para instância
    // única (deploy systemd atual); se a API rodar com múltiplas instâncias no
    // futuro, precisa virar uma tabela/Redis compartilhado.
    private static readonly ConcurrentDictionary<string, DateTime> _ultimoHeartbeat = new();
    private static readonly ConcurrentDictionary<string, DateTime> _ultimoIncident   = new();

    // ── POST /api/diagnostics/heartbeat ───────────────────────────────────────

    private static async Task<IResult> PostHeartbeat(
        DiagnosticHeartbeatRequest req, WavenDbContext db, IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Diagnostics");

        if (string.IsNullOrWhiteSpace(req.InstallationId))
            return Results.BadRequest(new { error = "installationId obrigatório" });

        var minIntervalo = TimeSpan.FromSeconds(opts.Value.Diagnostics.MinIntervaloHeartbeatSegundos);
        if (_ultimoHeartbeat.TryGetValue(req.InstallationId, out var ultimo) && DateTime.UtcNow - ultimo < minIntervalo)
        {
            // Não é erro — apenas ignorado. Responde 200 para o cliente remover da fila
            // offline: um cliente com bug mandando heartbeats rápido demais não deve
            // entrar em retry-loop por isto.
            return Results.Ok(new { ok = true, ignored = true, reason = "rate_limited" });
        }
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

    // ── GET /api/diagnostics/installations ────────────────────────────────────

    private static async Task<IResult> GetInstallations(WavenDbContext db)
    {
        var lista = await db.DiagnosticInstallations
            .OrderByDescending(i => i.UltimoWorkingSetMb)
            .ToListAsync();
        return Results.Ok(lista);
    }

    // ── GET /api/diagnostics/installations/{id}/snapshots?horas=6 ────────────

    private static async Task<IResult> GetSnapshots(string id, int? horas, WavenDbContext db)
    {
        var desde = DateTime.UtcNow.AddHours(-(horas is > 0 and <= 168 ? horas.Value : 24));
        var lista = await db.DiagnosticSnapshots
            .Where(s => s.InstallationId == id && s.TimestampUtc >= desde)
            .OrderBy(s => s.TimestampUtc)
            .ToListAsync();
        return Results.Ok(lista);
    }

    // ── GET /api/diagnostics/incidents?dias=1&evento=MEMORY_CRITICAL ─────────

    private static async Task<IResult> GetIncidents(int? dias, string? evento, WavenDbContext db)
    {
        var desde = DateTime.UtcNow.AddDays(-(dias is > 0 and <= 90 ? dias.Value : 7));
        var q = db.DiagnosticIncidents.Where(i => i.TimestampUtc >= desde);
        if (!string.IsNullOrWhiteSpace(evento))
            q = q.Where(i => i.Evento == evento);
        var lista = await q.OrderByDescending(i => i.TimestampUtc).Take(500).ToListAsync();
        return Results.Ok(lista);
    }
}
