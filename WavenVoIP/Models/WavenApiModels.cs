using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WavenVoIP.Models
{
    // ── Resposta de contato vinda da API ──────────────────────────────────────

    public class WavenApiContact
    {
        [JsonPropertyName("id")]                 public string    Id                 { get; set; } = string.Empty;
        [JsonPropertyName("nome")]               public string    Nome               { get; set; } = string.Empty;
        [JsonPropertyName("numero")]             public string    Numero             { get; set; } = string.Empty;
        [JsonPropertyName("numeroNormalizado")]  public string    NumeroNormalizado  { get; set; } = string.Empty;
        [JsonPropertyName("empresa")]            public string?   Empresa            { get; set; }
        [JsonPropertyName("observacao")]         public string?   Observacao         { get; set; }
        [JsonPropertyName("isFavorito")]         public bool      IsFavorito         { get; set; }
        [JsonPropertyName("tipoFavorito")]       public string?   TipoFavorito       { get; set; }
        [JsonPropertyName("criadoPor")]          public string    CriadoPor          { get; set; } = string.Empty;
        [JsonPropertyName("atualizadoPor")]      public string    AtualizadoPor      { get; set; } = string.Empty;
        [JsonPropertyName("criadoEm")]           public DateTime  CriadoEm           { get; set; }
        [JsonPropertyName("atualizadoEm")]       public DateTime  AtualizadoEm       { get; set; }
        [JsonPropertyName("excluido")]           public bool      Excluido           { get; set; }
        [JsonPropertyName("excluidoEm")]         public DateTime? ExcluidoEm         { get; set; }
    }

    // ── Resposta do endpoint /sync ────────────────────────────────────────────

    public class WavenApiSyncResponse
    {
        [JsonPropertyName("serverTime")]      public DateTime              ServerTime      { get; set; }
        [JsonPropertyName("contacts")]        public List<WavenApiContact> Contacts        { get; set; } = new();
        [JsonPropertyName("favoritosAtuais")] public List<string>          FavoritosAtuais { get; set; } = new();
    }

    // ── Resposta do /health ───────────────────────────────────────────────────

    public class WavenApiHealth
    {
        [JsonPropertyName("status")]  public string Status  { get; set; } = string.Empty;
        [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    }

    // ── Estado local de sync (persistido em api-sync.json) ───────────────────

    public class WavenApiSyncState
    {
        [JsonPropertyName("lastSyncUtc")]         public DateTime? LastSyncUtc           { get; set; }
        [JsonPropertyName("migracaoConcluida")]   public bool      MigracaoConcluida     { get; set; }
        [JsonPropertyName("seedGlobaisConcluido")] public bool     SeedGlobaisConcluido  { get; set; }
    }

    // ── Fase 2: CDR via API ───────────────────────────────────────────────────

    public class ApiCdrRow
    {
        [JsonPropertyName("callDate")]      public DateTime CallDate      { get; set; }
        [JsonPropertyName("src")]           public string   Src           { get; set; } = string.Empty;
        [JsonPropertyName("dst")]           public string   Dst           { get; set; } = string.Empty;
        [JsonPropertyName("channel")]       public string   Channel       { get; set; } = string.Empty;
        [JsonPropertyName("dstChannel")]    public string   DstChannel    { get; set; } = string.Empty;
        [JsonPropertyName("lastApp")]       public string   LastApp       { get; set; } = string.Empty;
        [JsonPropertyName("lastData")]      public string   LastData      { get; set; } = string.Empty;
        [JsonPropertyName("duration")]      public int      Duration      { get; set; }
        [JsonPropertyName("billSec")]       public int      BillSec       { get; set; }
        [JsonPropertyName("disposition")]   public string   Disposition   { get; set; } = string.Empty;
        [JsonPropertyName("uniqueId")]      public string   UniqueId      { get; set; } = string.Empty;
        [JsonPropertyName("recordingFile")] public string   RecordingFile { get; set; } = string.Empty;
        [JsonPropertyName("linkedId")]      public string   LinkedId      { get; set; } = string.Empty;
        [JsonPropertyName("clid")]          public string   Clid          { get; set; } = string.Empty;
        [JsonPropertyName("dContext")]      public string   DContext      { get; set; } = string.Empty;
    }

    public class ApiCdrResponse
    {
        [JsonPropertyName("calls")] public List<ApiCdrRow> Calls { get; set; } = new();
    }

    // ── Fase 3: AMI via API ───────────────────────────────────────────────────

    public class ApiAmiExtension
    {
        [JsonPropertyName("ramal")] public string Ramal { get; set; } = string.Empty;
        [JsonPropertyName("nome")]  public string Nome  { get; set; } = string.Empty;
    }

    public class ApiAmiPeer
    {
        [JsonPropertyName("ramal")]        public string Ramal        { get; set; } = string.Empty;
        [JsonPropertyName("nome")]         public string Nome         { get; set; } = string.Empty;
        [JsonPropertyName("status")]       public string Status       { get; set; } = "offline";
        [JsonPropertyName("registrado")]   public bool   Registrado   { get; set; }
        [JsonPropertyName("rawStatus")]    public string RawStatus    { get; set; } = string.Empty;
        [JsonPropertyName("tecnologia")]   public string Tecnologia   { get; set; } = string.Empty;
        [JsonPropertyName("numeroRemoto")] public string NumeroRemoto { get; set; } = string.Empty;
        [JsonPropertyName("nomeRemoto")]   public string NomeRemoto   { get; set; } = string.Empty;
        [JsonPropertyName("direcao")]      public string Direcao      { get; set; } = string.Empty;
        [JsonPropertyName("canal")]        public string Canal        { get; set; } = string.Empty;
        [JsonPropertyName("linhaUsada")]   public string LinhaUsada   { get; set; } = string.Empty;
        [JsonPropertyName("nomeCanal")]    public string NomeCanal    { get; set; } = string.Empty;
        [JsonPropertyName("tipoChamada")]  public string TipoChamada  { get; set; } = string.Empty;
    }

    // ── Fase 4: Filas em Tempo Real ───────────────────────────────────────────

    public class ApiAmiQueue
    {
        [JsonPropertyName("fila")]                 public string                  Fila                 { get; set; } = string.Empty;
        [JsonPropertyName("nome")]                 public string                  Nome                 { get; set; } = string.Empty;
        [JsonPropertyName("aguardando")]           public int                     Aguardando           { get; set; }
        [JsonPropertyName("atendidas")]            public int                     Atendidas            { get; set; }
        [JsonPropertyName("abandonadas")]          public int                     Abandonadas          { get; set; }
        [JsonPropertyName("tempoMedioEspera")]     public int                     TempoMedioEspera     { get; set; }
        [JsonPropertyName("agentesOnline")]        public int                     AgentesOnline        { get; set; }
        [JsonPropertyName("agentesOffline")]       public int                     AgentesOffline       { get; set; }
        [JsonPropertyName("agentesEmAtendimento")] public int                     AgentesEmAtendimento { get; set; }
        [JsonPropertyName("agentesPausados")]      public int                     AgentesPausados      { get; set; }
        [JsonPropertyName("agentesTotal")]         public int                     AgentesTotal         { get; set; }
        [JsonPropertyName("agentes")]              public List<ApiAmiQueueMember> Agentes              { get; set; } = new();
        [JsonPropertyName("clientes")]             public List<ApiAmiQueueEntry>  Clientes             { get; set; } = new();
    }

    public class ApiAmiQueueMember
    {
        [JsonPropertyName("nome")]    public string Nome    { get; set; } = string.Empty;
        [JsonPropertyName("ramal")]   public string Ramal   { get; set; } = string.Empty;
        [JsonPropertyName("status")]  public string Status  { get; set; } = string.Empty;
        [JsonPropertyName("pausado")] public bool   Pausado { get; set; }
    }

    public class ApiAmiQueueEntry
    {
        [JsonPropertyName("posicao")] public int    Posicao { get; set; }
        [JsonPropertyName("numero")]  public string Numero  { get; set; } = string.Empty;
        [JsonPropertyName("espera")]  public int    Espera  { get; set; }
    }

    // ── Operação na fila offline ──────────────────────────────────────────────

    public class WavenApiOfflineOp
    {
        [JsonPropertyName("op")]        public string  Op        { get; set; } = string.Empty; // CREATE | UPDATE | DELETE | FAVORITE_ADD | FAVORITE_REMOVE
        [JsonPropertyName("contactId")] public string? ContactId { get; set; }
        [JsonPropertyName("payload")]   public string  Payload   { get; set; } = string.Empty; // JSON do body
        [JsonPropertyName("ramal")]     public string? Ramal     { get; set; }
        [JsonPropertyName("criadoEm")] public DateTime CriadoEm  { get; set; } = DateTime.UtcNow;
    }
}
