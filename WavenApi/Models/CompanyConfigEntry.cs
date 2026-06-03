namespace WavenApi.Models;

public class CompanyConfigEntry
{
    public int Id { get; set; } = 1;
    public string ConfigJson { get; set; } = "{}";
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public string AtualizadoPor { get; set; } = string.Empty;
}
