namespace WavenVoIP.Models
{
    public class ConfiguracaoAudio
    {
        public string Microfone { get; set; } = "Padrão do sistema";
        public string AltoFalante { get; set; } = "Padrão do sistema";
        public string Toque { get; set; } = "Assets\\toque_padrao.mp3";
        public bool TocarEmTelaCheia { get; set; } = true;
        public bool ToqueSempreNoSpeakerPrincipal { get; set; } = true;
        public string DispositivoToqueId { get; set; } = string.Empty;
        public string DispositivoToqueNome { get; set; } = string.Empty;
        public int GoogleSyncIntervalMinutes { get; set; } = 0;
    }
}
