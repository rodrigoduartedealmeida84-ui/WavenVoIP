namespace WavenApi.Models;

// ── Entidades EF — v2.4.2 (contrato flat) ───────────────────────────────────

// Uma linha por instalação (installationId). Campos "Ultimo*" são denormalizados a
// partir do heartbeat mais recente — permitem listar/ordenar a frota inteira
// (GET /api/diagnostics/installations) sem varrer a tabela de snapshots.
public class DiagnosticInstallation
{
    public string Id            { get; set; } = string.Empty; // installationId (GUID do cliente)
    public string MachineId     { get; set; } = string.Empty;
    public string MachineAlias  { get; set; } = string.Empty;
    public string Ramal         { get; set; } = string.Empty;
    public string AppVersion    { get; set; } = string.Empty;
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc  { get; set; }

    public double UltimoWorkingSetMb      { get; set; }
    public double PicoWorkingSetMb        { get; set; }
    public double UltimoPrivateBytesMb    { get; set; }
    public double UltimoManagedHeapMb     { get; set; }
    public int    UltimoHandleCount       { get; set; }
    public int    UltimoThreadCount       { get; set; }
    public double UltimoCpuPercent        { get; set; }
    public long   UltimoCallsSinceStartup { get; set; }
    public string UltimoLastOperation     { get; set; } = "IDLE";
}

public class DiagnosticSnapshot
{
    public long   Id             { get; set; }
    public string InstallationId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public long   UptimeSeconds  { get; set; }

    public long CallsSinceStartup { get; set; }
    public long CallsIncoming     { get; set; }
    public long CallsOutgoing     { get; set; }
    public long CallsConnected    { get; set; }
    public long CallsEnded        { get; set; }

    public double WorkingSetMb    { get; set; }
    public double PrivateBytesMb  { get; set; }
    public double ManagedHeapMb   { get; set; }
    public double GcTotalMemoryMb { get; set; }
    public double Gen0Mb { get; set; }
    public double Gen1Mb { get; set; }
    public double Gen2Mb { get; set; }
    public double LohMb  { get; set; }
    public double PohMb  { get; set; }
    public long Gen0Collections { get; set; }
    public long Gen1Collections { get; set; }
    public long Gen2Collections { get; set; }

    public int HandleCount { get; set; }
    public int ThreadCount { get; set; }
    public int GdiObjectCount  { get; set; }
    public int UserObjectCount { get; set; }

    public double CpuPercent           { get; set; }
    public double CpuAvgRecentPercent  { get; set; }
    public double CpuPeakRecentPercent { get; set; }

    public double ReadBytesPerSecond  { get; set; }
    public double WriteBytesPerSecond { get; set; }
    public double ReadOpsPerSecond    { get; set; }
    public double WriteOpsPerSecond   { get; set; }

    public int    LogQueueCount          { get; set; }
    public long   HistoricoFileSizeBytes { get; set; }
    public int    HistoricoItens         { get; set; }
    public double LogsTotalKb            { get; set; }

    public long AudioNativeDisposeOkCount   { get; set; }
    public long AudioNativeDisposeFailCount { get; set; }
    public long AudioDeviceRecoveryCount    { get; set; }

    public long   UiFreezeCount           { get; set; }
    public double UiUltimaFreezeDuracaoMs { get; set; }
    public string LastOperation           { get; set; } = "IDLE";

    public long UnobservedExceptionCount { get; set; }
    public long CriticalExceptionCount   { get; set; }
}

public class DiagnosticIncident
{
    public long   Id             { get; set; }
    public string InstallationId { get; set; } = string.Empty;
    public string Ramal          { get; set; } = string.Empty;
    public string AppVersion     { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }

    public string  Evento    { get; set; } = string.Empty;
    public double? DuracaoMs { get; set; }
    public string  Detalhe   { get; set; } = string.Empty;

    public double WorkingSetMb   { get; set; }
    public double PrivateBytesMb { get; set; }
    public int    HandleCount    { get; set; }
    public int    ThreadCount    { get; set; }
    public double CpuPercent     { get; set; }
    public long   CallsSinceStartup { get; set; }
    public string LastOperation  { get; set; } = "IDLE";
}

// ── DTOs de request (nomes em PascalCase — binding do minimal API é
// case-insensitive, então o cliente envia PascalCase/camelCase indiferentemente) ──

public record DiagnosticHeartbeatRequest(
    string InstallationId, string MachineId, string? MachineAlias, string Ramal, string AppVersion,
    DateTime TimestampUtc, long UptimeSeconds,
    long CallsSinceStartup, long CallsIncoming, long CallsOutgoing, long CallsConnected, long CallsEnded,
    double WorkingSetMb, double PrivateBytesMb, double ManagedHeapMb, double GcTotalMemoryMb,
    double Gen0Mb, double Gen1Mb, double Gen2Mb, double LohMb, double PohMb,
    long Gen0Collections, long Gen1Collections, long Gen2Collections,
    int HandleCount, int ThreadCount, int GdiObjectCount, int UserObjectCount,
    double CpuPercent, double CpuAvgRecentPercent, double CpuPeakRecentPercent,
    double ReadBytesPerSecond, double WriteBytesPerSecond, double ReadOpsPerSecond, double WriteOpsPerSecond,
    int LogQueueCount, long HistoricoFileSizeBytes, int HistoricoItens, double LogsTotalKb,
    bool UsaCdr, bool UsaAmi, bool UsaGoogle,
    long AudioNativeDisposeOkCount, long AudioNativeDisposeFailCount, long AudioDeviceRecoveryCount,
    long UiFreezeCount, double UiUltimaFreezeDuracaoMs, string? LastOperation,
    long UnobservedExceptionCount, long CriticalExceptionCount
);

public record DiagnosticEventRequest(
    string InstallationId, string MachineId, string AppVersion, string Ramal, DateTime TimestampUtc,
    string Evento, double? DuracaoMs, string? Detalhe,
    double WorkingSetMb, double PrivateBytesMb, int HandleCount, int ThreadCount, double CpuPercent,
    long CallsSinceStartup, string? LastOperation
);
