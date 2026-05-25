using System;

namespace WavenVoIP.Services;

public enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    UpdateAvailable,
    Launching,
    Error
}

public enum UpdateCheckResult
{
    EmDia,
    AtualizacaoIniciada,
    Erro,
    JaVerificando
}

public sealed class UpdateProgress
{
    public UpdateStage Stage     { get; init; } = UpdateStage.Idle;
    public string      Message   { get; init; } = string.Empty;
    public double      Percent   { get; init; } = -1; // -1 = indeterminate
    public string?     RemoteVersion { get; init; }
    public string?     ErrorMessage  { get; init; }
    public DateTime?   CheckedAt     { get; init; }
}
