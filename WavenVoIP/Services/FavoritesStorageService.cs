using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class FavoritesStorageService
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP", "favorites.json");

        // Cache em memória: evita releitura do arquivo a cada chamada a Carregar()
        private static List<FavoriteItem>? _cache;
        private static readonly object _cacheLock = new object();

        // Disparado sempre que a lista de favoritos é persistida (add/remove/rename).
        // Permite que a UI se atualize automaticamente sem polling manual.
        public static event Action? FavoritosAlterados;

        private static void InvalidarCache() { lock (_cacheLock) { _cache = null; } }

        public static List<FavoriteItem> Carregar()
        {
            lock (_cacheLock)
            {
                if (_cache != null) return _cache;
                try
                {
                    if (!File.Exists(_path)) { _cache = new List<FavoriteItem>(); return _cache; }
                    var json = File.ReadAllText(_path);
                    _cache = (JsonSerializer.Deserialize<List<FavoriteItem>>(json) ?? new List<FavoriteItem>())
                             .OrderBy(f => f.Ordem).ThenBy(f => f.Nome).ToList();
                    Log($"FAVORITO_LOAD | count={_cache.Count} origem=arquivo");
                    return _cache;
                }
                catch
                {
                    _cache = new List<FavoriteItem>();
                    return _cache;
                }
            }
        }

        public static void Salvar(List<FavoriteItem> favoritos)
        {
            InvalidarCache();
            try
            {
                Log($"FAVORITE_SAVE_START | count={favoritos.Count}");
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var json = JsonSerializer.Serialize(favoritos, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
                Log($"FAVORITE_SAVE_OK | count={favoritos.Count}");
                Log($"FAVORITO_SAVE | count={favoritos.Count}");
                try { FavoritosAlterados?.Invoke(); } catch { }
            }
            catch (Exception ex) { Log($"FAVORITE_SAVE_ERROR | {ex.Message}"); }
        }

        // Regra de match (v2.4.1): 1) ID persistente do contato, se houver;
        // 2) número canônico normalizado (mesma normalização usada para discagem/CDR,
        //    trata sem-9/com-9/55/+55 como o MESMO favorito); nome nunca é chave.
        public static bool Adicionar(FavoriteItem item)
        {
            var lista = Carregar();
            var numeroNorm = NumeroCanonico(item.Numero);
            var jaExiste = lista.Any(f => EhMesmoFavorito(f, item.ContactId, numeroNorm));
            if (jaExiste)
            {
                Log($"FAVORITE_DUPLICATE_SKIP | nome={item.Nome} numero={MascararTelefone(item.Numero)}");
                return false;
            }
            item.Ordem = lista.Count > 0 ? lista.Max(f => f.Ordem) + 1 : 0;
            lista.Add(item);
            Salvar(lista);
            Log($"FAVORITE_ADDED | nome={item.Nome} numero={MascararTelefone(item.Numero)}");
            Log($"FAVORITO_ADD | contactId={item.ContactId ?? "(none)"} numero={MascararTelefone(item.Numero)} numeroNorm={MascararTelefone(numeroNorm)}");
            return true;
        }

        public static bool Remover(string numero, string? contactId = null)
        {
            var lista = Carregar();
            var n = NumeroCanonico(numero);
            var antes = lista.Count;
            lista.RemoveAll(f => EhMesmoFavorito(f, contactId, n));
            if (lista.Count == antes) return false;
            Salvar(lista);
            Log($"FAVORITE_REMOVED | numero={MascararTelefone(numero)}");
            Log($"FAVORITO_REMOVE | contactId={contactId ?? "(none)"} numero={MascararTelefone(numero)} numeroNorm={MascararTelefone(n)}");
            return true;
        }

        // Um FavoriteItem casa com (contactId, numeroNorm) se o ID bater (quando ambos
        // os lados têm ID) OU se o número canônico normalizado bater. Nunca usa nome.
        private static bool EhMesmoFavorito(FavoriteItem f, string? contactId, string numeroNorm)
        {
            if (!string.IsNullOrWhiteSpace(contactId) && !string.IsNullOrWhiteSpace(f.ContactId) &&
                string.Equals(f.ContactId, contactId, StringComparison.OrdinalIgnoreCase))
                return true;
            return NumeroCanonico(f.Numero) == numeroNorm;
        }

        private static string NumeroCanonico(string? numero)
            => PhoneNumberNormalizer.NormalizeBrazilPhone(SomenteDigitos(numero));

        /// <summary>
        /// Resolve o nome exibido de cada favorito a partir do contato atual (por ContactId,
        /// com fallback por telefone normalizado). Atualiza e persiste quando o nome mudou ou
        /// quando um favorito antigo (sem ContactId) pôde ser vinculado. Não apaga, não duplica,
        /// não altera Ordem/UsageCount/Numero.
        /// </summary>
        public static bool AtualizarNomesPorContatos(List<Contato> contatos)
        {
            if (contatos == null || contatos.Count == 0) return false;

            var lista = Carregar();
            var alterou = false;

            foreach (var fav in lista)
            {
                Contato? contato = null;

                if (!string.IsNullOrWhiteSpace(fav.ContactId))
                {
                    contato = contatos.FirstOrDefault(c =>
                        !string.IsNullOrWhiteSpace(c.WavenApiId) &&
                        string.Equals(c.WavenApiId, fav.ContactId, StringComparison.OrdinalIgnoreCase));
                    if (contato != null)
                        Log($"FAVORITE_CONTACT_LINK_BY_ID | contactId={fav.ContactId} nome={contato.Nome}");
                }

                if (contato == null)
                {
                    var favNum = PhoneNumberNormalizer.NormalizeBrazilPhone(SomenteDigitos(fav.Numero));
                    contato = contatos.FirstOrDefault(c =>
                        !c.EhRamalIssabel &&
                        PhoneNumberNormalizer.NormalizeBrazilPhone(SomenteDigitos(c.Numero)) == favNum);

                    if (contato != null)
                    {
                        Log($"FAVORITE_CONTACT_LINK_BY_PHONE | numero={MascararTelefone(fav.Numero)} nome={contato.Nome}");
                        if (!string.IsNullOrWhiteSpace(contato.WavenApiId) &&
                            !string.Equals(fav.ContactId, contato.WavenApiId, StringComparison.OrdinalIgnoreCase))
                        {
                            fav.ContactId = contato.WavenApiId;
                            alterou = true;
                        }
                    }
                }

                if (contato == null)
                {
                    Log($"FAVORITE_CONTACT_NOT_FOUND | numero={MascararTelefone(fav.Numero)} nomeAtual={fav.Nome}");
                    continue;
                }

                Log($"FAVORITE_CONTACT_LINK_FOUND | numero={MascararTelefone(fav.Numero)} contactId={contato.WavenApiId ?? "(sem id)"} nome={contato.Nome}");

                if (!string.IsNullOrWhiteSpace(contato.Nome) &&
                    !string.Equals(fav.Nome, contato.Nome, StringComparison.Ordinal))
                {
                    Log($"FAVORITE_CONTACT_NAME_UPDATED | numero={MascararTelefone(fav.Numero)} nomeAntigo={fav.Nome} nomeNovo={contato.Nome}");
                    fav.Nome = contato.Nome;
                    alterou = true;
                }
            }

            if (alterou)
            {
                Salvar(lista);
                Log($"FAVORITE_CONTACT_SYNC_PERSISTED | count={lista.Count}");
            }

            return alterou;
        }

        public static string MascararTelefone(string? numero)
        {
            var n = SomenteDigitos(numero);
            if (n.Length <= 4) return new string('*', n.Length);
            return n.Substring(0, 2) + new string('*', n.Length - 4) + n.Substring(n.Length - 2);
        }

        public static bool EhFavorito(string numero)
        {
            try
            {
                var lista = Carregar();
                var n = NumeroCanonico(numero);
                return lista.Any(f => NumeroCanonico(f.Numero) == n);
            }
            catch { return false; }
        }

        private static string SomenteDigitos(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return string.Empty;
            return new string(valor.Where(char.IsDigit).ToArray());
        }

        private static void Log(string msg)
        {
            try { LogHelper.Info(msg); }
            catch { }
        }
    }
}
