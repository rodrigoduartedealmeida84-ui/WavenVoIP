namespace WavenVoIP.Models
{
    public class WhatsAppResultado
    {
        public bool Sucesso { get; set; }
        public int HttpStatusCode { get; set; }
        public string RespostaBruta { get; set; } = string.Empty;
        public string Debug { get; set; } = string.Empty;
        public string ExternalKey { get; set; } = string.Empty;
        public string NumeroNormalizado { get; set; } = string.Empty;
    }
}
