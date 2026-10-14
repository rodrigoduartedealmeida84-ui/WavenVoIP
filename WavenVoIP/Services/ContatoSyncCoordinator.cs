using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    // v2.5.0 — controle de conflitos entre as três origens de um contato:
    // WavenVoIP (edição local), Waven API (contatos da empresa) e Google Contacts.
    //
    // Problema real (12–13/10/2026): o usuário renomeava um contato e o nome antigo voltava.
    //  • o arquivo de contatos que vem com o instalador regravava o nome a cada abertura do app,
    //    sem comparar datas;
    //  • o Google gravava "agora" como data de alteração a cada sincronização, então não havia
    //    como saber qual lado era mais recente;
    //  • datas da Waven API (UTC, sem fuso) eram lidas como hora local e pareciam 3–4 h mais novas.
    //
    // Regra única: vale a ALTERAÇÃO MAIS RECENTE, com todas as datas comparadas em UTC.
    //  • edição no WavenVoIP → grava a data, envia à Waven API (se o contato é da empresa) e ao
    //    Google (se veio de lá e há permissão de escrita);
    //  • Google mais recente que a empresa → o nome do Google passa a valer e é enviado à API;
    //  • empresa/edição local mais recente que o Google → o nome local fica; o Google é atualizado
    //    quando há permissão, senão o contato fica marcado como "não enviado ao Google".
    public static class ContatoSyncCoordinator
    {
        public const string PendenteGoogle = "google";
        public const string PendenteGoogleSemPermissao = "google_sem_permissao";
        public const string PendenteApi = "api";

        // Renomeações que precisam chegar à Waven API (drenadas pelo WavenApiSyncService).
        public static readonly ConcurrentQueue<(string wavenApiId, string nome, string? observacao, DateTime quandoUtc)> RenomearNaApi = new();

        // Datas sem fuso vêm da Waven API (UTC). Datas locais são convertidas.
        public static DateTime ParaUtc(DateTime? d)
        {
            if (d == null || d.Value == DateTime.MinValue) return DateTime.MinValue;
            return d.Value.Kind switch
            {
                DateTimeKind.Utc   => d.Value,
                DateTimeKind.Local => d.Value.ToUniversalTime(),
                _                  => DateTime.SpecifyKind(d.Value, DateTimeKind.Utc)
            };
        }

        public static bool NomesIguais(string? a, string? b) =>
            string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.Ordinal);

        public enum Decisao { NomesIguais, GoogleVence, LocalVence }

        // Contato da empresa/local (não Google) x o mesmo número no Google. Decisão pura, testável.
        // Margem de 60 s: relógios diferentes não devem inverter uma edição feita agora.
        public static Decisao Decidir(string nomeLocal, DateTime? alteradoLocal, string nomeGoogle, DateTime? alteradoGoogle)
        {
            if (NomesIguais(nomeLocal, nomeGoogle)) return Decisao.NomesIguais;
            var tg = ParaUtc(alteradoGoogle); var tl = ParaUtc(alteradoLocal);
            return tg != DateTime.MinValue && tg > tl.AddSeconds(60) ? Decisao.GoogleVence : Decisao.LocalVence;
        }

        // Aplica a decisão num contato local que tem o mesmo número de um contato do Google.
        // Devolve true se o contato local foi alterado.
        public static bool Reconciliar(Contato local, Contato google, Action<string> log)
        {
            var mudou = false;
            if (string.IsNullOrWhiteSpace(local.GoogleContactId) && !string.IsNullOrWhiteSpace(google.GoogleContactId))
            { local.GoogleContactId = google.GoogleContactId; mudou = true; }

            switch (Decidir(local.Nome, local.AtualizadoEm, google.Nome, google.AtualizadoEm))
            {
                case Decisao.NomesIguais:
                    if (!string.IsNullOrEmpty(local.SincronizacaoPendente) && local.SincronizacaoPendente.StartsWith(PendenteGoogle, StringComparison.Ordinal))
                    { local.SincronizacaoPendente = string.Empty; mudou = true; }
                    break;

                case Decisao.GoogleVence:
                    log($"CONTACT_CONFLICT_GOOGLE_WINS | numero={local.Numero} de={local.Nome} para={google.Nome} google={ParaUtc(google.AtualizadoEm):u} local={ParaUtc(local.AtualizadoEm):u}");
                    local.Nome = google.Nome;
                    local.AtualizadoEm = ParaUtc(google.AtualizadoEm);
                    local.SincronizacaoPendente = string.Empty;
                    if (!string.IsNullOrWhiteSpace(local.WavenApiId))
                        RenomearNaApi.Enqueue((local.WavenApiId!, local.Nome, local.Observacao, ParaUtc(local.AtualizadoEm)));
                    mudou = true;
                    break;

                case Decisao.LocalVence:
                    if (string.IsNullOrEmpty(local.SincronizacaoPendente))
                    {
                        log($"CONTACT_CONFLICT_LOCAL_WINS | numero={local.Numero} local={local.Nome} google={google.Nome} (nome antigo do Google nao sobrescreve)");
                        local.SincronizacaoPendente = PendenteGoogle;
                        mudou = true;
                    }
                    break;
            }
            return mudou;
        }

        // Envia ao Google os nomes editados aqui (quando há permissão de escrita). Chamado depois
        // de cada sincronização com o Google e logo após uma edição.
        public static async Task<(int enviados, int semPermissao)> EnviarPendentesGoogleAsync()
        {
            int enviados = 0, semPermissao = 0;
            try
            {
                var pendentes = ContatoStorageService.Carregar()
                    .Where(c => !c.FonteGoogle && !c.EhRamalIssabel && !string.IsNullOrWhiteSpace(c.GoogleContactId) &&
                                (c.SincronizacaoPendente == PendenteGoogle || c.SincronizacaoPendente == PendenteGoogleSemPermissao))
                    .Select(c => (c.Numero, c.GoogleContactId!, c.Nome)).ToList();
                if (pendentes.Count == 0) return (0, 0);

                var resultados = new Dictionary<string, string>();
                foreach (var (numero, gid, nome) in pendentes)
                {
                    var (ok, motivo) = await GoogleContactsService.AtualizarNomeAsync(gid, nome).ConfigureAwait(false);
                    resultados[numero] = ok ? string.Empty : motivo == "sem_permissao" ? PendenteGoogleSemPermissao : PendenteGoogle;
                    if (ok) enviados++; else if (motivo == "sem_permissao") { semPermissao++; }
                    if (motivo == "sem_permissao") { foreach (var p in pendentes) resultados.TryAdd(p.Numero, PendenteGoogleSemPermissao); semPermissao = pendentes.Count; break; }
                }

                ContatoStorageService.ComArquivoTravado(() =>
                {
                    var todos = ContatoStorageService.Carregar();
                    var alterou = false;
                    foreach (var c in todos.Where(c => !c.FonteGoogle && resultados.ContainsKey(c.Numero)))
                        if (c.SincronizacaoPendente != resultados[c.Numero]) { c.SincronizacaoPendente = resultados[c.Numero]; alterou = true; }
                    if (alterou) ContatoStorageService.Salvar(todos);
                    return alterou;
                });
                LogHelper.Info($"CONTACT_GOOGLE_WRITEBACK | pendentes={pendentes.Count} enviados={enviados} sem_permissao={semPermissao}");
            }
            catch (Exception ex) { LogHelper.Info($"CONTACT_GOOGLE_WRITEBACK_ERROR | {ex.Message}"); }
            return (enviados, semPermissao);
        }
    }
}
