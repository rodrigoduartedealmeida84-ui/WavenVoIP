namespace WavenVoIP.Models
{
    public class WhatsAppConfig
    {
        public string ApiUrl { get; set; } = "https://api.wavenchat.com.br/v2/api/external/SEU_API_ID";
        public string BearerToken { get; set; } = string.Empty;
        public string NumeroTeste { get; set; } = string.Empty;
        public string MensagemTeste { get; set; } = "Teste de envio pelo Waven VoIP.";
    }
}
