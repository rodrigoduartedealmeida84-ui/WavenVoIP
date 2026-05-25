using System;

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
    }
}
