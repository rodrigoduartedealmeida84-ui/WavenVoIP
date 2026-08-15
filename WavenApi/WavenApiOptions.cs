namespace WavenApi;

public class WavenApiOptions
{
    public string BearerToken    { get; set; } = string.Empty;
    public string DbPath         { get; set; } = "/opt/waven-api/data/waven.db";
    public int    MaxPayloadBytes { get; set; } = 1_048_576;

    // Fase 2 - CDR via MySQL local
    public MySqlOptions MySql { get; set; } = new();

    // Fase 3 - AMI via socket local
    public AmiOptions Ami { get; set; } = new();

    // Caminho local dos arquivos de gravacao do Asterisk
    public string RecordingsPath { get; set; } = "/var/spool/asterisk/monitor";

    // Diagnostico remoto (telemetria) — ver WavenApi/Endpoints/DiagnosticsEndpoints.cs
    public DiagnosticsOptions Diagnostics { get; set; } = new();

    public class DiagnosticsOptions
    {
        // Retencao: snapshots (heartbeats) sao volumosos e de baixo valor individual —
        // apagados apos poucos dias. Incidents (eventos de alerta) sao raros e valiosos
        // para investigacao — mantidos por mais tempo.
        public int RetencaoSnapshotsDias { get; set; } = 15;
        public int RetencaoIncidentsDias { get; set; } = 90;

        // Rate limit server-side por instalacao — rede de seguranca final independente
        // do rate limit do cliente (defende contra cliente com bug/versao antiga).
        public int MinIntervaloHeartbeatSegundos { get; set; } = 20;
        public int MinIntervaloIncidentSegundos  { get; set; } = 5;
    }

    public class MySqlOptions
    {
        public string Host                    { get; set; } = "127.0.0.1";
        public int    Port                    { get; set; } = 3306;
        public string Database                { get; set; } = "asteriskcdrdb";
        public string Table                   { get; set; } = "cdr";
        public string User                    { get; set; } = string.Empty;
        public string Password                { get; set; } = string.Empty;
        public int    ConnectionTimeoutSeconds { get; set; } = 10;
    }

    public class AmiOptions
    {
        public string Host            { get; set; } = "127.0.0.1";
        public int    Port            { get; set; } = 5038;
        public string User            { get; set; } = string.Empty;
        public string Password        { get; set; } = string.Empty;
        public int    ConnectTimeoutMs { get; set; } = 5000;
    }
}
