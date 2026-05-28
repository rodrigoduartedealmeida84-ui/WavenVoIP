using System;
using System.Text.Json.Serialization;

namespace WavenVoIP.Models
{
    public class CompanyContact
    {
        [JsonPropertyName("id")]                 public string    Id                 { get; set; } = string.Empty;
        [JsonPropertyName("nome")]               public string    Nome               { get; set; } = string.Empty;
        [JsonPropertyName("numero")]             public string    Numero             { get; set; } = string.Empty;
        [JsonPropertyName("numeroNormalizado")]  public string    NumeroNormalizado  { get; set; } = string.Empty;
        [JsonPropertyName("empresa")]            public string    Empresa            { get; set; } = string.Empty;
        [JsonPropertyName("favorito")]           public bool      Favorito           { get; set; }
        [JsonPropertyName("atualizadoEm")]       public DateTime? AtualizadoEm       { get; set; }
        [JsonPropertyName("excluido")]           public bool      Excluido           { get; set; }
    }
}
