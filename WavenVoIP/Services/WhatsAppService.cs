using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class WhatsAppService
    {
        public static string RemoverPrefixoRota(string telefone)
        {
            var n = SomenteDigitos(telefone);
            if (n.Length >= 11 && (n[0] == '1' || n[0] == '2' || n[0] == '3'))
                return n.Substring(1);
            return n;
        }

        public static string NormalizarTelefoneParaEnvio(string telefone)
        {
            var n = RemoverPrefixoRota(telefone);

            if (n.StartsWith("00"))
                n = n.Substring(2);

            if (n.StartsWith("0") && n.Length > 10)
                n = n.TrimStart('0');

            if (!n.StartsWith("55"))
                n = "55" + n;

            return n;
        }

        public static string GerarExternalKey(string tipoEvento)
        {
            return $"waven_{tipoEvento}_{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Envia template WABA nova_conversa via API oficial Waven Chat.
        /// Retorna Bloqueado=true se anti-spam (mesmo número nos últimos 5 min).
        /// </summary>
        public static async Task<WhatsAppResultado> EnviarTemplateWabaAsync(
            string telefone, string tipoEvento = "manual", string? externalKey = null)
        {
            var config = WhatsAppConfigService.Carregar();
            var resultado = new WhatsAppResultado();
            var numeroNormalizado = NormalizarTelefoneParaEnvio(telefone);
            resultado.NumeroNormalizado = numeroNormalizado;
            externalKey ??= GerarExternalKey(tipoEvento);
            resultado.ExternalKey = externalKey;

            if (string.IsNullOrWhiteSpace(numeroNormalizado) || numeroNormalizado.Length < 12)
            {
                resultado.Debug = "Número inválido. Use DDD + número.";
                LogHelper.WhatsApp($"WABA_INVALID_PHONE | numero_original={telefone} normalizado={numeroNormalizado}");
                return resultado;
            }

            if (string.IsNullOrWhiteSpace(config.ApiUrl))
            {
                resultado.Debug = "Configure a URL da API do WhatsApp em Configurações.";
                return resultado;
            }

            if (string.IsNullOrWhiteSpace(config.BearerToken))
            {
                resultado.Debug = "Configure o Bearer Token do canal em Configurações.";
                return resultado;
            }

            // Anti-spam: bloqueia se mesmo número recebeu template nos últimos 5 minutos
            if (WhatsAppLogService.EnviadoRecentementeParaNumero(numeroNormalizado, 5))
            {
                resultado.Bloqueado = true;
                resultado.Debug = "Template já enviado recentemente para este cliente.";
                LogHelper.WhatsApp($"WABA_TEMPLATE_BLOCKED | numero={numeroNormalizado}");
                return resultado;
            }

            var endpoint = config.ApiUrl.TrimEnd('/') + "/template";
            var tokenMascarado = Mascarar(config.BearerToken);

            LogHelper.WhatsApp(
                $"WABA_TEMPLATE_SEND_START | numero={numeroNormalizado} " +
                $"endpoint={endpoint} token={tokenMascarado}");

            var payload = new
            {
                number = numeroNormalizado,
                isClosed = false,
                templateData = new
                {
                    messaging_product = "whatsapp",
                    to = numeroNormalizado,
                    type = "template",
                    template = new
                    {
                        name = "nova_conversa",
                        language = new { code = "pt_BR" }
                    }
                }
            };

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.BearerToken);

                var json    = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(endpoint, content);

                resultado.HttpStatusCode = (int)response.StatusCode;
                resultado.RespostaBruta  = await response.Content.ReadAsStringAsync();
                resultado.Sucesso        = response.IsSuccessStatusCode;
                resultado.Debug          = $"POST {endpoint}\nNúmero: {numeroNormalizado}\nHTTP: {resultado.HttpStatusCode}";

                if (resultado.Sucesso)
                    LogHelper.WhatsApp(
                        $"WABA_TEMPLATE_SEND_SUCCESS | numero={numeroNormalizado} http={resultado.HttpStatusCode}");
                else
                    LogHelper.WhatsApp(
                        $"WABA_TEMPLATE_SEND_FAIL | numero={numeroNormalizado} http={resultado.HttpStatusCode} resposta={resultado.RespostaBruta}");
            }
            catch (TaskCanceledException)
            {
                resultado.Debug = "API indisponível (timeout).";
                LogHelper.WhatsApp($"WABA_TEMPLATE_SEND_FAIL | numero={numeroNormalizado} timeout=true");
            }
            catch (Exception ex)
            {
                resultado.Debug = ex.Message;
                LogHelper.WhatsApp(
                    $"WABA_TEMPLATE_SEND_FAIL | numero={numeroNormalizado} " +
                    $"ex={ex.GetType().Name}: {ex.Message}");
            }

            WhatsAppLogService.Registrar(new WhatsAppEnvioLog
            {
                TipoEvento    = tipoEvento,
                ExternalKey   = externalKey,
                Numero        = numeroNormalizado,
                Mensagem      = "template:nova_conversa",
                HttpStatusCode = resultado.HttpStatusCode,
                RespostaApi   = resultado.RespostaBruta,
                Debug         = resultado.Debug,
                DataHora      = DateTime.Now
            });

            return resultado;
        }

        /// <summary>
        /// Mantido para compatibilidade com telas de configuração e teste.
        /// Ignora o parâmetro mensagem e usa o template WABA nova_conversa.
        /// </summary>
        public static Task<WhatsAppResultado> EnviarMensagemAsync(
            string telefone, string mensagem, string tipoEvento = "manual", string? externalKey = null)
            => EnviarTemplateWabaAsync(telefone, tipoEvento, externalKey);

        private static string Mascarar(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "(vazio)";
            if (token.Length <= 10) return "***";
            return token[..6] + "************" + token[^4..];
        }

        private static string SomenteDigitos(string texto)
        {
            return new string((texto ?? string.Empty).Where(char.IsDigit).ToArray());
        }
    }
}
