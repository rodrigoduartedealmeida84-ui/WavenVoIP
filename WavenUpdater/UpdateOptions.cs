namespace WavenUpdater;

public sealed class UpdateOptions
{
    public int    MainPid    { get; init; }
    public string ZipUrl     { get; init; } = string.Empty;
    public string Sha256     { get; init; } = string.Empty;
    public string NewVersion { get; init; } = string.Empty;
    public string OldVersion { get; init; } = string.Empty;
    public string AppDir     { get; init; } = string.Empty;
    public string ExeName    { get; init; } = "WavenVoIP.exe";
}
