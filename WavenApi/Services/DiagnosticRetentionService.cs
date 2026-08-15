using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WavenApi.Data;

namespace WavenApi.Services;

// Apaga snapshots/incidents antigos periodicamente — retenção configurável
// (WavenApi:Diagnostics:RetencaoSnapshotsDias / RetencaoIncidentsDias). Roda uma vez
// pouco depois do start e depois a cada 6h; não guarda telemetria "para sempre".
public sealed class DiagnosticRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<WavenApiOptions> options,
    ILogger<DiagnosticRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); // deixa a API subir antes

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await LimparAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "DIAGNOSTIC_RETENTION_ERROR"); }

            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task LimparAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WavenDbContext>();

        var opts = options.Value.Diagnostics;
        var corteSnapshots = DateTime.UtcNow.AddDays(-opts.RetencaoSnapshotsDias);
        var corteIncidents = DateTime.UtcNow.AddDays(-opts.RetencaoIncidentsDias);

        var snapshotsApagados = await db.DiagnosticSnapshots
            .Where(s => s.TimestampUtc < corteSnapshots)
            .ExecuteDeleteAsync(ct);

        var incidentsApagados = await db.DiagnosticIncidents
            .Where(i => i.TimestampUtc < corteIncidents)
            .ExecuteDeleteAsync(ct);

        if (snapshotsApagados > 0 || incidentsApagados > 0)
            logger.LogInformation("DIAGNOSTIC_RETENTION_CLEANUP | snapshots={S} incidents={I}",
                snapshotsApagados, incidentsApagados);
    }
}
