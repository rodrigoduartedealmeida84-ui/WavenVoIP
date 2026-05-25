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
                request.PersonFields = "names,phoneNumbers";
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
                                Nome         = nome,
                                Numero       = numero,
                                Observacao   = "Google Contacts",
                                FonteGoogle  = true,
                                AtualizadoEm = DateTime.Now
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
                request.PersonFields = "names,phoneNumbers";
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
                                AtualizadoEm = DateTime.Now
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
