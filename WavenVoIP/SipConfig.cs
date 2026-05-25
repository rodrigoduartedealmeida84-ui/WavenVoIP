using System;
using System.IO;
using System.Text.Json;

namespace WavenVoIP
{
    public class SipConfig
    {
        public static string ConfigFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WavenVoIP", "sipconfig.json");

        public bool EstaCompleta =>
            !string.IsNullOrWhiteSpace(Ramal) &&
            !string.IsNullOrWhiteSpace(Login) &&
            !string.IsNullOrWhiteSpace(Senha) &&
            !string.IsNullOrWhiteSpace(ServerIp) &&
            (!string.IsNullOrWhiteSpace(NomeUsuario) || !string.IsNullOrWhiteSpace(DisplayName));

        public void Salvar()
        {
            AplicarPadroes();
            var pasta = Path.GetDirectoryName(ConfigFilePath);
            if (!string.IsNullOrWhiteSpace(pasta)) Directory.CreateDirectory(pasta);
            CriarBackup();
            File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static void CriarBackup()
        {
            try
            {
                if (!File.Exists(ConfigFilePath)) return;
                File.Copy(ConfigFilePath, Path.ChangeExtension(ConfigFilePath, ".backup.json"), overwrite: true);
            }
            catch { }
        }

        /// <summary>
        /// Fills every field that is empty/zero with the company default.
        /// Safe to call on any config — loaded from file or freshly constructed.
        /// </summary>
        public void RepairDefaults()
        {
            // SIP connection
            if (string.IsNullOrWhiteSpace(ServerIp))  ServerIp  = "191.252.202.208";
            if (Port <= 0)                             Port      = 5061;
            if (string.IsNullOrWhiteSpace(Transport)) Transport = "udp";
            if (string.IsNullOrWhiteSpace(Domain))    Domain    = ServerIp;
            if (string.IsNullOrWhiteSpace(ProxySip))  ProxySip  = $"{ServerIp}:{Port}";
            if (HistoricoRetencaoDias <= 0)            HistoricoRetencaoDias = 7;

            // AMI
            AmiAtivo = true;
            if (string.IsNullOrWhiteSpace(AmiHost))    AmiHost    = ServerIp;
            if (AmiPorta <= 0)                         AmiPorta   = 5038;
            if (string.IsNullOrWhiteSpace(AmiUsuario)) AmiUsuario = "waven";
            if (string.IsNullOrWhiteSpace(AmiSenha))   AmiSenha   = "Waven@2025";
            if (AmiIntervaloMinutos <= 0)               AmiIntervaloMinutos = 10;

            // Channels / routes
            if (string.IsNullOrWhiteSpace(SalaConferenciaIssabel)) SalaConferenciaIssabel = "800";
            if (string.IsNullOrWhiteSpace(CanalOperadora))         CanalOperadora   = "6631998716;IN-BRDID-6631998716";
            if (string.IsNullOrWhiteSpace(Canal0800))              Canal0800         = "08001901900;VONO-0800-ENTRADA";
            if (string.IsNullOrWhiteSpace(CanalWhatsAppTim))       CanalWhatsAppTim  = "556684263277;WAVOIP-556684263277";
            if (string.IsNullOrWhiteSpace(CanalWhatsAppVivo))      CanalWhatsAppVivo = "556696308630;WAVOIP-556696308630";

            // CDR
            if (CdrPorta <= 0)                              CdrPorta   = 3306;
            if (string.IsNullOrWhiteSpace(CdrBanco))        CdrBanco   = "asteriskcdrdb";
            if (string.IsNullOrWhiteSpace(CdrTabela))       CdrTabela  = "cdr";
            if (string.IsNullOrWhiteSpace(CdrUsuario))      CdrUsuario = "waven";
            if (string.IsNullOrWhiteSpace(HistoricoModoExibicao)) HistoricoModoExibicao = "MeuRamal";
            if (HistoricoSyncIntervalSeconds < 0)            HistoricoSyncIntervalSeconds = 3;
            if (AmiSyncIntervalSeconds    < 0)              AmiSyncIntervalSeconds       = 3;
            if (GoogleSyncIntervalSeconds < 0)              GoogleSyncIntervalSeconds    = 3;

            // Recordings
            if (string.IsNullOrWhiteSpace(GravacaoTipoAcesso)) GravacaoTipoAcesso = "URL";
            if (string.IsNullOrWhiteSpace(GravacaoUrlBase))    GravacaoUrlBase    = "http://pabx.almeidagas.com/gravacoes/monitor/";
        }

        // Kept for call-site compatibility — delegates to RepairDefaults.
        public void AplicarPadroes() => RepairDefaults();

        // Clears only operator identity — preserves all server/AMI/CDR/integration settings.
        public void ResetarIdentidadeUsuario()
        {
            NomeUsuario  = string.Empty;
            RamalNome    = string.Empty;
            DisplayName  = string.Empty;
            Ramal        = string.Empty;
            Login        = string.Empty;
            Senha        = string.Empty;
        }

        public static SipConfig? CarregarSalva()
        {
            try
            {
                if (!File.Exists(ConfigFilePath)) return null;
                var config = JsonSerializer.Deserialize<SipConfig>(File.ReadAllText(ConfigFilePath));
                config?.RepairDefaults();
                return config;
            }
            catch { return null; }
        }
        public string ServerIp { get; set; } = "191.252.202.208";
        public int Port { get; set; } = 5061;
        public string Transport { get; set; } = "udp";

        public string ProxySip { get; set; } = "191.252.202.208:5061";
        public string Domain { get; set; } = "191.252.202.208";
        public string Login { get; set; } = string.Empty;
        public string Ramal { get; set; } = string.Empty;
        public string Senha { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string RamalNome    { get; set; } = string.Empty;
        public string NomeUsuario  { get; set; } = string.Empty;
        public int HistoricoRetencaoDias { get; set; } = 7;

        // AMI do Issabel/Asterisk para sincronizar ramais e nomes automaticamente.
        public bool AmiAtivo { get; set; } = true;
        public string AmiHost { get; set; } = string.Empty;
        public int AmiPorta { get; set; } = 5038;
        public string AmiUsuario { get; set; } = "waven";
        public string AmiSenha { get; set; } = "Waven@2025";
        public int AmiIntervaloMinutos { get; set; } = 10;

        // Permite transferência cega/assistida para números externos.
        // Quando desligado, apenas ramais internos podem ser transferidos.
        // Conferência continua podendo chamar números externos.
        public bool PermitirTransferenciaExterna { get; set; } = true;
        public string SalaConferenciaIssabel { get; set; } = "800";

        // Mapeamento dos canais de entrada do Issabel. O app procura estes valores
        // no SIP recebido (DID/DDR, Request-URI, To, Diversion, P-Called-Party-ID ou nome da rota).
        public string CanalOperadora { get; set; } = "6631998716;IN-BRDID-6631998716";
        public string Canal0800 { get; set; } = "08001901900;VONO-0800-ENTRADA";
        public string CanalWhatsAppTim { get; set; } = "556684263277;WAVOIP-556684263277";
        public string CanalWhatsAppVivo { get; set; } = "556696308630;WAVOIP-556696308630";

        // CDR / Histórico real do Issabel (MySQL)
        public bool CdrAtivo { get; set; } = false;
        public string CdrHost { get; set; } = string.Empty;
        public int CdrPorta { get; set; } = 3306;
        public string CdrBanco { get; set; } = "asteriskcdrdb";
        public string CdrTabela { get; set; } = "cdr";
        public string CdrUsuario { get; set; } = "waven";
        public string CdrSenha { get; set; } = string.Empty;
        // "MeuRamal" or "TodosRamais"
        public string HistoricoModoExibicao { get; set; } = "MeuRamal";
        public int HistoricoSyncIntervalMinutes { get; set; } = 0; // kept for migration only
        public int HistoricoSyncIntervalSeconds { get; set; } = 3;
        public int AmiSyncIntervalSeconds    { get; set; } = 3;
        public int GoogleSyncIntervalSeconds { get; set; } = 3;

        // Gravações de chamadas
        public bool GravacaoAtiva { get; set; } = false;
        // "URL" or "Local"
        public string GravacaoTipoAcesso { get; set; } = "URL";
        public string GravacaoUrlBase { get; set; } = "http://pabx.almeidagas.com/gravacoes/monitor/";
        public string GravacaoCaminhoLocal { get; set; } = string.Empty;

        public bool AutoUpdateEnabled { get; set; } = true;
    }
}
