using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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

        public static async Task<WhatsAppResultado> EnviarMensagemAsync(string telefone, string mensagem, string tipoEvento = "manual", string? externalKey = null)
        {
            var config = WhatsAppConfigService.Carregar();
            var resultado = new WhatsAppResultado();
            var numeroNormalizado = NormalizarTelefoneParaEnvio(telefone);
            resultado.NumeroNormalizado = numeroNormalizado;
            externalKey ??= GerarExternalKey(tipoEvento);
            resultado.ExternalKey = externalKey;

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
            if (string.IsNullOrWhiteSpace(numeroNormalizado) || numeroNormalizado.Length < 12)
            {
                resultado.Debug = "Número inválido. Use DDD + número.";
                return resultado;
            }
            if (string.IsNullOrWhiteSpace(mensagem))
            {
                resultado.Debug = "Mensagem vazia.";
                return resultado;
            }
            if (WhatsAppLogService.JaExiste(externalKey))
            {
                resultado.Debug = "Envio ignorado: externalKey já usado.";
                return resultado;
            }

            var endpoint = MontarEndpoint(config.ApiUrl);
            var query = BuildQuery(new Dictionary<string, string>
            {
                ["body"] = mensagem,
                ["number"] = numeroNormalizado,
                ["externalKey"] = externalKey,
                ["bearertoken"] = config.BearerToken
            });
            var url = endpoint + "?" + query;

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.BearerToken);
                var response = await client.GetAsync(url);
                resultado.HttpStatusCode = (int)response.StatusCode;
                resultado.RespostaBruta = await response.Content.ReadAsStringAsync();
                resultado.Sucesso = response.IsSuccessStatusCode;
                resultado.Debug = $"GET {endpoint}\nNúmero: {numeroNormalizado}\nHTTP: {resultado.HttpStatusCode}";
            }
            catch (Exception ex)
            {
                resultado.Debug = ex.ToString();
            }

            WhatsAppLogService.Registrar(new WhatsAppEnvioLog
            {
                TipoEvento = tipoEvento,
                ExternalKey = externalKey,
                Numero = numeroNormalizado,
                Mensagem = mensagem,
                HttpStatusCode = resultado.HttpStatusCode,
                RespostaApi = resultado.RespostaBruta,
                Debug = resultado.Debug,
                DataHora = DateTime.Now
            });

            return resultado;
        }

        private static string MontarEndpoint(string apiUrl)
        {
            var url = (apiUrl ?? string.Empty).Trim().TrimEnd('/');
            if (url.EndsWith("/params", StringComparison.OrdinalIgnoreCase))
                return url + "/";
            if (url.EndsWith("/params/", StringComparison.OrdinalIgnoreCase))
                return url;
            return url + "/params/";
        }

        private static string BuildQuery(Dictionary<string, string> parametros)
        {
            return string.Join("&", parametros.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value ?? string.Empty)));
        }

        private static string SomenteDigitos(string texto)
        {
            return new string((texto ?? string.Empty).Where(char.IsDigit).ToArray());
        }
    }
}
