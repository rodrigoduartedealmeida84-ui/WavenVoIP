using System;
using System.Collections.Generic;
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
                        Log($"REPROCESS_NUM_FIX orig={original} novo={corrigido}");
                        item.Numero = corrigido;
                        numerosCorrigidos++;
                    }
                }

                // Remove ramais with > 5 digits (IVR/queue IDs, concatenated ramais)
                if (!string.IsNullOrWhiteSpace(item.RamalOrigem)  && !EhRamal(item.RamalOrigem))  item.RamalOrigem  = string.Empty;
                if (!string.IsNullOrWhiteSpace(item.RamalDestino) && !EhRamal(item.RamalDestino)) item.RamalDestino = string.Empty;
                if (!string.IsNullOrWhiteSpace(item.RamalAtendeu) && !EhRamal(item.RamalAtendeu)) item.RamalAtendeu = string.Empty;
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

            HistoricoStorageService.Salvar(itens.OrderByDescending(i => i.DataHora).Take(5000).ToList());
            Log($"REPROCESS_DONE total={itens.Count} numerosCorrigidos={numerosCorrigidos} urlsRemovidas={urlsRemovidas}");
            return (itens.Count, numerosCorrigidos, urlsRemovidas);
        }

        // ── Main sync ──────────────────────────────────────────────────────────────

        public static async Task<List<HistoricoLigacaoItem>> SincronizarAsync(
            SipConfig config, int diasRetencao = 7)
        {
            Log("CDR_SYNC_START");
            var resultado = new List<HistoricoLigacaoItem>();

            try
            {
                var linhas = await BuscarLinhasCdrAsync(config, diasRetencao);
                Log($"CDR_ROWS_FOUND quantidade={linhas.Count}");

                // Raw-row diagnostic for the target number
                foreach (var r in linhas.Where(row => GrupoContemNumero(new List<CdrChamada> { row }, NumeroAlvoDiagnostico)))
                    Log($"CDR_DEBUG_RAW_ROWS calldate={r.CallDate:yyyy-MM-dd HH:mm:ss} clid={r.Clid} src={r.Src} dst={r.Dst} " +
                        $"dcontext={r.DContext} channel={r.Channel} dstchannel={r.DstChannel} lastapp={r.LastApp} " +
                        $"lastdata={r.LastData} disposition={r.Disposition} duration={r.Duration} billsec={r.BillSec} " +
                        $"uniqueid={r.UniqueId} linkedid={r.LinkedId} recordingfile={r.RecordingFile}");

                // Load known internal ramais from contacts for ramal validation
                var ramaisConhecidos = CarregarRamaisConhecidos();
                Log($"CDR_RAMAIS_CONHECIDOS total={ramaisConhecidos.Count}");

                // ── Step 1: primary group by linkedid ─────────────────────────────
                var gruposPrimarios = linhas
                    .GroupBy(r => string.IsNullOrWhiteSpace(r.LinkedId) ? r.UniqueId : r.LinkedId,
                             StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.ToList())
                    .ToList();

                // ── Step 2: secondary merge — same external src within 90 s ───────
                // Catches queue scenarios where Asterisk issues distinct linkedids for
                // the inbound leg and each ramal ring attempt.
                var grupos = MergeGruposPorSrcJanela(gruposPrimarios, janelaSeg: 90);
                Log($"CDR_GROUPS primary={gruposPrimarios.Count} afterMerge={grupos.Count}");

                var ramal          = config.Ramal?.Trim() ?? string.Empty;
                var todosModoRamais = string.Equals(config.HistoricoModoExibicao, "TodosRamais",
                    StringComparison.OrdinalIgnoreCase);

                foreach (var grupo in grupos)
                {
                    var linkedIds   = string.Join(",", grupo.Select(r => r.LinkedId).Distinct());
                    var ehAlvo      = GrupoContemNumero(grupo, NumeroAlvoDiagnostico);

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

                    foreach (var sup in grupo.Where(r => !ReferenceEquals(r, principal)))
                        Log($"CDR_QUEUE_ATTEMPT_SUPPRESSED linkedid={sup.LinkedId} uid={sup.UniqueId} disp={sup.Disposition} dst={sup.Dst}");

                    var cdr        = principal;
                    var srcRamal   = ExtrairRamalSrc(cdr.Src);
                    var dstRamal   = ExtrairRamalDst(cdr.Dst, cdr.DstChannel, ramaisConhecidos);
                    var chRamal    = ExtrairRamalValidado(cdr.Channel, ramaisConhecidos);
                    var dstChRamal = ExtrairRamalValidado(cdr.DstChannel, ramaisConhecidos);

                    Log($"RAMAL_PARSE_DEBUG src={cdr.Src}→{srcRamal} dst={cdr.Dst}→{dstRamal} " +
                        $"ch={cdr.Channel}→{chRamal} dstch={cdr.DstChannel}→{dstChRamal}");

                    Log($"CDR_DIAG_CALL linkedids={linkedIds} src={cdr.Src} dst={cdr.Dst} " +
                        $"ch={cdr.Channel} dstch={cdr.DstChannel} disp={cdr.Disposition} " +
                        $"uniqueid={cdr.UniqueId} linkedid={cdr.LinkedId} " +
                        $"ramalAtendeu={ramalAtendeu} registros={grupo.Count} agentes={registrosPorAgente.Count}");

                    // Detect URA/IVR-only calls (no human ramal answered)
                    var grupoPorRotaNaoHumana = !foiAtendidaGlobalmente &&
                        grupo.Any(r => string.Equals(r.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase)
                                    && EhRotaNaoHumana(r));
                    if (grupoPorRotaNaoHumana)
                        Log($"CDR_NON_HUMAN_ROUTE_DETECTED linkedids={linkedIds} src={cdr.Src} dst={cdr.Dst} dcontext={cdr.DContext}");

                    // Fallbacks
                    var ramalGravacao = ExtrairRamalDeGravacao(cdr.RecordingFile);
                    if (string.IsNullOrWhiteSpace(srcRamal)  && !string.IsNullOrWhiteSpace(ramalGravacao)) srcRamal  = ramalGravacao;
                    if (string.IsNullOrWhiteSpace(dstRamal)  && !string.IsNullOrWhiteSpace(ramalGravacao)) dstRamal  = ramalGravacao;
                    if (string.IsNullOrWhiteSpace(dstRamal)  && !string.IsNullOrWhiteSpace(ramalAtendeu))  dstRamal  = ramalAtendeu;

                    var ehMeuRamal = !string.IsNullOrWhiteSpace(ramal) &&
                        (srcRamal == ramal || dstRamal == ramal ||
                         chRamal  == ramal || dstChRamal == ramal ||
                         ramalAtendeu == ramal);

                    if (!todosModoRamais && !string.IsNullOrWhiteSpace(ramal) && !ehMeuRamal)
                        continue;

                    if (!todosModoRamais && EhRamal(cdr.Src) && EhRamal(cdr.Dst) && !ehMeuRamal)
                        continue;

                    var tipo          = ClassificarChamada(cdr, ramal, foiAtendidaGlobalmente, foiParaCaixaPostal);
                    var numeroExterno = ObterNumeroExterno(cdr, ramal);
                    var duracaoFmt    = FormatarDuracao(cdr.BillSec > 0 ? cdr.BillSec : cdr.Duration);

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
                        }
                    }

                    Log($"CDR_RECORDING_FIELD_DEBUG linkedids={linkedIds} recordingfile={recordingFile} " +
                        $"grupoComGravacao={grupo.Count(r => !string.IsNullOrWhiteSpace(r.RecordingFile))}");

                    var gravacaoUrl = ResolverUrlGravacao(recordingFile, config, recordingDate);

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
                        OrigemSaida     = DedurzirTronco(cdr),
                        RamalOrigem     = string.IsNullOrWhiteSpace(srcRamal)  ? chRamal    : srcRamal,
                        RamalDestino    = string.IsNullOrWhiteSpace(dstRamal)  ? dstChRamal : dstRamal,
                        RamalAtendeu    = ramalAtendeu,
                        GravacaoArquivo = recordingFile,
                        GravacaoUrl     = gravacaoUrl,
                        FonteCdr        = true
                    };

                    resultado.Add(item);
                    Log($"CDR_CALL_CONSOLIDATED linkedids={linkedIds} tipo={tipo} numero={numeroExterno} " +
                        $"ramalAtendeu={ramalAtendeu} caixaPostal={foiParaCaixaPostal} gravacao={!string.IsNullOrWhiteSpace(gravacaoUrl)}");
                }

                // Validate CDR-derived recording URLs in parallel (removes 404 entries)
                await ValidarUrlsCdrAsync(resultado);

                // HTTP directory listing fallback for calls still missing a recording URL
                await PreencherGravacoesPorDirListingAsync(resultado, config);

                var totalGravacoes = resultado.Count(i => !string.IsNullOrWhiteSpace(i.GravacaoUrl));
                Log($"CDR_SYNC_DONE chamadas={resultado.Count} gravacoes={totalGravacoes}");
            }
            catch (Exception ex)
            {
                Log($"CDR_SYNC_FAIL erro={ex.Message}", LogLevel.ERROR);
                throw;
            }

            return resultado;
        }

        // ── Group merging ──────────────────────────────────────────────────────────

        // Merges groups that share the same external src number within janelaSeg seconds.
        // This handles Issabel queue scenarios where each ramal ring attempt gets its own linkedid.
        private static List<List<CdrChamada>> MergeGruposPorSrcJanela(
            List<List<CdrChamada>> grupos, int janelaSeg = 90)
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
                    return (dataMin - resMax).TotalSeconds <= janelaSeg &&
                           (resMin - dataMax).TotalSeconds <= janelaSeg;
                });

                if (existente != null)
                    existente.AddRange(grupo);
                else
                    resultado.Add(new List<CdrChamada>(grupo));
            }

            return resultado;
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
            if (ctx.Contains("ivr")      || ctx.Contains("ura"))       return true;
            if (ctx.StartsWith("queue")  || ctx.Contains("macro"))     return true;
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

            // 5. Most recent NO ANSWER
            var noAns = grupo
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

        private static TipoHistoricoLigacao ClassificarChamada(
            CdrChamada cdr, string ramal,
            bool foiAtendidaGlobalmente, bool foiParaCaixaPostal)
        {
            var disp     = (cdr.Disposition ?? string.Empty).ToUpperInvariant();
            var srcRamal = ExtrairRamalSrc(cdr.Src);
            var ehOrigem = !string.IsNullOrWhiteSpace(ramal) && srcRamal == ramal;

            // Voicemail-only: nobody answered with a real phone
            if (foiParaCaixaPostal && !foiAtendidaGlobalmente)
                return TipoHistoricoLigacao.CaixaPostal;

            if (disp == "ANSWERED" && cdr.BillSec > 0 && !EhVoicemail(cdr))
                return ehOrigem ? TipoHistoricoLigacao.Realizada : TipoHistoricoLigacao.Recebida;

            if (disp is "NO ANSWER" or "BUSY" or "FAILED" or "CONGESTION")
            {
                if (foiAtendidaGlobalmente) return TipoHistoricoLigacao.NaoAtendidaNesseRamal;
                if (foiParaCaixaPostal)     return TipoHistoricoLigacao.CaixaPostal;
                return TipoHistoricoLigacao.Perdida;
            }

            if (disp == "ANSWERED" && !EhVoicemail(cdr))
                return ehOrigem ? TipoHistoricoLigacao.Realizada : TipoHistoricoLigacao.Recebida;

            return TipoHistoricoLigacao.Perdida;
        }

        // ── Number helpers ─────────────────────────────────────────────────────────

        private static string ObterNumeroExterno(CdrChamada cdr, string ramal)
        {
            if (!string.IsNullOrWhiteSpace(ramal) && ExtrairRamalSrc(cdr.Src) == ramal)
                return cdr.Dst;

            // Extract ONLY the phone number from CLID — never the name part.
            // CLID format: "Name" <number>  or  <number>  or  plain digits
            if (!string.IsNullOrWhiteSpace(cdr.Clid))
            {
                var m = System.Text.RegularExpressions.Regex.Match(cdr.Clid, @"<(\d+)>");
                if (m.Success) return m.Groups[1].Value;
                var soNum = new string(cdr.Clid.Where(char.IsDigit).ToArray());
                if (soNum.Length >= 6) return soNum;
            }

            return cdr.Src;
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
            return string.IsNullOrWhiteSpace(soNum) ? fallback : soNum;
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
