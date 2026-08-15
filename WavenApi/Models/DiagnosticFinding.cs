namespace WavenApi.Models;

// Diagnóstico PERSISTENTE — não desaparece quando a RAM volta ao normal, o GC atua,
// o WavenVoIP/Windows reinicia ou o usuário fecha o programa (ver DiagnosticAnalysisService).
// Criado/atualizado automaticamente pelo motor de correlação; classificação e status
// (até CAUSA_PROVAVEL) são automáticos — CAUSA_CONFIRMADA e correção nunca são
// preenchidos automaticamente, exigem investigação humana/administrativa do código.
public class DiagnosticFinding
{
    public long   Id             { get; set; }
    public string InstallationId { get; set; } = string.Empty;
    public string Ramal          { get; set; } = string.Empty;
    public string AppVersion     { get; set; } = string.Empty;

    public DateTime  StartedUtc     { get; set; }
    public DateTime? PeakUtc        { get; set; }
    public DateTime? NormalizedUtc  { get; set; } // preenchido quando volta perto do normal; a linha NUNCA é apagada
    public DateTime  UpdatedUtc     { get; set; }

    public long UptimeSecondsAtStart { get; set; }
    public long CallsAtStart         { get; set; }
    public long CallsAtPeak          { get; set; }

    public double WorkingSetStartMb { get; set; }
    public double WorkingSetPeakMb  { get; set; }
    public double PrivateStartMb    { get; set; }
    public double PrivatePeakMb     { get; set; }
    public double ManagedHeapStartMb { get; set; }
    public double ManagedHeapPeakMb  { get; set; }
    public double GcTotalMemoryPeakMb { get; set; }
    public double LohPeakMb { get; set; }
    public double PohPeakMb { get; set; }
    public int    HandlesStart { get; set; }
    public int    HandlesPeak  { get; set; }
    public int    ThreadsPeak  { get; set; }
    public int    GdiPeak      { get; set; }
    public int    UserPeak     { get; set; }
    public double CpuAtPeak    { get; set; }
    public double ReadMBsAtPeak  { get; set; }
    public double WriteMBsAtPeak { get; set; }
    public int    LogQueuePeak { get; set; }
    public string LastOperationAtPeak { get; set; } = string.Empty;

    // CSV de tags — ex.: "NATIVE_GROWTH_SUSPECT,HANDLE_GROWTH_SUSPECT". Ver seção 10 do pedido.
    public string Classifications { get; set; } = string.Empty;
    public string Severity        { get; set; } = "ATENCAO"; // ATENCAO/CRITICO/EMERGENCIA
    public string Status          { get; set; } = "NOVO";    // ver DiagnosticInvestigationStatus

    public string Hypothesis         { get; set; } = string.Empty;
    public string Evidence           { get; set; } = string.Empty;
    public string Contradictions     { get; set; } = string.Empty;
    public string SuspectComponents  { get; set; } = string.Empty;
    public string ProbableCause      { get; set; } = string.Empty;
    public string ConfirmedCause     { get; set; } = string.Empty; // nunca preenchido automaticamente
    public string RecommendedFix     { get; set; } = string.Empty; // nunca preenchido automaticamente
    public string FixVersion         { get; set; } = string.Empty; // nunca preenchido automaticamente

    public long? FirstSnapshotId { get; set; }
    public long? PeakSnapshotId  { get; set; }
}

// Estados possíveis de Status — ladder unidirecional (nunca regride).
// Do NOVO até CAUSA_PROVAVEL: automático, pelo motor de correlação.
// De CAUSA_CONFIRMADA em diante: só manual/administrativo (não implementado nesta fase —
// não há endpoint de escrita, ver item 27/30 do pedido).
public static class DiagnosticInvestigationStatus
{
    public const string Novo               = "NOVO";
    public const string EmObservacao       = "EM_OBSERVACAO";
    public const string EvidenciaSuficiente = "EVIDENCIA_SUFICIENTE";
    public const string CausaProvavel      = "CAUSA_PROVAVEL";
    public const string CausaConfirmada    = "CAUSA_CONFIRMADA";
    public const string CorrecaoProposta   = "CORRECAO_PROPOSTA";
    public const string Corrigido          = "CORRIGIDO";
    public const string MonitorandoCorrecao = "MONITORANDO_CORRECAO";
    public const string Encerrado          = "ENCERRADO";
}
