using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.PeopleService.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class GoogleContactsService
    {
        private static readonly string _tokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP", "GoogleToken");

        private static readonly string _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP", "google_contacts_cache.json");

        private static readonly string _credentialsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Config", "google_credentials.json");

        private static readonly string[] _scopes = { PeopleServiceService.ScopeConstants.ContactsReadonly };
        private const string _appName = "WavenVoIP";

        public static bool EstaConectado()
        {
            try
            {
                var tokenFile = Path.Combine(_tokenPath,
                    "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user");
                return File.Exists(tokenFile);
            }
            catch { return false; }
        }

        public static async Task<UserCredential> AutenticarAsync(CancellationToken cancellationToken = default)
        {
            await using var stream = new FileStream(_credentialsPath, FileMode.Open, FileAccess.Read);
            var secrets = await GoogleClientSecrets.FromStreamAsync(stream, cancellationToken);
            Directory.CreateDirectory(_tokenPath);

            LogHelper.Info("[GOOGLE_LOGIN_STARTED_BY_USER] AutenticarAsync iniciado pelo usuario");
            return await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets.Secrets,
                _scopes,
                "user",
                cancellationToken,
                new FileDataStore(_tokenPath, true));
        }

        /// Loads existing token without opening any browser.
        /// Returns null if token is absent or refresh fails — never prompts for login.
        public static async Task<UserCredential?> ObterCredencialSilenciosamente(CancellationToken ct = default)
        {
            try
            {
                if (!EstaConectado()) { LogHelper.Info("[GOOGLE_TOKEN_MISSING] Token ausente"); return null; }
                if (!File.Exists(_credentialsPath)) return null;

                await using var stream = new FileStream(_credentialsPath, FileMode.Open, FileAccess.Read);
                var secrets = await GoogleClientSecrets.FromStreamAsync(stream, ct);

                var flow = new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow(
                    new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow.Initializer
                    {
                        ClientSecrets = secrets.Secrets,
                        Scopes        = _scopes,
                        DataStore     = new FileDataStore(_tokenPath, true)
                    });

                var token = await flow.LoadTokenAsync("user", ct);
                if (token == null) { LogHelper.Info("[GOOGLE_TOKEN_MISSING] Token nao encontrado no store"); return null; }

                var credential = new UserCredential(flow, "user", token);
                if (credential.Token.IsExpired(Google.Apis.Util.SystemClock.Default))
                    await credential.RefreshTokenAsync(ct); // silent refresh using refresh_token, no browser

                return credential;
            }
            catch (Exception ex)
            {
                // v2.5.0 — falha de rede ao renovar o token (ex.: computador voltando da suspensão,
                // sem DNS por alguns segundos) NÃO é token inválido. Antes virava "acesso revogado":
                // aviso de reconectar na tela e a sincronização com o Google parava de vez.
                // Erro de autenticação de verdade (invalid_grant etc.) continua devolvendo null.
                var semRede = false;
                for (var e = ex; e != null && !semRede; e = e.InnerException)
                    semRede = e is System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException
                                or TimeoutException or OperationCanceledException;
                if (semRede)
                {
                    LogHelper.Info($"[GOOGLE_TOKEN_REFRESH_SEM_REDE] {ex.Message}");
                    throw;
                }
                LogHelper.Info($"[GOOGLE_TOKEN_INVALID] Falha ao carregar/renovar token: {ex.Message}");
                return null;
            }
        }

        /// Syncs contacts using the stored token only — NEVER opens a browser.
        /// Throws UnauthorizedAccessException if token is absent or invalid.
        public static async Task<(List<Contato> Contatos, int Total)> SincronizarSemBrowserAsync(
            CancellationToken ct = default)
        {
            var credential = await ObterCredencialSilenciosamente(ct);
            if (credential == null)
                throw new UnauthorizedAccessException("GOOGLE_TOKEN_MISSING — faça login pelo botão Conectar Google.");

            var service = new PeopleServiceService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName       = _appName
            });

            var contatos  = new List<Contato>();
            string? pageToken = null;

            do
            {
                var request = service.People.Connections.List("people/me");
                request.PersonFields = "names,phoneNumbers,metadata";
                request.PageSize     = 1000;
                if (pageToken != null) request.PageToken = pageToken;

                var response = await request.ExecuteAsync(ct);

                if (response.Connections != null)
                {
                    foreach (var person in response.Connections)
                    {
                        var nome = person.Names?.FirstOrDefault()?.DisplayName?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(nome)) continue;

                        var telefones = person.PhoneNumbers ?? new List<Google.Apis.PeopleService.v1.Data.PhoneNumber>();
                        foreach (var phone in telefones)
                        {
                            var numero = SomenteDigitos(phone.Value ?? string.Empty);
                            if (string.IsNullOrWhiteSpace(numero) || numero.Length < 7) continue;

                            contatos.Add(new Contato
                            {
                                Nome            = nome,
                                Numero          = numero,
                                Observacao      = "Google Contacts",
                                FonteGoogle     = true,
                                AtualizadoEm    = DataDeAlteracao(person),   // data real da última alteração no Google
                                GoogleContactId = person.ResourceName // ex: "people/c1234567890"
                            });
                        }
                    }
                }
                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            await SalvarCacheAsync(contatos);
            return (contatos, contatos.Count);
        }

        public static async Task<(List<Contato> Contatos, int Total)> SincronizarContatosAsync(
            CancellationToken cancellationToken = default)
        {
            var credential = await AutenticarAsync(cancellationToken);

            var service = new PeopleServiceService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = _appName
            });

            var contatos = new List<Contato>();
            string? pageToken = null;

            do
            {
                var request = service.People.Connections.List("people/me");
                request.PersonFields = "names,phoneNumbers,metadata";
                request.PageSize = 1000;
                if (pageToken != null) request.PageToken = pageToken;

                var response = await request.ExecuteAsync(cancellationToken);

                if (response.Connections != null)
                {
                    foreach (var person in response.Connections)
                    {
                        var nome = person.Names?.FirstOrDefault()?.DisplayName?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(nome)) continue;

                        var telefones = person.PhoneNumbers ?? new List<Google.Apis.PeopleService.v1.Data.PhoneNumber>();
                        foreach (var phone in telefones)
                        {
                            var numero = SomenteDigitos(phone.Value ?? string.Empty);
                            if (string.IsNullOrWhiteSpace(numero) || numero.Length < 7) continue;

                            contatos.Add(new Contato
                            {
                                Nome = nome,
                                Numero = numero,
                                Observacao = "Google Contacts",
                                FonteGoogle = true,
                                AtualizadoEm = DataDeAlteracao(person),
                                GoogleContactId = person.ResourceName
                            });
                        }
                    }
                }

                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            await SalvarCacheAsync(contatos);
            return (contatos, contatos.Count);
        }

        public static async Task<List<Contato>> CarregarCacheAsync()
        {
            try
            {
                if (!File.Exists(_cachePath)) return new List<Contato>();
                var json = await File.ReadAllTextAsync(_cachePath);
                return JsonSerializer.Deserialize<List<Contato>>(json) ?? new List<Contato>();
            }
            catch { return new List<Contato>(); }
        }

        // v2.5.0 — data da última alteração do contato NO GOOGLE (antes era gravado "agora" a cada
        // sincronização, o que impedia saber qual lado tinha o nome mais recente).
        private static DateTime? DataDeAlteracao(Google.Apis.PeopleService.v1.Data.Person person)
        {
            try
            {
                DateTime? maior = null;
                foreach (var s in person.Metadata?.Sources ?? new List<Google.Apis.PeopleService.v1.Data.Source>())
                {
                    var t = s.UpdateTimeDateTimeOffset?.UtcDateTime;
                    if (t != null && (maior == null || t > maior)) maior = t;
                }
                return maior;
            }
            catch { return null; }
        }

        private static bool TemEscopoDeEscrita(UserCredential? credential)
        {
            var escopos = (credential?.Token?.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return escopos.Any(e => string.Equals(e, PeopleServiceService.ScopeConstants.Contacts, StringComparison.OrdinalIgnoreCase));
        }

        /// O WavenVoIP pode alterar contatos no Google? (a conexão padrão é só de leitura)
        public static async Task<bool> TemPermissaoDeEscritaAsync(CancellationToken ct = default)
        {
            try { return TemEscopoDeEscrita(await ObterCredencialSilenciosamente(ct)); } catch { return false; }
        }

        /// Pede ao usuário, no navegador, a permissão de edição dos contatos do Google.
        public static async Task<bool> AutorizarEscritaAsync(CancellationToken ct = default)
        {
            try
            {
                await LimparTokenAsync();
                await using var stream = new FileStream(_credentialsPath, FileMode.Open, FileAccess.Read);
                var secrets = await GoogleClientSecrets.FromStreamAsync(stream, ct);
                Directory.CreateDirectory(_tokenPath);
                LogHelper.Info("[GOOGLE_LOGIN_STARTED_BY_USER] AutorizarEscritaAsync (permissao de edicao de contatos)");
                var cred = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    secrets.Secrets, new[] { PeopleServiceService.ScopeConstants.Contacts }, "user", ct, new FileDataStore(_tokenPath, true));
                var ok = TemEscopoDeEscrita(cred);
                LogHelper.Info($"GOOGLE_WRITE_SCOPE_GRANTED={ok}");
                return ok;
            }
            catch (Exception ex)
            {
                LogHelper.Info($"GOOGLE_WRITE_SCOPE_FAILED | {ex.Message}");
                return false;
            }
        }

        /// Altera o nome de um contato no Google. motivo: "" (ok), "sem_token", "sem_permissao", "erro: ...".
        public static async Task<(bool ok, string motivo)> AtualizarNomeAsync(string resourceName, string novoNome, CancellationToken ct = default)
        {
            try
            {
                var credential = await ObterCredencialSilenciosamente(ct);
                if (credential == null) return (false, "sem_token");
                if (!TemEscopoDeEscrita(credential)) return (false, "sem_permissao");

                var service = new PeopleServiceService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName       = _appName
                });
                var get = service.People.Get(resourceName);
                get.PersonFields = "names,metadata";
                var person = await get.ExecuteAsync(ct);
                var atual = person.Names?.FirstOrDefault()?.DisplayName?.Trim() ?? string.Empty;
                if (string.Equals(atual, novoNome.Trim(), StringComparison.Ordinal)) return (true, string.Empty);

                person.Names = new List<Google.Apis.PeopleService.v1.Data.Name>
                {
                    new Google.Apis.PeopleService.v1.Data.Name { GivenName = novoNome.Trim(), UnstructuredName = novoNome.Trim() }
                };
                var upd = service.People.UpdateContact(person, resourceName);
                upd.UpdatePersonFields = "names";
                await upd.ExecuteAsync(ct);
                LogHelper.Info($"GOOGLE_CONTACT_RENAMED | resourceName={resourceName} de={atual} para={novoNome}");
                return (true, string.Empty);
            }
            catch (Google.GoogleApiException gex) when ((int)gex.HttpStatusCode == 403)
            {
                LogHelper.Info($"GOOGLE_CONTACT_RENAME_DENIED | resourceName={resourceName}");
                return (false, "sem_permissao");
            }
            catch (Exception ex)
            {
                LogHelper.Info($"GOOGLE_CONTACT_RENAME_FAILED | resourceName={resourceName} error={ex.Message}");
                return (false, "erro: " + ex.Message);
            }
        }

        /// Exclui um contato do Google Contacts pelo resourceName (ex: "people/c1234").
        /// Requer escopo contacts (leitura+escrita). Retorna false se token ausente ou sem permissão.
        public static async Task<bool> ExcluirContatoGoogleAsync(string resourceName, CancellationToken ct = default)
        {
            try
            {
                var credential = await ObterCredencialSilenciosamente(ct);
                if (credential == null)
                {
                    LogHelper.Info($"GOOGLE_CONTACT_DELETE_SKIPPED | reason=no_token resourceName={resourceName}");
                    return false;
                }

                var service = new PeopleServiceService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName       = _appName
                });

                await service.People.DeleteContact(resourceName).ExecuteAsync(ct);
                LogHelper.Info($"GOOGLE_CONTACT_DELETED | resourceName={resourceName}");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.Info($"GOOGLE_CONTACT_DELETE_FAILED | resourceName={resourceName} error={ex.Message}");
                return false;
            }
        }

        public static async Task LimparTokenAsync()
        {
            try
            {
                if (Directory.Exists(_tokenPath))
                    foreach (var f in Directory.GetFiles(_tokenPath))
                        File.Delete(f);
            }
            catch { }
            try
            {
                if (File.Exists(_cachePath))
                    File.Delete(_cachePath);
            }
            catch { }
            await Task.CompletedTask;
        }

        private static async Task SalvarCacheAsync(List<Contato> contatos)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                var json = JsonSerializer.Serialize(contatos, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_cachePath, json);
            }
            catch { }
        }

        private static string SomenteDigitos(string valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return string.Empty;
            return new string(valor.Where(char.IsDigit).ToArray());
        }
    }
}
