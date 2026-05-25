using System;

namespace WavenVoIP.Models
{
    public class WhatsAppEnvioLog
    {
        public string TipoEvento { get; set; } = string.Empty;
        public string ExternalKey { get; set; } = string.Empty;
        public string Numero { get; set; } = string.Empty;
        public string Mensagem { get; set; } = string.Empty;
        public int HttpStatusCode { get; set; }
        public string RespostaApi { get; set; } = string.Empty;
        public string Debug { get; set; } = string.Empty;
        public DateTime DataHora { get; set; } = DateTime.Now;
    }
}
