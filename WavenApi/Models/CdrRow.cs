using System.Text.Json.Serialization;

namespace WavenApi.Models;

public class CdrRow
{
    [JsonPropertyName("callDate")]      public DateTime CallDate      { get; set; }
    [JsonPropertyName("src")]           public string   Src           { get; set; } = "";
    [JsonPropertyName("dst")]           public string   Dst           { get; set; } = "";
    [JsonPropertyName("channel")]       public string   Channel       { get; set; } = "";
    [JsonPropertyName("dstChannel")]    public string   DstChannel    { get; set; } = "";
    [JsonPropertyName("lastApp")]       public string   LastApp       { get; set; } = "";
    [JsonPropertyName("lastData")]      public string   LastData      { get; set; } = "";
    [JsonPropertyName("duration")]      public int      Duration      { get; set; }
    [JsonPropertyName("billSec")]       public int      BillSec       { get; set; }
    [JsonPropertyName("disposition")]   public string   Disposition   { get; set; } = "";
    [JsonPropertyName("uniqueId")]      public string   UniqueId      { get; set; } = "";
    [JsonPropertyName("recordingFile")] public string   RecordingFile { get; set; } = "";
    [JsonPropertyName("linkedId")]      public string   LinkedId      { get; set; } = "";
    [JsonPropertyName("clid")]          public string   Clid          { get; set; } = "";
    [JsonPropertyName("dContext")]      public string   DContext      { get; set; } = "";
}
