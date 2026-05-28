using System;
using System.Text.Json.Serialization;

namespace WavenVoIP.Models
{
    public class Contato
    {
        public string Nome { get; set; } = string.Empty;
        public string Numero { get; set; } = string.Empty;
        public string Observacao { get; set; } = string.Empty;
        public bool EhRamalIssabel { get; set; } = false;
        public bool FonteGoogle { get; set; } = false;
        public DateTime? AtualizadoEm { get; set; }

        // Transient — set at display time, never persisted
        [JsonIgnore]
        public bool EhFavorito { get; set; }
    }
}
