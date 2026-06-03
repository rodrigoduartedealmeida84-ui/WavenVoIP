using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    /// <summary>
    /// Gerencia sincronização periódica de contatos com a Waven API.
    /// Inclui: migração inicial de favoritos globais, offline queue, sync incremental.
    /// </summary>
    public sealed class WavenApiSyncService : IDisposable
    {
        // ── Paths ─────────────────────────────────────────────────────────────

        private static readonly string StateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP", "api-sync.json");

        private static readonly string QueueFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP", "api-queue.json");

        // ── Estado ────────────────────────────────────────────────────────────

        private readonly string _ramal;
        private readonly Timer _timer;
        private readonly CancellationTokenSource _cts = new();
        private int _syncEmAndamento;

        public WavenApiSyncService(string ramal)
        {
            _ramal = ramal;
            // Sync a cada 60 segundos; primeira execução após 3s (deixa a UI carregar)
            _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60));
        }

        // ── Timer callback ────────────────────────────────────────────────────

        private void OnTick(object? _)
        {
            if (_cts.IsCancellationRequested) return;
            if (Interlocked.CompareExchange(ref _syncEmAndamento, 1, 0) != 0) return;
            Task.Run(async () =>
            {
                try   { await SincronizarAsync().ConfigureAwait(false); }
                finally { Interlocked.Exchange(ref _syncEmAndamento, 0); }
            }, _cts.Token);
        }

        // ── Sync principal ────────────────────────────────────────────────────

        public async Task SincronizarAsync()
        {
            var cfg = SipConfig.CarregarSalva();
            if (cfg?.UsarWavenApi != true ||
                string.IsNullOrWhiteSpace(cfg.WavenApiUrl) ||
                string.IsNullOrWhiteSpace(cfg.WavenApiToken)) return;

            Log("API_CONTACT_SYNC_START");

            // 1. Migração única: exporta favoritos atuais como globais de sistema
            var state = CarregarState();
            if (!state.MigracaoConcluida)
            {
                await MigrarFavoritosGlobaisAsync();
                state.MigracaoConcluida = true;
                SalvarState(state);
            }

            // 2. Drena fila offline antes de sincronizar
            await DrenaFilaOfflineAsync();

            // 3. Sync incremental
            var sync = await WavenApiService.SyncAsync(_ramal, state.LastSyncUtc).ConfigureAwait(false);

            if (sync == null)
            {
                Log("API_CONTACT_SYNC_FAILED | offline ou erro de autenticação");
                return;
            }

            // 4. Aplica contatos recebidos na storage local
            AplicarContatos(sync.Contacts, sync.FavoritosAtuais);

            state.LastSyncUtc = sync.ServerTime;
            SalvarState(state);

            Log($"API_CONTACT_SYNC_DONE | contacts={sync.Contacts.Count} favoritos={sync.FavoritosAtuais.Count}");
        }

        // ── Migração inicial dos 7 favoritos globais ──────────────────────────

        private async Task MigrarFavoritosGlobaisAsync()
        {
            Log("API_CONTACT_GLOBAL_FAVORITE_MIGRATION_START");

            var favoritos = FavoritesStorageService.Carregar();
            if (favoritos.Count == 0)
            {
                Log("API_CONTACT_GLOBAL_FAVORITE_MIGRATION_DONE | nenhum favorito local para migrar");
                return;
            }

            var contatos = ContatoStorageService.Carregar();
            var migrados = 0;

            foreach (var fav in favoritos)
            {
                var numNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(
                    new string(fav.Numero.Where(char.IsDigit).ToArray()));

                // Encontra o contato local correspondente
                var contatoLocal = contatos.FirstOrDefault(c =>
                {
                    var cn = PhoneNumberNormalizer.NormalizeBrazilPhone(
                        new string(c.Numero.Where(char.IsDigit).ToArray()));
                    return cn == numNorm;
                });

                var nome = contatoLocal?.Nome ?? fav.Nome;
                var numero = numNorm;

                if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(numero)) continue;

                // Cria o contato na API (retorna existente se já existe)
                var (ok, apiContact, _) = await WavenApiService.CriarContatoAsync(
                    nome, numero,
                    empresa: contatoLocal?.Observacao,
                    observacao: null,
                    ramal: "system").ConfigureAwait(false);

                if (!ok || apiContact == null)
                {
                    // Contato duplicado (409) — tenta obter o ID via sync full
                    var syncFull = await WavenApiService.SyncAsync(_ramal, null).ConfigureAwait(false);
                    apiContact = syncFull?.Contacts.FirstOrDefault(c => c.NumeroNormalizado == numNorm);
                }

                if (apiContact == null) continue;

                // Marca como favorito GLOBAL (CriadoPor="system")
                var favOk = await WavenApiService.AdicionarFavoritoAsync(
                    apiContact.Id, ramal: "", tipoFavorito: "global", criadoPor: "system")
                    .ConfigureAwait(false);

                if (favOk)
                {
                    Log($"API_CONTACT_FAVORITE_MIGRATED | id={apiContact.Id} nome={nome} tipo=global");
                    migrados++;
                }
            }

            Log($"API_CONTACT_GLOBAL_FAVORITE_MIGRATION_DONE | migrados={migrados}");
        }

        // ── Aplica contatos da API na storage local ───────────────────────────

        private void AplicarContatos(List<WavenApiContact> apiContacts, List<string> favoritosAtuaisIds)
        {
            var favSet = new HashSet<string>(favoritosAtuaisIds, StringComparer.OrdinalIgnoreCase);
            var contatos = ContatoStorageService.Carregar();
            var alterou = false;

            foreach (var ac in apiContacts)
            {
                if (ac.Excluido)
                {
                    // Tombstone da API: remove contato local se existir (não ramal, não Google)
                    var removidos = contatos.RemoveAll(c =>
                    {
                        if (c.EhRamalIssabel || c.FonteGoogle) return false;
                        var cn = PhoneNumberNormalizer.NormalizeBrazilPhone(
                            new string(c.Numero.Where(char.IsDigit).ToArray()));
                        return cn == ac.NumeroNormalizado;
                    });
                    if (removidos > 0)
                    {
                        Log($"API_CONTACT_DELETED | nome={ac.Nome} numero={ac.NumeroNormalizado}");
                        alterou = true;
                    }
                    continue;
                }

                var existente = contatos.FirstOrDefault(c =>
                {
                    if (c.EhRamalIssabel || c.FonteGoogle) return false;
                    var cn = PhoneNumberNormalizer.NormalizeBrazilPhone(
                        new string(c.Numero.Where(char.IsDigit).ToArray()));
                    return cn == ac.NumeroNormalizado;
                });

                if (existente != null)
                {
                    // Normaliza ambos para UTC antes de comparar (evita bug de timezone)
                    var localDtUtc = NormalizarParaUtc(existente.AtualizadoEm ?? DateTime.MinValue);
                    var apiDtUtc   = NormalizarParaUtc(ac.AtualizadoEm);

                    // Não sobrescrever se há UPDATE pendente na fila — preserva edição local
                    if (TemOpPendenteOffline(ac.Id, "UPDATE"))
                    {
                        Log($"API_CONTACT_UPDATE_SKIPPED_PENDING | id={ac.Id} nome={ac.Nome}");
                    }
                    else if (apiDtUtc > localDtUtc)
                    {
                        existente.Nome        = ac.Nome;
                        existente.Observacao  = ac.Empresa ?? ac.Observacao ?? string.Empty;
                        existente.AtualizadoEm = ac.AtualizadoEm;
                        Log($"API_CONTACT_UPDATED | nome={ac.Nome} numero={ac.NumeroNormalizado}");
                        alterou = true;
                    }

                    // Sempre atualiza WavenApiId se ainda não estava vinculado
                    if (string.IsNullOrWhiteSpace(existente.WavenApiId))
                    {
                        existente.WavenApiId = ac.Id;
                        alterou = true;
                    }
                }
                else
                {
                    // Anti-ressurreição: não recriar contato que foi excluído localmente
                    if (ContactTombstoneService.EstaSuprimidoPorWavenApiId(ac.Id))
                    {
                        Log($"API_SYNC_SUPPRESSED_BY_TOMBSTONE | id={ac.Id} nome={ac.Nome}");
                        continue;
                    }

                    // Anti-ressurreição: não recriar se DELETE ainda está na fila offline
                    if (TemOpPendenteOffline(ac.Id, "DELETE"))
                    {
                        Log($"API_SYNC_SKIPPED_DELETE_PENDING | id={ac.Id} nome={ac.Nome}");
                        continue;
                    }

                    contatos.Add(new Contato
                    {
                        Nome         = ac.Nome,
                        Numero       = ac.NumeroNormalizado,
                        Observacao   = ac.Empresa ?? ac.Observacao ?? string.Empty,
                        AtualizadoEm = ac.AtualizadoEm,
                        WavenApiId   = ac.Id
                    });
                    Log($"API_CONTACT_CREATED | nome={ac.Nome} numero={ac.NumeroNormalizado}");
                    alterou = true;
                }
            }

            if (alterou)
                ContatoStorageService.Salvar(contatos);

            // Atualiza favoritos locais baseado na lista atual da API
            AtualizarFavoritosLocais(contatos, favSet);
        }

        private void AtualizarFavoritosLocais(List<Contato> contatos, HashSet<string> favoritosIds)
        {
            // Monta set de números normalizados que devem ser favoritos
            var favNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var favNomes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var id in favoritosIds)
            {
                var c = contatos.FirstOrDefault(x => x.WavenApiId == id);
                if (c == null) continue;
                var num = PhoneNumberNormalizer.NormalizeBrazilPhone(
                    new string(c.Numero.Where(char.IsDigit).ToArray()));
                favNums.Add(num);
                favNomes[num] = c.Nome;
            }

            // Adiciona favoritos que faltam localmente
            foreach (var num in favNums)
            {
                if (!FavoritesStorageService.EhFavorito(num))
                {
                    FavoritesStorageService.Adicionar(new FavoriteItem
                    {
                        Nome     = favNomes.TryGetValue(num, out var n) ? n : num,
                        Numero   = num,
                        Favorito = true
                    });
                    Log($"API_CONTACT_FAVORITE_UPDATED | numero={num} favorito=true");
                }
            }

            // Remove favoritos locais que não estão mais na lista da API
            // (apenas para contatos que vieram da API, identificados por WavenApiId)
            var favLocais = FavoritesStorageService.Carregar();
            foreach (var fLocal in favLocais.ToList())
            {
                var fNum = PhoneNumberNormalizer.NormalizeBrazilPhone(
                    new string(fLocal.Numero.Where(char.IsDigit).ToArray()));

                // Só remove se o contato for gerenciado pela API (tem WavenApiId)
                var contatoApi = contatos.FirstOrDefault(c =>
                {
                    var cn = PhoneNumberNormalizer.NormalizeBrazilPhone(
                        new string(c.Numero.Where(char.IsDigit).ToArray()));
                    return cn == fNum && !string.IsNullOrWhiteSpace(c.WavenApiId);
                });
                if (contatoApi == null) continue;

                if (!favNums.Contains(fNum))
                {
                    FavoritesStorageService.Remover(fNum);
                    Log($"API_CONTACT_FAVORITE_UPDATED | numero={fNum} favorito=false");
                }
            }
        }

        // ── Fila offline ──────────────────────────────────────────────────────

        public static void EnfileirarOffline(WavenApiOfflineOp op)
        {
            try
            {
                var lista = CarregarFila();
                lista.Add(op);
                SalvarFila(lista);
                Log($"API_CONTACT_OFFLINE_QUEUE_SAVED | op={op.Op} contactId={op.ContactId}");
            }
            catch { }
        }

        private static async Task DrenaFilaOfflineAsync()
        {
            var fila = CarregarFila();
            if (fila.Count == 0) return;

            Log($"API_CONTACT_OFFLINE_QUEUE_SENDING | ops={fila.Count}");
            var pendentes = new List<WavenApiOfflineOp>();

            foreach (var op in fila)
            {
                var ok = await ExecutarOpAsync(op).ConfigureAwait(false);
                if (!ok) pendentes.Add(op); // reenfileira se falhar
            }

            SalvarFila(pendentes);
            var enviados = fila.Count - pendentes.Count;
            if (enviados > 0)
                Log($"API_CONTACT_OFFLINE_QUEUE_SENT | enviados={enviados} restantes={pendentes.Count}");
        }

        private static async Task<bool> ExecutarOpAsync(WavenApiOfflineOp op)
        {
            try
            {
                switch (op.Op)
                {
                    case "CREATE":
                    {
                        var p = JsonSerializer.Deserialize<CreateOpPayload>(op.Payload);
                        if (p == null) return true;
                        var (ok, _, _) = await WavenApiService.CriarContatoAsync(
                            p.Nome, p.Numero, empresa: null, observacao: p.Observacao,
                            ramal: p.CriadoPor).ConfigureAwait(false);
                        return ok;
                    }

                    case "UPDATE" when op.ContactId != null:
                    {
                        var p = JsonSerializer.Deserialize<UpdateOpPayload>(op.Payload);
                        if (p == null) return true;
                        return await WavenApiService.AtualizarContatoAsync(
                            op.ContactId, p.Nome, empresa: p.Empresa, observacao: p.Observacao,
                            ramal: op.Ramal ?? "", atualizadoEm: p.AtualizadoEm).ConfigureAwait(false);
                    }

                    case "DELETE" when op.ContactId != null:
                        return await WavenApiService.ExcluirContatoAsync(op.ContactId, op.Ramal ?? "").ConfigureAwait(false);

                    case "FAVORITE_ADD" when op.ContactId != null:
                    {
                        var p = JsonSerializer.Deserialize<FavoriteOpPayload>(op.Payload);
                        if (p == null) return true;
                        return await WavenApiService.AdicionarFavoritoAsync(
                            op.ContactId, op.Ramal ?? "", p.TipoFavorito, p.CriadoPor).ConfigureAwait(false);
                    }

                    case "FAVORITE_REMOVE" when op.ContactId != null:
                    {
                        var p = JsonSerializer.Deserialize<FavoriteOpPayload>(op.Payload);
                        if (p == null) return true;
                        return await WavenApiService.RemoverFavoritoAsync(
                            op.ContactId, op.Ramal ?? "", p.TipoFavorito).ConfigureAwait(false);
                    }

                    default:
                        Log($"API_OFFLINE_QUEUE_UNKNOWN_OP | op={op.Op} — descartando");
                        return true;
                }
            }
            catch { return false; }
        }

        // ── Persistência do estado ────────────────────────────────────────────

        private static WavenApiSyncState CarregarState()
        {
            try
            {
                if (!File.Exists(StateFile)) return new WavenApiSyncState();
                return JsonSerializer.Deserialize<WavenApiSyncState>(File.ReadAllText(StateFile))
                       ?? new WavenApiSyncState();
            }
            catch { return new WavenApiSyncState(); }
        }

        private static void SalvarState(WavenApiSyncState state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
                File.WriteAllText(StateFile,
                    JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private static List<WavenApiOfflineOp> CarregarFila()
        {
            try
            {
                if (!File.Exists(QueueFile)) return new List<WavenApiOfflineOp>();
                return JsonSerializer.Deserialize<List<WavenApiOfflineOp>>(File.ReadAllText(QueueFile))
                       ?? new List<WavenApiOfflineOp>();
            }
            catch { return new List<WavenApiOfflineOp>(); }
        }

        private static void SalvarFila(List<WavenApiOfflineOp> fila)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(QueueFile)!);
                File.WriteAllText(QueueFile,
                    JsonSerializer.Serialize(fila, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            _cts.Cancel();
            _timer.Dispose();
            _cts.Dispose();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void Log(string msg)
        {
            try { LogHelper.Info(msg); } catch { }
        }

        // Normaliza qualquer DateTime para UTC para comparações corretas entre fuso horários
        private static DateTime NormalizarParaUtc(DateTime dt)
        {
            if (dt == DateTime.MinValue) return DateTime.MinValue;
            return dt.Kind switch
            {
                DateTimeKind.Utc   => dt,
                DateTimeKind.Local => dt.ToUniversalTime(),
                _                  => DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime()
            };
        }

        // Verifica se há operação pendente na fila offline para o contactId e op indicados
        private static bool TemOpPendenteOffline(string contactId, string op)
        {
            if (string.IsNullOrWhiteSpace(contactId)) return false;
            try
            {
                var fila = CarregarFila();
                return fila.Any(o =>
                    string.Equals(o.Op, op, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(o.ContactId, contactId, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private record FavoriteOpPayload(string TipoFavorito, string CriadoPor);
        private record CreateOpPayload(string Nome, string Numero, string? Observacao, string CriadoPor);
        private record UpdateOpPayload(string Nome, string? Empresa, string? Observacao, DateTime AtualizadoEm);
    }
}
