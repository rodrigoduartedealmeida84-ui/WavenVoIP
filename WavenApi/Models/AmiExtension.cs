using System.Text.Json.Serialization;

namespace WavenApi.Models;

public class AmiExtension
{
    [JsonPropertyName("ramal")] public string Ramal { get; set; } = "";
    [JsonPropertyName("nome")]  public string Nome  { get; set; } = "";
}
