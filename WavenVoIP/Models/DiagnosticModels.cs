using System;

namespace WavenVoIP.Models
{
    // ── Payload enviado a cada heartbeat (POST /api/diagnostics/heartbeat) — v2.4.2 ──
    // Estrutura FLAT (sem objetos aninhados) por contrato explícito do pedido de
    // telemetria de produção. Apenas métricas de processo/saúde — nunca dados de
    // negócio (números, nomes, conteúdo de mensagens, credenciais). Ver
    // DiagnosticTelemetryService para sanitização e origem de cada campo.
    public sealed class DiagnosticHeartbeat
    {
        // Identidade
        public string InstallationId { get; set; } = "";
        public string MachineId      { get; set; } = ""; // hash one-way, nunca o hostname
        public string MachineAlias   { get; set; } = "";
        public string Ramal          { get; set; } = "";
        public string AppVersion     { get; set; } = "";
        public DateTime TimestampUtc { get; set; }
        public long UptimeSeconds    { get; set; }

        // Chamadas
        public long CallsSinceStartup { get; set; }
        public long CallsIncoming     { get; set; } // ofertadas (ring) — atende ou não
        public long CallsOutgoing     { get; set; } // discadas por este ramal
        public long CallsConnected    { get; set; } // efetivamente conectadas (qualquer direção)
        public long CallsEnded        { get; set; }

        // Memória
        public double WorkingSetMb    { get; set; }
        public double PrivateBytesMb  { get; set; }
        public double ManagedHeapMb   { get; set; } // GC.GetGCMemoryInfo().HeapSizeBytes
        public double GcTotalMemoryMb { get; set; } // GC.GetTotalMemory(false) — sem forçar coleta
        public double Gen0Mb          { get; set; }
        public double Gen1Mb          { get; set; }
        public double Gen2Mb          { get; set; }
        public double LohMb           { get; set; }
        public double PohMb           { get; set; }
        public long   Gen0Collections { get; set; } // GC.CollectionCount(0)
        public long   Gen1Collections { get; set; }
        public long   Gen2Collections { get; set; }

        // Processo
        public int HandleCount { get; set; }
        public int ThreadCount { get; set; }
        public int GdiObjectCount  { get; set; }
        public int UserObjectCount { get; set; }

        // CPU
        public double CpuPercent           { get; set; }
        public double CpuAvgRecentPercent  { get; set; }
        public double CpuPeakRecentPercent { get; set; }

        // I/O
        public double ReadBytesPerSecond   { get; set; }
        public double WriteBytesPerSecond  { get; set; }
        public double ReadOpsPerSecond     { get; set; }
        public double WriteOpsPerSecond    { get; set; }

        // Waven — observação, sem abrir/reparsear arquivos a cada heartbeat
        public int    LogQueueCount         { get; set; }
        public long   HistoricoFileSizeBytes{ get; set; }
        public int    HistoricoItens        { get; set; }
        public double LogsTotalKb           { get; set; }
        public bool   UsaCdr                { get; set; }
        public bool   UsaAmi                { get; set; }
        public bool   UsaGoogle             { get; set; }

        // Áudio (preserva o hotfix v2.4.1)
        public long AudioNativeDisposeOkCount   { get; set; }
        public long AudioNativeDisposeFailCount { get; set; }
        public long AudioDeviceRecoveryCount    { get; set; }

        // UI / última operação conhecida
        public long   UiFreezeCount           { get; set; }
        public double UiUltimaFreezeDuracaoMs { get; set; }
        public string LastOperation           { get; set; } = "IDLE"; // IDLE/DIALING/CALL_CONNECTED/CALL_ENDING/CDR_SYNC/GOOGLE_SYNC/AMI_SYNC/CONTACT_SYNC

        // Exceções
        public long UnobservedExceptionCount { get; set; }
        public long CriticalExceptionCount   { get; set; }
    }

    // ── Payload enviado imediatamente para incidentes (POST /api/diagnostics/event) ─
    public sealed class DiagnosticEvent
    {
        public string InstallationId { get; set; } = "";
        public string MachineId      { get; set; } = "";
        public string AppVersion     { get; set; } = "";
        public string Ramal          { get; set; } = "";
        public DateTime TimestampUtc { get; set; }

        public string  Evento    { get; set; } = ""; // MEMORY_WARNING/CRITICAL/EMERGENCY, UI_FREEZE_DETECTED, ...
        public double? DuracaoMs { get; set; }
        public string  Detalhe   { get; set; } = ""; // curto, sanitizado — nunca stack trace/mensagem crua

        public double WorkingSetMb   { get; set; }
        public double PrivateBytesMb { get; set; }
        public int    HandleCount    { get; set; }
        public int    ThreadCount    { get; set; }
        public double CpuPercent     { get; set; }
        public long   CallsSinceStartup { get; set; }
        public string LastOperation  { get; set; } = "IDLE";
    }

    // Fila offline local, tamanho máximo — nunca cresce indefinidamente
    // (ver DiagnosticTelemetryService.MaxOfflineQueueItems).
    public sealed class DiagnosticOfflineItem
    {
        public string Kind { get; set; } = ""; // "heartbeat" | "event"
        public string Json { get; set; } = "";
        public DateTime EnfileiradoEmUtc { get; set; }
    }
}
