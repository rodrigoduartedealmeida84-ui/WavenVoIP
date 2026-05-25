using System;

namespace WavenVoIP.Models
{
    public class CdrChamada
    {
        public DateTime CallDate { get; set; }
        public string Src { get; set; } = string.Empty;
        public string Dst { get; set; } = string.Empty;
        public string Channel { get; set; } = string.Empty;
        public string DstChannel { get; set; } = string.Empty;
        public string LastApp { get; set; } = string.Empty;
        public string LastData { get; set; } = string.Empty;
        public int Duration { get; set; }
        public int BillSec { get; set; }
        public string Disposition { get; set; } = string.Empty;
        public string UniqueId { get; set; } = string.Empty;
        public string RecordingFile { get; set; } = string.Empty;
        public string LinkedId { get; set; } = string.Empty;
        public string Clid { get; set; } = string.Empty;
        public string DContext { get; set; } = string.Empty;
    }
}
