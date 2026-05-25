namespace WavenVoIP.Models
{
    public class CallRecordingInfo
    {
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public bool Exists { get; set; }
        public string LinkedId { get; set; } = string.Empty;
        public string UniqueId { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
    }
}
