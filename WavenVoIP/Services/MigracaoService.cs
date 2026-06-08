using System;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    /// <summary>
    /// Migrações automáticas de configuração aplicadas silenciosamente no startup.
    /// Cada migração roda uma única vez por máquina (flag no SipConfig).
    /// </summary>
    public static class MigracaoService
    {
        // Credenciais embarcadas v1.4.2
        private const string WavenApiUrl142   = "https://api.almeidagas.com";
        private const string WavenApiToken142 = "Waven2026_AlmeidaGas_API_x7K9mP4Q2zN8vL5R1cT6hY3";
        private const string WhatsApiUrl142   = "https://api.wavenchat.com.br/v2/api/external/bf8b5bbb-81fc-45b3-847b-45ac9203951e";
        private const string WhatsToken142    = "a02541d689cba8209551b8fcd8de6103d168279ddfb2834de8b0d92cb22e2fcf";

        // Credenciais WABA v1.4.4 — API oficial Waven Chat
        private const string WhatsApiUrl144   = "https://api.wavenchat.com.br/v2/api/external/9dc34814-f12c-4647-8722-03751ead7902";
        private const string WhatsToken144    = "15fdce0f8d4e5fa41a88147faa6b11a9cc94d757964e9cc5cf109b51973e12e9";

        /// <summary>
        /// Aplica a migração v1.4.2 se ainda não foi aplicada nesta máquina.
        /// Preserva: usuário SIP, ramal, senha, Google token, áudio, favoritos pessoais.
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

                cfg.UsarWavenApi  = true;
                cfg.WavenApiUrl   = WavenApiUrl142;
                cfg.WavenApiToken = WavenApiToken142;
                cfg.CdrAtivo      = true;
                cfg.AmiAtivo      = true;
                cfg.MigracaoAplicada142 = true;

                try { cfg.Salvar(); }
                catch (Exception ex)
                {
                    App.LogCrash("AUTO_MIGRATION_142_SAVE_ERROR", "Falha ao salvar SipConfig na migracao 142", ex);
                }

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

        /// <summary>
        /// Aplica a migração v1.4.4 WABA se ainda não foi aplicada nesta máquina.
        /// Atualiza WhatsApp ApiUrl e BearerToken para a API oficial WABA.
        /// Não altera SIP, ramal, senha, Waven API, áudio, contatos, histórico ou favoritos.
        /// </summary>
        public static void AplicarMigracao144()
        {
            try
            {
                var cfg = SipConfig.CarregarSalva() ?? new SipConfig();

                if (cfg.MigracaoWaba144Aplicada)
                {
                    LogHelper.Info("AUTO_MIGRATION_WABA_144_ALREADY_APPLIED");
                    return;
                }

                cfg.MigracaoWaba144Aplicada = true;

                try { cfg.Salvar(); }
                catch (Exception ex)
                {
                    App.LogCrash("AUTO_MIGRATION_WABA_144_SAVE_ERROR", "Falha ao salvar SipConfig na migracao 144", ex);
                }

                try
                {
                    var wCfg = WhatsAppConfigService.Carregar();
                    wCfg.ApiUrl      = WhatsApiUrl144;
                    wCfg.BearerToken = WhatsToken144;
                    WhatsAppConfigService.Salvar(wCfg);
                }
                catch (Exception ex)
                {
                    App.LogCrash("AUTO_MIGRATION_WABA_144_WHATS_ERROR", "Falha ao salvar WhatsAppConfig na migracao 144", ex);
                }

                LogHelper.Info(
                    $"AUTO_MIGRATION_WABA_144_APPLIED | " +
                    $"whats_url={WhatsApiUrl144} whats_token={Mascarar(WhatsToken144)}");
            }
            catch (Exception ex)
            {
                App.LogCrash("AUTO_MIGRATION_WABA_144_ERROR", "Excecao inesperada na migracao v1.4.4 WABA", ex);
            }
        }

        private static string Mascarar(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "(vazio)";
            if (token.Length <= 10) return "***";
            return token[..6] + "************" + token[^4..];
        }
    }
}
