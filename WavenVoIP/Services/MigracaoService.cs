using System;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    /// <summary>
    /// Migracoes automaticas de configuracao aplicadas silenciosamente no startup.
    /// Cada migracao roda uma unica vez por maquina (flag no SipConfig).
    /// </summary>
    public static class MigracaoService
    {
        // Credenciais embarcadas v1.4.2 — distribuidas para toda a empresa
        private const string WavenApiUrl142   = "https://api.almeidagas.com";
        private const string WavenApiToken142 = "Waven2026_AlmeidaGas_API_x7K9mP4Q2zN8vL5R1cT6hY3";
        private const string WhatsApiUrl142   = "https://api.wavenchat.com.br/v2/api/external/bf8b5bbb-81fc-45b3-847b-45ac9203951e";
        private const string WhatsToken142    = "a02541d689cba8209551b8fcd8de6103d168279ddfb2834de8b0d92cb22e2fcf";

        /// <summary>
        /// Aplica a migracao v1.4.2 se ainda nao foi aplicada nesta maquina.
        /// Preserva: usuario SIP, ramal, senha, Google token, audio, favoritos pessoais.
        /// Sobrescreve: Waven API URL/token, UsarWavenApi, WhatsApp API URL/token.
        /// </summary>
        public static void AplicarMigracao142()
        {
            try
            {
                var cfg = SipConfig.CarregarSalva() ?? new SipConfig();

                if (cfg.MigracaoAplicada142)
                {
                    LogHelper.Info("AUTO_MIGRATION_142_ALREADY_APPLIED");
                    return;
                }

                // ── Waven API ─────────────────────────────────────────────────
                cfg.UsarWavenApi          = true;
                cfg.WavenApiUrl           = WavenApiUrl142;
                cfg.WavenApiToken         = WavenApiToken142;

                // Habilita CDR e AMI para que o historico e ramais funcionem via API
                cfg.CdrAtivo = true;
                cfg.AmiAtivo = true;

                // Marca migracao concluida ANTES de salvar para nao re-executar
                // em caso de falha parcial nas proximas etapas
                cfg.MigracaoAplicada142 = true;

                try
                {
                    cfg.Salvar();
                }
                catch (Exception ex)
                {
                    App.LogCrash("AUTO_MIGRATION_142_SAVE_ERROR", "Falha ao salvar SipConfig na migracao 142", ex);
                }

                // ── WhatsApp API ──────────────────────────────────────────────
                try
                {
                    var wCfg = WhatsAppConfigService.Carregar();
                    wCfg.ApiUrl      = WhatsApiUrl142;
                    wCfg.BearerToken = WhatsToken142;
                    WhatsAppConfigService.Salvar(wCfg);
                }
                catch (Exception ex)
                {
                    App.LogCrash("AUTO_MIGRATION_142_WHATS_ERROR", "Falha ao salvar WhatsAppConfig na migracao 142", ex);
                }

                // Log com tokens mascarados (nunca logar token completo)
                LogHelper.Info(
                    $"AUTO_MIGRATION_142_APPLIED | " +
                    $"waven_url={WavenApiUrl142} waven_token={Mascarar(WavenApiToken142)} | " +
                    $"whats_token={Mascarar(WhatsToken142)} | " +
                    $"cdr_ativo=true ami_ativo=true usar_api=true");
            }
            catch (Exception ex)
            {
                App.LogCrash("AUTO_MIGRATION_142_ERROR", "Excecao inesperada na migracao v1.4.2", ex);
            }
        }

        private static string Mascarar(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "(vazio)";
            return token.Length > 6 ? token[..6] + "***" : "***";
        }
    }
}
