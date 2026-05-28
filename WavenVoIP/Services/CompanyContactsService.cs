using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class CompanyContactsService
    {
        // ── Paths ─────────────────────────────────────────────────────────────────

        // Local JSON shipped with the installer (read-only, admin-distributed)
        public static readonly string ArquivoPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Config", "company-contacts.json");

        // Writable export file maintained by the app; admin copies this to GitHub
        public static readonly string ExportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WavenVoIP", "company-contacts-export.json");

        // ── Remote (GitHub raw – read-only) ───────────────────────────────────────

        private const string RemoteUrl =
            "https://raw.githubusercontent.com/rodrigoduartedealmeida84-ui/WavenVoIP/main/servidor_hostinger/company-contacts.json";

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        // ── Remote sync ───────────────────────────────────────────────────────────

        public static async Task<(int sincronizados, int atualizados, string status)> SincronizarRemotoAsync()
        {
            Log("CONTACTS_SYNC_START");
            try
            {
                var json = await _http.GetStringAsync(RemoteUrl).ConfigureAwait(false);
                var remota = JsonSerializer.Deserialize<List<CompanyContact>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<CompanyContact>();

                var contatos        = ContatoStorageService.Carregar();
                var sincronizados   = 0;
                var atualizados     = 0;
                var favoritosAdic   = 0;
                var alterou         = false;

                foreach (var cc in remota)
                {
                    var numero = SomenteDigitos(cc.Numero);
                    if (string.IsNullOrWhiteSpace(numero)) continue;
                    var numNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(numero);
                    if (string.IsNullOrWhiteSpace(numNorm)) numNorm = numero;

                    if (cc.Excluido)
                    {
                        var removidos = contatos.RemoveAll(c =>
                        {
                            if (c.EhRamalIssabel || c.FonteGoogle) return false;
                            var cn    = SomenteDigitos(c.Numero);
                            var cnNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(cn);
                            return cn == numNorm || cnNorm == numNorm;
                        });
                        if (removidos > 0)
                        {
                            Log($"CONTACT_DELETED | numero={numNorm}");
                            alterou = true;
                        }
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(cc.Nome)) continue;

                    var existente = contatos.FirstOrDefault(c =>
                    {
                        var cn    = SomenteDigitos(c.Numero);
                        var cnNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(cn);
                        return cn == numNorm || cnNorm == numNorm;
                    });

                    if (existente != null)
                    {
                        var localDt  = existente.AtualizadoEm ?? DateTime.MinValue;
                        var remoteDt = cc.AtualizadoEm        ?? DateTime.MinValue;

                        if (remoteDt > localDt && !existente.EhRamalIssabel && !existente.FonteGoogle)
                        {
                            existente.Nome       = cc.Nome;
                            if (!string.IsNullOrWhiteSpace(cc.Empresa)) existente.Observacao = cc.Empresa;
                            existente.AtualizadoEm = remoteDt;
                            Log($"CONTACT_UPDATED | nome={cc.Nome} numero={numNorm}");
                            atualizados++;
                            alterou = true;
                        }
                        else
                        {
                            Log($"CONTACT_DUPLICATE_MERGED | nome={cc.Nome} numero={numNorm} local_mais_recente");
                        }
                    }
                    else
                    {
                        contatos.Add(new Contato
                        {
                            Nome       = cc.Nome,
                            Numero     = numNorm,
                            Observacao = cc.Empresa,
                            AtualizadoEm = cc.AtualizadoEm ?? DateTime.Now
                        });
                        Log($"CONTACT_CREATED | nome={cc.Nome} numero={numNorm}");
                        sincronizados++;
                        alterou = true;
                    }

                    if (cc.Favorito)
                    {
                        var nomeFav = existente?.Nome ?? cc.Nome;
                        bool adicionado = FavoritesStorageService.Adicionar(new FavoriteItem
                        {
                            Nome    = nomeFav,
                            Numero  = numNorm,
                            Favorito = true
                        });
                        if (adicionado)
                        {
                            Log($"FAVORITE_SYNCED | nome={nomeFav} numero={numNorm}");
                            favoritosAdic++;
                        }
                    }
                }

                if (alterou)
                    ContatoStorageService.Salvar(contatos);

                Log($"CONTACTS_SYNC_DONE | sincronizados={sincronizados} atualizados={atualizados} favoritos={favoritosAdic}");
                return (sincronizados, atualizados, $"Sync OK: +{sincronizados} atualizados={atualizados}");
            }
            catch (HttpRequestException ex)
            {
                Log($"CONTACTS_SYNC_FAILED | offline: {ex.Message}");
                return (0, 0, "Offline");
            }
            catch (TaskCanceledException)
            {
                Log("CONTACTS_SYNC_FAILED | timeout");
                return (0, 0, "Timeout");
            }
            catch (JsonException ex)
            {
                Log($"CONTACTS_SYNC_FAILED | JSON inválido: {ex.Message}");
                return (0, 0, "JSON inválido");
            }
            catch (Exception ex)
            {
                Log($"CONTACTS_SYNC_FAILED | {ex.GetType().Name}: {ex.Message}");
                return (0, 0, $"Erro: {ex.Message}");
            }
        }

        // ── Local import (from installer Config folder) ────────────────────────────

        public static (int importados, int atualizados, int favoritos) ImportarSeExistir()
        {
            if (!File.Exists(ArquivoPath)) return (0, 0, 0);

            List<CompanyContact> lista;
            try
            {
                var json = File.ReadAllText(ArquivoPath);
                lista = JsonSerializer.Deserialize<List<CompanyContact>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<CompanyContact>();
            }
            catch (Exception ex)
            {
                Log($"COMPANY_CONTACTS_IMPORT_ERROR | {ex.Message}");
                return (0, 0, 0);
            }

            Log($"COMPANY_CONTACTS_IMPORT_START | arquivo={ArquivoPath} total={lista.Count}");

            var contatos = ContatoStorageService.Carregar();
            var importados          = 0;
            var atualizados         = 0;
            var favoritosAdicionados = 0;
            var alterou             = false;

            foreach (var cc in lista.Where(c => !c.Excluido))
            {
                var numero = SomenteDigitos(cc.Numero);
                if (string.IsNullOrWhiteSpace(numero) || string.IsNullOrWhiteSpace(cc.Nome))
                    continue;

                var numeroNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(numero);
                if (string.IsNullOrWhiteSpace(numeroNorm)) numeroNorm = numero;

                var existente = contatos.FirstOrDefault(c =>
                {
                    var cn    = SomenteDigitos(c.Numero);
                    var cnNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(cn);
                    return cn == numero || cnNorm == numeroNorm || cn == numeroNorm;
                });

                if (existente != null)
                {
                    bool mudouNome    = !string.Equals(existente.Nome,       cc.Nome,    StringComparison.OrdinalIgnoreCase);
                    bool mudouEmpresa = !string.IsNullOrWhiteSpace(cc.Empresa) &&
                                       !string.Equals(existente.Observacao, cc.Empresa, StringComparison.OrdinalIgnoreCase);

                    if (!mudouNome && !mudouEmpresa)
                    {
                        Log($"COMPANY_CONTACT_DUPLICATE_SKIPPED | nome={cc.Nome} numero={numero}");
                    }
                    else
                    {
                        if (mudouNome)    existente.Nome       = cc.Nome;
                        if (mudouEmpresa) existente.Observacao = cc.Empresa;
                        existente.AtualizadoEm = DateTime.Now;
                        Log($"COMPANY_CONTACT_UPDATED | nome={cc.Nome} numero={numero}");
                        atualizados++;
                        alterou = true;
                    }
                }
                else
                {
                    contatos.Add(new Contato
                    {
                        Nome         = cc.Nome,
                        Numero       = numeroNorm,
                        Observacao   = cc.Empresa,
                        AtualizadoEm = cc.AtualizadoEm ?? DateTime.Now
                    });
                    Log($"COMPANY_CONTACT_IMPORTED | nome={cc.Nome} numero={numeroNorm}");
                    importados++;
                    alterou = true;
                }

                if (cc.Favorito)
                {
                    var numFav  = existente?.Numero ?? numeroNorm;
                    var nomeFav = cc.Nome;
                    bool adicionado = FavoritesStorageService.Adicionar(new FavoriteItem
                    {
                        Nome    = nomeFav,
                        Numero  = numFav,
                        Favorito = true
                    });
                    if (adicionado)
                    {
                        Log($"COMPANY_FAVORITE_IMPORTED | nome={nomeFav} numero={numFav}");
                        favoritosAdicionados++;
                    }
                }
            }

            if (alterou)
                ContatoStorageService.Salvar(contatos);

            if (importados > 0)
                Log($"COMPANY_CONTACTS_IMPORTED_ON_UPDATE | importados={importados}");
            if (favoritosAdicionados > 0)
                Log($"COMPANY_FAVORITES_IMPORTED_ON_UPDATE | favoritos={favoritosAdicionados}");
            Log($"COMPANY_CONTACTS_IMPORT_DONE | importados={importados} atualizados={atualizados} favoritos={favoritosAdicionados}");
            return (importados, atualizados, favoritosAdicionados);
        }

        // ── Local export (for admin to upload to GitHub) ──────────────────────────

        public static void AtualizarArquivoLocal(IEnumerable<Contato> contatos, IEnumerable<FavoriteItem> favoritos)
        {
            Log("COMPANY_CONTACTS_EXPORT_START");
            try
            {
                var favNums = new HashSet<string>(
                    favoritos.Select(f => PhoneNumberNormalizer.NormalizeBrazilPhone(SomenteDigitos(f.Numero))),
                    StringComparer.OrdinalIgnoreCase);

                // Load existing export to preserve tombstones (excluido: true)
                List<CompanyContact> exportExistente = new();
                if (File.Exists(ExportPath))
                {
                    try
                    {
                        exportExistente = JsonSerializer.Deserialize<List<CompanyContact>>(
                            File.ReadAllText(ExportPath),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                    }
                    catch { }
                }

                var tombs = exportExistente.Where(e => e.Excluido).ToList();

                var ativos = contatos
                    .Where(c => !c.EhRamalIssabel && !c.FonteGoogle)
                    .Select(c =>
                    {
                        var numNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(SomenteDigitos(c.Numero));
                        if (string.IsNullOrWhiteSpace(numNorm)) numNorm = SomenteDigitos(c.Numero);
                        return new CompanyContact
                        {
                            Id                = numNorm,
                            Nome              = c.Nome,
                            Numero            = c.Numero,
                            NumeroNormalizado = numNorm,
                            Empresa           = c.Observacao ?? string.Empty,
                            Favorito          = favNums.Contains(numNorm),
                            AtualizadoEm      = c.AtualizadoEm ?? DateTime.Now,
                            Excluido          = false
                        };
                    })
                    .ToList();

                // Tombstones for numbers that are no longer in active contacts
                var ativosNums = new HashSet<string>(
                    ativos.Select(a => a.NumeroNormalizado), StringComparer.OrdinalIgnoreCase);
                var tombsFiltrados = tombs.Where(t => !ativosNums.Contains(t.NumeroNormalizado)).ToList();

                var exportLista = ativos.Concat(tombsFiltrados).OrderBy(c => c.Nome).ToList();

                var dir = Path.GetDirectoryName(ExportPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ExportPath,
                    JsonSerializer.Serialize(exportLista, new JsonSerializerOptions { WriteIndented = true }));
                Log($"COMPANY_CONTACTS_EXPORT_DONE | ativos={ativos.Count} total={exportLista.Count}");
            }
            catch (Exception ex)
            {
                Log($"CONTACTS_EXPORT_UPDATE_ERROR | {ex.Message}");
            }
        }

        public static void MarcarExcluidoNoExport(string numero)
        {
            try
            {
                var numNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(SomenteDigitos(numero));
                if (string.IsNullOrWhiteSpace(numNorm)) return;

                List<CompanyContact> lista = new();
                if (File.Exists(ExportPath))
                {
                    try
                    {
                        lista = JsonSerializer.Deserialize<List<CompanyContact>>(
                            File.ReadAllText(ExportPath),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                    }
                    catch { }
                }

                var existente = lista.FirstOrDefault(c =>
                    string.Equals(c.NumeroNormalizado, numNorm, StringComparison.OrdinalIgnoreCase));

                if (existente != null)
                {
                    existente.Excluido    = true;
                    existente.AtualizadoEm = DateTime.Now;
                }
                else
                {
                    lista.Add(new CompanyContact
                    {
                        Id                = numNorm,
                        NumeroNormalizado = numNorm,
                        Numero            = numNorm,
                        Excluido          = true,
                        AtualizadoEm      = DateTime.Now
                    });
                }

                var dir = Path.GetDirectoryName(ExportPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ExportPath,
                    JsonSerializer.Serialize(lista, new JsonSerializerOptions { WriteIndented = true }));
                Log($"CONTACT_DELETED | numero={numNorm} marcado_excluido");
            }
            catch (Exception ex)
            {
                Log($"CONTACTS_EXPORT_DELETE_ERROR | {ex.Message}");
            }
        }

        public static void Exportar(string destPath, IEnumerable<Contato> contatos, IEnumerable<FavoriteItem> favoritos)
        {
            // First update the local export (ensures tombstones are included)
            AtualizarArquivoLocal(contatos, favoritos);

            // Copy the maintained export to the chosen destination
            if (File.Exists(ExportPath) && !string.Equals(ExportPath, destPath, StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                File.Copy(ExportPath, destPath, overwrite: true);
                Log($"COMPANY_CONTACTS_EXPORTED | destino={destPath}");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static string SomenteDigitos(string? valor) =>
            string.IsNullOrWhiteSpace(valor) ? "" : new string(valor.Where(char.IsDigit).ToArray());

        private static void Log(string msg)
        {
            try { LogHelper.Info(msg); } catch { }
        }
    }
}
