using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WavenApi.Models;

namespace WavenApi.Endpoints;

public static class AmiEndpoints
{
    // Cache de direção por Linkedid — preserva Saída/Entrada durante toda a chamada.
    // Chave = Linkedid (ambos os lados do bridge compartilham o mesmo Linkedid).
    // Entradas são removidas 60 s após a última vez que foram vistas no CoreShowChannels.
    private static readonly ConcurrentDictionary<string, DirCacheEntry> _dirCache =
        new(StringComparer.OrdinalIgnoreCase);

    private record DirCacheEntry(
        bool     IsOutgoing,
        DateTime FirstSeen,
        DateTime LastSeen,
        string   Motivo);

    public static void MapAmiEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/ami");
        g.MapGet("/extensions",      GetExtensions);
        g.MapGet("/peers",           GetPeers);
        g.MapGet("/live-extensions", GetPeers);  // alias para /peers
        g.MapGet("/queues-live",     GetQueuesLive);
        g.MapGet("/status",          GetStatus);
        g.MapPost("/test",           TestAmi);
    }

    // ── GET /api/ami/extensions ───────────────────────────────────────────────

    private static async Task<IResult> GetExtensions(
        IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Ami");
        var o = opts.Value.Ami;

        if (string.IsNullOrWhiteSpace(o.User))
        {
            logger.LogError("API_AMI_CONNECT_ERROR | Ami.User nao configurado em appsettings.Production.json");
            return Results.Problem("AMI nao configurado no servidor.", statusCode: 503);
        }

        logger.LogInformation("API_AMI_CONNECT_START | host={Host}:{Port}", o.Host, o.Port);

        try
        {
            var ramais = await BuscarRamaisAmiAsync(o, logger);
            logger.LogInformation("API_AMI_CONNECT_OK | extensions={Count}", ramais.Count);
            return Results.Ok(ramais);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "API_AMI_CONNECT_ERROR | {Message}", ex.Message);
            return Results.Problem($"Falha ao conectar AMI: {ex.Message}", statusCode: 502);
        }
    }

    // ── GET /api/ami/peers ────────────────────────────────────────────────────

    private static async Task<IResult> GetPeers(
        IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Ami");
        var o = opts.Value.Ami;

        if (string.IsNullOrWhiteSpace(o.User))
            return Results.Problem("AMI nao configurado no servidor.", statusCode: 503);

        logger.LogInformation("API_AMI_PEERS_START | host={Host}:{Port}", o.Host, o.Port);
        try
        {
            var peers = await BuscarStatusPeersAsync(o, logger);
            var online    = peers.Count(p => p.Status == "online");
            var emLigacao = peers.Count(p => p.Status is "emligacao" or "tocando" or "chamando");
            var offline   = peers.Count(p => p.Status == "offline");
            logger.LogInformation(
                "API_AMI_PEERS_OK | total={Total} online={Online} emLigacao={EmLigacao} offline={Offline}",
                peers.Count, online, emLigacao, offline);
            return Results.Ok(peers);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "API_AMI_PEERS_ERROR | {Message}", ex.Message);
            return Results.Problem($"Falha ao buscar status dos ramais: {ex.Message}", statusCode: 502);
        }
    }

    // ── GET /api/ami/queues-live ──────────────────────────────────────────────

    private static async Task<IResult> GetQueuesLive(
        IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Ami");
        var o = opts.Value.Ami;

        if (string.IsNullOrWhiteSpace(o.User))
            return Results.Problem("AMI nao configurado no servidor.", statusCode: 503);

        logger.LogInformation("API_AMI_QUEUES_START | host={Host}:{Port}", o.Host, o.Port);
        try
        {
            var filas = await BuscarStatusFilasAsync(o, logger);
            logger.LogInformation("API_AMI_QUEUES_OK | total={Total}", filas.Count);
            return Results.Ok(filas);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "API_AMI_QUEUES_ERROR | {Message}", ex.Message);
            return Results.Problem($"Falha ao buscar filas: {ex.Message}", statusCode: 502);
        }
    }

    // ── BuscarStatusFilasAsync ────────────────────────────────────────────────

    private static async Task<List<AmiQueue>> BuscarStatusFilasAsync(
        WavenApiOptions.AmiOptions o, ILogger logger)
    {
        using var client = new TcpClient();
        var cto = new CancellationTokenSource(o.ConnectTimeoutMs);
        await client.ConnectAsync(o.Host, o.Port, cto.Token);
        client.ReceiveTimeout = 12000;
        client.SendTimeout    = 5000;
        using var stream = client.GetStream();

        await LerDisponivelAsync(stream, 700);

        await EnviarAsync(stream,
            $"Action: Login\r\nUsername: {o.User}\r\nSecret: {o.Password}\r\n" +
            "Events: off\r\nActionID: WAVEN_LOGIN\r\n\r\n");
        var login = await LerAteAsync(stream, "ActionID: WAVEN_LOGIN", 5000);
        if (login.IndexOf("Success", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("AMI recusou login — verifique usuario, senha e permissoes.");

        // QueueStatus retorna QueueParams + QueueMember + QueueEntry por fila
        await EnviarAsync(stream, "Action: QueueStatus\r\nActionID: WAVEN_QUEUES\r\n\r\n");
        var queuesRaw = await LerAteAsync(stream, "QueueStatusComplete", 10000);

        await EnviarAsync(stream, "Action: Logoff\r\nActionID: WAVEN_LOGOFF\r\n\r\n");

        var filas = new Dictionary<string, AmiQueue>(StringComparer.OrdinalIgnoreCase);

        foreach (var bloco in SepararEventos(queuesRaw))
        {
            var evt = ObterCampo(bloco, "Event") ?? "";

            if (evt.Equals("QueueParams", StringComparison.OrdinalIgnoreCase))
            {
                var nome = ObterCampo(bloco, "Queue") ?? "";
                if (string.IsNullOrWhiteSpace(nome)) continue;
                if (!filas.TryGetValue(nome, out var fila))
                {
                    fila = new AmiQueue { Fila = nome };
                    filas[nome] = fila;
                }
                if (int.TryParse(ObterCampo(bloco, "Calls"),     out var calls))     fila.Aguardando       = calls;
                if (int.TryParse(ObterCampo(bloco, "Completed"), out var completed)) fila.Atendidas        = completed;
                if (int.TryParse(ObterCampo(bloco, "Abandoned"), out var abandoned)) fila.Abandonadas      = abandoned;
                if (int.TryParse(ObterCampo(bloco, "Holdtime"),  out var hold))      fila.TempoMedioEspera = hold;
                logger.LogDebug("QUEUE_PARAMS | fila={F} aguardando={A} atendidas={At} abandonadas={Ab}",
                    nome, fila.Aguardando, fila.Atendidas, fila.Abandonadas);
            }
            else if (evt.Equals("QueueMember", StringComparison.OrdinalIgnoreCase))
            {
                var nome = ObterCampo(bloco, "Queue") ?? "";
                if (string.IsNullOrWhiteSpace(nome)) continue;
                if (!filas.TryGetValue(nome, out var fila))
                {
                    fila = new AmiQueue { Fila = nome };
                    filas[nome] = fila;
                }
                var stateIface = ObterCampo(bloco, "StateInterface") ?? "";
                var location   = ObterCampo(bloco, "Location")       ?? "";
                var ramal      = ExtrairRamalMembro(stateIface) ?? ExtrairRamalMembro(location) ?? "";
                var memberName = ObterCampo(bloco, "Name") ?? ramal;
                int.TryParse(ObterCampo(bloco, "Status") ?? "0", out var statusCode);
                int.TryParse(ObterCampo(bloco, "Paused") ?? "0", out var paused);

                var statusStr = statusCode switch
                {
                    1 => "idle",
                    2 => "inuse",
                    3 => "busy",
                    6 => "ringing",
                    7 => "ringinuse",
                    8 => "onhold",
                    _ => "unavail"
                };
                fila.Agentes.Add(new AmiQueueMember
                {
                    Nome    = LimparNomeMembro(memberName),
                    Ramal   = ramal,
                    Status  = statusStr,
                    Pausado = paused == 1
                });
                logger.LogDebug("QUEUE_MEMBER | fila={F} ramal={R} status={S}", nome, ramal, statusStr);
            }
            else if (evt.Equals("QueueEntry", StringComparison.OrdinalIgnoreCase))
            {
                var nome = ObterCampo(bloco, "Queue") ?? "";
                if (string.IsNullOrWhiteSpace(nome)) continue;
                if (!filas.TryGetValue(nome, out var fila))
                {
                    fila = new AmiQueue { Fila = nome };
                    filas[nome] = fila;
                }
                int.TryParse(ObterCampo(bloco, "Position") ?? "0", out var pos);
                int.TryParse(ObterCampo(bloco, "Wait")     ?? "0", out var wait);
                fila.Clientes.Add(new AmiQueueEntry
                {
                    Posicao = pos,
                    Numero  = ObterCampo(bloco, "CallerIDNum") ?? "",
                    Espera  = wait
                });
            }
        }

        // Calcular contadores
        foreach (var fila in filas.Values)
        {
            fila.AgentesTotal         = fila.Agentes.Count;
            fila.AgentesPausados      = fila.Agentes.Count(a => a.Pausado);
            fila.AgentesEmAtendimento = fila.Agentes.Count(a => !a.Pausado && a.Status is "inuse" or "busy" or "ringinuse" or "onhold");
            fila.AgentesOnline        = fila.Agentes.Count(a => !a.Pausado && a.Status == "idle");
            fila.AgentesOffline       = fila.Agentes.Count(a => a.Status == "unavail" && !a.Pausado);
            fila.Clientes             = fila.Clientes.OrderBy(c => c.Posicao).ToList();
        }

        return filas.Values.OrderBy(f => f.Fila).ToList();
    }

    private static string? ExtrairRamalMembro(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s, @"(?:SIP|PJSIP)/(\d{2,6})(?:[@\-]|$)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(s, @"Local/(\d{2,6})@", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        return null;
    }

    private static string LimparNomeMembro(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        var m = Regex.Match(s, @"Local/(\d+)@");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(s, @"(?:SIP|PJSIP)/(\d+)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        return s.Trim();
    }

    // ── BuscarStatusPeersAsync ────────────────────────────────────────────────

    private static async Task<List<AmiPeer>> BuscarStatusPeersAsync(
        WavenApiOptions.AmiOptions o, ILogger logger)
    {
        var result = new Dictionary<string, AmiPeer>(StringComparer.OrdinalIgnoreCase);

        using var client = new TcpClient();
        var cto = new CancellationTokenSource(o.ConnectTimeoutMs);
        await client.ConnectAsync(o.Host, o.Port, cto.Token);
        client.ReceiveTimeout = 8000;
        client.SendTimeout    = 5000;
        using var stream = client.GetStream();

        await LerDisponivelAsync(stream, 700);

        await EnviarAsync(stream,
            $"Action: Login\r\nUsername: {o.User}\r\nSecret: {o.Password}\r\n" +
            "Events: off\r\nActionID: WAVEN_LOGIN\r\n\r\n");
        var login = await LerAteAsync(stream, "ActionID: WAVEN_LOGIN", 5000);
        if (login.IndexOf("Success", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("AMI recusou login — verifique usuario, senha e permissoes.");

        var nomes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await EnviarAsync(stream,
            "Action: Command\r\nCommand: database show AMPUSER\r\nActionID: WAVEN_AMPUSER\r\n\r\n");
        var ampuser = await LerAteAsync(stream, "--END COMMAND--", 7000);
        foreach (Match m in Regex.Matches(ampuser, @"/AMPUSER/(?<r>\d{2,6})/cidname\s*:\s*(?<n>.+)"))
        {
            var r = m.Groups["r"].Value.Trim();
            var n = LimparNome(m.Groups["n"].Value);
            if (EhRamalValido(r)) nomes[r] = string.IsNullOrWhiteSpace(n) ? r : n;
        }

        await EnviarAsync(stream, "Action: SIPpeers\r\nActionID: WAVEN_SIPPEERS\r\n\r\n");
        var peersRaw = await LerAteAsync(stream, "PeerlistComplete", 7000);
        foreach (var bloco in SepararEventos(peersRaw))
        {
            if ((ObterCampo(bloco, "Event") ?? "").IndexOf("PeerEntry", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var ramal     = SomenteDigitos(ObterCampo(bloco, "ObjectName") ?? ObterCampo(bloco, "Peer") ?? "");
            if (!EhRamalValido(ramal)) continue;
            var statusRaw = ObterCampo(bloco, "Status") ?? "";
            var status    = ParsePeerStatus(statusRaw);
            var nome      = nomes.TryGetValue(ramal, out var n) ? n : ramal;
            result[ramal] = new AmiPeer
            {
                Ramal = ramal, Nome = nome, Status = status,
                Registrado = status == "online", RawStatus = $"SIP|Status={statusRaw}", Tecnologia = "SIP"
            };
            logger.LogDebug("SIP_PEER | ramal={R} status={S}", ramal, status);
        }

        await EnviarAsync(stream, "Action: PJSIPShowEndpoints\r\nActionID: WAVEN_PJSIP\r\n\r\n");
        var pjsipRaw = await LerAteAsync(stream, "EndpointListComplete", 7000);
        foreach (var bloco in SepararEventos(pjsipRaw))
        {
            if ((ObterCampo(bloco, "Event") ?? "").IndexOf("EndpointList", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var ramal = SomenteDigitos(ObterCampo(bloco, "ObjectName") ?? "");
            if (!EhRamalValido(ramal) || result.ContainsKey(ramal)) continue;

            var ds       = ObterCampo(bloco, "DeviceState") ?? "";
            var contacts = ObterCampo(bloco, "Contacts")    ?? "";
            var nome     = nomes.TryGetValue(ramal, out var n) ? n : ramal;

            bool temContato  = !string.IsNullOrWhiteSpace(contacts);
            bool unavailable = ds.IndexOf("Unavailable", StringComparison.OrdinalIgnoreCase) >= 0;
            bool notInUse    = ds.IndexOf("Not_Inuse",   StringComparison.OrdinalIgnoreCase) >= 0
                            || ds.IndexOf("Not_in_use",  StringComparison.OrdinalIgnoreCase) >= 0;
            bool inUse       = ds.IndexOf("In_use",      StringComparison.OrdinalIgnoreCase) >= 0
                            || ds.IndexOf("In_Use",      StringComparison.OrdinalIgnoreCase) >= 0;
            bool ringing     = ds.IndexOf("Ringing",     StringComparison.OrdinalIgnoreCase) >= 0;

            string status;
            if (!temContato || unavailable) status = "offline";
            else if (inUse)                 status = "emligacao";
            else if (ringing)               status = "tocando";
            else if (notInUse)              status = "online";
            else                            status = "indisponivel";

            result[ramal] = new AmiPeer
            {
                Ramal = ramal, Nome = nome, Status = status,
                Registrado = temContato && !unavailable,
                RawStatus = $"PJSIP|DS={ds}|Contacts={contacts}", Tecnologia = "PJSIP"
            };
            logger.LogDebug("PJSIP_ENDPOINT | ramal={R} DS={DS} status={S}", ramal, ds, status);
        }

        await EnviarAsync(stream, "Action: CoreShowChannels\r\nActionID: WAVEN_CHANNELS\r\n\r\n");
        var channelsRaw = await LerAteAsync(stream, "CoreShowChannelsComplete", 7000);
        int canaisAtivos = 0;

        // Passo 1: coletar TODOS os canais (extensões + troncos) com Uniqueid e Linkedid
        var allChans = new List<(string Ch, string CidNum, string ConnNum, string ConnName, int State, string UniqueId, string LinkedId)>();
        foreach (var bloco in SepararEventos(channelsRaw))
        {
            if ((ObterCampo(bloco, "Event") ?? "").IndexOf("CoreShowChannel", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var ch = ObterCampo(bloco, "Channel") ?? "";
            if (string.IsNullOrWhiteSpace(ch)) continue;
            var cidNum   = ObterCampo(bloco, "CallerIDNum")       ?? "";
            var connNum  = ObterCampo(bloco, "ConnectedLineNum")  ?? "";
            var connName = ObterCampo(bloco, "ConnectedLineName") ?? "";
            var uid      = ObterCampo(bloco, "Uniqueid")          ?? "";
            var lid      = ObterCampo(bloco, "Linkedid")          ?? uid;
            if (connNum  == "<unknown>") connNum  = "";
            if (connName == "<unknown>") connName = "";
            if (string.IsNullOrWhiteSpace(lid)) lid = uid;
            int.TryParse(ObterCampo(bloco, "ChannelState") ?? "0", out var st);
            var cidNameLog  = ObterCampo(bloco, "CallerIDName")  ?? "";
            var extenLog    = ObterCampo(bloco, "Exten")         ?? "";
            var contextLog  = ObterCampo(bloco, "Context")       ?? "";
            var appLog      = ObterCampo(bloco, "Application")   ?? "";
            var bridgeIdLog = ObterCampo(bloco, "BridgeId")      ?? "";
            if (cidNameLog == "<unknown>") cidNameLog = "";
            allChans.Add((ch, cidNum, connNum, connName, st, uid, lid));
            logger.LogInformation(
                "CHAN_RAW | ch={Ch} cid={Cid} cidName={CidName} conn={Conn} connName={ConnName} " +
                "uid={Uid} lid={Lid} exten={Exten} ctx={Ctx} app={App} bridgeId={Bid} state={St}",
                ch, cidNum, cidNameLog, connNum, connName, uid, lid,
                extenLog, contextLog, appLog, bridgeIdLog, st);
        }

        // Agrupar por LinkedId — ambos os lados do bridge compartilham o mesmo Linkedid
        var chansByLinkedId = allChans
            .GroupBy(c => c.LinkedId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Passo 2: processar apenas canais de extensões reais (SIP|PJSIP/RAMAL-)
        foreach (var (ch, cidNum, connNum, connName, state, uid, lid) in allChans)
        {
            var cm = Regex.Match(ch, @"(?:SIP|PJSIP)/(\d{2,6})-", RegexOptions.IgnoreCase);
            if (!cm.Success) continue;
            var ramal = cm.Groups[1].Value;
            if (!EhRamalValido(ramal)) continue;

            // ── Análise do grupo Linkedid ──────────────────────────────────────────
            chansByLinkedId.TryGetValue(lid, out var lidGrp);

            // Canal de tronco/externo pareado (não extensão, não Local/)
            var paired = lidGrp?
                .Where(c =>
                    !string.Equals(c.Ch, ch, StringComparison.OrdinalIgnoreCase) &&
                    !Regex.IsMatch(c.Ch, @"(?:SIP|PJSIP)/\d{2,6}-", RegexOptions.IgnoreCase) &&
                    !c.Ch.StartsWith("Local/", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault() ?? default;
            bool hasPaired = !string.IsNullOrWhiteSpace(paired.Ch);

            // Outro ramal no mesmo grupo (chamada interna)?
            bool hasPairedExt = lidGrp?.Any(c =>
                !string.Equals(c.Ch, ch, StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(c.Ch, @"(?:SIP|PJSIP)/\d{2,6}-", RegexOptions.IgnoreCase)) ?? false;

            bool isInternalCall = !hasPaired && hasPairedExt;

            string originadorCh = lidGrp?
                .Where(c => string.Equals(c.UniqueId, lid, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Ch)
                .FirstOrDefault() ?? "?";

            bool thisIsOriginator  = string.Equals(uid, lid, StringComparison.OrdinalIgnoreCase);
            bool trunkIsOriginator = hasPaired &&
                string.Equals(paired.UniqueId, paired.LinkedId, StringComparison.OrdinalIgnoreCase);
            bool otherExtIsOrig = isInternalCall && (lidGrp?.Any(c =>
                !string.Equals(c.Ch, ch, StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(c.Ch, @"(?:SIP|PJSIP)/\d{2,6}-", RegexOptions.IgnoreCase) &&
                string.Equals(c.UniqueId, lid, StringComparison.OrdinalIgnoreCase)) ?? false);

            // ── Cache por Linkedid|Ramal — cada ramal tem sua própria direção ──────
            string cacheKey    = $"{lid}|{ramal}";
            bool?  cachedDir   = null;
            string motivoCache = "MISS";

            if (_dirCache.TryGetValue(cacheKey, out var cachedEntry))
            {
                cachedDir   = cachedEntry.IsOutgoing;
                motivoCache = "HIT";
                _dirCache[cacheKey] = cachedEntry with { LastSeen = DateTime.UtcNow };
            }

            // ── Calcular direção fresca ─────────────────────────────────────────────
            // P1 trunk_is_originator — tronco é A-leg → SEMPRE entrada (prevalece sobre cidNum==ramal)
            // P2 thisIsOriginator    — esta extensão é A-leg → saída
            // P3 otherExtIsOrig      — outro ramal é A-leg → entrada interna
            // P4 trunk_connNum_ramal — tronco conectado a este ramal → entrada
            // P5 hasPaired           — via Local channel (FreePBX)
            // P6 isInternalCall      — interna sem sinal uid
            // P7 fallback
            bool   isOutgoingCalc;
            string motivoCalculo;

            if (trunkIsOriginator)
            {
                isOutgoingCalc = false;
                motivoCalculo  = "trunk_is_originator";
            }
            else if (thisIsOriginator)
            {
                isOutgoingCalc = true;
                motivoCalculo  = isInternalCall ? "internal_originator" : "ext_is_originator";
            }
            else if (otherExtIsOrig)
            {
                isOutgoingCalc = false;
                motivoCalculo  = "internal_callee_other_orig";
            }
            else if (hasPaired && string.Equals(paired.ConnNum, ramal, StringComparison.OrdinalIgnoreCase))
            {
                isOutgoingCalc = false;
                motivoCalculo  = "trunk_connNum_eq_ramal";
            }
            else if (hasPaired)
            {
                var trunkConnLen = new string((paired.ConnNum ?? "").Where(char.IsDigit).ToArray()).Length;
                if (trunkConnLen >= 7)
                {
                    isOutgoingCalc = true;
                    motivoCalculo  = "trunk_connNum_external";
                }
                else
                {
                    var cidLen = new string(cidNum.Where(char.IsDigit).ToArray()).Length;
                    isOutgoingCalc = cidLen >= 8;
                    motivoCalculo  = isOutgoingCalc ? "cidNum_cid_rewrite" : "cidNum_short_fallback";
                }
            }
            else if (isInternalCall)
            {
                if (string.Equals(cidNum, ramal, StringComparison.OrdinalIgnoreCase))
                {
                    isOutgoingCalc = false;
                    motivoCalculo  = "internal_own_cid_callee";
                }
                else
                {
                    var cidDigits  = new string(cidNum.Where(char.IsDigit).ToArray());
                    bool cidIsLocal = cidDigits.Length >= 2 && cidDigits.Length <= 6;
                    isOutgoingCalc  = !(cidIsLocal &&
                        !string.Equals(cidDigits, ramal, StringComparison.OrdinalIgnoreCase));
                    motivoCalculo   = isOutgoingCalc ? "internal_caller" : "internal_callee";
                }
            }
            else
            {
                var cidDigits  = new string(cidNum.Where(char.IsDigit).ToArray());
                bool cidIsLocal = cidDigits.Length >= 2 && cidDigits.Length <= 6;
                if (cidIsLocal && !string.Equals(cidDigits, ramal, StringComparison.OrdinalIgnoreCase))
                {
                    isOutgoingCalc = false;
                    motivoCalculo  = "solo_cidNum_local";
                }
                else
                {
                    isOutgoingCalc = true;
                    motivoCalculo  = "solo_fallback_saida";
                }
            }

            // ── Decisão final: cache tem prioridade absoluta ────────────────────────
            bool   isOutgoing;
            string motivoFinal;

            if (cachedDir.HasValue)
            {
                isOutgoing  = cachedDir.Value;
                motivoFinal = cachedDir.Value == isOutgoingCalc
                    ? "cache_confirmado"
                    : "cache_preservou_direcao";
            }
            else
            {
                isOutgoing  = isOutgoingCalc;
                motivoFinal = motivoCalculo;
                _dirCache[cacheKey] = new DirCacheEntry(isOutgoing, DateTime.UtcNow, DateTime.UtcNow, motivoCalculo);
            }

            string tipoChamada = isInternalCall
                ? "interna"
                : (isOutgoing ? "externa_saida" : "externa_entrada");

            var callStatus = state >= 6 ? "emligacao" : (isOutgoing ? "chamando" : "tocando");
            var tec        = ch.StartsWith("PJSIP/", StringComparison.OrdinalIgnoreCase) ? "PJSIP" : "SIP";

            // ── Destino/Origem, linha usada, nome do canal ─────────────────────────
            string remoteNum, linhaUsada, nomeCanal;

            if (isInternalCall)
            {
                var connDigits = new string(connNum.Where(char.IsDigit).ToArray());
                remoteNum  = connDigits.Length >= 2 && connDigits.Length <= 6 ? connNum : cidNum;
                linhaUsada = "";
                nomeCanal  = "Ramal interno";
            }
            else if (isOutgoing)
            {
                remoteNum = hasPaired && !string.IsNullOrWhiteSpace(paired.ConnNum)
                    ? paired.ConnNum : connNum;
                if (hasPaired && !string.IsNullOrWhiteSpace(paired.CidNum))
                {
                    var pCidLen = new string(paired.CidNum.Where(char.IsDigit).ToArray()).Length;
                    linhaUsada = pCidLen >= 7 ? paired.CidNum : connNum;
                }
                else
                {
                    linhaUsada = connNum;
                }
                nomeCanal = hasPaired ? ExtrairNomeCanal(paired.Ch) : "";
            }
            else
            {
                // Entrada externa: usar CID do tronco (mais confiável — CID da extensão
                // pode ser reescrito para o próprio ramal após bridge)
                if (hasPaired && !string.IsNullOrWhiteSpace(paired.CidNum))
                {
                    var pCidLen = new string(paired.CidNum.Where(char.IsDigit).ToArray()).Length;
                    remoteNum = pCidLen >= 7 ? paired.CidNum : cidNum;
                }
                else
                {
                    remoteNum = cidNum;
                }
                linhaUsada = !string.IsNullOrWhiteSpace(connNum) &&
                             !string.Equals(connNum, cidNum, StringComparison.OrdinalIgnoreCase)
                    ? connNum : "";
                nomeCanal = hasPaired ? ExtrairNomeCanal(paired.Ch) : "";
            }

            logger.LogInformation(
                "CHAN_PROC | linkedid={Lid} ramal={R} cacheKey={CK} " +
                "calculada={Calc} cacheHit={CacheHit} cache={Cache} final={Final} tipo={Tipo} motivo={Motivo} " +
                "canalRamal={CR} canalPareado={CP} originador={Orig} " +
                "numCliente={Remote} linhaUsada={Linha} nomeCanal={Canal} status={CS}",
                lid, ramal, cacheKey,
                isOutgoingCalc ? "Saida" : "Entrada",
                motivoCache,
                cachedDir.HasValue ? (cachedDir.Value ? "Saida" : "Entrada") : "-",
                isOutgoing ? "Saida" : "Entrada",
                tipoChamada, motivoFinal,
                ch, paired.Ch ?? "-", originadorCh,
                remoteNum, linhaUsada, nomeCanal, callStatus);

            canaisAtivos++;
            if (result.TryGetValue(ramal, out var peer))
            {
                peer.Status       = callStatus;
                peer.NumeroRemoto = remoteNum;
                peer.NomeRemoto   = connName;
                peer.Direcao      = isOutgoing ? "saida" : "entrada";
                peer.Canal        = ch;
                peer.LinhaUsada   = linhaUsada;
                peer.NomeCanal    = nomeCanal;
                peer.TipoChamada  = tipoChamada;
                peer.Registrado   = true;
                peer.RawStatus    = $"{peer.Tecnologia}|Channel|State={state}|{callStatus}";
            }
            else
            {
                var nome = nomes.TryGetValue(ramal, out var n) ? n : ramal;
                result[ramal] = new AmiPeer
                {
                    Ramal = ramal, Nome = nome, Status = callStatus, Registrado = true,
                    RawStatus    = $"{tec}|Channel|State={state}",
                    Tecnologia   = tec,
                    NumeroRemoto = remoteNum,
                    NomeRemoto   = connName,
                    Direcao      = isOutgoing ? "saida" : "entrada",
                    Canal        = ch,
                    LinhaUsada   = linhaUsada,
                    NomeCanal    = nomeCanal,
                    TipoChamada  = tipoChamada
                };
            }
        }

        // Limpar entradas do cache de chamadas encerradas há mais de 60 s
        var cacheCutoff  = DateTime.UtcNow.AddSeconds(-60);
        var staleEntries = _dirCache
            .Where(kv => kv.Value.LastSeen < cacheCutoff)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in staleEntries) _dirCache.TryRemove(key, out _);
        logger.LogInformation("CHANNELS_TOTAL | ativos={C} cache_vivos={V} cache_removidos={R}",
            canaisAtivos, _dirCache.Count, staleEntries.Count);

        await EnviarAsync(stream, "Action: Logoff\r\nActionID: WAVEN_LOGOFF\r\n\r\n");
        return result.Values.OrderBy(p => p.Ramal).ToList();
    }

    private static string ParsePeerStatus(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "offline";
        var s = raw.ToUpperInvariant();
        if (s.StartsWith("OK"))         return "online";
        if (s.Contains("UNREACHABLE"))  return "offline";
        if (s.Contains("UNMONITORED") || s.Contains("UNKNOWN")) return "indisponivel";
        return "offline";
    }

    // ── GET /api/ami/status ───────────────────────────────────────────────────

    private static async Task<IResult> GetStatus(
        IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Ami");
        var o = opts.Value.Ami;

        if (string.IsNullOrWhiteSpace(o.User))
            return Results.Ok(new { ok = false, message = "Ami.User nao configurado" });

        try
        {
            using var client = new TcpClient();
            var ct = new CancellationTokenSource(o.ConnectTimeoutMs);
            await client.ConnectAsync(o.Host, o.Port, ct.Token);
            logger.LogInformation("API_AMI_CONNECT_OK | status check host={Host}:{Port}", o.Host, o.Port);
            return Results.Ok(new { ok = true, message = $"AMI acessivel em {o.Host}:{o.Port}" });
        }
        catch (Exception ex)
        {
            return Results.Ok(new { ok = false, message = ex.Message });
        }
    }

    // ── POST /api/ami/test ────────────────────────────────────────────────────

    private static async Task<IResult> TestAmi(
        IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Ami");
        var o = opts.Value.Ami;

        if (string.IsNullOrWhiteSpace(o.User))
            return Results.Ok(new { ok = false, message = "Ami.User nao configurado" });

        try
        {
            using var client = new TcpClient();
            var ct = new CancellationTokenSource(o.ConnectTimeoutMs);
            await client.ConnectAsync(o.Host, o.Port, ct.Token);
            client.ReceiveTimeout = 3000;
            client.SendTimeout    = 3000;
            using var stream = client.GetStream();

            await LerDisponivelAsync(stream, 700);
            await EnviarAsync(stream,
                $"Action: Login\r\nUsername: {o.User}\r\nSecret: {o.Password}\r\n" +
                "Events: off\r\nActionID: WAVEN_TEST\r\n\r\n");
            var resp = await LerAteAsync(stream, "ActionID: WAVEN_TEST", 3000);
            var ok = resp.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0;
            await EnviarAsync(stream, "Action: Logoff\r\nActionID: WAVEN_LOGOFF\r\n\r\n");

            logger.LogInformation("API_AMI_TEST_RESULT | ok={Ok}", ok);
            return Results.Ok(new
            {
                ok,
                message = ok
                    ? $"Login AMI OK — {o.Host}:{o.Port}"
                    : "Login recusado — verifique usuario/senha e permissoes do AMI"
            });
        }
        catch (Exception ex)
        {
            return Results.Ok(new { ok = false, message = ex.Message });
        }
    }

    // ── BuscarRamaisAmiAsync (/api/ami/extensions) ───────────────────────────

    private static async Task<List<AmiExtension>> BuscarRamaisAmiAsync(
        WavenApiOptions.AmiOptions o, ILogger logger)
    {
        var resultado = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var client = new TcpClient();
        var ct = new CancellationTokenSource(o.ConnectTimeoutMs);
        await client.ConnectAsync(o.Host, o.Port, ct.Token);
        client.ReceiveTimeout = 7000;
        client.SendTimeout    = 5000;
        using var stream = client.GetStream();

        await LerDisponivelAsync(stream, 700);
        await EnviarAsync(stream,
            $"Action: Login\r\nUsername: {o.User}\r\nSecret: {o.Password}\r\n" +
            "Events: off\r\nActionID: WAVEN_LOGIN\r\n\r\n");
        var login = await LerAteAsync(stream, "ActionID: WAVEN_LOGIN", 5000);
        if (login.IndexOf("Success", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("AMI recusou login — verifique usuario, senha e permissoes.");

        await EnviarAsync(stream,
            "Action: Command\r\nCommand: database show AMPUSER\r\nActionID: WAVEN_AMPUSER\r\n\r\n");
        ParseDatabaseAmpuser(await LerAteAsync(stream, "--END COMMAND--", 7000), resultado);

        await EnviarAsync(stream, "Action: SIPpeers\r\nActionID: WAVEN_SIPPEERS\r\n\r\n");
        ParseSipPeers(await LerAteAsync(stream, "PeerlistComplete", 7000), resultado);

        await EnviarAsync(stream, "Action: PJSIPShowEndpoints\r\nActionID: WAVEN_PJSIP\r\n\r\n");
        ParsePjsipEndpoints(await LerAteAsync(stream, "EndpointListComplete", 7000), resultado);

        await EnviarAsync(stream, "Action: Logoff\r\nActionID: WAVEN_LOGOFF\r\n\r\n");

        return resultado
            .Where(kv => EhRamalValido(kv.Key))
            .Select(kv => new AmiExtension { Ramal = kv.Key, Nome = string.IsNullOrWhiteSpace(kv.Value) ? kv.Key : kv.Value })
            .OrderBy(e => e.Ramal)
            .ToList();
    }

    private static void ParseDatabaseAmpuser(string texto, Dictionary<string, string> ramais)
    {
        foreach (Match m in Regex.Matches(texto ?? "", @"/AMPUSER/(?<ramal>\d{2,6})/cidname\s*:\s*(?<nome>.+)"))
        {
            var ramal = m.Groups["ramal"].Value.Trim();
            var nome  = LimparNome(m.Groups["nome"].Value);
            if (EhRamalValido(ramal)) ramais[ramal] = string.IsNullOrWhiteSpace(nome) ? ramal : nome;
        }
        foreach (Match m in Regex.Matches(texto ?? "", @"/AMPUSER/(?<ramal>\d{2,6})/device\s*:\s*(?<device>\d{2,6})"))
        {
            var ramal = m.Groups["ramal"].Value.Trim();
            if (EhRamalValido(ramal) && !ramais.ContainsKey(ramal)) ramais[ramal] = ramal;
        }
    }

    private static void ParseSipPeers(string texto, Dictionary<string, string> ramais)
    {
        foreach (var bloco in SepararEventos(texto))
        {
            if ((ObterCampo(bloco, "Event") ?? "").IndexOf("PeerEntry", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var ramal = SomenteDigitos(ObterCampo(bloco, "ObjectName") ?? ObterCampo(bloco, "Peer") ?? "");
            if (!EhRamalValido(ramal)) continue;
            var nome = LimparNome(ObterCampo(bloco, "Description") ?? ObterCampo(bloco, "Callerid") ?? "");
            if (!ramais.ContainsKey(ramal)) ramais[ramal] = string.IsNullOrWhiteSpace(nome) ? ramal : nome;
        }
    }

    private static void ParsePjsipEndpoints(string texto, Dictionary<string, string> ramais)
    {
        foreach (var bloco in SepararEventos(texto))
        {
            if ((ObterCampo(bloco, "Event") ?? "").IndexOf("EndpointList", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var ramal = SomenteDigitos(ObterCampo(bloco, "ObjectName") ?? "");
            if (EhRamalValido(ramal) && !ramais.ContainsKey(ramal)) ramais[ramal] = ramal;
        }
    }

    private static IEnumerable<string> SepararEventos(string texto) =>
        (texto ?? "").Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

    private static string? ObterCampo(string bloco, string campo)
    {
        foreach (var linha in (bloco ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = linha.IndexOf(':');
            if (idx <= 0) continue;
            if (string.Equals(linha[..idx].Trim(), campo, StringComparison.OrdinalIgnoreCase))
                return linha[(idx + 1)..].Trim();
        }
        return null;
    }

    private static string LimparNome(string nome)
    {
        nome = (nome ?? "").Trim().Trim('"');
        var m = Regex.Match(nome, "\"(?<n>[^\"]+)\"");
        if (m.Success) nome = m.Groups["n"].Value;
        nome = Regex.Replace(nome, @"<[^>]+>", "").Trim();
        return nome;
    }

    private static string SomenteDigitos(string v) =>
        new string((v ?? "").Where(char.IsDigit).ToArray());

    // Exclui troncos (ex: "08002") que iniciam com "0" e têm 4+ dígitos.
    private static bool EhRamalValido(string r) =>
        !string.IsNullOrWhiteSpace(r) &&
        r.All(char.IsDigit) &&
        r.Length >= 2 && r.Length <= 6 &&
        !(r.Length >= 4 && r[0] == '0');

    // Extrai nome amigável do canal de tronco.
    // "SIP/whatsapp_vivo-0000001" → "Whatsapp Vivo"
    // "Local/701@from-queue" → "Fila 701"
    private static string ExtrairNomeCanal(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return "";
        if (channel.StartsWith("Local/", StringComparison.OrdinalIgnoreCase))
        {
            var qm = Regex.Match(channel, @"Local/(\d+)@");
            if (qm.Success) return $"Fila {qm.Groups[1].Value}";
            return "Local";
        }
        int slashIdx = channel.IndexOf('/');
        if (slashIdx < 0) return "";
        var afterSlash = channel[(slashIdx + 1)..];
        int lastDash = afterSlash.LastIndexOf('-');
        var epName = lastDash > 0 ? afterSlash[..lastDash] : afterSlash;
        if (epName.All(char.IsDigit)) return "";   // extensão numérica — ignorar
        return TitleCase(epName.Replace("_", " ").Replace(".", " "));
    }

    private static string TitleCase(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        var words = s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            words[i] = w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w;
        }
        return string.Join(" ", words);
    }

    private static async Task EnviarAsync(NetworkStream s, string texto)
    {
        var b = Encoding.ASCII.GetBytes(texto);
        await s.WriteAsync(b);
        await s.FlushAsync();
    }

    private static async Task<string> LerAteAsync(NetworkStream s, string marcador, int timeoutMs)
    {
        var sb  = new StringBuilder();
        var buf = new byte[8192];
        var limite = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < limite)
        {
            while (s.DataAvailable)
            {
                var n = await s.ReadAsync(buf);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.ToString().IndexOf(marcador, StringComparison.OrdinalIgnoreCase) >= 0)
                    return sb.ToString();
            }
            await Task.Delay(80);
        }
        return sb.ToString();
    }

    private static async Task<string> LerDisponivelAsync(NetworkStream s, int timeoutMs)
    {
        var sb  = new StringBuilder();
        var buf = new byte[4096];
        var limite = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < limite)
        {
            while (s.DataAvailable)
            {
                var n = await s.ReadAsync(buf);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            }
            await Task.Delay(50);
        }
        return sb.ToString();
    }
}
