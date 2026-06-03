using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    /// <summary>
    /// Cliente HTTP para a Waven API. Sem estado — usa a config atual do SipConfig a cada chamada.
    /// </summary>
    public static class WavenApiService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // ── Config helpers ────────────────────────────────────────────────────

        private static (string url, string token) ObterConfig()
        {
            var cfg = SipConfig.CarregarSalva();
            return (cfg?.WavenApiUrl?.TrimEnd('/') ?? string.Empty,
                    cfg?.WavenApiToken ?? string.Empty);
        }

        private static HttpRequestMessage CriarRequest(HttpMethod method, string url, string token, object? body = null)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null)
            {
                var json = JsonSerializer.Serialize(body);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            return req;
        }

        // ── Health ────────────────────────────────────────────────────────────

        public static async Task<(bool ok, string versao, string erro)> TestarAsync()
        {
            var (url, _) = ObterConfig();
            if (string.IsNullOrWhiteSpace(url)) return (false, "", "URL não configurada");
            try
            {
                var resp = await _http.GetAsync($"{url}/health").ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var health = JsonSerializer.Deserialize<WavenApiHealth>(json, _jsonOpts);
                return (health?.Status == "ok", health?.Version ?? "", "");
            }
            catch (Exception ex)
            {
                return (false, "", ex.Message);
            }
        }

        // ── Sync ──────────────────────────────────────────────────────────────

        public static async Task<WavenApiSyncResponse?> SyncAsync(string ramal, DateTime? since)
        {
            var (url, token) = ObterConfig();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token)) return null;

            var sinceStr = since.HasValue
                ? Uri.EscapeDataString(since.Value.ToUniversalTime().ToString("O"))
                : Uri.EscapeDataString(DateTime.MinValue.ToString("O"));

            var uri = $"{url}/api/contacts/sync?ramal={Uri.EscapeDataString(ramal)}&since={sinceStr}";
            try
            {
                using var req = CriarRequest(HttpMethod.Get, uri, token);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonSerializer.Deserialize<WavenApiSyncResponse>(json, _jsonOpts);
            }
            catch
            {
                return null;
            }
        }

        // ── CRUD contatos ─────────────────────────────────────────────────────

        public static async Task<(bool ok, WavenApiContact? contato, string erro)> CriarContatoAsync(
            string nome, string numero, string? empresa, string? observacao, string ramal)
        {
            var (url, token) = ObterConfig();
            if (string.IsNullOrWhiteSpace(url)) return (false, null, "URL não configurada");

            var body = new { nome, numero, empresa, observacao, criadoPor = ramal };
            try
            {
                using var req = CriarRequest(HttpMethod.Post, $"{url}/api/contacts", token, body);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 201)
                {
                    var c = JsonSerializer.Deserialize<WavenApiContact>(json, _jsonOpts);
                    return (true, c, "");
                }
                // 409 = duplicado — retorna o existente
                if ((int)resp.StatusCode == 409)
                {
                    Log($"API_CONTACT_DUPLICATE | numero={numero}");
                    return (true, null, "Duplicado");
                }
                return (false, null, $"HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        public static async Task<bool> AtualizarContatoAsync(
            string id, string nome, string? empresa, string? observacao, string ramal, DateTime atualizadoEm)
        {
            var (url, token) = ObterConfig();
            if (string.IsNullOrWhiteSpace(url)) return false;

            var body = new { nome, empresa, observacao, atualizadoPor = ramal, atualizadoEm };
            try
            {
                using var req = CriarRequest(HttpMethod.Put, $"{url}/api/contacts/{id}", token, body);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public static async Task<bool> ExcluirContatoAsync(string id, string ramal)
        {
            var (url, token) = ObterConfig();
            if (string.IsNullOrWhiteSpace(url)) return false;

            var body = new { excluidoPor = ramal };
            try
            {
                using var req = CriarRequest(HttpMethod.Delete, $"{url}/api/contacts/{id}", token, body);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ── Favoritos ─────────────────────────────────────────────────────────

        public static async Task<bool> AdicionarFavoritoAsync(
            string contactId, string ramal, string tipoFavorito = "usuario", string criadoPor = "")
        {
            var (url, token) = ObterConfig();
            if (string.IsNullOrWhiteSpace(url)) return false;

            var body = new
            {
                ramal         = tipoFavorito == "global" ? (string?)null : ramal,
                tipoFavorito,
                criadoPor     = string.IsNullOrWhiteSpace(criadoPor) ? ramal : criadoPor
            };
            try
            {
                using var req = CriarRequest(HttpMethod.Post, $"{url}/api/contacts/{contactId}/favorite", token, body);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return resp.IsSuccessStatusCode || (int)resp.StatusCode == 201;
            }
            catch { return false; }
        }

        public static async Task<bool> RemoverFavoritoAsync(
            string contactId, string ramal, string tipoFavorito = "usuario")
        {
            var (url, token) = ObterConfig();
            if (string.IsNullOrWhiteSpace(url)) return false;

            var body = new
            {
                ramal         = tipoFavorito == "global" ? (string?)null : ramal,
                tipoFavorito,
                atualizadoPor = ramal
            };
            try
            {
                using var req = CriarRequest(HttpMethod.Delete, $"{url}/api/contacts/{contactId}/favorite", token, body);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ── Fase 2: CDR ──────────────────────────────────────────────────────────

        public static async Task<List<Models.ApiCdrRow>?> GetCdrCallsAsync(string ramal, int dias)
        {
            var (url, token) = ObterConfig();
            if (string.IsNullOrWhiteSpace(url)) return null;

            var uri = $"{url}/api/cdr/calls?ramal={Uri.EscapeDataString(ramal)}&dias={dias}";
            try
            {
                using var req = CriarRequest(HttpMethod.Get, uri, token);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Log($"API_CDR_QUERY_ERROR | status={(int)resp.StatusCode}");
                    return null;
                }
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var result = JsonSerializer.Deserialize<Models.ApiCdrResponse>(json, _jsonOpts);
                return result?.Calls;
            }
            catch (Exception ex) { Log($"API_CDR_QUERY_ERROR | {ex.Message}"); return null; }
        }

        public static async Task<(bool ok, string message)> TestarCdrAsync()
        {
            var (url, token) = ObterConfig();
            if (string.IsNullOrWhiteSpace(url)) return (false, "URL nao configurada");
            try
            {
                using var req  = CriarRequest(HttpMethod.Get, $"{url}/api/cdr/test", token);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return await ParseTesteResponseAsync(resp, "cdr/test");
            }
            catch (Exception ex)
            {
                Log($"API_CDR_TEST_ERROR | {ex.GetType().Name}: {ex.Message}");
                return (false, ex.Message);
            }
        }

        // ── Fase 3: AMI ──────────────────────────────────────────────────────────

        public static async Task<List<Models.ApiAmiExtension>?> GetAmiExtensionsAsync()
        {
            var (url, token) = ObterConfig();
            if (string.IsNullOrWhiteSpace(url)) return null;

            try
            {
                using var req = CriarRequest(HttpMethod.Get, $"{url}/api/ami/extensions", token);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Log($"API_AMI_CONNECT_ERROR | status={(int)resp.StatusCode}");
                    return null;
                }
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonSerializer.Deserialize<List<Models.ApiAmiExtension>>(json, _jsonOpts);
            }
            catch (Exception ex) { Log($"API_AMI_CONNECT_ERROR | {ex.Message}"); return null; }
        }

        public static async Task<(bool ok, string message)> TestarAmiAsync()
        {
            var (url, token) = ObterConfig();
            if (string.IsNullOrWhiteSpace(url)) return (false, "URL nao configurada");
            try
            {
                using var req  = CriarRequest(HttpMethod.Post, $"{url}/api/ami/test", token);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return await ParseTesteResponseAsync(resp, "ami/test");
            }
            catch (Exception ex)
            {
                Log($"API_AMI_TEST_ERROR | {ex.GetType().Name}: {ex.Message}");
                return (false, ex.Message);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static async Task<(bool ok, string message)> ParseTesteResponseAsync(
            HttpResponseMessage resp, string endpoint)
        {
            var status = (int)resp.StatusCode;
            var ct     = resp.Content.Headers.ContentType?.MediaType ?? "";
            var body   = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            Log($"CLIENT_API_RAW_RESPONSE | endpoint={endpoint} status={status} ct={ct} " +
                $"body={body[..Math.Min(300, body.Length)].Replace('\n', ' ').Replace('\r', ' ')}");

            // Body vazio
            if (string.IsNullOrWhiteSpace(body))
                return (false, $"Servidor retornou HTTP {status} com body vazio — " +
                               $"endpoint pode nao existir na versao atual da API.");

            // Resposta nao-JSON (HTML de 404/502 do Apache, pagina de erro, etc.)
            if (!ct.Contains("json", StringComparison.OrdinalIgnoreCase) &&
                !body.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                var preview = body.Length > 120 ? body[..120].Trim() + "..." : body.Trim();
                return (false, $"HTTP {status} — resposta nao-JSON recebida. " +
                               $"Provavel causa: endpoint nao existe nessa versao da API. Preview: {preview}");
            }

            try
            {
                using var doc = JsonDocument.Parse(body);

                // Formato { ok, message } — retornado pelos endpoints de teste
                if (doc.RootElement.TryGetProperty("ok", out var okProp))
                {
                    var ok  = okProp.GetBoolean();
                    var msg = doc.RootElement.TryGetProperty("message", out var mp)
                              ? (mp.GetString() ?? "")
                              : (doc.RootElement.TryGetProperty("error", out var ep)
                                 ? (ep.GetString() ?? "") : "");
                    return (ok, msg);
                }

                // Formato ProblemDetails { status, detail } — retornado por Results.Problem
                if (doc.RootElement.TryGetProperty("detail", out var detailProp))
                    return (false, $"HTTP {status}: {detailProp.GetString()}");

                return (false, $"HTTP {status} — JSON sem campo 'ok': {body[..Math.Min(200, body.Length)]}");
            }
            catch (JsonException jex)
            {
                return (false, $"HTTP {status} — falha ao parsear JSON: {jex.Message} | body: {body[..Math.Min(150, body.Length)]}");
            }
        }

        private static void Log(string msg)
        {
            try { LogHelper.Info(msg); } catch { }
        }
    }
}
