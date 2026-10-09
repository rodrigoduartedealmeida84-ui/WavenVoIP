using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    /// <summary>
    /// Fonte das linhas de CDR vindas da Waven API, com sincronização incremental.
    ///
    /// Antes, cada ciclo de sync (a cada 2–5 s) baixava de novo a janela inteira de CDR
    /// (~7 dias). Agora a janela completa é baixada uma vez e fica em memória; os ciclos
    /// seguintes pedem só os grupos de chamada alterados desde o serverTime da resposta
    /// anterior e os mesclam aqui. O resto do pipeline (IssabelCdrService.SincronizarAsync)
    /// continua recebendo a lista COMPLETA de linhas, exatamente como antes — agrupamento por
    /// linkedid e classificação não mudam.
    ///
    /// Volta a baixar a janela completa: na primeira vez, quando pedido explicitamente
    /// ("Atualizar CDR"), depois de qualquer falha da API, se a URL/ramal mudar, se a API não
    /// suportar incremental, e periodicamente como reconciliação.
    /// </summary>
    internal static class CdrApiIncrementalService
    {
        // Reconciliação: o incremental se baseia em "calldate + duration" (o CDR não tem
        // coluna de inserção); uma consulta completa periódica cobre qualquer linha gravada
        // com atraso maior que a margem do servidor.
        private static readonly TimeSpan IntervaloReconciliacao = TimeSpan.FromMinutes(10);
        private const int LimiteLinhas = 5000; // mesmo LIMIT da consulta completa na API
        private const string FormatoCursor = "yyyy-MM-dd HH:mm:ss";

        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static List<ApiCdrRow>? _linhas;
        private static string   _cursor = string.Empty;
        private static string   _chave  = string.Empty;
        private static int      _dias;
        private static DateTime _ultimoCompletoUtc;

        public static async Task<List<ApiCdrRow>?> ObterLinhasAsync(string ramal, int dias, bool forcarCompleto)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var chave = WavenApiService.UrlAtual() + "|" + ramal;
                var mesmaOrigem = _linhas != null && _chave == chave;

                string motivo;
                if (forcarCompleto)                      motivo = "manual";
                else if (_linhas == null)                motivo = "inicial";
                else if (_chave != chave)                motivo = "origem_mudou";
                else if (_dias < dias)                   motivo = "janela_maior";
                else if (string.IsNullOrEmpty(_cursor))  motivo = "api_sem_incremental";
                else if (DateTime.UtcNow - _ultimoCompletoUtc >= IntervaloReconciliacao)
                                                         motivo = "reconciliacao";
                else                                     motivo = string.Empty;

                if (motivo.Length == 0)
                {
                    var delta = await WavenApiService.GetCdrAsync(ramal, _dias, _cursor).ConfigureAwait(false);
                    if (delta == null) { Invalidar(); return null; }

                    if (delta.Incremental && !string.IsNullOrEmpty(delta.ServerTime))
                    {
                        Mesclar(delta);
                        if (delta.Calls.Count > 0)
                            LogHelper.Info($"CDR_FETCH modo=incremental recebidas={delta.Calls.Count} total={_linhas!.Count} bytes={delta.TamanhoBytes}");
                    }
                    else
                    {
                        // A API ignorou "desde" (versão antiga): a resposta já é a janela inteira.
                        Adotar(delta, chave, _dias);
                        LogHelper.Info($"CDR_FETCH modo=completo motivo=api_sem_incremental total={_linhas!.Count} bytes={delta.TamanhoBytes}");
                    }
                    return Recortar(dias);
                }

                // Não encolhe a janela em cache por causa de um pedido pontual menor.
                var diasCompleto = mesmaOrigem ? Math.Max(dias, _dias) : dias;
                var completo = await WavenApiService.GetCdrAsync(ramal, diasCompleto, null).ConfigureAwait(false);
                if (completo == null) { Invalidar(); return null; }

                Adotar(completo, chave, diasCompleto);
                LogHelper.Info($"CDR_FETCH modo=completo motivo={motivo} total={_linhas!.Count} bytes={completo.TamanhoBytes}");
                return Recortar(dias);
            }
            finally { _gate.Release(); }
        }

        // Depois de uma falha não dá para confiar que o cursor cobre o intervalo perdido:
        // a próxima consulta bem-sucedida baixa a janela inteira de novo.
        private static void Invalidar()
        {
            _linhas = null;
            _cursor = string.Empty;
        }

        private static void Adotar(ApiCdrResponse resp, string chave, int dias)
        {
            _linhas = resp.Calls;
            _cursor = resp.ServerTime ?? string.Empty;
            _chave  = chave;
            _dias   = dias;
            _ultimoCompletoUtc = DateTime.UtcNow;
        }

        private static string ChaveGrupo(ApiCdrRow r) =>
            string.IsNullOrWhiteSpace(r.LinkedId) ? r.UniqueId : r.LinkedId;

        // A API devolve cada grupo alterado INTEIRO: troca o grupo todo em vez de casar linha
        // a linha (o CDR não tem chave por linha), o que também elimina duplicidade quando o
        // mesmo grupo volta em ciclos seguidos por causa da margem de sobreposição.
        private static void Mesclar(ApiCdrResponse delta)
        {
            var linhas = _linhas!;

            if (delta.Calls.Count > 0)
            {
                var grupos = new HashSet<string>(delta.Calls.Select(ChaveGrupo), StringComparer.OrdinalIgnoreCase);
                linhas.RemoveAll(r => grupos.Contains(ChaveGrupo(r)));
                linhas.AddRange(delta.Calls);
            }

            if (TryParseCursor(delta.WindowStart, out var inicioJanela))
                linhas.RemoveAll(r => r.CallDate < inicioJanela);

            if (delta.Calls.Count > 0)
            {
                // Mesma ordem da consulta completa (calldate DESC).
                linhas = linhas.OrderByDescending(r => r.CallDate).ToList();
                if (linhas.Count > LimiteLinhas) linhas.RemoveRange(LimiteLinhas, linhas.Count - LimiteLinhas);
                _linhas = linhas;
            }

            _cursor = delta.ServerTime;
        }

        // Devolve uma cópia (o cache não pode ser alterado por quem consome) limitada à janela
        // pedida, quando ela é menor que a que está em cache.
        private static List<ApiCdrRow> Recortar(int dias)
        {
            var linhas = _linhas!;
            if (dias < _dias && TryParseCursor(_cursor, out var agoraServidor))
            {
                var inicio = agoraServidor.AddDays(-Math.Max(1, dias));
                return linhas.Where(r => r.CallDate >= inicio).ToList();
            }
            return linhas.ToList();
        }

        private static bool TryParseCursor(string? s, out DateTime valor) =>
            DateTime.TryParseExact(s, FormatoCursor, CultureInfo.InvariantCulture, DateTimeStyles.None, out valor);
    }
}
