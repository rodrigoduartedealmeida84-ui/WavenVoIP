using System.Text.Json.Serialization;

namespace WavenApi.Models;

public class AmiExtension
{
    [JsonPropertyName("ramal")] public string Ramal { get; set; } = "";
    [JsonPropertyName("nome")]  public string Nome  { get; set; } = "";
}

public class AmiPeer
{
    [JsonPropertyName("ramal")]        public string Ramal        { get; set; } = "";
    [JsonPropertyName("nome")]         public string Nome         { get; set; } = "";
    [JsonPropertyName("status")]       public string Status       { get; set; } = "offline";
    [JsonPropertyName("registrado")]   public bool   Registrado   { get; set; }
    [JsonPropertyName("rawStatus")]    public string RawStatus    { get; set; } = "";
    [JsonPropertyName("tecnologia")]   public string Tecnologia   { get; set; } = "";
    [JsonPropertyName("numeroRemoto")] public string NumeroRemoto { get; set; } = "";
    [JsonPropertyName("nomeRemoto")]   public string NomeRemoto   { get; set; } = "";
    [JsonPropertyName("direcao")]      public string Direcao      { get; set; } = "";
    [JsonPropertyName("canal")]        public string Canal        { get; set; } = "";
    [JsonPropertyName("linhaUsada")]   public string LinhaUsada   { get; set; } = "";
    [JsonPropertyName("nomeCanal")]    public string NomeCanal    { get; set; } = "";
    [JsonPropertyName("tipoChamada")]  public string TipoChamada  { get; set; } = "";
}

public class AmiQueue
{
    [JsonPropertyName("fila")]                 public string               Fila                 { get; set; } = "";
    [JsonPropertyName("nome")]                 public string               Nome                 { get; set; } = "";
    [JsonPropertyName("aguardando")]           public int                  Aguardando           { get; set; }
    [JsonPropertyName("atendidas")]            public int                  Atendidas            { get; set; }
    [JsonPropertyName("abandonadas")]          public int                  Abandonadas          { get; set; }
    [JsonPropertyName("tempoMedioEspera")]     public int                  TempoMedioEspera     { get; set; }
    [JsonPropertyName("agentesOnline")]        public int                  AgentesOnline        { get; set; }
    [JsonPropertyName("agentesOffline")]       public int                  AgentesOffline       { get; set; }
    [JsonPropertyName("agentesEmAtendimento")] public int                  AgentesEmAtendimento { get; set; }
    [JsonPropertyName("agentesPausados")]      public int                  AgentesPausados      { get; set; }
    [JsonPropertyName("agentesTotal")]         public int                  AgentesTotal         { get; set; }
    [JsonPropertyName("agentes")]              public List<AmiQueueMember> Agentes              { get; set; } = new();
    [JsonPropertyName("clientes")]             public List<AmiQueueEntry>  Clientes             { get; set; } = new();
}

public class AmiQueueMember
{
    [JsonPropertyName("nome")]    public string Nome    { get; set; } = "";
    [JsonPropertyName("ramal")]   public string Ramal   { get; set; } = "";
    [JsonPropertyName("status")]  public string Status  { get; set; } = "";
    [JsonPropertyName("pausado")] public bool   Pausado { get; set; }
}

public class AmiQueueEntry
{
    [JsonPropertyName("posicao")] public int    Posicao { get; set; }
    [JsonPropertyName("numero")]  public string Numero  { get; set; } = "";
    [JsonPropertyName("espera")]  public int    Espera  { get; set; }
}
