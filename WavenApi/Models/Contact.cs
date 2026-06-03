namespace WavenApi.Models;

public class Contact
{
    public string Id { get; set; } = string.Empty;
    public string Nome { get; set; } = string.Empty;
    public string Numero { get; set; } = string.Empty;
    public string NumeroNormalizado { get; set; } = string.Empty;
    public string? Empresa { get; set; }
    public string? Observacao { get; set; }
    public string CriadoPor { get; set; } = string.Empty;
    public string AtualizadoPor { get; set; } = string.Empty;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public bool Excluido { get; set; }
    public DateTime? ExcluidoEm { get; set; }

    public ICollection<Favorito> Favoritos { get; set; } = new List<Favorito>();
}
