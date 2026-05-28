namespace WavenVoIP.Models
{
    public class FavoriteItem
    {
        public string Nome { get; set; } = string.Empty;
        public string Numero { get; set; } = string.Empty;
        public string Ramal { get; set; } = string.Empty;
        public bool Favorito { get; set; } = true;
        public int Ordem { get; set; }
        public int UsageCount { get; set; }
    }
}
