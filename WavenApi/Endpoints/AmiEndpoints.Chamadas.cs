using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace WavenApi.Endpoints;

// "Puxar ligação" (WavenVoIP 2.5.0).
//
// A porta do AMI só aceita o próprio servidor e os IPs fixos do escritório, então o WavenVoIP
// não consegue ler os canais nem pedir a captura diretamente de qualquer rede. Estes dois
// endpoints fazem isso pela sessão AMI local da API:
//
//   GET  /api/ami/calls   — os canais ativos (CoreShowChannels), crus; o WavenVoIP monta a
//                           lista (em atendimento, fila, URA, fora do horário, caixa postal).
//   POST /api/ami/pickup  — assume uma chamada: confere que o canal do cliente ainda existe e
//                           é o mesmo, que ninguém o está puxando, e garante a gravação.
//                           • Cliente sem atendente (fila, URA, fora do horário, caixa postal,
//                             tocando): o ramal de quem puxa toca com a aplicação Bridge e o
//                             cliente só muda de lugar quando esse ramal atende.
//                           • Cliente JÁ em conversa com um ramal: ninguém é desconectado. O
//                             ramal de quem puxa toca e entra na sala de conferência; só depois
//                             que ele atende, cliente e atendente original são levados juntos
//                             para a mesma sala (cliente + dois operadores). Quem sair depois
//                             não derruba os demais.
public static partial class AmiEndpoints
{
    public sealed record AmiCanalAtivo(
        string Channel, string Linkedid, string Uniqueid, string BridgeId,
        string CallerIDNum, string CallerIDName, string ConnectedLineNum,
        string Context, string Exten, string Application, string ApplicationData, int DuracaoSeg);

    public sealed record PickupRequest(string? CanalCliente, string? Linkedid, string? Ramal, string? Nome, string? Numero, string? Sala = null);
    public sealed record PickupResponse(bool Ok, string Mensagem);

    private static readonly CacheCurto<List<AmiCanalAtivo>> _cacheCanais = new();
    private static readonly TimeSpan TtlCanais = TimeSpan.FromMilliseconds(1500);

    private static readonly Regex _reRamal       = new(@"^\d{2,5}$", RegexOptions.Compiled);
    private static readonly Regex _reCanalRamalP = new(@"^(SIP|PJSIP|IAX2)/\d{2,5}-", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _reCanalTronco = new(@"^(SIP|PJSIP|IAX2|DAHDI)/[\w .@:+\-]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void MapChamadasEndpoints(RouteGroupBuilder g)
    {
        g.MapGet("/calls",   GetCalls);
        g.MapPost("/pickup", PostPickup);
    }

    private static async Task<List<AmiCanalAtivo>> LerCanaisNaSessaoAsync(NetworkStream stream)
    {
        await EnviarAsync(stream, "Action: CoreShowChannels\r\nActionID: WAVEN_CALLS\r\n\r\n");
        var bruto = await LerRespostaAsync(stream, "WAVEN_CALLS", "CoreShowChannelsComplete", 7000);
        var lista = new List<AmiCanalAtivo>();
        foreach (var bloco in SepararEventos(bruto))
        {
            if (!string.Equals(ObterCampo(bloco, "Event"), "CoreShowChannel", StringComparison.OrdinalIgnoreCase)) continue;
            var ch = ObterCampo(bloco, "Channel") ?? "";
            if (string.IsNullOrWhiteSpace(ch)) continue;
            var dur = TimeSpan.TryParse(ObterCampo(bloco, "Duration") ?? "", out var d) ? (int)d.TotalSeconds : 0;
            lista.Add(new AmiCanalAtivo(
                ch,
                ObterCampo(bloco, "Linkedid") ?? "", ObterCampo(bloco, "Uniqueid") ?? "", ObterCampo(bloco, "BridgeId") ?? "",
                ObterCampo(bloco, "CallerIDNum") ?? "", ObterCampo(bloco, "CallerIDName") ?? "", ObterCampo(bloco, "ConnectedLineNum") ?? "",
                ObterCampo(bloco, "Context") ?? "", ObterCampo(bloco, "Exten") ?? "",
                ObterCampo(bloco, "Application") ?? "", ObterCampo(bloco, "ApplicationData") ?? "", dur));
        }
        return lista;
    }

    // ── GET /api/ami/calls ─────────────────────────────────────────────────

    private static async Task<IResult> GetCalls(IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Ami");
        var o = opts.Value.Ami;
        if (string.IsNullOrWhiteSpace(o.User))
            return Results.Problem("AMI nao configurado no servidor.", statusCode: 503);
        try
        {
            var (canais, _) = await _cacheCanais.ObterAsync(TtlCanais,
                () => ComSessaoAmiAsync(o, logger, LerCanaisNaSessaoAsync));
            return Results.Ok(canais);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "API_AMI_CALLS_ERROR | {Message}", ex.Message);
            return Results.Problem($"Falha ao ler chamadas ativas: {ex.Message}", statusCode: 502);
        }
    }

    // ── POST /api/ami/pickup ───────────────────────────────────────────────

    private static string SemQuebras(string? s) =>
        new string((s ?? "").Where(c => !char.IsControl(c) && c != '"' && c != '<' && c != '>').ToArray()).Trim();

    private static async Task<IResult> PostPickup(PickupRequest req, IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Ami");
        var o = opts.Value.Ami;
        if (string.IsNullOrWhiteSpace(o.User))
            return Results.Problem("AMI nao configurado no servidor.", statusCode: 503);

        var ramal = (req.Ramal ?? "").Trim();
        var canal = (req.CanalCliente ?? "").Trim();
        if (!_reRamal.IsMatch(ramal)) return Results.BadRequest(new PickupResponse(false, "Ramal inválido."));
        if (!_reCanalTronco.IsMatch(canal) || _reCanalRamalP.IsMatch(canal) || canal.StartsWith("Local/", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new PickupResponse(false, "Canal inválido para puxar."));

        var numero = new string(SemQuebras(req.Numero).Where(char.IsDigit).ToArray());
        var nome = SemQuebras(req.Nome); if (nome.Length > 30) nome = nome[..30];

        try
        {
            var resposta = await ComSessaoAmiAsync(o, logger, async stream =>
            {
                var canais = await LerCanaisNaSessaoAsync(stream);
                var alvo = canais.FirstOrDefault(c => string.Equals(c.Channel, canal, StringComparison.OrdinalIgnoreCase));
                if (alvo == null) return new PickupResponse(false, "Essa ligação já foi encerrada. Nada foi alterado.");
                if (!string.IsNullOrWhiteSpace(req.Linkedid) && !string.Equals(alvo.Linkedid, req.Linkedid, StringComparison.OrdinalIgnoreCase))
                    return new PickupResponse(false, "Essa ligação já foi encerrada (o canal agora pertence a outra chamada). Nada foi alterado.");

                // Quem está em conversa com o cliente agora (mesma ponte)?
                var naPonte = string.IsNullOrWhiteSpace(alvo.BridgeId) ? new List<AmiCanalAtivo>()
                    : canais.Where(c => c.Channel != alvo.Channel && string.Equals(c.BridgeId, alvo.BridgeId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (naPonte.Count > 1) return new PickupResponse(false, "Essa ligação já está em conferência.");
                var canalOperador = CanalDoRamalEmConversa(alvo, canais);
                if (canalOperador != null && (canalOperador.StartsWith($"SIP/{ramal}-", StringComparison.OrdinalIgnoreCase) ||
                                              canalOperador.StartsWith($"PJSIP/{ramal}-", StringComparison.OrdinalIgnoreCase)))
                    return new PickupResponse(false, "Essa ligação já está no seu ramal.");

                // Captura duplicada: algum canal já executa Bridge para este cliente, ou algum
                // ramal está tocando por um pedido de captura deste mesmo canal?
                var jaPuxando = canais.FirstOrDefault(c =>
                    string.Equals(c.Application, "Bridge", StringComparison.OrdinalIgnoreCase) &&
                    c.ApplicationData.Split(',')[0].Trim().Equals(canal, StringComparison.OrdinalIgnoreCase));
                if (jaPuxando != null) return new PickupResponse(false, "Outro operador já está puxando essa ligação. Nada foi alterado.");
                if (_capturasPendentes.TryGetValue(canal, out var pend) && DateTime.UtcNow - pend.Quando < TimeSpan.FromSeconds(27) && pend.Ramal != ramal)
                    return new PickupResponse(false, $"O ramal {pend.Ramal} já pediu para puxar essa ligação. Nada foi alterado.");
                _capturasPendentes[canal] = (ramal, DateTime.UtcNow);

                // Gravação: se o canal ainda não tem arquivo (URA, anúncio, caixa postal), inicia
                // uma agora no padrão de nome do PABX; se já tem, ela continua no mesmo arquivo.
                var gravacao = "existente";
                try
                {
                    await EnviarAsync(stream, $"Action: Getvar\r\nActionID: WAVEN_PK_GV\r\nChannel: {canal}\r\nVariable: CDR(recordingfile)\r\n\r\n");
                    var gv = await LerRespostaAsync(stream, "WAVEN_PK_GV", null, 4000);
                    var valor = (ObterCampo(gv, "Value") ?? "").Trim();
                    if (gv.Contains("Success", StringComparison.OrdinalIgnoreCase) && valor.Length == 0)
                    {
                        var agora = DateTime.Now;
                        var arquivo = $"force-{numero}-{ramal}-{agora:yyyyMMdd}-{agora:HHmmss}-{alvo.Uniqueid}.wav";
                        await EnviarAsync(stream, $"Action: MixMonitor\r\nActionID: WAVEN_PK_MM\r\nChannel: {canal}\r\nFile: {agora:yyyy}/{agora:MM}/{agora:dd}/{arquivo}\r\n\r\n");
                        var mm = await LerRespostaAsync(stream, "WAVEN_PK_MM", null, 4000);
                        if (mm.Contains("Success", StringComparison.OrdinalIgnoreCase))
                        {
                            await EnviarAsync(stream, $"Action: Setvar\r\nActionID: WAVEN_PK_SV\r\nChannel: {canal}\r\nVariable: CDR(recordingfile)\r\nValue: {arquivo}\r\n\r\n");
                            await LerRespostaAsync(stream, "WAVEN_PK_SV", null, 4000);
                            gravacao = "iniciada:" + arquivo;
                        }
                        else gravacao = "falhou";
                    }
                }
                catch (Exception exGrav) { gravacao = "erro:" + exGrav.Message; }

                var rotulo = nome.Length > 0 ? nome : numero;

                // ── Ligação já atendida: conferência (cliente + atendente original + quem puxa) ──
                if (canalOperador != null)
                {
                    var sala = new string((req.Sala ?? "800").Where(char.IsDigit).ToArray());
                    if (sala.Length < 2 || sala.Length > 6) sala = "800";
                    if (canais.Any(c => string.Equals(c.Application, "ConfBridge", StringComparison.OrdinalIgnoreCase) &&
                                        c.ApplicationData.Split(',')[0].Trim() == sala))
                    {
                        _capturasPendentes.TryRemove(canal, out _);
                        return new PickupResponse(false, $"A sala de conferência {sala} está em uso por outra ligação. Tente em instantes. Nada foi alterado.");
                    }

                    await EnviarAsync(stream,
                        "Action: Originate\r\nActionID: WAVEN_PK_CF\r\n" +
                        $"Channel: SIP/{ramal}\r\nApplication: ConfBridge\r\nData: {sala}\r\n" +
                        $"CallerID: \"Entrar: {rotulo}\" <{numero}>\r\nTimeout: 25000\r\nAsync: true\r\n\r\n");
                    var cf = await LerRespostaAsync(stream, "WAVEN_PK_CF", null, 6000);
                    var okCf = cf.Contains("Success", StringComparison.OrdinalIgnoreCase);
                    logger.LogInformation("API_AMI_PICKUP_CONF | ok={Ok} ramal={Ramal} cliente={Canal} operador={Op} sala={Sala} gravacao={Grav}",
                        okCf, ramal, canal, canalOperador, sala, gravacao);
                    if (!okCf) { _capturasPendentes.TryRemove(canal, out _); return new PickupResponse(false, "O PABX recusou o pedido. A ligação não foi alterada."); }

                    // Só leva cliente e atendente para a sala depois que quem puxa atender e entrar nela.
                    _ = Task.Run(() => ConcluirConferenciaAsync(o, logger, ramal, canal, alvo.Linkedid, canalOperador, sala));
                    return new PickupResponse(true, "Seu ramal está tocando: ao atender, você entra na ligação em conferência com o cliente e o atendente. Ninguém é desconectado; se não atender, nada muda.");
                }

                await EnviarAsync(stream,
                    "Action: Originate\r\nActionID: WAVEN_PK_OR\r\n" +
                    $"Channel: SIP/{ramal}\r\nApplication: Bridge\r\nData: {canal}\r\n" +
                    $"CallerID: \"Puxar: {rotulo}\" <{numero}>\r\nTimeout: 25000\r\nAsync: true\r\n\r\n");
                var or = await LerRespostaAsync(stream, "WAVEN_PK_OR", null, 6000);
                var ok = or.Contains("Success", StringComparison.OrdinalIgnoreCase);
                logger.LogInformation("API_AMI_PICKUP | ok={Ok} ramal={Ramal} canal={Canal} linkedid={Lid} gravacao={Grav}",
                    ok, ramal, canal, alvo.Linkedid, gravacao);
                if (!ok) _capturasPendentes.TryRemove(canal, out _);
                return ok
                    ? new PickupResponse(true, "Seu ramal está tocando: atenda para assumir a ligação. Se não atender, nada muda.")
                    : new PickupResponse(false, "O PABX recusou o pedido. A ligação não foi alterada.");
            });
            return Results.Ok(resposta);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "API_AMI_PICKUP_ERROR | {Message}", ex.Message);
            return Results.Problem($"Falha ao puxar a ligação: {ex.Message}", statusCode: 502);
        }
    }

    // Canal do ramal que está em conversa com o cliente: mesma ponte, atravessando as duas
    // metades de um canal Local (chamada entregue pela fila). null = ninguém atendendo.
    private static string? CanalDoRamalEmConversa(AmiCanalAtivo origem, List<AmiCanalAtivo> canais)
    {
        var atual = origem;
        for (var salto = 0; salto < 4; salto++)
        {
            if (string.IsNullOrWhiteSpace(atual.BridgeId)) return null;
            var pares = canais.Where(c => c.Channel != atual.Channel && string.Equals(c.BridgeId, atual.BridgeId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (pares.Count != 1) return null;
            var par = pares[0];
            if (_reCanalRamalP.IsMatch(par.Channel)) return par.Channel;
            if (!par.Channel.StartsWith("Local/", StringComparison.OrdinalIgnoreCase)) return null;
            var sufixo = par.Channel.EndsWith(";1") ? ";2" : par.Channel.EndsWith(";2") ? ";1" : null;
            if (sufixo == null) return null;
            var outra = par.Channel[..^2] + sufixo;
            var prox = canais.FirstOrDefault(c => string.Equals(c.Channel, outra, StringComparison.OrdinalIgnoreCase));
            if (prox == null) return null;
            atual = prox;
        }
        return null;
    }

    // Espera (até ~28 s) quem puxa atender e entrar na sala; então confere de novo que cliente e
    // atendente continuam na mesma conversa e leva os dois, juntos, para a mesma sala (Redirect
    // duplo, como a conferência do WavenVoIP já faz). Se quem puxa não atender, ou a ligação
    // acabar antes, nada é movido.
    private static async Task ConcluirConferenciaAsync(WavenApiOptions.AmiOptions o, ILogger logger,
        string ramal, string canalCliente, string linkedid, string canalOperador, string sala)
    {
        try
        {
            for (var i = 0; i < 28; i++)
            {
                await Task.Delay(1000);
                var canais = await ComSessaoAmiAsync(o, logger, LerCanaisNaSessaoAsync);
                var cliente = canais.FirstOrDefault(c => string.Equals(c.Channel, canalCliente, StringComparison.OrdinalIgnoreCase));
                if (cliente == null || !string.Equals(cliente.Linkedid, linkedid, StringComparison.OrdinalIgnoreCase))
                { logger.LogInformation("API_AMI_PICKUP_CONF_FIM | motivo=ligacao_encerrada ramal={Ramal}", ramal); return; }

                var meu = canais.FirstOrDefault(c =>
                    (c.Channel.StartsWith($"SIP/{ramal}-", StringComparison.OrdinalIgnoreCase) || c.Channel.StartsWith($"PJSIP/{ramal}-", StringComparison.OrdinalIgnoreCase)) &&
                    string.Equals(c.Application, "ConfBridge", StringComparison.OrdinalIgnoreCase) && c.ApplicationData.Split(',')[0].Trim() == sala);
                if (meu == null) continue;                                   // ainda tocando

                var operadorAgora = CanalDoRamalEmConversa(cliente, canais);
                if (!string.Equals(operadorAgora, canalOperador, StringComparison.OrdinalIgnoreCase))
                { logger.LogInformation("API_AMI_PICKUP_CONF_FIM | motivo=conversa_mudou ramal={Ramal}", ramal); return; }

                var ok = await ComSessaoAmiAsync(o, logger, async stream =>
                {
                    await EnviarAsync(stream,
                        "Action: Redirect\r\nActionID: WAVEN_PK_RD\r\n" +
                        $"Channel: {canalCliente}\r\nContext: from-internal\r\nExten: {sala}\r\nPriority: 1\r\n" +
                        $"ExtraChannel: {canalOperador}\r\nExtraContext: from-internal\r\nExtraExten: {sala}\r\nExtraPriority: 1\r\n\r\n");
                    var rd = await LerRespostaAsync(stream, "WAVEN_PK_RD", null, 7000);
                    return rd.Contains("Success", StringComparison.OrdinalIgnoreCase) ? "ok" : "falhou";
                });
                logger.LogInformation("API_AMI_PICKUP_CONF_REDIRECT | resultado={Ok} ramal={Ramal} cliente={Cli} operador={Op} sala={Sala}",
                    ok, ramal, canalCliente, canalOperador, sala);
                if (ok == "ok") _ = Task.Run(() => VigiarConferenciaAsync(o, logger, sala, canalCliente, linkedid));
                return;
            }
            logger.LogInformation("API_AMI_PICKUP_CONF_FIM | motivo=ramal_nao_atendeu ramal={Ramal}", ramal);
        }
        catch (Exception ex) { logger.LogError(ex, "API_AMI_PICKUP_CONF_ERROR | {Message}", ex.Message); }
        finally { _capturasPendentes.TryRemove(canalCliente, out _); }
    }

    // v2.5.0 — fim da conversa numa conferência criada pelo "Puxar ligação".
    // Caso real (13/10/2026 11:41): o cliente desligou e o operador ficou sozinho na sala por 26 s,
    // com o WavenVoIP mostrando a ligação ativa, até desligar à mão (a sala do PABX não termina
    // sozinha). A API acompanha a sala a cada 1,5 s e, confirmando em duas leituras seguidas:
    //   • não há mais cliente/convidado (só ramais)  → desliga os ramais que ficaram nessa sala;
    //   • não há mais nenhum ramal (cliente sozinho)  → desliga o cliente, que ficaria ouvindo música.
    // Só atua nos canais que estão nesta mesma ponte de conferência; nenhuma outra chamada é tocada.
    private static bool EhCanalDeServicoDaSala(string canal) =>
        canal.StartsWith("CBAnn/", StringComparison.OrdinalIgnoreCase) || canal.StartsWith("CBRec/", StringComparison.OrdinalIgnoreCase);

    private static List<AmiCanalAtivo> CanaisNaSala(List<AmiCanalAtivo> canais, string sala) =>
        canais.Where(c => string.Equals(c.Application, "ConfBridge", StringComparison.OrdinalIgnoreCase) &&
                          c.ApplicationData.Split(',')[0].Trim() == sala && !EhCanalDeServicoDaSala(c.Channel)).ToList();

    // (ramais a desligar, externos a desligar): decisão pura, testável.
    internal static (List<string> ramais, List<string> externos, string motivo) OrfaosDaSala(List<AmiCanalAtivo> naPonte)
    {
        var ramais = naPonte.Where(c => _reCanalRamalP.IsMatch(c.Channel)).Select(c => c.Channel).ToList();
        var externos = naPonte.Where(c => !_reCanalRamalP.IsMatch(c.Channel)).Select(c => c.Channel).ToList();
        if (ramais.Count > 0 && externos.Count == 0) return (ramais, new List<string>(), "sem_cliente");
        if (ramais.Count == 0 && externos.Count > 0) return (new List<string>(), externos, "sem_atendente");
        return (new List<string>(), new List<string>(), "");
    }

    private static async Task VigiarConferenciaAsync(WavenApiOptions.AmiOptions o, ILogger logger, string sala, string canalCliente, string linkedid)
    {
        try
        {
            string? ponte = null; var semPonte = 0; var orfaoSeguidas = 0; var motivoAnterior = "";
            var inicio = DateTime.UtcNow;
            while (DateTime.UtcNow - inicio < TimeSpan.FromHours(6))
            {
                await Task.Delay(1500);
                var canais = await ComSessaoAmiAsync(o, logger, LerCanaisNaSessaoAsync);
                var naSala = CanaisNaSala(canais, sala);

                if (ponte == null)
                {
                    // a ponte é a da sala onde o cliente entrou; se ele desligou antes de entrar, a dos ramais
                    var cs = naSala.FirstOrDefault(c => string.Equals(c.Channel, canalCliente, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.BridgeId));
                    var clienteVivo = canais.Any(c => string.Equals(c.Channel, canalCliente, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Linkedid, linkedid, StringComparison.OrdinalIgnoreCase));
                    if (cs != null) ponte = cs.BridgeId;
                    else if (!clienteVivo && ++semPonte >= 2) ponte = naSala.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.BridgeId))?.BridgeId;
                    else if (DateTime.UtcNow - inicio > TimeSpan.FromSeconds(40)) { logger.LogInformation("API_AMI_CONF_WATCH_FIM | motivo=cliente_nao_entrou sala={Sala}", sala); return; }
                    if (ponte == null) { if (naSala.Count == 0 && !clienteVivo) return; continue; }
                }

                var naPonte = naSala.Where(c => string.Equals(c.BridgeId, ponte, StringComparison.OrdinalIgnoreCase)).ToList();
                // canal ainda entrando/saindo da sala (sem ponte) não conta como órfão nem como presente
                if (naPonte.Count == 0 && !naSala.Any(c => string.IsNullOrWhiteSpace(c.BridgeId)))
                { logger.LogInformation("API_AMI_CONF_WATCH_FIM | motivo=sala_vazia sala={Sala}", sala); return; }

                var (ramais, externos, motivo) = OrfaosDaSala(naPonte);
                if (motivo.Length == 0 || DateTime.UtcNow - inicio < TimeSpan.FromSeconds(6)) { orfaoSeguidas = 0; motivoAnterior = ""; continue; }
                orfaoSeguidas = motivo == motivoAnterior ? orfaoSeguidas + 1 : 1; motivoAnterior = motivo;
                // cliente saiu: 2 leituras (3 s). Cliente sozinho: 4 leituras (6 s) — um atendente pode estar entrando.
                if (orfaoSeguidas < (motivo == "sem_cliente" ? 2 : 4)) continue;

                var alvos = motivo == "sem_cliente" ? ramais : externos;
                var feitos = await ComSessaoAmiAsync(o, logger, async stream =>
                {
                    // confere de novo, na mesma sessão, antes de desligar qualquer canal
                    var agora = CanaisNaSala(await LerCanaisNaSessaoAsync(stream), sala).Where(c => string.Equals(c.BridgeId, ponte, StringComparison.OrdinalIgnoreCase)).ToList();
                    var (r2, e2, m2) = OrfaosDaSala(agora);
                    if (m2 != motivo) return "mudou";
                    var n = 0;
                    foreach (var ch in (motivo == "sem_cliente" ? r2 : e2))
                    {
                        await EnviarAsync(stream, $"Action: Hangup\r\nActionID: WAVEN_CW_HG{n}\r\nChannel: {ch}\r\nCause: 16\r\n\r\n");
                        await LerRespostaAsync(stream, $"WAVEN_CW_HG{n}", null, 4000);
                        n++;
                    }
                    return n.ToString();
                });
                logger.LogInformation("API_AMI_CONF_WATCH_ENCERROU | motivo={Motivo} sala={Sala} canais={Canais} resultado={Res}", motivo, sala, string.Join(",", alvos), feitos);
                if (feitos != "mudou") return;
                orfaoSeguidas = 0;
            }
        }
        catch (Exception ex) { logger.LogError(ex, "API_AMI_CONF_WATCH_ERROR | {Message}", ex.Message); }
    }

    // Pedidos de captura enviados há menos de 27 s (tempo máximo de toque do ramal de quem puxa):
    // enquanto o ramal ainda toca, o canal dele não executa Bridge e não apareceria na conferência acima.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Ramal, DateTime Quando)> _capturasPendentes =
        new(StringComparer.OrdinalIgnoreCase);
}
