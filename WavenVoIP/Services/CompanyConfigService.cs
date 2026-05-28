using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class CompanyConfigService
    {
        private const string RemoteUrl =
            "https://raw.githubusercontent.com/rodrigoduartedealmeida84-ui/WavenVoIP/main/servidor_hostinger/company-config.json";

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        // ── Remote JSON model ─────────────────────────────────────────────────

        private sealed class RemoteConfig
        {
            [JsonPropertyName("versaoConfig")]  public string VersaoConfig { get; set; } = string.Empty;
            [JsonPropertyName("whatsapp")]      public RemoteWhatsApp? WhatsApp { get; set; }
            [JsonPropertyName("cdr")]           public RemoteCdr? Cdr { get; set; }
        }

        private sealed class RemoteWhatsApp
        {
            [JsonPropertyName("apiUrl")]       public string ApiUrl { get; set; } = string.Empty;
            [JsonPropertyName("bearerToken")]  public string BearerToken { get; set; } = string.Empty;
            [JsonPropertyName("ativo")]        public bool Ativo { get; set; }
        }

        private sealed class RemoteCdr
        {
            [JsonPropertyName("host")]     public string Host { get; set; } = string.Empty;
            [JsonPropertyName("porta")]    public int Porta { get; set; }
            [JsonPropertyName("banco")]    public string Banco { get; set; } = string.Empty;
            [JsonPropertyName("tabela")]   public string Tabela { get; set; } = string.Empty;
            [JsonPropertyName("usuario")]  public string Usuario { get; set; } = string.Empty;
            [JsonPropertyName("senha")]    public string Senha { get; set; } = string.Empty;
            [JsonPropertyName("ativo")]    public bool Ativo { get; set; }
        }

        // ── Public API ────────────────────────────────────────────────────────

        public static async Task<(bool aplicou, string status)> SincronizarAsync()
        {
            Log("COMPANY_CONFIG_CHECK_START");
            try
            {
                var json = await _http.GetStringAsync(RemoteUrl).ConfigureAwait(false);
                var remote = JsonSerializer.Deserialize<RemoteConfig>(json);

                if (remote == null || string.IsNullOrWhiteSpace(remote.VersaoConfig))
                {
                    Log("COMPANY_CONFIG_SYNC_FAILED | JSON vazio ou sem versaoConfig");
                    return (false, "Falha: JSON inválido");
                }

                var config = SipConfig.CarregarSalva() ?? new SipConfig();
                var versaoLocal = config.CompanyConfigVersion ?? string.Empty;

                Log($"COMPANY_CONFIG_REMOTE_VERSION | versao={remote.VersaoConfig}");
                Log($"COMPANY_CONFIG_LOCAL_VERSION  | versao={versaoLocal}");

                if (!RemotaMaisNova(remote.VersaoConfig, versaoLocal))
                {
                    Log("COMPANY_CONFIG_NO_CHANGE | versao local ja esta atualizada");
                    return (false, $"Já atualizado (v{remote.VersaoConfig})");
                }

                Log($"COMPANY_CONFIG_UPDATE_AVAILABLE | remota={remote.VersaoConfig} local={versaoLocal}");

                bool mudouWpp = false;
                bool mudouCdr = false;

                // ── WhatsApp ──────────────────────────────────────────────────
                if (remote.WhatsApp != null && remote.WhatsApp.Ativo &&
                    !string.IsNullOrWhiteSpace(remote.WhatsApp.ApiUrl) &&
                    !string.IsNullOrWhiteSpace(remote.WhatsApp.BearerToken))
                {
                    var wpp = WhatsAppConfigService.Carregar();
                    wpp.ApiUrl = remote.WhatsApp.ApiUrl;
                    wpp.BearerToken = remote.WhatsApp.BearerToken;
                    WhatsAppConfigService.Salvar(wpp);
                    Log($"COMPANY_CONFIG_WHATSAPP_UPDATED | apiUrl={remote.WhatsApp.ApiUrl} token={MascararToken(remote.WhatsApp.BearerToken)}");
                    mudouWpp = true;
                }

                // ── CDR ───────────────────────────────────────────────────────
                if (remote.Cdr != null && remote.Cdr.Ativo &&
                    !string.IsNullOrWhiteSpace(remote.Cdr.Host))
                {
                    config.CdrAtivo  = true;
                    config.CdrHost   = remote.Cdr.Host;
                    if (remote.Cdr.Porta > 0)                          config.CdrPorta   = remote.Cdr.Porta;
                    if (!string.IsNullOrWhiteSpace(remote.Cdr.Banco))  config.CdrBanco   = remote.Cdr.Banco;
                    if (!string.IsNullOrWhiteSpace(remote.Cdr.Tabela)) config.CdrTabela  = remote.Cdr.Tabela;
                    if (!string.IsNullOrWhiteSpace(remote.Cdr.Usuario))config.CdrUsuario = remote.Cdr.Usuario;
                    if (!string.IsNullOrWhiteSpace(remote.Cdr.Senha))  config.CdrSenha   = remote.Cdr.Senha;
                    Log($"COMPANY_CONFIG_CDR_UPDATED | host={remote.Cdr.Host} banco={remote.Cdr.Banco}");
                    mudouCdr = true;
                }

                // ── Persist version + timestamp ───────────────────────────────
                config.CompanyConfigVersion  = remote.VersaoConfig;
                config.CompanyConfigLastSync = DateTime.Now;
                config.Salvar();

                Log($"COMPANY_CONFIG_APPLIED | versao={remote.VersaoConfig} wpp={mudouWpp} cdr={mudouCdr}");
                return (true, $"Aplicado v{remote.VersaoConfig}");
            }
            catch (HttpRequestException ex)
            {
                Log($"COMPANY_CONFIG_SYNC_FAILED | sem internet ou servidor inacessível: {ex.Message}");
                return (false, "Offline ou servidor inacessível");
            }
            catch (TaskCanceledException)
            {
                Log("COMPANY_CONFIG_SYNC_FAILED | timeout na requisição");
                return (false, "Timeout");
            }
            catch (JsonException ex)
            {
                Log($"COMPANY_CONFIG_SYNC_FAILED | JSON inválido: {ex.Message}");
                return (false, "Falha: JSON inválido");
            }
            catch (Exception ex)
            {
                Log($"COMPANY_CONFIG_SYNC_FAILED | {ex.GetType().Name}: {ex.Message}");
                return (false, $"Erro: {ex.Message}");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool RemotaMaisNova(string remota, string local)
        {
            if (string.IsNullOrWhiteSpace(remota)) return false;
            if (string.IsNullOrWhiteSpace(local))  return true;
            try
            {
                return Version.Parse(remota) > Version.Parse(local);
            }
            catch
            {
                return !string.Equals(remota, local, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string MascararToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length < 12) return "***";
            return token[..6] + "..." + token[^4..];
        }

        private static void Log(string msg)
        {
            try { LogHelper.Info(msg); } catch { }
        }
    }
}
