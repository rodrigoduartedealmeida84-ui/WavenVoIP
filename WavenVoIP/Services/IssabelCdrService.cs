using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using MySqlConnector;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class IssabelCdrService
    {
        // Number used for diagnostic verbose logging
        private const string NumeroAlvoDiagnostico = "556699063093";

        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly HttpClient _headHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        private static void Log(string msg, LogLevel level = LogLevel.INFO)
        {
            try { LogHelper.Cdr(msg, level); }
            catch { }
        }

        // v2.3.6 — auditoria de logs: o sync de CDR reprocessa e reloga TODO grupo/linkedid
        // retido a CADA ciclo (poucos segundos), não só os novos/alterados. Marcadores de rastreio
        // por linha/grupo (dump de linhas cruas, seleção de perna principal, evidência de direção,
        // etc.) — úteis numa investigação profunda, mas repetidos indefinidamente pra chamadas já
        // classificadas há muito tempo — cresciam o cdr_sync.log em dezenas de MB por sessão de
        // teste (CALL_CLASSIFY_GROUP sozinho: ~5,5 MB numa manhã de testes). Ligado pelo mesmo
        // toggle "Logs detalhados" de Configurações (LogHelper.IsDetailedEnabled — já existia,
        // criado mas nunca conectado a nenhum ponto de log), desligado por padrão. O rastro que
        // efetivamente diagnostica Cancelada/Recusada/NaoAtendida (decisão final por tentativa,
        // conflitos SIP×CDR, proteção do stub Cancelada, timings) continua SEMPRE ligado — ver
        // Log() acima — só os detalhes linha-a-linha ficam atrás do toggle.
        private static void LogDetalhado(string msg, LogLevel level = LogLevel.INFO)
        {
            if (!LogHelper.IsDetailedEnabled) return;
            try { LogHelper.Cdr(msg, level); }
            catch { }
        }

        private static string BuildConnectionString(SipConfig config)
        {
            var host = string.IsNullOrWhiteSpace(config.CdrHost) ? config.ServerIp : config.CdrHost;
            return $"Server={host};Port={config.CdrPorta};Database={config.CdrBanco};" +
                   $"User ID={config.CdrUsuario};Password={config.CdrSenha};" +
                   $"Connection Timeout=10;Allow Zero Datetime=True;Convert Zero Datetime=True;";
        }

        public static async Task<bool> TestarConexaoAsync(SipConfig config)
        {
            Log("CDR_CONNECTION_TEST_START");
            try
            {
                await using var conn = new MySqlConnection(BuildConnectionString(config));
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM `{config.CdrTabela}` LIMIT 1";
                await cmd.ExecuteScalarAsync();
                Log("CDR_CONNECTION_OK");
                return true;
            }
            catch (Exception ex)
            {
                Log($"CDR_CONNECTION_FAIL erro={ex.Message}", LogLevel.ERROR);
                throw;
            }
        }

        // ── Local history reprocessing ─────────────────────────────────────────────

        // Fixes saved local history: deduplicates concatenated phone numbers, removes invalid ramais,
        // and optionally validates/removes recording URLs that return 404.
        // validarUrls=false is fast (no HTTP) — suitable for startup.
        // validarUrls=true performs HEAD validation — for manual requests and CDR sync.
        public static async Task<(int reprocessados, int numerosCorrigidos, int urlsRemovidas)>
            ReprocessarHistoricoCdrLocalAsync(bool validarUrls = true)
        {
            var swTotal = Stopwatch.StartNew();
            var itens = HistoricoStorageService.Carregar();
            if (itens.Count == 0) return (0, 0, 0);

            Log($"REPROCESS_START total={itens.Count} validarUrls={validarUrls}");
            int numerosCorrigidos = 0;

            foreach (var item in itens)
            {
                // Fix concatenated numbers (e.g. "6699939769866999397698" → "66999397698").
                // Only process purely numeric Numero values to avoid touching contact names.
                var original = item.Numero ?? string.Empty;
                var soDigitos = new string(original.Where(char.IsDigit).ToArray());
                if (soDigitos.Length > 0 && string.Equals(soDigitos, original.Trim(), StringComparison.Ordinal))
                {
                    var corrigido = DialPlanService.RemoverDuplicacaoSequencial(soDigitos);
                    if (!string.Equals(corrigido, soDigitos, StringComparison.Ordinal))
                    {
                        LogDetalhado($"REPROCESS_NUM_FIX orig={original} novo={corrigido}");
                        item.Numero = corrigido;
                        numerosCorrigidos++;
                    }
                }

                // Corrige Nome quando também é número concatenado. Ocorre quando o CDR não
                // tem CLID e o fallback (numero externo) era duplicado — após Numero ser
                // corrigido, Nome fica diferente e NomeExibido retornaria o Nome errado.
                var nomeOrig   = item.Nome ?? string.Empty;
                var nomeDigits = new string(nomeOrig.Where(char.IsDigit).ToArray());
                if (nomeDigits.Length > 0 &&
                    string.Equals(nomeDigits, nomeOrig.Trim(), StringComparison.Ordinal))
                {
                    var nomeCorr = DialPlanService.RemoverDuplicacaoSequencial(nomeDigits);
                    if (!string.Equals(nomeCorr, nomeDigits, StringComparison.Ordinal))
                    {
                        LogDetalhado($"REPROCESS_NOME_FIX orig={nomeOrig} novo={nomeCorr}");
                        item.Nome = nomeCorr;
                    }
                }

                // Remove ramais with > 5 digits (IVR/queue IDs, concatenated ramais)
                if (!string.IsNullOrWhiteSpace(item.RamalOrigem)  && !EhRamal(item.RamalOrigem))  item.RamalOrigem  = string.Empty;
                if (!string.IsNullOrWhiteSpace(item.RamalDestino) && !EhRamal(item.RamalDestino)) item.RamalDestino = string.Empty;
                if (!string.IsNullOrWhiteSpace(item.RamalAtendeu) && !EhRamal(item.RamalAtendeu)) item.RamalAtendeu = string.Empty;

                // Fix CDR entries for ramal-to-ramal calls where Numero was incorrectly set
                // from the caller's SIP CLID (external mobile/WhatsApp number) instead of cdr.Src.
                // RamalOrigem and RamalDestino are the ground truth for internal calls.
                if (item.FonteCdr &&
                    !string.IsNullOrWhiteSpace(item.RamalOrigem) &&
                    !string.IsNullOrWhiteSpace(item.RamalDestino) &&
                    EhRamal(item.RamalOrigem) &&
                    EhRamal(item.RamalDestino) &&
                    !string.Equals(item.RamalOrigem, item.RamalDestino, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.OrigemSaida, "Queue", StringComparison.OrdinalIgnoreCase) &&
                    (item.OrigemSaida ?? string.Empty).IndexOf("WhatsApp", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    if (!string.Equals(item.OrigemSaida, "Ramal interno", StringComparison.OrdinalIgnoreCase))
                    {
                        LogDetalhado($"REPROCESS_RAMAL_ORIGEM_FIX origemSaida={item.OrigemSaida} -> Ramal interno uid={item.UniqueId}");
                        item.OrigemSaida = "Ramal interno";
                    }

                    var numNorm = DialPlanService.RemoverDuplicacaoSequencial(item.Numero ?? string.Empty);
                    if (!DialPlanService.EhRamalInterno(numNorm))
                    {
                        var sipRamal = SipConfig.CarregarSalva()?.Ramal?.Trim() ?? string.Empty;
                        var outroRamal = !string.IsNullOrWhiteSpace(sipRamal) &&
                                         string.Equals(item.RamalOrigem, sipRamal, StringComparison.OrdinalIgnoreCase)
                            ? item.RamalDestino
                            : item.RamalOrigem;
                        LogDetalhado($"REPROCESS_RAMAL_NUMERO_FIX numero={item.Numero} -> {outroRamal} uid={item.UniqueId}");
                        item.Numero = outroRamal;
                        numerosCorrigidos++;
                    }
                }
            }

            // Validate recording URLs in parallel and remove those that return 404
            int urlsRemovidas = 0;
            if (validarUrls)
            {
                var comUrl = itens
                    .Where(i => !string.IsNullOrWhiteSpace(i.GravacaoUrl) &&
                                (i.GravacaoUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 i.GravacaoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (comUrl.Count > 0)
                {
                    Log($"REPROCESS_HEAD_VALIDATE count={comUrl.Count}");
                    using var semaphore = new System.Threading.SemaphoreSlim(8);
                    var tarefas = comUrl.Select(async item =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var ok = await ValidarUrlHttpHeadAsync(item.GravacaoUrl);
                            if (!ok)
                            {
                                Log($"REPROCESS_URL_404 url={item.GravacaoUrl}");
                                item.GravacaoUrl = string.Empty;
                            }
                        }
                        finally { semaphore.Release(); }
                    });
                    await Task.WhenAll(tarefas);
                    urlsRemovidas = comUrl.Count(i => string.IsNullOrWhiteSpace(i.GravacaoUrl));
                    Log($"REPROCESS_HEAD_DONE removidas={urlsRemovidas}/{comUrl.Count}");
                }
            }

            // Clean up stale duplicates from earlier syncs: a Perdida/NaoAtendidaNesseRamal
            // entry left over for a call another ramal already answered. Must run BEFORE the
            // number+time dedup below — that dedup has no Tipo awareness and, when a Perdida
            // and a Recebida entry collide on the same number/window, can keep either one
            // arbitrarily. Suppressing the Perdida sibling first guarantees the Recebida entry
            // (the real outcome) is the one that survives.
            itens = SuprimirPerdidasAtendidasPorOutroRamal(itens);

            // Deduplicate legacy entries: CDR+local-SIP pairs and multi-ring-attempt CDR duplicates
            // that survived previous syncs. Uses a number-only comparison (no tipo check) so that
            // a local SIP "Realizada" matches a CDR "Recebida" for the same outgoing trunk call.
            var antesDedup = itens.Count;
            itens = DeduplicarPorNumeroETempo(itens);
            if (itens.Count != antesDedup)
                Log($"REPROCESS_DEDUP_DONE removidos={antesDedup - itens.Count}");

            HistoricoStorageService.Salvar(itens.OrderByDescending(i => i.DataHora).Take(5000).ToList());
            Log($"REPROCESS_DONE total={itens.Count} numerosCorrigidos={numerosCorrigidos} urlsRemovidas={urlsRemovidas}");
            Log($"REPROCESS_TIMING ms={swTotal.ElapsedMilliseconds} total={itens.Count} validarUrls={validarUrls}");
            return (itens.Count, numerosCorrigidos, urlsRemovidas);
        }

        // ── Main sync ──────────────────────────────────────────────────────────────

        public static async Task<List<HistoricoLigacaoItem>> SincronizarAsync(
            SipConfig config, int diasRetencao = 7)
        {
            Log("CDR_SYNC_START");
            Log("HISTORY_REFRESH_START");
            var resultado = new List<HistoricoLigacaoItem>();

            // v2.3.6 — medição de tempo por estágio (investigação de travamentos reportados após
            // a v2.3.6). Sempre logada, custo desprezível (poucas chamadas a Stopwatch por sync).
            var swTotal = Stopwatch.StartNew();
            var swEstagio = Stopwatch.StartNew();

            try
            {
                // ── Branch: CDR via Waven API ─────────────────────────────────────
                List<CdrChamada> linhas;
                if (config.UsarWavenApi &&
                    !string.IsNullOrWhiteSpace(config.WavenApiUrl) &&
                    !string.IsNullOrWhiteSpace(config.WavenApiToken))
                {
                    Log($"CLIENT_CDR_USING_API | ramal={config.Ramal} dias={diasRetencao}");
                    var apiRows = await WavenApiService.GetCdrCallsAsync(config.Ramal, diasRetencao)
                                                       .ConfigureAwait(false);
                    if (apiRows == null)
                    {
                        Log("API_CDR_QUERY_ERROR | falha ao buscar CDR via API");
                        return resultado;
                    }
                    Log($"API_CDR_QUERY_OK | rows={apiRows.Count}");
                    linhas = apiRows.Select(r => new CdrChamada
                    {
                        CallDate      = r.CallDate,
                        Src           = r.Src,
                        Dst           = r.Dst,
                        Channel       = r.Channel,
                        DstChannel    = r.DstChannel,
                        LastApp       = r.LastApp,
                        LastData      = r.LastData,
                        Duration      = r.Duration,
                        BillSec       = r.BillSec,
                        Disposition   = r.Disposition,
                        UniqueId      = r.UniqueId,
                        RecordingFile = r.RecordingFile,
                        LinkedId      = r.LinkedId,
                        Clid          = r.Clid,
                        DContext      = r.DContext
                    }).ToList();
                }
                else
                {
                    linhas = await BuscarLinhasCdrAsync(config, diasRetencao);
                }
                Log($"CDR_ROWS_FOUND quantidade={linhas.Count}");
                Log($"HISTORY_CDR_READ quantidade={linhas.Count} dias={diasRetencao}");
                Log($"CDR_SYNC_TIMING estagio=download ms={swEstagio.ElapsedMilliseconds} linhas={linhas.Count}");
                swEstagio.Restart();

                // Raw-row diagnostic for the target number
                foreach (var r in linhas.Where(row => GrupoContemNumero(new List<CdrChamada> { row }, NumeroAlvoDiagnostico)))
                    Log($"CDR_DEBUG_RAW_ROWS calldate={r.CallDate:yyyy-MM-dd HH:mm:ss} clid={r.Clid} src={r.Src} dst={r.Dst} " +
                        $"dcontext={r.DContext} channel={r.Channel} dstchannel={r.DstChannel} lastapp={r.LastApp} " +
                        $"lastdata={r.LastData} disposition={r.Disposition} duration={r.Duration} billsec={r.BillSec} " +
                        $"uniqueid={r.UniqueId} linkedid={r.LinkedId} recordingfile={r.RecordingFile}");

                // Load known internal ramais from contacts for ramal validation
                var ramaisConhecidos = CarregarRamaisConhecidos();
                Log($"CDR_RAMAIS_CONHECIDOS total={ramaisConhecidos.Count}");

                // Entradas locais (FonteCdr=false) já classificadas ao vivo pelo SipService com o
                // tempo real de Ringing→BusyHere — carregadas UMA vez aqui para o CDR (que só tem
                // disposition/duration, sem o timestamp exato do Ringing) poder respeitar esse
                // resultado em vez de reclassificar com informação mais pobre. Ver uso abaixo.
                // v2.3.6 — Cancelada incluída: uma tentativa que o PRÓPRIO OPERADOR encerrou antes
                // do atendimento (SipService.LastOutboundWasCancelledLocally) normalmente aparece no
                // CDR como disposition NO ANSWER minutos depois, que ClassificarChamada mapeia pra
                // NaoAtendida — sem isso aqui, o sync de CDR sobrescrevia silenciosamente "Cancelada"
                // por "Não atendida", exatamente o bug relatado num teste real. A prioridade "local
                // sempre vence" já existe abaixo (OUTBOUND_RESULT_CONFLICT) — só precisava incluir
                // Cancelada na lista de resultados locais confiáveis a proteger.
                var locaisComResultadoSip = HistoricoStorageService.Carregar()
                    .Where(i => !i.FonteCdr &&
                                (i.Tipo == TipoHistoricoLigacao.Recusada ||
                                 i.Tipo == TipoHistoricoLigacao.NaoAtendida ||
                                 i.Tipo == TipoHistoricoLigacao.Cancelada))
                    .ToList();

                // ── Step 1: primary group by linkedid ─────────────────────────────
                var gruposPrimarios = linhas
                    .GroupBy(r => string.IsNullOrWhiteSpace(r.LinkedId) ? r.UniqueId : r.LinkedId,
                             StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.ToList())
                    .ToList();

                // ── Step 2: secondary merge — same external src within a wide window ──
                // Catches queue/ring-group scenarios where Asterisk issues distinct linkedids
                // for the inbound leg and each ramal ring attempt. 180s comfortably covers a
                // full multi-cycle ring group (5 cycles x ~15-20s can add up to 90s+ already).
                var grupos = MergeGruposPorSrcJanela(gruposPrimarios, janelaSeg: 180);
                Log($"CDR_GROUPS primary={gruposPrimarios.Count} afterMerge={grupos.Count}");
                Log($"CDR_SYNC_TIMING estagio=agrupamento ms={swEstagio.ElapsedMilliseconds} grupos={grupos.Count}");
                swEstagio.Restart();

                var ramal          = config.Ramal?.Trim() ?? string.Empty;
                var todosModoRamais = string.Equals(config.HistoricoModoExibicao, "TodosRamais",
                    StringComparison.OrdinalIgnoreCase);

                foreach (var grupo in grupos)
                {
                    var linkedIds   = string.Join(",", grupo.Select(r => r.LinkedId).Distinct());
                    var ehAlvo      = GrupoContemNumero(grupo, NumeroAlvoDiagnostico);

                    LogDetalhado($"CALL_CLASSIFY_START linkedids={linkedIds} registros={grupo.Count}");
                    // v2.3.6 — dcontext/lastapp/lastdata adicionados (antes só saíam pro número de
                    // diagnóstico fixo abaixo): eram exatamente os campos que faltavam pra provar a
                    // direção real de uma chamada com uma única linha de CDR ambígua por
                    // Channel/Src/Dst sozinhos — ver CALL_DIRECTION_EVIDENCE mais abaixo.
                    LogDetalhado($"CALL_CLASSIFY_GROUP linkedids={linkedIds} " +
                        string.Join(" | ", grupo.OrderBy(x => x.CallDate).Select(r =>
                            $"[uid={r.UniqueId} src={MascararNumeroLog(r.Src)} dst={MascararNumeroLog(r.Dst)} " +
                            $"ch={r.Channel} dstch={r.DstChannel} disp={r.Disposition} dcontext={r.DContext} " +
                            $"lastapp={r.LastApp} lastdata={r.LastData}]")));

                    // ── Diagnostic verbose log ─────────────────────────────────────
                    if (ehAlvo)
                    {
                        Log($"CDR_DEBUG_GROUP_FOR_NUMBER numero={NumeroAlvoDiagnostico} linkedids={linkedIds} registros={grupo.Count}");
                        foreach (var r in grupo.OrderBy(x => x.CallDate))
                            Log($"  CDR_ROW calldate={r.CallDate:yyyy-MM-dd HH:mm:ss} src={r.Src} dst={r.Dst} " +
                                $"channel={r.Channel} dstchannel={r.DstChannel} disposition={r.Disposition} " +
                                $"lastapp={r.LastApp} lastdata={r.LastData} billsec={r.BillSec} duration={r.Duration} " +
                                $"uniqueid={r.UniqueId} linkedid={r.LinkedId} recordingfile={r.RecordingFile}");
                    }

                    // ── Voicemail detection ────────────────────────────────────────
                    var foiParaCaixaPostal = grupo.Any(r =>
                        string.Equals(r.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase) &&
                        EhVoicemail(r));

                    // ── Was answered by a real ramal (not voicemail)? ──────────────
                    var registrosAtendidosReais = grupo
                        .Where(r => string.Equals(r.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase)
                                 && !EhVoicemail(r))
                        .ToList();
                    var foiAtendidaGlobalmente = registrosAtendidosReais.Any();

                    // ── Who answered? ──────────────────────────────────────────────
                    // Try non-IVR records first so queue/URA legs don't mask the real agent
                    var registrosPorAgente = registrosAtendidosReais
                        .Where(r => !EhRotaNaoHumana(r))
                        .OrderByDescending(r => r.BillSec).ToList();

                    var ramalAtendeu = (registrosPorAgente.Count > 0 ? registrosPorAgente : registrosAtendidosReais)
                        .Select(r => ExtrairRamalAtendente(r, ramaisConhecidos))
                        .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) ?? string.Empty;

                    if (ehAlvo)
                        Log($"  CDR_RESULT foiAtendida={foiAtendidaGlobalmente} caixaPostal={foiParaCaixaPostal} ramalAtendeu={ramalAtendeu}");

                    // ── Pick one principal CDR record ──────────────────────────────
                    var principal = EscolherCdrPrincipal(grupo, ramaisConhecidos);
                    LogDetalhado($"CDR_MAIN_LEG_SELECTED linkedids={linkedIds} uid={principal.UniqueId} " +
                        $"disp={principal.Disposition} billsec={principal.BillSec} src={principal.Src} dst={principal.Dst}");

                    foreach (var sup in grupo.Where(r => !ReferenceEquals(r, principal)))
                        LogDetalhado($"CDR_QUEUE_ATTEMPT_SUPPRESSED linkedid={sup.LinkedId} uid={sup.UniqueId} disp={sup.Disposition} dst={sup.Dst}");

                    var cdr        = principal;
                    var srcRamal   = ExtrairRamalSrc(cdr.Src);
                    var dstRamal   = ExtrairRamalDst(cdr.Dst, cdr.DstChannel, ramaisConhecidos);
                    var chRamal    = ExtrairRamalValidado(cdr.Channel, ramaisConhecidos);
                    var dstChRamal = ExtrairRamalValidado(cdr.DstChannel, ramaisConhecidos);

                    // Este ramal ORIGINOU a chamada? cdr.Src nem sempre é o número do ramal —
                    // troncos de saída (Operadora/WhatsApp TIM/Vivo) substituem o Caller-ID pelo
                    // DID/identidade do próprio tronco (prática padrão de telefonia), então cdr.Src
                    // pode vir como "+556696308630" (nosso próprio DID) em vez de "104". O canal
                    // (cdr.Channel = "SIP/104-...") continua confiável nesses casos — por isso
                    // checa os dois, igual ao ehMeuRamal mais abaixo.
                    //
                    // v2.3.6 — CORRIGIDO com CDR real de produção (duas falhas, não uma):
                    //
                    // 1) Comparar contra `ramal` (o ramal configurado NESTA máquina) é a fonte
                    //    errada de verdade em modo TodosRamais — o CDR processado pode pertencer a
                    //    QUALQUER ramal da empresa. Uma ligação de SAÍDA originada pelo ramal 109
                    //    era classificada como "Recebida" (porque `ramal` desta máquina, ex. 100,
                    //    não batia) e o guard SELF_NUMBER_LEG_DETECTED (mais abaixo, feito
                    //    exatamente pra suprimir o DID da própria empresa quando ele vaza pro
                    //    Caller-ID do tronco) nunca disparava — o Histórico mostrava uma "chamada
                    //    recebida" do nosso próprio número. srcRamal/chRamal já são validados
                    //    contra a lista de ramais conhecidos — não precisam bater com O ramal
                    //    desta máquina especificamente, só precisam ser um ramal interno real.
                    //
                    // 2) cdr.Channel="Local/100@from-queue-..." (fila OFERECENDO a ligação ao
                    //    ramal 100) também "contém" o ramal 100, igual a cdr.Channel="SIP/100-..."
                    //    (ramal 100 discando de verdade) — mas são direções opostas. Sem excluir
                    //    canais Local/, uma ligação de CLIENTE ofertada pela fila ao ramal cujo
                    //    número coincidisse com o desta máquina virava "NaoAtendida" (rótulo de
                    //    chamada de SAÍDA) em vez de "Perdida" — e, pior, nunca era limpa pela
                    //    supressão de duplicata (que ignora NaoAtendida de propósito, reservada a
                    //    tentativas de SAÍDA reais).
                    var chRamalDeCanalReal = !string.IsNullOrWhiteSpace(chRamal) &&
                        !(cdr.Channel ?? string.Empty).StartsWith("Local/", StringComparison.OrdinalIgnoreCase);

                    // v2.3.6 — reforço com dcontext: um grupo de 1 única linha (sem perna de entrada
                    // separada pra correlacionar) pode, em tese, entregar a ligação a um ramal por um
                    // canal SIP/ direto (não Local/) mesmo sendo ENTRADA — Channel sozinho não é
                    // garantia absoluta. dcontext é o veredito do próprio dialplan do Asterisk sobre
                    // de onde essa perna veio: "from-internal" é o contexto usado quando um RAMAL
                    // origina a chamada (confirmado com CDR real: dcontext=from-internal, lastapp=
                    // Dial, lastdata=SIP/Wavoip/<numero>,... — prova inequívoca de discagem de saída).
                    // Contextos de entrada (fila/URA/tronco) nunca são "from-internal" — quando o
                    // dcontext indicar claramente entrada, isso VETA a conclusão de origem mesmo que
                    // o canal pareça de um ramal real.
                    var dctxNorm = (cdr.DContext ?? string.Empty).ToLowerInvariant();
                    var dcontextSugereEntrada =
                        dctxNorm.Contains("queue") || dctxNorm.Contains("ivr") ||
                        dctxNorm.Contains("trunk") || dctxNorm.Contains("pstn") ||
                        (dctxNorm.StartsWith("from-") && !dctxNorm.Contains("internal"));

                    var ehOrigemPrincipal = (!string.IsNullOrWhiteSpace(srcRamal) || chRamalDeCanalReal) &&
                        !dcontextSugereEntrada;

                    LogDetalhado($"RAMAL_PARSE_DEBUG src={cdr.Src}→{srcRamal} dst={cdr.Dst}→{dstRamal} " +
                        $"ch={cdr.Channel}→{chRamal} dstch={cdr.DstChannel}→{dstChRamal}");
                    LogDetalhado($"CALL_CLASSIFY_DIRECTION linkedids={linkedIds} ramal={ramal} ehOrigemPrincipal={ehOrigemPrincipal} " +
                        $"srcRamal={srcRamal} chRamal={chRamal}");

                    // v2.3.6 — evidência bruta para investigar direção quando o grupo tem 1 única
                    // linha (sem perna de entrada separada pra correlacionar): não decide nada
                    // sozinha ainda, só expõe os sinais para auditoria (pedido explícito de
                    // investigação). dstDigits com dígito de rota (1/2/3) no início é o sinal mais
                    // forte de SAÍDA que existe — só o AplicarRegraDeDiscagem do próprio Waven
                    // prefixa um número assim antes de mandar pro Dial() do Asterisk.
                    var dstDigitsEvidencia = new string((cdr.Dst ?? string.Empty).Where(char.IsDigit).ToArray());
                    var dstPareceRotaPrefixada = dstDigitsEvidencia.Length >= 10 &&
                        (dstDigitsEvidencia[0] == '1' || dstDigitsEvidencia[0] == '2' || dstDigitsEvidencia[0] == '3');
                    LogDetalhado($"CALL_DIRECTION_EVIDENCE linkedids={linkedIds} uid={cdr.UniqueId} " +
                        $"dcontext={cdr.DContext} lastapp={cdr.LastApp} lastdata={cdr.LastData} " +
                        $"dstPareceRotaPrefixada={dstPareceRotaPrefixada} chRamalDeCanalReal={chRamalDeCanalReal} " +
                        $"registrosNoGrupo={grupo.Count}");

                    LogDetalhado($"CDR_DIAG_CALL linkedids={linkedIds} src={cdr.Src} dst={cdr.Dst} " +
                        $"ch={cdr.Channel} dstch={cdr.DstChannel} disp={cdr.Disposition} " +
                        $"uniqueid={cdr.UniqueId} linkedid={cdr.LinkedId} " +
                        $"ramalAtendeu={ramalAtendeu} registros={grupo.Count} agentes={registrosPorAgente.Count}");

                    // Detect URA/IVR-only calls (no human ramal answered)
                    var grupoPorRotaNaoHumana = !foiAtendidaGlobalmente &&
                        grupo.Any(r => string.Equals(r.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase)
                                    && EhRotaNaoHumana(r));
                    if (grupoPorRotaNaoHumana)
                        Log($"CDR_NON_HUMAN_ROUTE_DETECTED linkedids={linkedIds} src={cdr.Src} dst={cdr.Dst} dcontext={cdr.DContext}");

                    // Skip pure queue delivery attempt groups: no external number, not answered,
                    // and clearly a queue routing leg (from-queue dcontext / Local/ channel).
                    // These are Asterisk ring-attempt CDR artifacts (src=queue, dst=agent ramal)
                    // with their own linkedid — they must not appear as separate history entries.
                    var temNumExterno = grupo.Any(r =>
                        new string((r.Src ?? string.Empty).Where(char.IsDigit).ToArray()).Length >= 8 ||
                        new string((r.Dst ?? string.Empty).Where(char.IsDigit).ToArray()).Length >= 8);
                    if (!temNumExterno && !foiAtendidaGlobalmente && grupo.Any(EhRotaNaoHumana))
                    {
                        Log($"CDR_QUEUE_DELIVERY_ATTEMPT_SKIP linkedids={linkedIds} src={cdr.Src} dst={cdr.Dst} dcontext={cdr.DContext}");
                        continue;
                    }

                    // Fallbacks
                    var ramalGravacao = ExtrairRamalDeGravacao(cdr.RecordingFile);
                    if (string.IsNullOrWhiteSpace(srcRamal)  && !string.IsNullOrWhiteSpace(ramalGravacao)) srcRamal  = ramalGravacao;
                    if (string.IsNullOrWhiteSpace(dstRamal)  && !string.IsNullOrWhiteSpace(ramalGravacao)) dstRamal  = ramalGravacao;
                    if (string.IsNullOrWhiteSpace(dstRamal)  && !string.IsNullOrWhiteSpace(ramalAtendeu))  dstRamal  = ramalAtendeu;

                    // Chamada de fila/ring-group abandonada: EscolherCdrPrincipal (passo 5) evita
                    // escolher a perna de tentativa de toque por agente como principal — de propósito,
                    // para o main leg (entrada na fila) não virar RamalDestino. Isso significa que,
                    // para uma chamada NO ANSWER de fila, srcRamal/dstRamal/chRamal/dstChRamal acima
                    // (todos extraídos só do registro principal) nunca referenciam o ramal do agente,
                    // mesmo que ele tenha tocado de verdade em vários ciclos — o toque fica só nas
                    // OUTRAS linhas do grupo. Sem isso, a chamada nunca aparece no Histórico deste
                    // ramal (bug relatado: abandono na fila não foi registrado).
                    var ramaisNoGrupo = grupo
                        .SelectMany(r => new[]
                        {
                            ExtrairRamalSrc(r.Src),
                            ExtrairRamalDst(r.Dst, r.DstChannel, ramaisConhecidos),
                            ExtrairRamalValidado(r.Channel, ramaisConhecidos),
                            ExtrairRamalValidado(r.DstChannel, ramaisConhecidos)
                        })
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var ehMeuRamal = !string.IsNullOrWhiteSpace(ramal) &&
                        (srcRamal == ramal || dstRamal == ramal ||
                         chRamal  == ramal || dstChRamal == ramal ||
                         ramalAtendeu == ramal ||
                         ramaisNoGrupo.Contains(ramal));

                    if (ehAlvo)
                        Log($"  CDR_RAMAL_GRUPO_DEBUG ramaisNoGrupo={string.Join(",", ramaisNoGrupo)} meuRamal={ramal} ehMeuRamal={ehMeuRamal}");

                    if (!todosModoRamais && !string.IsNullOrWhiteSpace(ramal) && !ehMeuRamal)
                        continue;

                    if (!todosModoRamais && EhRamal(cdr.Src) && EhRamal(cdr.Dst) && !ehMeuRamal)
                        continue;

                    // Maior Duration/BillSec entre as linhas que pertencem ao MESMO linkedid do
                    // registro principal — nunca do grupo inteiro (v2.3.5: com múltiplas discagens
                    // manuais agora impedidas de se fundir — ver PodeMesclarGruposPorJanela — isso
                    // é normalmente só 1-2 linhas; mas em grupos legitimamente fundidos, como fila,
                    // ainda protege contra herdar a duração de uma perna de OUTRO linkedid).
                    // Ex.: Asterisk grava uma linha "Dial" com a duração real do toque e depois uma
                    // linha "Busy" com duration=0 para o mesmo linkedid — pega o maior das duas.
                    var duracaoToqueGrupoSeg = grupo
                        .Where(r => string.Equals(r.LinkedId, cdr.LinkedId, StringComparison.OrdinalIgnoreCase))
                        .Select(r => Math.Max(r.Duration, r.BillSec))
                        .DefaultIfEmpty(0)
                        .Max();
                    LogDetalhado($"OUTBOUND_ATTEMPT_DURATION uid={cdr.UniqueId} linkedid={cdr.LinkedId} ringDurationSeg={duracaoToqueGrupoSeg}");

                    var tipo          = ClassificarChamada(cdr, ehOrigemPrincipal, foiAtendidaGlobalmente, foiParaCaixaPostal, duracaoToqueGrupoSeg);
                    var numeroExterno = ObterNumeroExterno(cdr, ramal);
                    var duracaoFmt    = FormatarDuracao(cdr.BillSec > 0 ? cdr.BillSec : cdr.Duration);
                    // v2.3.6 — propagado da entrada local durante a reconciliação abaixo, se o
                    // resultado local for Cancelada. Preservado no item final (ver construção do
                    // HistoricoLigacaoItem mais abaixo) — a prova de "operador cancelou" nunca é
                    // perdida quando o CDR enriquece o registro (UniqueId, gravação, duração real).
                    var canceladaPeloOperadorPropagado = false;

                    LogDetalhado($"CALL_CLASSIFY_FINAL linkedids={linkedIds} uid={cdr.UniqueId} tipo={tipo} " +
                        $"disp={cdr.Disposition} ehOrigemPrincipal={ehOrigemPrincipal} numero={MascararNumeroLog(numeroExterno)}");

                    // Um "Perdida"/"NaoAtendidaNesseRamal" no CDR só reflete que ESTA tentativa/
                    // perna terminou — não que a chamada saiu da fila. Se o cliente ainda aparece
                    // esperando numa fila ao vivo (AMI), o CDR desta tentativa é prematuro: a fila
                    // pode oferecer a chamada de novo. Não importa esse resultado ainda; o próximo
                    // sync vai reavaliar quando a fila realmente confirmar o desfecho final.
                    //
                    // Válvula de segurança: nunca suspende para sempre. Se o CDR desta chamada já é
                    // mais antigo que _janelaMaximaSuspensaoPorFila (bem acima do maior tempo de
                    // espera configurável de qualquer fila real), um "ainda na fila" persistente só
                    // pode ser cruzamento de número desatualizado/incorreto — importa mesmo assim,
                    // em vez de deixar a chamada pendente indefinidamente (bug relatado: "a proteção
                    // ficou permanente").
                    if (tipo == TipoHistoricoLigacao.Perdida || tipo == TipoHistoricoLigacao.NaoAtendidaNesseRamal)
                    {
                        var idadeCdr = DateTime.Now - cdr.CallDate;
                        if (ExisteClienteNaFilaAoVivo(numeroExterno, out var filaAoVivo))
                        {
                            if (idadeCdr < _janelaMaximaSuspensaoPorFila)
                            {
                                Log($"QUEUE_STILL_ACTIVE linkedid={linkedIds} numero={MascararNumeroLog(numeroExterno)} " +
                                    $"fila={filaAoVivo} idadeCdrSeg={idadeCdr.TotalSeconds:F0}");
                                continue;
                            }

                            Log($"QUEUE_STILL_ACTIVE_TIMEOUT_OVERRIDE linkedid={linkedIds} " +
                                $"numero={MascararNumeroLog(numeroExterno)} fila={filaAoVivo} idadeCdrSeg={idadeCdr.TotalSeconds:F0} " +
                                $"motivo=cdr_antigo_demais_para_ainda_estar_na_fila_real");
                        }
                    }

                    // Recording — try all records in group
                    var recordingFile = cdr.RecordingFile;
                    var recordingDate = cdr.CallDate;
                    if (string.IsNullOrWhiteSpace(recordingFile))
                    {
                        var comGravacao = grupo.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.RecordingFile));
                        if (comGravacao != null)
                        {
                            recordingFile = comGravacao.RecordingFile;
                            recordingDate = comGravacao.CallDate;
                            if (cdr.BillSec == 0 && cdr.Duration == 0 &&
                                (comGravacao.BillSec > 0 || comGravacao.Duration > 0))
                                duracaoFmt = FormatarDuracao(comGravacao.BillSec > 0 ? comGravacao.BillSec : comGravacao.Duration);
                            LogDetalhado($"CDR_RECORDING_LINKED_BY_LINKEDID linkedid={cdr.LinkedId} arquivo={recordingFile}");
                        }
                    }
                    else
                    {
                        LogDetalhado($"CDR_RECORDING_LINKED_BY_UNIQUEID uniqueid={cdr.UniqueId} arquivo={recordingFile}");
                    }

                    LogDetalhado($"CDR_RECORDING_FIELD_DEBUG linkedids={linkedIds} recordingfile={recordingFile} " +
                        $"grupoComGravacao={grupo.Count(r => !string.IsNullOrWhiteSpace(r.RecordingFile))}");

                    // Outgoing PSTN calls generate a CDR from the trunk/gateway perspective:
                    // src=trunk_DID (e.g. 556684263277), dst=ramal (100).
                    // The real called number is embedded in the recording filename with a route prefix
                    // (e.g. force-266984671226-100-... → 2=route, 66984671226=destination).
                    // Extract it and override numeroExterno/origemSaida so the entry shows the real
                    // destination. NÃO força mais tipo=Realizada aqui (v2.3.5): o Waven grava o arquivo
                    // force-record assim que o Dial() começa, mesmo quando a chamada termina em
                    // BUSY/NO ANSWER sem nunca conectar — forçar Realizada faria uma chamada Recusada/
                    // Não atendida "virar" Realizada só por ter esse arquivo. O tipo já veio correto de
                    // ClassificarChamada acima (que agora detecta ehOrigem corretamente via Channel,
                    // mesmo quando o tronco substitui o Caller-ID em cdr.Src).
                    var origemSaida = DedurzirTronco(cdr);

                    // v2.3.6 — canal EXTERNO real de entrada (Operadora/0800/WhatsApp TIM/Vivo),
                    // resolvido em PARALELO a origemSaida e sem depender dela estar vazia. Antes,
                    // quando a ligação passava por URA/fila antes de chegar num agente, o registro
                    // "principal" escolhido para classificação era a perna Local/<ramal>@from-queue
                    // do agente (sem nenhuma info de tronco) — DedurzirTronco via o Channel Local/
                    // e devolvia "Queue" (não vazio), o que bloqueava a detecção via nome de
                    // gravação abaixo (guardada por `string.IsNullOrWhiteSpace(origemSaida)`), então
                    // o canal externo real nunca era recuperado e o Histórico mostrava só "URA".
                    // CanalEntrada guarda o canal real separadamente — OrigemSaida/DedurzirTronco
                    // continuam intactos para não quebrar os rótulos de fluxo interno já validados
                    // ("Abandonada na fila"/"Desligou antes da fila", ver HistoricoLigacaoItem).
                    var canalEntrada = string.Empty;
                    if (!string.IsNullOrWhiteSpace(recordingFile))
                    {
                        var numeroDeGravacao = ExtrairNumeroDestinoDeGravacao(recordingFile);
                        if (!string.IsNullOrWhiteSpace(numeroDeGravacao))
                        {
                            Log($"CDR_RECORDING_CORRECTS_NUMBER | anterior={numeroExterno} correto={numeroDeGravacao} arquivo={recordingFile}");
                            numeroExterno = numeroDeGravacao;
                            // Chamadas saintes: extrai rota (Operadora/WhatsApp TIM/Vivo) pelo prefixo do filename
                            var origemDeGravacao = ExtrairOrigemSaidaDeGravacao(recordingFile);
                            if (!string.IsNullOrWhiteSpace(origemDeGravacao))
                                origemSaida = origemDeGravacao;
                        }
                        else
                        {
                            // Chamadas recebidas: o filename começa pelo DID do tronco que recebeu
                            // (ex: force-556684263277-caller-... → WhatsApp TIM; force-08001901900-
                            // caller-... → 0800). ExtrairNumeroDestinoDeGravacao retorna vazio para
                            // entradas pois não há prefixo de rota — mas ExtrairOrigemSaidaDeGravacao
                            // reconhece o DID.
                            var origemDeGravacao = ExtrairOrigemSaidaDeGravacao(recordingFile);
                            if (!string.IsNullOrWhiteSpace(origemDeGravacao))
                            {
                                canalEntrada = origemDeGravacao;
                                LogDetalhado($"CDR_INCOMING_CHANNEL_FROM_RECORDING | canal={origemDeGravacao} arquivo={recordingFile}");
                                if (string.IsNullOrWhiteSpace(origemSaida))
                                    origemSaida = origemDeGravacao;
                            }
                        }
                    }

                    // v2.3.6 (perf) — computado uma vez e reaproveitado abaixo (era calculado só
                    // depois, tarde demais para servir de guarda aqui — toda ligação ramal-a-ramal
                    // acabava pagando o custo da varredura de grupo abaixo à toa, já que uma
                    // chamada interna NUNCA tem canal externo pra encontrar).
                    var numExtNormalizado    = DialPlanService.RemoverDuplicacaoSequencial(numeroExterno ?? string.Empty);
                    var ehNumeroExternoRamal = DialPlanService.EhRamalInterno(numExtNormalizado);

                    // Sem gravação, ou DID não reconhecido no nome do arquivo: procura o canal
                    // externo em QUALQUER linha do grupo — a perna original de entrada (tronco →
                    // URA/fila) normalmente é uma linha diferente da escolhida como "principal".
                    // Pulado para ligações ramal-a-ramal (nunca têm canal externo a encontrar) e
                    // para ligações que ESTE ramal originou (ehOrigemPrincipal — CanalEntrada é
                    // conceito de ENTRADA; uma chamada de saída nunca tem um a descobrir, e
                    // escanear o grupo à toa nessas era o maior desperdício de CPU a cada sync,
                    // multiplicado por TODA chamada de saída da empresa em modo TodosRamais).
                    if (string.IsNullOrWhiteSpace(canalEntrada) && !ehNumeroExternoRamal && !ehOrigemPrincipal)
                    {
                        var canalDoGrupo = IdentificarCanalExternoDoGrupo(grupo);
                        if (!string.IsNullOrWhiteSpace(canalDoGrupo))
                        {
                            canalEntrada = canalDoGrupo;
                            LogDetalhado($"CDR_INCOMING_CHANNEL_FROM_GROUP_SCAN | canal={canalDoGrupo} linkedids={linkedIds}");
                            if (string.IsNullOrWhiteSpace(origemSaida))
                                origemSaida = canalDoGrupo;
                        }
                    }

                    // Perna auxiliar de retentativa de rota (Operadora/WhatsApp TIM/Vivo tentados em
                    // sequência quando a primeira rota falha): cada tentativa que este ramal ORIGINOU
                    // e que NUNCA conectou (não é Realizada) recebe seu próprio linkedid do Asterisk —
                    // não são unificadas pelo agrupamento acima porque o "número externo" varia por
                    // tentativa. Nessas tentativas, o tronco de saída substitui o Caller-ID pelo
                    // próprio DID da rota (prática padrão de telefonia) — então cdr.Src carrega nosso
                    // PRÓPRIO número (ex.: "+556696308630"), que vaza para numeroExterno via fallback
                    // de CLID em ObterNumeroExterno. Checado só AQUI (depois da correção via nome do
                    // arquivo de gravação acima) para não suprimir uma chamada cujo número real FOI
                    // recuperado com sucesso do nome do arquivo — só suprime quando o número seguiu
                    // sendo o nosso próprio DID mesmo depois dessa tentativa de correção. Exige >=7
                    // dígitos para nunca comparar ramal-para-ramal (2-5 dígitos) contra os DIDs.
                    var numeroExternoDigitos = new string((numeroExterno ?? string.Empty).Where(char.IsDigit).ToArray());
                    if (ehOrigemPrincipal && tipo != TipoHistoricoLigacao.Realizada &&
                        numeroExternoDigitos.Length >= 7 &&
                        !string.IsNullOrWhiteSpace(CanalIdentificacaoService.IdentificarPorValor(numeroExterno)))
                    {
                        Log($"SELF_NUMBER_LEG_DETECTED linkedids={linkedIds} uid={cdr.UniqueId} " +
                            $"numero={MascararNumeroLog(numeroExterno)} tipo={tipo} src={cdr.Src} dst={cdr.Dst}");
                        Log($"SELF_NUMBER_LEG_SUPPRESSED linkedids={linkedIds} uid={cdr.UniqueId}");
                        continue;
                    }

                    // O SIP ao vivo (SipService, no momento da discagem) mede o tempo real entre
                    // Ringing e BusyHere — informação mais precisa que a duração aproximada do CDR.
                    // Se existe um registro local (FonteCdr=false) já classificado ao vivo para essa
                    // MESMA tentativa, o resultado do CDR NUNCA rebaixa/substitui esse resultado — só
                    // confirma ou, se divergir, cede. Não há uniqueid/linkedid do lado do cliente (o
                    // Asterisk só atribui isso depois, do lado servidor) — por isso a correlação usa,
                    // em ordem de confiança: número (obrigatório) + janela ESTREITA de tempo (45s —
                    // cobre a defasagem de relógio cliente/servidor observada, ~15s, sem invadir o
                    // intervalo típico entre redes discagens manuais, sempre >=80s nos testes reais)
                    // → se houver mais de um candidato na janela estreita, desempata por ramal
                    // originador e rota → só cai para a janela ampla (120s) se a estreita não achar
                    // nada. Se mesmo assim sobrar mais de um candidato, a correlação é ambígua e o
                    // CDR NÃO é sobrescrito (mantém o valor que ele mesmo calculou, que já é confiável
                    // agora que o merge não funde mais discagens manuais independentes).
                    //
                    // v2.3.6 — tipo==Realizada TAMBÉM entra aqui agora. Teste real com o tronco
                    // WAVOIP provou que Asterisk pode registrar disp=ANSWERED billsec>0 pro CDR
                    // mesmo quando o operador cancelou ainda no toque (CALL_LOCAL_HANGUP
                    // eraEmChamada=False — nosso lado NUNCA viu 200 OK, ok=False) — aparenta ser o
                    // gateway WAVOIP sinalizando "atendido" internamente antes do destino real no
                    // WhatsApp sequer tocar, uma ambiguidade do lado do servidor que o cliente SIP
                    // não tem como prever. A fonte de verdade local (LastOutboundWasCancelledLocally,
                    // só setada quando eraEmChamada==False no momento do cancelamento — nunca durante
                    // uma chamada já conectada) tem prioridade sobre esse "Realizada" genérico do
                    // CDR. Seguro por construção: só existe entrada local Cancelada para tentativas
                    // que o PRÓPRIO operador cancelou antes de conectar (ver IniciarLigacaoAsync) —
                    // uma chamada realmente atendida e encerrada normalmente nunca cria uma, então
                    // nunca pode ser encontrada aqui por engano.
                    if (ehOrigemPrincipal && (tipo == TipoHistoricoLigacao.Recusada ||
                                               tipo == TipoHistoricoLigacao.NaoAtendida ||
                                               tipo == TipoHistoricoLigacao.Realizada))
                    {
                        var numCdrNorm = NormalizarNumeroParaAgrupamento(numeroExterno);
                        if (numCdrNorm.Length >= 7)
                        {
                            var candidatosEstreitos = locaisComResultadoSip.Where(l =>
                                Math.Abs((l.DataHora - cdr.CallDate).TotalSeconds) <= 45 &&
                                string.Equals(NormalizarNumeroParaAgrupamento(l.Numero), numCdrNorm, StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            var candidatos = candidatosEstreitos.Count > 0
                                ? candidatosEstreitos
                                : locaisComResultadoSip.Where(l =>
                                    Math.Abs((l.DataHora - cdr.CallDate).TotalSeconds) <= 120 &&
                                    string.Equals(NormalizarNumeroParaAgrupamento(l.Numero), numCdrNorm, StringComparison.OrdinalIgnoreCase))
                                    .ToList();

                            if (candidatos.Count > 1)
                            {
                                var desempate = candidatos.Where(l =>
                                    (string.IsNullOrWhiteSpace(l.RamalOrigem) ||
                                     string.Equals(l.RamalOrigem, srcRamal, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(l.RamalOrigem, chRamal, StringComparison.OrdinalIgnoreCase)) &&
                                    (string.IsNullOrWhiteSpace(l.OrigemSaida) ||
                                     string.Equals(l.OrigemSaida, origemSaida, StringComparison.OrdinalIgnoreCase)))
                                    .ToList();
                                if (desempate.Count == 1) candidatos = desempate;
                            }

                            // v2.3.6 — candidatos=0 é o caso NORMAL para qualquer chamada já assentada
                            // (sem stub local pendente há muito tempo) — deixou de ser exceção rara
                            // assim que a causa raiz da Cancelada foi corrigida, e virou a maior fonte
                            // de linhas do cdr_sync.log (reprocessa TODO o histórico retido a cada
                            // ciclo). Resumo + dump completo só no modo detalhado; o rastro que
                            // importa quando HÁ de fato uma tentativa Cancelada/Recusada/NaoAtendida
                            // local pra reconciliar (candidatos=1) continua sempre ligado, abaixo.
                            LogDetalhado($"OUTBOUND_CDR_RECONCILE_MATCH linkedids={linkedIds} uid={cdr.UniqueId} candidatos={candidatos.Count}");
                            if (candidatos.Count == 0)
                            {
                                LogDetalhado($"OUTBOUND_CDR_RECONCILE_DEBUG linkedids={linkedIds} uid={cdr.UniqueId} " +
                                    $"numCdrNorm={numCdrNorm} cdrCallDate={cdr.CallDate:yyyy-MM-dd HH:mm:ss} " +
                                    $"locaisComResultadoSip_total={locaisComResultadoSip.Count}");
                                // v2.3.6 — dump item-a-item era "diagnóstico temporário" da investigação
                                // da Cancelada (causa raiz já encontrada e corrigida — ver DeduplicarPorNumeroETempo/
                                // DeduplicarChamadasMesmaRaiz). A linha de resumo acima já basta no dia a dia;
                                // o detalhe completo só é útil numa investigação nova — atrás do toggle.
                                foreach (var l in locaisComResultadoSip)
                                    LogDetalhado($"  OUTBOUND_CDR_RECONCILE_DEBUG_ITEM id={l.Id} numero={MascararNumeroLog(l.Numero)} " +
                                        $"numeroNorm={NormalizarNumeroParaAgrupamento(l.Numero)} tipo={l.Tipo} " +
                                        $"dataHora={l.DataHora:yyyy-MM-dd HH:mm:ss} diffSeg={(l.DataHora - cdr.CallDate).TotalSeconds:F0} " +
                                        $"canceladaPeloOperador={l.CanceladaPeloOperador} fonteCdr={l.FonteCdr}");
                            }

                            if (candidatos.Count == 1)
                            {
                                var localMatch = candidatos[0];
                                if (localMatch.Tipo == TipoHistoricoLigacao.Cancelada)
                                    Log($"OUTBOUND_CANCELLED_CDR_MATCH linkedids={linkedIds} uid={cdr.UniqueId} cdr_tipo={tipo}");

                                if (localMatch.Tipo != tipo)
                                {
                                    Log($"OUTBOUND_RESULT_CONFLICT linkedids={linkedIds} uid={cdr.UniqueId} sip={localMatch.Tipo} cdr={tipo}");
                                    tipo = localMatch.Tipo;
                                    Log($"OUTBOUND_CDR_LOCAL_PRESERVED linkedids={linkedIds} uid={cdr.UniqueId} resultado={tipo}");
                                    if (tipo == TipoHistoricoLigacao.Cancelada)
                                    {
                                        canceladaPeloOperadorPropagado = localMatch.CanceladaPeloOperador;
                                        Log($"OUTBOUND_CANCELLED_OVERWRITE_BLOCKED linkedids={linkedIds} uid={cdr.UniqueId} " +
                                            $"cdr_teria_classificado_como=Realizada_ou_NaoAtendida");
                                    }
                                }
                                else
                                {
                                    Log($"OUTBOUND_RESULT_CDR_APPLIED linkedids={linkedIds} uid={cdr.UniqueId} resultado={tipo} motivo=concorda_com_sip");
                                    if (tipo == TipoHistoricoLigacao.Cancelada)
                                    {
                                        canceladaPeloOperadorPropagado = localMatch.CanceladaPeloOperador;
                                        Log($"OUTBOUND_CANCELLED_PRESERVED linkedids={linkedIds} uid={cdr.UniqueId}");
                                    }
                                }
                            }
                            else if (candidatos.Count > 1)
                            {
                                Log($"OUTBOUND_CDR_RECONCILE_AMBIGUOUS linkedids={linkedIds} uid={cdr.UniqueId} " +
                                    $"candidatos={candidatos.Count} motivo=nao_substitui_mantem_cdr_proprio");
                            }
                        }
                    }

                    // Redundante com CDR_CALL_CONSOLIDATED logo abaixo (mesmo tipo+uid, com mais
                    // contexto) — mantido só no modo detalhado.
                    LogDetalhado($"OUTBOUND_LINKEDID_PRESERVED linkedids={linkedIds} uid={cdr.UniqueId} tipo={tipo}");

                    var gravacaoUrl = ResolverUrlGravacao(recordingFile, config, recordingDate);

                    // When no trunk origin was identified but the external number is a ramal,
                    // store the channel explicitly so history displays correctly without dynamic fallback.
                    if (string.IsNullOrWhiteSpace(origemSaida) && ehNumeroExternoRamal)
                    {
                        origemSaida = "Ramal interno";
                        Log($"CDR_RAMAL_INTERNO_DETECTED numero={numExtNormalizado} tipo={tipo}");
                    }

                    var item = new HistoricoLigacaoItem
                    {
                        Id              = Guid.NewGuid().ToString("N"),
                        UniqueId        = cdr.UniqueId,
                        LinkedId        = cdr.LinkedId,
                        Numero          = numeroExterno,
                        Nome            = LimparClid(cdr.Clid, numeroExterno),
                        Tipo            = tipo,
                        DataHora        = cdr.CallDate,
                        Duracao         = duracaoFmt,
                        OrigemSaida     = origemSaida,
                        CanalEntrada    = canalEntrada,
                        RamalOrigem     = string.IsNullOrWhiteSpace(srcRamal)  ? chRamal    : srcRamal,
                        RamalDestino    = string.IsNullOrWhiteSpace(dstRamal)  ? dstChRamal : dstRamal,
                        RamalAtendeu    = ramalAtendeu,
                        GravacaoArquivo = recordingFile,
                        GravacaoUrl     = gravacaoUrl,
                        FonteCdr        = true,
                        CanceladaPeloOperador = canceladaPeloOperadorPropagado
                    };

                    resultado.Add(item);
                    // v2.3.6 — resumo final por grupo, repetido a cada sync pra TODO o histórico
                    // retido (não só chamadas novas) — maior contribuinte isolado de volume do
                    // cdr_sync.log. O Tipo final de cada chamada já está sempre disponível no
                    // historico.json (fonte de verdade); isto é só rastro de máquina, útil ao
                    // investigar o próprio sync — fica no modo detalhado.
                    LogDetalhado($"CDR_CALL_CONSOLIDATED linkedids={linkedIds} tipo={tipo} numero={numeroExterno} " +
                        $"ramalAtendeu={ramalAtendeu} caixaPostal={foiParaCaixaPostal} gravacao={!string.IsNullOrWhiteSpace(gravacaoUrl)}");
                }

                Log($"CDR_SYNC_TIMING estagio=classificacao ms={swEstagio.ElapsedMilliseconds} chamadas={resultado.Count}");
                swEstagio.Restart();

                // Safety net: a call answered by one ramal must not also appear as a separate
                // Perdida/NaoAtendidaNesseRamal entry for another ramal that only rang. This
                // catches queue ring-attempt legs Asterisk records under their own linkedid,
                // which the grouping/merge steps above may not have folded into the answered call.
                resultado = SuprimirPerdidasAtendidasPorOutroRamal(resultado);

                // Deduplicate trunk-leg CDR entries: outgoing calls generate two CDR rows
                // (one from the ramal, one from the PSTN/trunk). After ObterNumeroExterno fix,
                // both show the same external number → merge recording into ramal entry.
                resultado = DeduplicarChamadasMesmaRaiz(resultado);

                Log($"CDR_SYNC_TIMING estagio=reconciliacao ms={swEstagio.ElapsedMilliseconds} chamadas={resultado.Count}");
                swEstagio.Restart();

                // Validate CDR-derived recording URLs in parallel (removes 404 entries)
                await ValidarUrlsCdrAsync(resultado);

                // HTTP directory listing fallback for calls still missing a recording URL
                await PreencherGravacoesPorDirListingAsync(resultado, config);

                Log($"CDR_SYNC_TIMING estagio=gravacoes ms={swEstagio.ElapsedMilliseconds}");

                var totalGravacoes = resultado.Count(i => !string.IsNullOrWhiteSpace(i.GravacaoUrl));
                Log($"CDR_SYNC_DONE chamadas={resultado.Count} gravacoes={totalGravacoes}");
                Log($"HISTORY_REFRESH_OK total={resultado.Count}");
                Log($"CDR_SYNC_TIMING estagio=TOTAL ms={swTotal.ElapsedMilliseconds}");
            }
            catch (Exception ex)
            {
                Log($"CDR_SYNC_FAIL erro={ex.Message} ms_ate_falha={swTotal.ElapsedMilliseconds}", LogLevel.ERROR);
                throw;
            }

            return resultado;
        }

        // ── Trunk-leg deduplication ────────────────────────────────────────────────

        // Outgoing calls through a PSTN trunk produce two CDR rows:
        //   Row A (ramal leg): src=ramal(109), dst=66984671226 — no recording
        //   Row B (trunk leg): src=trunk(6684263277), dst=66984671226 — HAS recording
        // After ObterNumeroExterno fix both rows show Numero=66984671226 and the same Tipo.
        // This method merges them: keeps the ramal-leg entry and copies the recording from the trunk leg.
        private static List<HistoricoLigacaoItem> DeduplicarChamadasMesmaRaiz(List<HistoricoLigacaoItem> itens)
        {
            if (itens.Count < 2) return itens;

            var removidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < itens.Count; i++)
            {
                var a = itens[i];
                if (removidos.Contains(a.Id)) continue;

                var numA = PhoneNumberNormalizer.NormalizeBrazilPhone(
                    new string((a.Numero ?? string.Empty).Where(char.IsDigit).ToArray()));
                if (numA.Length < 7) continue;

                // v2.3.5 — Recusada/NaoAtendida nunca conectam (billsec=0), então nunca têm a perna
                // de tronco com gravação separada que este método existe pra unificar (esse padrão
                // é exclusivo de chamada ANSWERED — ver comentário do método). Sem essa guarda, duas
                // discagens manuais reais e distintas pro mesmo número com o mesmo desfecho (ex.:
                // Recusada duas vezes seguidas, ~90-115s de intervalo, observado em teste real)
                // seriam incorretamente unificadas numa só.
                // v2.3.6 — Cancelada incluída: também nunca conecta (billsec=0), mesmo motivo. Sem
                // isso, DUAS discagens de teste canceladas pro mesmo número em <2min (cenário real
                // que expôs o bug "Cancelada virou Não atendida") podiam ser fundidas em uma só aqui
                // — a fusão em si não trocava o Tipo, mas o CONSUMIA como "b" de outra rodada,
                // deixando o registro genuinamente distinto sem chance de ser corrigido depois.
                if (a.Tipo == TipoHistoricoLigacao.Recusada || a.Tipo == TipoHistoricoLigacao.NaoAtendida ||
                    a.Tipo == TipoHistoricoLigacao.Cancelada)
                    continue;

                for (int j = i + 1; j < itens.Count; j++)
                {
                    var b = itens[j];
                    if (removidos.Contains(b.Id)) continue;
                    if (a.Tipo != b.Tipo) continue;

                    // Only look within a 2-minute window (ramal/trunk rows are milliseconds apart)
                    var diffSec = Math.Abs((a.DataHora - b.DataHora).TotalSeconds);
                    if (diffSec > 120) continue;

                    var numB = PhoneNumberNormalizer.NormalizeBrazilPhone(
                        new string((b.Numero ?? string.Empty).Where(char.IsDigit).ToArray()));
                    if (!string.Equals(numA, numB, StringComparison.OrdinalIgnoreCase)) continue;

                    // Same call: merge recording (from whichever row has it) into entry a
                    if (string.IsNullOrWhiteSpace(a.GravacaoUrl) && !string.IsNullOrWhiteSpace(b.GravacaoUrl))
                    {
                        a.GravacaoUrl     = b.GravacaoUrl;
                        a.GravacaoArquivo = b.GravacaoArquivo;
                    }
                    if (a.Duracao == "00:00" && b.Duracao != "00:00")
                        a.Duracao = b.Duracao;

                    removidos.Add(b.Id);
                    Log($"CDR_HISTORY_ROW_CONSOLIDATED | num={numA} tipo={a.Tipo} " +
                        $"mantido={a.UniqueId} removido={b.UniqueId} diff_sec={diffSec:F0} " +
                        $"gravacao_copiada={!string.IsNullOrWhiteSpace(a.GravacaoUrl)}");
                }
            }

            var total = itens.Count;
            var result = itens.Where(i => !removidos.Contains(i.Id)).ToList();
            if (removidos.Count > 0)
                Log($"CDR_HISTORY_DEDUP_DONE | removidos={removidos.Count} total_antes={total} total_depois={result.Count}");
            return result;
        }

        // v2.3.6 — CAUSA RAIZ real encontrada com log de produção: registros locais (RegistrarHistorico,
        // em DialerShellWindow) salvam Numero=numeroFinal, que inclui o DÍGITO DE ROTA (1/2/3) —
        // ex.: "266984671226" — enquanto o número vindo do CDR (numeroExterno) já chega SEM esse
        // dígito — ex.: "66984671226" (extraído do nome da gravação). Sem remover o prefixo dos DOIS
        // lados antes de comparar, a correlação abaixo SEMPRE retornava candidatos=0 — a
        // reconciliação SIP-ao-vivo↔CDR nunca encontrava o registro local correspondente, para
        // NENHUM tipo (Recusada/NaoAtendida/Cancelada), silenciosamente. Mesmo padrão já usado
        // corretamente em HistoricoStorageService.MesclarCdr (RemoverPrefixoDeRota antes de comparar).
        private static string NormalizarNumeroParaAgrupamento(string numero)
            => PhoneNumberNormalizer.NormalizeBrazilPhone(
                DialPlanService.RemoverPrefixoDeRota(numero ?? string.Empty));

        private static string MascararNumeroLog(string numero)
        {
            var n = new string((numero ?? string.Empty).Where(char.IsDigit).ToArray());
            if (n.Length <= 4) return new string('*', n.Length);
            return n.Substring(0, 2) + new string('*', n.Length - 4) + n.Substring(n.Length - 2);
        }

        // Nunca suspende a importação de uma "Perdida"/"Abandonada" por mais que isso, mesmo que
        // ExisteClienteNaFilaAoVivo continue retornando true — é bem acima do maior "tempo máximo
        // de espera" configurável de qualquer fila real do Issabel (ex.: 2min40s no caso reportado).
        private static readonly TimeSpan _janelaMaximaSuspensaoPorFila = TimeSpan.FromMinutes(10);

        // Consulta o snapshot ao vivo de "Filas em Tempo Real" (mesmo dado do endpoint
        // /api/ami/queues-live já existente, via AmiMonitorService — nenhum endpoint novo) para
        // saber se o cliente ainda está esperando em alguma fila. Isso é ESTADO real da fila
        // (QueueEntry), não uma perna SIP de toque — a fonte de verdade certa para decidir se uma
        // chamada realmente saiu da fila ou só terminou uma tentativa de oferta a um agente.
        private static bool ExisteClienteNaFilaAoVivo(string numeroExterno, out string filaEncontrada)
        {
            filaEncontrada = string.Empty;
            try
            {
                var svc = AmiMonitorService.Current;
                if (svc == null) return false;

                var numAlvo = NormalizarNumeroParaAgrupamento(numeroExterno);
                if (numAlvo.Length < 7) return false;

                foreach (var fila in svc.Filas)
                {
                    foreach (var cliente in fila.Clientes)
                    {
                        var numCliente = NormalizarNumeroParaAgrupamento(cliente.Numero);
                        if (numCliente.Length >= 7 && string.Equals(numAlvo, numCliente, StringComparison.OrdinalIgnoreCase))
                        {
                            filaEncontrada = fila.Fila;
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"QUEUE_ENTRY_ACTIVE_CHECK_ERROR erro={ex.Message}", LogLevel.WARN);
            }
            return false;
        }

        // A call is Perdida only when nobody answered it anywhere. Ring-attempt CDR rows that
        // escaped grouping/merge (e.g. no external number to key the merge on, or Asterisk gave
        // each attempt its own linkedid) can otherwise surface as extra history rows for the
        // SAME real call. Two passes clean this up:
        //   1) A Perdida/NaoAtendidaNesseRamal row for a number another row shows as answered
        //      (Recebida/Realizada) within a short window is not a real missed call — someone
        //      already answered it.
        //   2) Multiple Perdida/NaoAtendidaNesseRamal rows for the same number/window with no
        //      answered sibling are ring-attempt duplicates of ONE real missed call — keep only
        //      the most recent (closest to the call's real end).
        // Together this guarantees History shows exactly one row per real call outcome, and
        // MISSED-call notifications never fire more than once for it.
        // internal (não private) — v2.3.6 reaproveita esta função em HistoricoStorageService.MesclarCdr
        // para podar entradas órfãs já persistidas (ver comentário lá).
        internal static List<HistoricoLigacaoItem> SuprimirPerdidasAtendidasPorOutroRamal(
            List<HistoricoLigacaoItem> itens, int janelaSeg = 180)
        {
            if (itens.Count < 2) return itens;

            var removidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Passo 1: Perdida/NaoAtendida cuja mesma chamada foi atendida em outro lugar.
            var atendidas = itens.Where(i =>
                i.Tipo == TipoHistoricoLigacao.Recebida || i.Tipo == TipoHistoricoLigacao.Realizada).ToList();

            foreach (var atendida in atendidas)
            {
                var numAtendida = NormalizarNumeroParaAgrupamento(atendida.Numero);
                if (numAtendida.Length < 7) continue;

                foreach (var outro in itens)
                {
                    if (ReferenceEquals(outro, atendida) || removidos.Contains(outro.Id)) continue;

                    // v2.3.6 — cobre também NaoAtendida com OrigemSaida=="Queue": assinatura
                    // inequívoca de uma entrada órfã de tentativa de fila criada pela versão
                    // anterior do bug de classificação (uma ligação de CLIENTE ofertada pela fila
                    // era classificada como "chamada de SAÍDA sem resposta" quando o ramal
                    // ofertado coincidia com o ramal desta máquina — ver ehOrigemPrincipal em
                    // IssabelCdrService.SincronizarAsync). NUNCA suprime uma NaoAtendida real (essa
                    // sempre tem uma rota de verdade — Operadora/WhatsApp TIM/Vivo — como
                    // OrigemSaida, nunca "Queue", que só DedurzirTronco atribui a fluxo interno).
                    var elegivelParaSupressao =
                        outro.Tipo == TipoHistoricoLigacao.Perdida ||
                        outro.Tipo == TipoHistoricoLigacao.NaoAtendidaNesseRamal ||
                        (outro.Tipo == TipoHistoricoLigacao.NaoAtendida &&
                         string.Equals(outro.OrigemSaida, "Queue", StringComparison.OrdinalIgnoreCase));
                    if (!elegivelParaSupressao) continue;

                    var numOutro = NormalizarNumeroParaAgrupamento(outro.Numero);
                    if (!string.Equals(numAtendida, numOutro, StringComparison.OrdinalIgnoreCase)) continue;

                    var diffSec = Math.Abs((atendida.DataHora - outro.DataHora).TotalSeconds);
                    if (diffSec > janelaSeg) continue;

                    removidos.Add(outro.Id);
                    Log($"HISTORY_MISSED_SUPPRESSED linkedid={outro.LinkedId} motivo=answered_by_other_extension " +
                        $"numero={numOutro} atendidoPor={atendida.RamalAtendeu} diff_sec={diffSec:F0}");
                }
            }

            // Passo 2: entre as Perdida/NaoAtendida restantes (sem nenhuma atendida por perto),
            // colapsa duplicatas do mesmo número/janela em uma única entrada final — mantém a
            // mais recente, que reflete melhor o desfecho real da chamada.
            var restantesPerdidas = itens
                .Where(i => !removidos.Contains(i.Id) &&
                            (i.Tipo == TipoHistoricoLigacao.Perdida || i.Tipo == TipoHistoricoLigacao.NaoAtendidaNesseRamal))
                .OrderByDescending(i => i.DataHora)
                .ToList();

            for (int i = 0; i < restantesPerdidas.Count; i++)
            {
                var a = restantesPerdidas[i];
                if (removidos.Contains(a.Id)) continue;
                var numA = NormalizarNumeroParaAgrupamento(a.Numero);
                if (numA.Length < 7) continue;

                for (int j = i + 1; j < restantesPerdidas.Count; j++)
                {
                    var b = restantesPerdidas[j];
                    if (removidos.Contains(b.Id)) continue;
                    var numB = NormalizarNumeroParaAgrupamento(b.Numero);
                    if (!string.Equals(numA, numB, StringComparison.OrdinalIgnoreCase)) continue;

                    var diffSec = Math.Abs((a.DataHora - b.DataHora).TotalSeconds);
                    if (diffSec > janelaSeg) continue;

                    removidos.Add(b.Id);
                    Log($"HISTORY_DUPLICATE_MISSED_REMOVED linkedid={b.LinkedId} mantido_linkedid={a.LinkedId} " +
                        $"numero={numB} diff_sec={diffSec:F0}");
                }
            }

            if (removidos.Count == 0) return itens;
            var result = itens.Where(i => !removidos.Contains(i.Id)).ToList();
            Log($"HISTORY_MISSED_SUPPRESSED_DONE removidos={removidos.Count} total_antes={itens.Count} total_depois={result.Count}");
            return result;
        }

        // Deduplicates history entries by normalized number + time window only (no tipo check).
        // Used during local history reprocessing to clean up legacy duplicates such as CDR entries
        // that collide with local-SIP entries that were not removed by MesclarCdr due to number
        // format mismatches (e.g. "5566..." CDR vs "66..." local). When both entries survive,
        // the CDR entry (FonteCdr=true) wins; otherwise the first occurrence is kept.
        private static List<HistoricoLigacaoItem> DeduplicarPorNumeroETempo(List<HistoricoLigacaoItem> itens)
        {
            if (itens.Count < 2) return itens;

            // Sort CDR entries first so they win the "keep first" election
            var ordenados = itens.OrderByDescending(i => i.FonteCdr).ThenByDescending(i => i.DataHora).ToList();
            var removidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < ordenados.Count; i++)
            {
                var a = ordenados[i];
                if (removidos.Contains(a.Id)) continue;

                // v2.3.6 — RemoverPrefixoDeRota adicionado: um stub local de saída guarda Numero COM
                // o dígito de rota (1/2/3, ex. "266984671226"), mas o CDR já vem sem ele (ex.
                // "66984671226") — sem remover o prefixo dos dois lados antes de comparar, esse par
                // nunca era reconhecido como a MESMA chamada (mesmo bug de raiz encontrado em
                // NormalizarNumeroParaAgrupamento, que protege Cancelada/Recusada/NaoAtendida).
                var numA = PhoneNumberNormalizer.NormalizeBrazilPhone(DialPlanService.RemoverPrefixoDeRota(a.Numero ?? string.Empty));
                if (numA.Length < 8) continue;

                for (int j = i + 1; j < ordenados.Count; j++)
                {
                    var b = ordenados[j];
                    if (removidos.Contains(b.Id)) continue;

                    // v2.3.5 — duas entradas já confirmadas por CDR (FonteCdr=true, UniqueId real e
                    // distinto do Asterisk) NUNCA são duplicata uma da outra por número+tempo — são,
                    // por definição, duas chamadas REAIS diferentes (ex.: operador rediscando pro
                    // mesmo número minutos depois). Esse dedup existe pra limpar o par CDR×stub local
                    // (FonteCdr=false) da MESMA chamada — nunca pra colapsar dois registros de CDR já
                    // confirmados. Sem essa guarda, discagens manuais reais próximas no tempo (ex.:
                    // 87-115s de intervalo, observado em teste real) eram descartadas indevidamente.
                    if (a.FonteCdr && b.FonteCdr) continue;

                    // v2.3.6 — CAUSA RAIZ real do bug "Cancelada virou Não atendida": este dedup
                    // colapsa por número+tempo (±120s) SEM saber se dois registros são a MESMA
                    // chamada. Numa rajada de testes reais (5 discagens canceladas pro mesmo número
                    // em ~90s — cenário de QA comum, mas também um redial manual normal), todos os
                    // stubs Cancelada e CDRs vizinhos caem na janela de 120s uns dos outros. A versão
                    // anterior só propagava CanceladaPeloOperador/Tipo=Cancelada pro sobrevivente `a`
                    // na PRIMEIRA fusão; a partir da segunda fusão do MESMO `a` (que já virava
                    // Cancelada), `!a.CanceladaPeloOperador` já era falso e o próximo stub Cancelada
                    // era descartado em silêncio — várias ligações canceladas reais e distintas
                    // colapsavam numa só, e os CDRs órfãos das outras (cujo stub tinha sido "roubado"
                    // por essa fusão) ficavam com o Tipo genérico do Asterisk (NaoAtendida/Recusada),
                    // exatamente o bug relatado. Fix: um registro CanceladaPeloOperador=true nunca
                    // participa deste dedup cego — nem como sobrevivente `a` (evita "roubar" um stub
                    // alheio) nem como removido `b` (evita perder um stub próprio). A fusão correta
                    // por chamada já existe e é confiável em HistoricoStorageService.MesclarCdr
                    // (stub-enrichment por UniqueId real do Asterisk, nunca por número+tempo) — este
                    // dedup não precisa e não deve tentar reproduzi-la.
                    if (a.CanceladaPeloOperador || b.CanceladaPeloOperador) continue;

                    var diffSec = Math.Abs((a.DataHora - b.DataHora).TotalSeconds);
                    if (diffSec > 120) continue;

                    var numB = PhoneNumberNormalizer.NormalizeBrazilPhone(DialPlanService.RemoverPrefixoDeRota(b.Numero ?? string.Empty));
                    if (!string.Equals(numA, numB, StringComparison.OrdinalIgnoreCase)) continue;

                    if (string.IsNullOrWhiteSpace(a.GravacaoUrl) && !string.IsNullOrWhiteSpace(b.GravacaoUrl))
                    {
                        a.GravacaoUrl     = b.GravacaoUrl;
                        a.GravacaoArquivo = b.GravacaoArquivo;
                    }
                    if (a.Duracao == "00:00" && b.Duracao != "00:00") a.Duracao = b.Duracao;

                    removidos.Add(b.Id);
                    Log($"REPROCESS_DUPLICATE_REMOVED | num={numA} mantido={a.UniqueId}({a.Tipo}) removido={b.UniqueId}({b.Tipo}) diff_sec={diffSec:F0}");
                }
            }

            return ordenados.Where(i => !removidos.Contains(i.Id)).ToList();
        }

        // ── Group merging ──────────────────────────────────────────────────────────

        // Merges groups that share the same external src number within janelaSeg seconds.
        // This handles Issabel queue scenarios where each ramal ring attempt gets its own linkedid.
        //
        // v2.3.5 — investigação real provou que isso também fundia discagens MANUAIS
        // independentes para o mesmo número (ex.: operador clica ligar, cai, clica de novo minutos
        // depois) porque o "número externo" (com prefixo de rota) é idêntico entre tentativas e
        // 180s é uma janela larga o bastante pra cobrir o intervalo típico entre cliques. Resultado:
        // até 4 discagens reais viravam 1 registro no Histórico, com EscolherCdrPrincipal sempre
        // escolhendo a mais recente e descartando as outras (CDR_QUEUE_ATTEMPT_SUPPRESSED) —
        // Recusada virava Não atendida por herdar duração de outra tentativa, tentativas
        // "sumiam", UniqueId principal trocava a cada nova discagem. PodeMesclarGruposPorJanela
        // agora bloqueia o merge quando AMBOS os grupos são discagens outbound diretas
        // (dcontext=from-internal originado por um ramal) — cada clique do operador já tem seu
        // próprio linkedid genuíno do Asterisk e nunca deveria ser fundido com outro clique.
        private static List<List<CdrChamada>> MergeGruposPorSrcJanela(
            List<List<CdrChamada>> grupos, int janelaSeg = 180)
        {
            var ordenados = grupos.OrderBy(g => g.Min(r => r.CallDate)).ToList();
            var resultado = new List<List<CdrChamada>>();

            foreach (var grupo in ordenados)
            {
                var srcExt  = ObterSrcExternoDoGrupo(grupo);
                var dataMin = grupo.Min(r => r.CallDate);
                var dataMax = grupo.Max(r => r.CallDate);

                // Only merge incoming calls that share an identifiable external caller
                if (string.IsNullOrWhiteSpace(srcExt))
                {
                    resultado.Add(new List<CdrChamada>(grupo));
                    continue;
                }

                var existente = resultado.LastOrDefault(res =>
                {
                    var resSrc = ObterSrcExternoDoGrupo(res);
                    if (!string.Equals(resSrc, srcExt, StringComparison.OrdinalIgnoreCase)) return false;
                    var resMax = res.Max(x => x.CallDate);
                    var resMin = res.Min(x => x.CallDate);
                    // Groups overlap or are within the window
                    var dentroDaJanela = (dataMin - resMax).TotalSeconds <= janelaSeg &&
                                          (resMin - dataMax).TotalSeconds <= janelaSeg;
                    if (!dentroDaJanela) return false;
                    return PodeMesclarGruposPorJanela(res, grupo);
                });

                if (existente != null)
                    existente.AddRange(grupo);
                else
                    resultado.Add(new List<CdrChamada>(grupo));
            }

            return resultado;
        }

        // Só bloqueia quando os DOIS lados são claramente discagens outbound diretas — nunca
        // restringe o caso original (pernas de fila/ring-group/URA de uma mesma chamada RECEBIDA),
        // que continua fundindo normalmente.
        private static bool PodeMesclarGruposPorJanela(List<CdrChamada> grupoExistente, List<CdrChamada> grupoNovo)
        {
            var linkedidsExistente = string.Join(",", grupoExistente.Select(r => r.LinkedId).Distinct());
            var linkedidsNovo      = string.Join(",", grupoNovo.Select(r => r.LinkedId).Distinct());
            LogDetalhado($"CDR_GROUP_MERGE_CHECK existente={linkedidsExistente} novo={linkedidsNovo}");

            if (EhGrupoOutboundDireto(grupoExistente) && EhGrupoOutboundDireto(grupoNovo))
            {
                LogDetalhado($"CDR_GROUP_MERGE_BLOCKED_OUTBOUND existente={linkedidsExistente} novo={linkedidsNovo} " +
                    $"motivo=ambos_outbound_manual_linkedid_proprio");
                return false;
            }

            LogDetalhado($"CDR_GROUP_MERGE_ALLOWED existente={linkedidsExistente} novo={linkedidsNovo}");
            return true;
        }

        // Discagem manual direta de um ramal: dcontext=from-internal (contexto do dialplan do
        // Issabel pra ramal ligando pra fora) E o canal de origem resolve pra um ramal válido
        // (SIP/104-...). Esse padrão é exclusivo de Dial() iniciado diretamente por um ramal —
        // pernas de fila/ring-group/URA usam outros dcontext (from-queue, ext-group, etc.) ou
        // canais Local/, então EhRotaNaoHumana também protege contra falso positivo aqui.
        private static bool EhGrupoOutboundDireto(List<CdrChamada> grupo)
        {
            return grupo.Any(r =>
                string.Equals((r.DContext ?? string.Empty).Trim(), "from-internal", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(ExtrairRamal(r.Channel)) &&
                !EhRotaNaoHumana(r));
        }

        // Returns the external phone number for a group by searching Src and Dst.
        // Needed for queue legs where Asterisk sets Src = ramal/queue, not the external caller.
        private static string ObterSrcExternoDoGrupo(List<CdrChamada> grupo)
        {
            return grupo
                .SelectMany(r => new[] { r.Src, r.Dst })
                .Select(n => new string((n ?? string.Empty).Where(char.IsDigit).ToArray()))
                .FirstOrDefault(s => s.Length >= 8) // Brazilian numbers are always ≥8 digits
                ?? string.Empty;
        }

        private static bool GrupoContemNumero(List<CdrChamada> grupo, string numero)
        {
            var n = new string(numero.Where(char.IsDigit).ToArray());
            return grupo.Any(r =>
                (r.Src ?? string.Empty).Contains(n, StringComparison.OrdinalIgnoreCase) ||
                (r.Dst ?? string.Empty).Contains(n, StringComparison.OrdinalIgnoreCase));
        }

        // ── URA / IVR / non-human route detection ─────────────────────────────────

        // Returns true when a CDR record was answered only by automated routing,
        // not by a real SIP ramal (queue, IVR, Local/ channel, macro, etc.)
        private static bool EhRotaNaoHumana(CdrChamada cdr)
        {
            var dst  = (cdr.Dst      ?? string.Empty).ToLowerInvariant();
            var ctx  = (cdr.DContext ?? string.Empty).ToLowerInvariant();
            var dstCh = (cdr.DstChannel ?? string.Empty).ToLowerInvariant();

            if (dst.StartsWith("ivr-")   || dst.StartsWith("ura-"))   return true;
            if (dst.StartsWith("queue-") || dst.StartsWith("app-"))    return true;
            if (ctx.Contains("ivr")   || ctx.Contains("ura"))          return true;
            // Contains("queue") catches both "queue-..." and "from-queue" (Asterisk delivery context)
            if (ctx.Contains("queue") || ctx.Contains("macro"))         return true;
            // Local/ channels are pure Asterisk internal routing legs
            if (dstCh.StartsWith("local/"))                            return true;
            return false;
        }

        // ── Voicemail detection ────────────────────────────────────────────────────

        private static bool EhVoicemail(CdrChamada cdr)
        {
            var lastApp  = (cdr.LastApp  ?? string.Empty).ToUpperInvariant();
            var lastData = (cdr.LastData ?? string.Empty).ToLowerInvariant();
            var dst      = (cdr.Dst      ?? string.Empty).ToLowerInvariant();
            var dctx     = (cdr.DContext ?? string.Empty).ToLowerInvariant();

            if (lastApp.Contains("VOICEMAIL")) return true;
            if (lastData.Contains("voicemail")) return true;
            if (dst.StartsWith("vm-") || dst.Contains("voicemail")) return true;
            if (dctx.Contains("voicemail")) return true;
            return false;
        }

        // ── Ramal validation ───────────────────────────────────────────────────────

        private static System.Collections.Generic.HashSet<string> CarregarRamaisConhecidos()
        {
            try
            {
                var contatos = ContatoStorageService.Carregar();
                return new System.Collections.Generic.HashSet<string>(
                    contatos
                        .Select(c => new string((c.Numero ?? string.Empty).Where(char.IsDigit).ToArray()))
                        .Where(n => n.Length >= 2 && n.Length <= 5),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch { return new System.Collections.Generic.HashSet<string>(); }
        }

        // 2-3 digit numbers are always valid internal ramais.
        // 4-5 digit numbers must appear in the known contacts/AMI list.
        private static bool EhRamalValido(string ramal, System.Collections.Generic.HashSet<string> conhecidos)
        {
            if (!EhRamal(ramal)) return false;
            if (ramal.Length <= 3) return true;
            return conhecidos.Count == 0 || conhecidos.Contains(ramal);
        }

        // Strict agent validation: requires membership in the known contacts list for ALL lengths.
        // Unlike EhRamalValido, never accepts a 3-digit ramal by length alone — prevents IVR
        // extensions like 700 from being misidentified as the answering agent.
        private static bool EhRamalAgente(string ramal, System.Collections.Generic.HashSet<string> conhecidos)
        {
            if (!EhRamal(ramal)) return false;
            if (conhecidos.Count == 0) return true;
            return conhecidos.Contains(ramal);
        }

        private static string ExtrairRamalValidado(string channel,
            System.Collections.Generic.HashSet<string> conhecidos)
        {
            var r = ExtrairRamal(channel);
            if (string.IsNullOrWhiteSpace(r)) return string.Empty;
            if (EhRamalValido(r, conhecidos)) return r;
            Log($"INVALID_EXTENSION_DETECTED canal={channel} extraido={r} motivo=nao_validado");
            return string.Empty;
        }

        // ── Principal record selection ─────────────────────────────────────────────

        private static CdrChamada EscolherCdrPrincipal(
            List<CdrChamada> grupo,
            System.Collections.Generic.HashSet<string> conhecidos)
        {
            // 1. ANSWERED by agent ramal (not IVR/queue/voicemail), highest billsec
            var ans = grupo
                .Where(r => string.Equals(r.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase)
                         && r.BillSec > 0 && !EhVoicemail(r) && !EhRotaNaoHumana(r))
                .OrderByDescending(r => r.BillSec)
                .FirstOrDefault();
            if (ans != null) return ans;

            // 2. ANSWERED by real ramal (including IVR legs), highest billsec
            ans = grupo
                .Where(r => string.Equals(r.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase)
                         && r.BillSec > 0 && !EhVoicemail(r))
                .OrderByDescending(r => r.BillSec)
                .FirstOrDefault();
            if (ans != null) return ans;

            // 3. ANSWERED real ramal, billsec = 0
            ans = grupo.FirstOrDefault(r =>
                string.Equals(r.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase) && !EhVoicemail(r));
            if (ans != null) return ans;

            // 4. Voicemail record (so classification can detect CaixaPostal)
            ans = grupo.FirstOrDefault(r =>
                string.Equals(r.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase) && EhVoicemail(r));
            if (ans != null) return ans;

            // 5. NO ANSWER — prefer the real inbound leg (non-queue row) over ring attempts.
            // Ring attempt rows (src=queue, dst=ramal) are usually more recent than the inbound
            // leg, so the old "most recent" heuristic picked them first, causing the queue ring
            // destination to become RamalDestino on the main history entry.
            var noAns = grupo
                .Where(r => string.Equals(r.Disposition, "NO ANSWER", StringComparison.OrdinalIgnoreCase)
                         && !EhRotaNaoHumana(r))
                .OrderByDescending(r => r.CallDate)
                .FirstOrDefault()
                ?? grupo
                .Where(r => string.Equals(r.Disposition, "NO ANSWER", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.CallDate)
                .FirstOrDefault();
            if (noAns != null) return noAns;

            return grupo.OrderByDescending(r => r.CallDate).First();
        }

        // ── Atendente identification ───────────────────────────────────────────────

        private static string ExtrairRamalAtendente(
            CdrChamada cdr,
            System.Collections.Generic.HashSet<string> conhecidos)
        {
            if (EhVoicemail(cdr)) return string.Empty;

            // dstchannel: handles SIP/109-xxx and Local/109@from-queue-xxx
            // Uses EhRamalAgente so IVR extensions (e.g. 700) are rejected
            var r = ExtrairRamal(cdr.DstChannel);
            if (!string.IsNullOrWhiteSpace(r) && EhRamalAgente(r, conhecidos)) return r;

            // dst as direct ramal — strict agent check (rejects IVR extensions like 700)
            if (EhRamalAgente(cdr.Dst, conhecidos)) return cdr.Dst;

            // channel (src-side of the ANSWERED leg)
            r = ExtrairRamal(cdr.Channel);
            if (!string.IsNullOrWhiteSpace(r) && EhRamalAgente(r, conhecidos)) return r;

            // src as last resort
            if (EhRamalAgente(cdr.Src, conhecidos)) return cdr.Src;

            // Extract from recording filename
            var fromFile = ExtrairRamalDeGravacao(cdr.RecordingFile);
            return EhRamalAgente(fromFile, conhecidos) ? fromFile : string.Empty;
        }

        // ── CDR fetch ─────────────────────────────────────────────────────────────

        private static async Task<List<CdrChamada>> BuscarLinhasCdrAsync(SipConfig config, int diasRetencao)
        {
            var lista = new List<CdrChamada>();
            var dataInicio = DateTime.Now.AddDays(-Math.Max(1, diasRetencao));

            await using var conn = new MySqlConnection(BuildConnectionString(config));
            await conn.OpenAsync();

            var colunasDisponiveis = await ObterColunasAsync(conn, config.CdrBanco, config.CdrTabela);
            var temRecordingFile = colunasDisponiveis.Contains("recordingfile", StringComparer.OrdinalIgnoreCase);
            var temLinkedId      = colunasDisponiveis.Contains("linkedid",      StringComparer.OrdinalIgnoreCase);
            var temLastData      = colunasDisponiveis.Contains("lastdata",      StringComparer.OrdinalIgnoreCase);

            var selectCols = new System.Text.StringBuilder(
                "calldate, src, dst, channel, dstchannel, lastapp, duration, billsec, disposition, uniqueid, " +
                "COALESCE(clid, '') AS clid, COALESCE(dcontext, '') AS dcontext");

            selectCols.Append(temLastData ? ", COALESCE(lastdata, '') AS lastdata" : ", '' AS lastdata");
            selectCols.Append(temRecordingFile ? ", COALESCE(recordingfile, '') AS recordingfile" : ", '' AS recordingfile");
            selectCols.Append(temLinkedId ? ", COALESCE(linkedid, uniqueid) AS linkedid" : ", uniqueid AS linkedid");

            var sql = $"SELECT {selectCols} FROM `{config.CdrTabela}` " +
                      "WHERE calldate >= @DataInicio " +
                      "ORDER BY calldate DESC LIMIT 5000";

            LogHelper.Cdr($"CDR_QUERY dataInicio={dataInicio:yyyy-MM-dd} tabela={config.CdrTabela} modo={config.HistoricoModoExibicao}");
            LogHelper.Cdr($"CDR_SQL {sql}");

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@DataInicio", dataInicio);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lista.Add(new CdrChamada
                {
                    CallDate      = reader.GetDateTime("calldate"),
                    Src           = reader.GetString("src"),
                    Dst           = reader.GetString("dst"),
                    Channel       = reader.GetString("channel"),
                    DstChannel    = reader.GetString("dstchannel"),
                    LastApp       = reader.GetString("lastapp"),
                    LastData      = reader.GetString("lastdata"),
                    Duration      = reader.IsDBNull(reader.GetOrdinal("duration")) ? 0 : reader.GetInt32("duration"),
                    BillSec       = reader.IsDBNull(reader.GetOrdinal("billsec"))  ? 0 : reader.GetInt32("billsec"),
                    Disposition   = reader.GetString("disposition"),
                    UniqueId      = reader.GetString("uniqueid"),
                    RecordingFile = reader.GetString("recordingfile"),
                    LinkedId      = reader.GetString("linkedid"),
                    Clid          = reader.GetString("clid"),
                    DContext      = reader.GetString("dcontext")
                });
            }

            LogHelper.Cdr($"CDR_FETCH_RESULT total={lista.Count} primeiro={lista.FirstOrDefault()?.CallDate:yyyy-MM-dd HH:mm} ultimo={lista.LastOrDefault()?.CallDate:yyyy-MM-dd HH:mm}");
            return lista;
        }

        private static async Task<List<string>> ObterColunasAsync(MySqlConnection conn, string banco, string tabela)
        {
            var cols = new List<string>();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS " +
                                  "WHERE TABLE_SCHEMA = @banco AND TABLE_NAME = @tabela";
                cmd.Parameters.AddWithValue("@banco", banco);
                cmd.Parameters.AddWithValue("@tabela", tabela);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    cols.Add(reader.GetString(0));
            }
            catch { }
            return cols;
        }

        // ── Classification ─────────────────────────────────────────────────────────

        // ehOrigem: este ramal ORIGINOU a chamada (calculado pelo chamador com
        // srcRamal==ramal || chRamal==ramal — ver comentário em SincronizarAsync sobre por que
        // cdr.Src sozinho não é confiável quando o tronco de saída substitui o Caller-ID).
        //
        // Regra de direção (nunca misturar): Perdida é EXCLUSIVO de chamadas RECEBIDAS sem
        // atendimento. Uma chamada que ESTE ramal originou e que não conectou é sempre
        // Recusada (BUSY — cliente rejeitou/estava ocupado) ou NaoAtendida (NO ANSWER/FAILED/
        // CONGESTION — tocou sem resposta ou falha técnica de rota), nunca Perdida.
        // duracaoToqueSegundos: maior Duration/BillSec entre todas as linhas do grupo — proxy do
        // tempo de toque até o desfecho (confirmado por teste real: bate quase exatamente com o
        // tempo medido ao vivo via SIP Ringing→BusyHere — 11s vs 11s, 60s vs 61s num teste real).
        // Único jeito de aplicar a mesma heurística de Recusada/NaoAtendida quando NÃO há
        // observação SIP ao vivo disponível (ramal de outro operador em modo TodosRamais, app
        // fechado durante a chamada, sync após reinício). Ver SipService.ClassificarResultadoLigacao
        // para a versão ao vivo e a mesma constante de limiar (SipService.OutboundDeclineThresholdSeconds).
        private static TipoHistoricoLigacao ClassificarChamada(
            CdrChamada cdr, bool ehOrigem,
            bool foiAtendidaGlobalmente, bool foiParaCaixaPostal,
            int duracaoToqueSegundos)
        {
            var disp = (cdr.Disposition ?? string.Empty).ToUpperInvariant();

            // Voicemail-only: nobody answered with a real phone
            if (foiParaCaixaPostal && !foiAtendidaGlobalmente)
                return TipoHistoricoLigacao.CaixaPostal;

            // v2.3.6 — trilha de decisão (LogDetalhado, não Log): esta função reclassifica TODO
            // grupo outbound retido a cada ciclo de sync, não só os novos — era a maior fonte de
            // linhas repetidas do cdr_sync.log depois da limpeza acima. A heurística em si (limiar
            // Recusada/NaoAtendida) continua INTOCADA — só o log de cada passo passa a exigir
            // "Logs detalhados" em Configurações. O Tipo final de cada chamada sempre está no
            // historico.json; para investigar OUTRA classificação incorreta, ligue "Logs
            // detalhados" e reproduza — mesmo padrão já usado em outros pontos deste arquivo.
            if (disp == "ANSWERED" && cdr.BillSec > 0 && !EhVoicemail(cdr))
            {
                if (ehOrigem) LogDetalhado($"OUTBOUND_RESULT_ANSWERED uid={cdr.UniqueId}");
                return ehOrigem ? TipoHistoricoLigacao.Realizada : TipoHistoricoLigacao.Recebida;
            }

            if (disp is "NO ANSWER" or "BUSY" or "FAILED" or "CONGESTION")
            {
                if (ehOrigem)
                {
                    if (disp == "BUSY")
                    {
                        // Investigação real confirmou: a operadora/gateway devolve BUSY tanto para
                        // recusa explícita quanto para timeout de toque — disposition sozinho NUNCA
                        // basta. Sem duração de toque confiável (0 = campo zerado/ausente), a
                        // escolha conservadora é NaoAtendida — nunca presumir recusa sem sinal.
                        LogDetalhado($"OUTBOUND_RING_DURATION uid={cdr.UniqueId} ringDurationSeg={duracaoToqueSegundos} fonte=cdr_duration");
                        var resultado = duracaoToqueSegundos > 0 && duracaoToqueSegundos < SipService.OutboundDeclineThresholdSeconds
                            ? TipoHistoricoLigacao.Recusada
                            : TipoHistoricoLigacao.NaoAtendida;
                        LogDetalhado($"OUTBOUND_BUSY_CLASSIFICATION uid={cdr.UniqueId} ringDurationSeg={duracaoToqueSegundos} " +
                            $"threshold={SipService.OutboundDeclineThresholdSeconds} result={resultado}");
                        LogDetalhado(resultado == TipoHistoricoLigacao.Recusada
                            ? $"OUTBOUND_CLASSIFIED_DECLINED uid={cdr.UniqueId}"
                            : $"OUTBOUND_CLASSIFIED_NO_ANSWER uid={cdr.UniqueId} motivo=sem_duracao_confiavel_ou_proximo_do_timeout");
                        return resultado;
                    }
                    LogDetalhado($"OUTBOUND_RESULT_NO_ANSWER uid={cdr.UniqueId} disposition={disp}");
                    LogDetalhado($"OUTBOUND_CLASSIFIED_NO_ANSWER uid={cdr.UniqueId} motivo=disposition_{disp}");
                    return TipoHistoricoLigacao.NaoAtendida;
                }

                if (foiAtendidaGlobalmente) return TipoHistoricoLigacao.NaoAtendidaNesseRamal;
                if (foiParaCaixaPostal)     return TipoHistoricoLigacao.CaixaPostal;
                return TipoHistoricoLigacao.Perdida;
            }

            if (disp == "ANSWERED" && !EhVoicemail(cdr))
                return ehOrigem ? TipoHistoricoLigacao.Realizada : TipoHistoricoLigacao.Recebida;

            return ehOrigem ? TipoHistoricoLigacao.NaoAtendida : TipoHistoricoLigacao.Perdida;
        }

        // ── Number helpers ─────────────────────────────────────────────────────────

        private static string ObterNumeroExterno(CdrChamada cdr, string ramal)
        {
            LogDetalhado($"CDR_RECORDING_LINK_START linkedid={cdr.LinkedId} src={cdr.Src} dst={cdr.Dst} ramal={ramal}");

            if (!string.IsNullOrWhiteSpace(ramal) && ExtrairRamalSrc(cdr.Src) == ramal)
            {
                LogDetalhado($"CDR_CALL_MAIN_NUMBER_SELECTED numero={cdr.Dst} via=ramal_src_match");
                return cdr.Dst;
            }

            // Ramal-to-ramal: both src and dst are internal extensions.
            // Use cdr.Src directly instead of CLID, which may contain the caller's
            // external/mobile phone number registered in their SIP CLID configuration.
            if (EhRamal(cdr.Src ?? string.Empty) && EhRamal(cdr.Dst ?? string.Empty))
            {
                LogDetalhado($"CDR_CALL_MAIN_NUMBER_SELECTED numero={cdr.Src} via=ramal_to_ramal");
                return cdr.Src ?? string.Empty;
            }

            // Extract ONLY the phone number from CLID — never the name part.
            // CLID format: "Name" <number>  or  <number>  or  plain digits
            if (!string.IsNullOrWhiteSpace(cdr.Clid))
            {
                var m = System.Text.RegularExpressions.Regex.Match(cdr.Clid, @"<(\d+)>");
                if (m.Success)
                {
                    LogDetalhado($"CDR_CALL_MAIN_NUMBER_SELECTED numero={m.Groups[1].Value} via=clid_bracket");
                    return m.Groups[1].Value;
                }
                var soNum = new string(cdr.Clid.Where(char.IsDigit).ToArray());
                if (soNum.Length >= 6)
                {
                    LogDetalhado($"CDR_CALL_MAIN_NUMBER_SELECTED numero={soNum} via=clid_digits");
                    return soNum;
                }
            }

            // Trunk-leg detection: both src and dst are long external numbers (neither is a ramal).
            // This happens on outgoing PSTN calls where Asterisk records a separate CDR for the
            // trunk/carrier leg with src=trunk_number (e.g. 6684263277) and dst=real_called_number.
            // In this case dst IS the number the user actually called.
            var srcDigits = new string((cdr.Src ?? string.Empty).Where(char.IsDigit).ToArray());
            var dstDigits = new string((cdr.Dst ?? string.Empty).Where(char.IsDigit).ToArray());
            if (!EhRamal(cdr.Src ?? string.Empty) && srcDigits.Length >= 7 && !EhRamal(cdr.Dst ?? string.Empty) && dstDigits.Length >= 7)
            {
                LogDetalhado($"CDR_RECORDING_IGNORED_TRUNK_NUMBER | numero_tronco={srcDigits} numero_real={dstDigits}");
                LogDetalhado($"CDR_CALL_MAIN_NUMBER_SELECTED numero={cdr.Dst ?? string.Empty} via=trunk_leg_dst");
                return cdr.Dst ?? string.Empty;
            }

            LogDetalhado($"CDR_CALL_MAIN_NUMBER_SELECTED numero={cdr.Src ?? string.Empty} via=fallback_src");
            return cdr.Src ?? string.Empty;
        }

        private static string DedurzirTronco(CdrChamada cdr)
        {
            var ch  = ((cdr.DstChannel ?? string.Empty) + (cdr.Channel ?? string.Empty)).ToUpperInvariant();
            var ctx = (cdr.DContext ?? string.Empty).ToLowerInvariant();
            var dst = (cdr.Dst      ?? string.Empty).ToLowerInvariant();

            if (ch.Contains("DAHDI")) return "DAHDI";
            if (ch.Contains("TRUNK")) return "Tronco";

            // Queue / IVR / URA routing: mark so NormalizarCanal can label correctly
            if (ch.Contains("LOCAL/")           ||
                ctx.StartsWith("queue")         || ctx.Contains("ivr") || ctx.Contains("ura") ||
                dst.StartsWith("queue-")        || dst.StartsWith("ivr-") || dst.StartsWith("ura-") ||
                dst.StartsWith("app-")          || dst.StartsWith("s-"))
                return "Queue";

            // Para chamadas SIP/PJSIP: o channel do Asterisk contém o DID/tronco
            // (ex: "PJSIP/WAVOIP-556684263277-00000001"). Usa IdentificarEntrada para
            // extrair o canal configurado (WhatsApp TIM / Vivo / Operadora) via substring
            // matching dos DIDs conhecidos. Sem isso, entradas via WhatsApp ficam como "Operadora".
            var textoCanal = (cdr.Channel ?? string.Empty) + " " + (cdr.DstChannel ?? string.Empty);
            var canalIdentificado = CanalIdentificacaoService.IdentificarEntrada(textoCanal);
            if (!string.IsNullOrEmpty(canalIdentificado) && canalIdentificado != "Entrada não identificada")
            {
                LogDetalhado($"CDR_CHANNEL_IDENTIFIED | channel={cdr.Channel} dstchannel={cdr.DstChannel} => {canalIdentificado}");
                return canalIdentificado;
            }

            return string.Empty;
        }

        // v2.3.6 — Busca o canal externo real (Operadora/0800/WhatsApp TIM/Vivo) em QUALQUER linha
        // do grupo, não apenas no registro "principal" escolhido para classificação. Quando a
        // ligação passa por URA/fila antes de chegar num agente, o registro principal costuma ser
        // a perna Local/<ramal>@from-queue-... do agente humano (sem nenhuma info de tronco) — a
        // identidade do tronco de entrada normalmente só aparece numa linha diferente do mesmo
        // grupo (a perna original, ligando da operadora/WhatsApp/0800 para a fila/URA). Sem isso,
        // o canal externo real fica perdido atrás do rótulo genérico "Queue"/"URA".
        private static string IdentificarCanalExternoDoGrupo(List<CdrChamada> grupo)
        {
            foreach (var r in grupo.OrderBy(x => x.CallDate))
            {
                var texto = (r.Channel ?? string.Empty) + " " + (r.DstChannel ?? string.Empty);
                var canal = CanalIdentificacaoService.IdentificarEntrada(texto);
                if (!string.IsNullOrEmpty(canal) && canal != "Entrada não identificada")
                    return canal;
            }
            return string.Empty;
        }

        private static string ResolverUrlGravacao(string recordingFile, SipConfig config, DateTime callDate)
        {
            if (!config.GravacaoAtiva) return string.Empty;
            if (string.IsNullOrWhiteSpace(recordingFile)) return string.Empty;

            Log($"RECORDING_SEARCH arquivo={recordingFile} data={callDate:yyyy-MM-dd}");
            var datePath = $"{callDate:yyyy}/{callDate:MM}/{callDate:dd}";

            if (config.GravacaoTipoAcesso == "URL" && !string.IsNullOrWhiteSpace(config.GravacaoUrlBase))
            {
                var base_ = config.GravacaoUrlBase.TrimEnd('/');
                var url   = (recordingFile.Contains('/') || recordingFile.Contains('\\'))
                    ? $"{base_}/{recordingFile.Replace('\\', '/')}"
                    : $"{base_}/{datePath}/{recordingFile}";
                Log($"RECORDING_URL_RESOLVED url={url}");
                return url;
            }

            if (config.GravacaoTipoAcesso == "Local" && !string.IsNullOrWhiteSpace(config.GravacaoCaminhoLocal))
            {
                var c1 = Path.Combine(config.GravacaoCaminhoLocal, recordingFile);
                if (File.Exists(c1)) { Log($"RECORDING_MATCH c1={c1}"); return c1; }
                var c2 = Path.Combine(config.GravacaoCaminhoLocal,
                    callDate.Year.ToString(), callDate.Month.ToString("D2"),
                    callDate.Day.ToString("D2"), recordingFile);
                if (File.Exists(c2)) { Log($"RECORDING_MATCH c2={c2}"); return c2; }
                Log($"RECORDING_NOT_FOUND arquivo={c1}");
                return string.Empty;
            }

            return string.Empty;
        }

        // ── Ramal extraction helpers ───────────────────────────────────────────────

        // Extracts ramal from channel strings — handles all Asterisk formats:
        //   SIP/109-0000abc         → "109"
        //   PJSIP/109-0000abc       → "109"
        //   Local/109@from-queue-x  → "109"   (split at '@' before '-')
        private static string ExtrairRamal(string channel)
        {
            if (string.IsNullOrWhiteSpace(channel)) return string.Empty;
            var idx = channel.IndexOf('/');
            if (idx < 0) return string.Empty;
            var parte   = channel.Substring(idx + 1);
            var atIdx   = parte.IndexOf('@');
            var dashIdx = parte.IndexOf('-');
            int end = (atIdx > 0 && dashIdx > 0) ? Math.Min(atIdx, dashIdx)
                    : (atIdx   > 0)               ? atIdx
                    : (dashIdx > 0)               ? dashIdx
                    : -1;
            var ramal = end > 0 ? parte.Substring(0, end) : parte;
            return EhRamal(ramal) ? ramal : string.Empty;
        }

        // Extracts ramal from recording filename: exten-104-101-20260517... → "104"
        private static string ExtrairRamalDeGravacao(string recordingFile)
        {
            if (string.IsNullOrWhiteSpace(recordingFile)) return string.Empty;
            var fname  = Path.GetFileNameWithoutExtension(recordingFile);
            var partes = fname.Split('-', '_');
            for (int i = 1; i < partes.Length; i++)
                if (EhRamal(partes[i])) return partes[i];
            return string.Empty;
        }

        // Extracts the real called number from Asterisk forced-recording filenames.
        // Format: force-{dst_with_route_prefix}-{src_ramal}-{date}-{time}-{uniqueid}.wav
        // Example: force-266984671226-100-20260527-164921-... → "66984671226"
        // Returns non-empty ONLY when the first 10+-digit segment has a route prefix (1/2/3),
        // proving it is an outgoing call. Incoming calls have no route prefix → returns empty.
        //
        // v2.3.6 — CDR real de produção provou que o comprimento (DialPlanService.TemPrefixoDeRota,
        // pensada pra número DIGITADO por operador) classificava incorretamente um destino fixo de
        // 10 dígitos (DDD+8, prefixado = 11 dígitos — mesmo comprimento de um celular de 11 dígitos
        // SEM prefixo) como DID de entrada, deixando o número real da chamada esconder atrás do
        // nosso próprio DID (que vaza pro Caller-ID via troca do tronco — ver comentário mais acima
        // sobre ehOrigemPrincipal). Aqui o dígito inicial 1/2/3 já basta — o arquivo é gerado pelo
        // PRÓPRIO Waven ao discar (AplicarRegraDeDiscagem sempre prefixa com a rota), então não há
        // ambiguidade de "número digitado por humano" a resolver. DID configurado é checado
        // primeiro e tem prioridade — nenhum DID atual (Operadora/0800/WhatsApp TIM/Vivo) começa
        // com 1/2/3, mas a checagem evita qualquer colisão futura.
        private static string ExtrairNumeroDestinoDeGravacao(string recordingFile)
        {
            if (string.IsNullOrWhiteSpace(recordingFile)) return string.Empty;
            var fname  = Path.GetFileNameWithoutExtension(recordingFile);
            var partes = fname.Split('-', '_');
            for (int i = 1; i < partes.Length; i++)
            {
                var raw = new string(partes[i].Where(char.IsDigit).ToArray());
                if (raw.Length < 10) continue;

                if (!string.IsNullOrEmpty(CanalIdentificacaoService.IdentificarPorValor(raw)))
                    return string.Empty; // DID de entrada conhecido — nunca é destino de saída

                if (raw[0] == '1' || raw[0] == '2' || raw[0] == '3')
                    return PhoneNumberNormalizer.NormalizeBrazilPhone(raw.Substring(1));

                // First 10+ digit segment found but no route prefix → incoming call
                return string.Empty;
            }
            return string.Empty;
        }

        // Returns the route name ("Operadora", "WhatsApp TIM", "WhatsApp Vivo") from the
        // recording filename. Suporta dois formatos:
        //   Sainte:  force-{prefixo+destino}-{ramal}-... → prefixo 1/2/3 identifica rota
        //   Recebida: force-{DID}-{caller}-...           → DID identifica canal (ex: 556684263277 = WhatsApp TIM)
        // v2.3.6 — mesma correção de ExtrairNumeroDestinoDeGravacao acima: checa DID primeiro
        // (explícito, sem ambiguidade), só então o dígito de rota — não mais o comprimento.
        private static string ExtrairOrigemSaidaDeGravacao(string recordingFile)
        {
            if (string.IsNullOrWhiteSpace(recordingFile)) return string.Empty;
            var fname  = Path.GetFileNameWithoutExtension(recordingFile);
            var partes = fname.Split('-', '_');
            for (int i = 1; i < partes.Length; i++)
            {
                var raw = new string(partes[i].Where(char.IsDigit).ToArray());
                if (raw.Length < 10) continue;

                // Chamadas recebidas: o primeiro segmento numérico é o DID do tronco
                // Ex: force-556684263277-caller-... → DID "556684263277" = WhatsApp TIM
                var canalPorDid = CanalIdentificacaoService.IdentificarPorValor(raw);
                if (!string.IsNullOrEmpty(canalPorDid))
                    return canalPorDid;

                // Chamadas saintes: dígito de rota no início do número de destino (1=Operadora,
                // 2=TIM, 3=Vivo) — sempre presente, o Waven quem gera o nome do arquivo.
                if (raw[0] == '1' || raw[0] == '2' || raw[0] == '3')
                    return DialPlanService.NomeSaidaPeloPrefixo(raw);

                return string.Empty;
            }
            return string.Empty;
        }

        private static string ExtrairRamalSrc(string src)
            => EhRamal(src) ? src : string.Empty;

        private static string ExtrairRamalDst(string dst, string dstChannel,
            System.Collections.Generic.HashSet<string> conhecidos)
        {
            if (EhRamalValido(dst, conhecidos)) return dst;
            return ExtrairRamalValidado(dstChannel, conhecidos);
        }

        // Only 2-5 digit all-numeric strings are candidate ramais.
        // 6+ digits are external numbers, queue IDs, IVR routing codes, etc.
        private static bool EhRamal(string valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return false;
            return valor.All(char.IsDigit) && valor.Length >= 2 && valor.Length <= 5;
        }

        private static string FormatarDuracao(int segundos)
        {
            if (segundos <= 0) return "00:00";
            var ts = TimeSpan.FromSeconds(segundos);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        private static string LimparClid(string clid, string fallback)
        {
            if (string.IsNullOrWhiteSpace(clid)) return fallback;
            var match = System.Text.RegularExpressions.Regex.Match(clid, @"^""?([^""<>]+)""?\s*<");
            if (match.Success)
            {
                var nome = match.Groups[1].Value.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(nome) && nome != fallback) return nome;
            }
            var soNum = new string(clid.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(soNum)) return fallback;

            // v2.3.6 — CLID puramente numérico que bate com um DID configurado da própria empresa
            // (Operadora/0800/WhatsApp TIM/Vivo) é ruído de substituição de Caller-ID pelo tronco de
            // SAÍDA (mesmo fenômeno documentado em ehOrigemPrincipal/SELF_NUMBER_LEG_DETECTED acima
            // — Asterisk troca o CLID pelo DID/identidade do próprio tronco em chamadas outbound),
            // nunca é a identidade real do cliente. Ex.: clid="+556684263277" (nosso DID WhatsApp
            // TIM) numa ligação de SAÍDA fazia o card mostrar nosso próprio número como se fosse
            // quem ligou — usar o fallback (número externo já resolvido, ex. pelo nome do arquivo
            // de gravação) em vez do nosso próprio DID.
            if (!string.IsNullOrWhiteSpace(CanalIdentificacaoService.IdentificarPorValor(soNum)))
            {
                LogDetalhado($"CLID_SELF_DID_IGNORED clid_digits={soNum} fallback_usado={fallback}");
                return fallback;
            }

            return soNum;
        }

        // ── HTTP directory listing recording fallback ──────────────────────────────

        // For calls where CDR has no recordingfile, try fetching the web directory listing
        // for that day and match a file by call time (±5 min) and optionally phone number.
        private static async Task PreencherGravacoesPorDirListingAsync(
            List<HistoricoLigacaoItem> itens, SipConfig config)
        {
            if (!config.GravacaoAtiva ||
                config.GravacaoTipoAcesso != "URL" ||
                string.IsNullOrWhiteSpace(config.GravacaoUrlBase))
                return;

            var semGravacao = itens.Where(i => string.IsNullOrWhiteSpace(i.GravacaoUrl)).ToList();
            if (semGravacao.Count == 0) return;

            var base_ = config.GravacaoUrlBase.TrimEnd('/');

            var diasDistintos = semGravacao
                .Select(i => i.DataHora.Date)
                .Distinct()
                .ToList();

            foreach (var dia in diasDistintos)
            {
                var datePath = $"{dia:yyyy}/{dia:MM}/{dia:dd}";
                var dirUrl   = $"{base_}/{datePath}/";

                List<string> arquivos;
                try   { arquivos = await ListarDiretorioHttpAsync(dirUrl); }
                catch { continue; }

                if (arquivos.Count == 0) continue;
                Log($"CDR_RECORDING_DIR_LISTING data={dia:yyyy-MM-dd} arquivos={arquivos.Count} url={dirUrl}");

                foreach (var item in semGravacao.Where(i => i.DataHora.Date == dia))
                {
                    var candidatos = CandidatosArquivoDeGravacao(arquivos, item);
                    foreach (var arquivo in candidatos)
                    {
                        var url = $"{base_}/{datePath}/{arquivo}";
                        var ok  = await ValidarUrlHttpHeadAsync(url);
                        if (!ok)
                        {
                            Log($"RECORDING_404 url={url}");
                            Log($"RECORDING_MATCH_RETRY candidatos_restantes={candidatos.Count - candidatos.IndexOf(arquivo) - 1}");
                            continue;
                        }

                        item.GravacaoArquivo = arquivo;
                        item.GravacaoUrl     = url;
                        Log($"CDR_RECORDING_FIELD_DEBUG dir_match={arquivo} numero={item.Numero} datahora={item.DataHora:HH:mm:ss}");
                        break;
                    }
                }
            }
        }

        private static async Task<List<string>> ListarDiretorioHttpAsync(string url)
        {
            var html     = await _httpClient.GetStringAsync(url);
            var arquivos = new List<string>();

            var matches = System.Text.RegularExpressions.Regex.Matches(
                html,
                @"href=""([^""]+\.(wav|mp3|gsm|ogg))""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var raw   = m.Groups[1].Value;
                var fname = raw.Contains('/') ? raw.Substring(raw.LastIndexOf('/') + 1) : raw;
                if (!string.IsNullOrWhiteSpace(fname) && !arquivos.Contains(fname))
                    arquivos.Add(fname);
            }

            return arquivos;
        }

        // Returns an ordered list of candidate recording files for the given history item.
        // Priority: timestamp match ±5 min (with optional number filter) → number-only match.
        // The caller validates each candidate via HTTP HEAD and stops at the first valid one.
        private static List<string> CandidatosArquivoDeGravacao(List<string> arquivos, HistoricoLigacaoItem item)
        {
            var hora = item.DataHora;
            var num  = new string((item.Numero ?? string.Empty).Where(char.IsDigit).ToArray());

            var comTempo = arquivos
                .Select(a => new { arquivo = a, t = ExtrairHoraDoNome(a) })
                .Where(x => x.t.HasValue)
                .Select(x => new { x.arquivo, diff = Math.Abs((hora - x.t!.Value).TotalSeconds) })
                .Where(x => x.diff <= 300)
                .Where(x => string.IsNullOrWhiteSpace(num) || num.Length < 8 || x.arquivo.Contains(num))
                .OrderBy(x => x.diff)
                .Select(x => x.arquivo)
                .ToList();

            // If no timestamp candidates, try number-only
            if (comTempo.Count == 0 && num.Length >= 8)
            {
                var porNum = arquivos.FirstOrDefault(a => a.Contains(num));
                if (!string.IsNullOrWhiteSpace(porNum)) comTempo.Add(porNum!);
            }

            return comTempo;
        }

        // Validates CDR-derived recording URLs in parallel (up to 8 concurrent HEAD requests).
        // Zeroes out URLs that return 404 so the dir-listing fallback can try to find the file.
        private static async Task ValidarUrlsCdrAsync(List<HistoricoLigacaoItem> itens)
        {
            var comUrl = itens
                .Where(i => !string.IsNullOrWhiteSpace(i.GravacaoUrl) &&
                            (i.GravacaoUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             i.GravacaoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (comUrl.Count == 0) return;
            Log($"RECORDING_CDR_HEAD_VALIDATE count={comUrl.Count}");

            using var semaphore = new System.Threading.SemaphoreSlim(8);
            var tarefas = comUrl.Select(async item =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var ok = await ValidarUrlHttpHeadAsync(item.GravacaoUrl);
                    if (!ok)
                    {
                        Log($"RECORDING_CDR_URL_404 url={item.GravacaoUrl}");
                        item.GravacaoUrl = string.Empty;
                    }
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tarefas);
            var confirmadas = comUrl.Count(i => !string.IsNullOrWhiteSpace(i.GravacaoUrl));
            Log($"RECORDING_CDR_HEAD_VALIDATE_DONE confirmadas={confirmadas}/{comUrl.Count}");
        }

        private static async Task<bool> ValidarUrlHttpHeadAsync(string url)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await _headHttpClient.SendAsync(req);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // Extracts DateTime from common Asterisk recording filename patterns:
        //   - 14 consecutive digits: YYYYMMDDHHmmss
        //   - 8 digits + separator + 6 digits: YYYYMMDD-HHmmss
        private static DateTime? ExtrairHoraDoNome(string arquivo)
        {
            var nome = Path.GetFileNameWithoutExtension(arquivo);

            var m = System.Text.RegularExpressions.Regex.Match(nome, @"(\d{14})");
            if (m.Success &&
                DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt14))
                return dt14;

            m = System.Text.RegularExpressions.Regex.Match(nome, @"(\d{8})[_-](\d{6})");
            if (m.Success &&
                DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt8))
                return dt8;

            return null;
        }
    }
}
