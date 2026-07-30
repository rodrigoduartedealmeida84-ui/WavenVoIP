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

        // Vínculo estável ao contato (Contato.WavenApiId). Null quando o contato
        // ainda não foi sincronizado com a Waven API — nesse caso o vínculo é
        // resolvido por telefone normalizado (fallback), ver FavoritesStorageService.
        public string? ContactId { get; set; }
    }
}
