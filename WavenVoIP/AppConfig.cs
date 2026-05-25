namespace WavenVoIP;

public class AppConfig
{
    public string ServerIp { get; set; } = "191.252.202.208";
    public int Port { get; set; } = 5061;
    public string Transport { get; set; } = "udp";
    public bool AlwaysAskRoute { get; set; } = true;
}
