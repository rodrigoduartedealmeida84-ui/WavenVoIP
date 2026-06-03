namespace WavenApi.Models;

public class Favorito
{
    public string Id { get; set; } = string.Empty;
    public string ContactId { get; set; } = string.Empty;
    public Contact? Contact { get; set; }

    // null quando TipoFavorito="global"
    public string? Ramal { get; set; }

    // "usuario" | "global"
    public string TipoFavorito { get; set; } = "usuario";

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public string CriadoPor { get; set; } = string.Empty;
}
