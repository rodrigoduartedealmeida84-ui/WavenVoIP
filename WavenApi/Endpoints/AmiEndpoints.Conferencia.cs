using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WavenApi.Data;

namespace WavenApi.Endpoints;

// "Remover atendente" (WavenVoIP 2.5.0) — privacidade depois de puxar uma ligação em conferência.
//
//   GET  /api/ami/conference/permission?ramal=100  — se o ramal pode remover atendentes.
//   POST /api/ami/conference/remove                — tira UM atendente da conferência em que o
//        solicitante está. Confere antes: solicitante autorizado e dentro da sala; alvo é canal de
//        ramal na MESMA ponte; há cliente na ligação. Confere depois (lendo os canais de novo):
//        o atendente saiu, o cliente e o solicitante continuam. Nunca atua sobre o canal do cliente.
//        Gravação: se o arquivo em curso pertence ao canal do atendente que vai sair (ligação que
//        ele mesmo originou), inicia antes a gravação no canal do cliente para o restante da conversa.
public static partial class AmiEndpoints
{
    public sealed record RemoverAtendenteRequest(string? RamalSolicitante, string? RamalAlvo);
    public sealed record RemoverAtendenteResponse(bool Ok, string Mensagem, bool AtendenteSaiu, bool ClienteNaLigacao, string Gravacao);

    private static void MapConferenciaEndpoints(RouteGroupBuilder g)
    {
        g.MapGet("/conference/permission", GetPermissaoRemover);
        g.MapPost("/conference/remove",    PostRemoverAtendente);
    }

    // Quem pode remover atendentes — configuração CENTRAL, sem reinstalar nada:
    //   1) configuração da empresa guardada na própria API (PUT /api/company/config), chave
    //      "ramaisQuePodemRemoverAtendente": ["100","104"]  → vale na hora para todos os postos;
    //   2) se a chave não existir, o padrão do servidor (Ami:RamaisQuePodemRemoverAtendente = ["100"]).
    private static async Task<string[]> RamaisAutorizadosAsync(WavenApiOptions.AmiOptions o, WavenDbContext db)
    {
        try
        {
            var cfg = await db.CompanyConfig.FindAsync(1);
            if (!string.IsNullOrWhiteSpace(cfg?.ConfigJson))
            {
                using var doc = JsonDocument.Parse(cfg.ConfigJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("ramaisQuePodemRemoverAtendente", out var el) && el.ValueKind == JsonValueKind.Array)
                    return el.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
                             .Select(x => x.Trim()).Where(x => _reRamal.IsMatch(x)).ToArray();
            }
        }
        catch { }
        return o.RamaisQuePodemRemoverAtendente ?? Array.Empty<string>();
    }

    private static async Task<bool> PodeRemoverAsync(WavenApiOptions.AmiOptions o, WavenDbContext db, string ramal) =>
        (await RamaisAutorizadosAsync(o, db)).Any(r => string.Equals(r?.Trim(), ramal, StringComparison.OrdinalIgnoreCase));

    private static bool EhCanalDoRamal(string canal, string ramal) =>
        canal.StartsWith($"SIP/{ramal}-", StringComparison.OrdinalIgnoreCase) || canal.StartsWith($"PJSIP/{ramal}-", StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> GetPermissaoRemover(string? ramal, IOptions<WavenApiOptions> opts, WavenDbContext db)
    {
        var r = (ramal ?? "").Trim();
        return Results.Ok(new { podeRemover = _reRamal.IsMatch(r) && await PodeRemoverAsync(opts.Value.Ami, db, r) });
    }

    private static async Task<bool> TemGravacaoAsync(NetworkStream stream, string canal, string id)
    {
        await EnviarAsync(stream, $"Action: Command\r\nActionID: {id}\r\nCommand: mixmonitor list {canal}\r\n\r\n");
        var r = await LerRespostaAsync(stream, id, null, 4000);
        return r.Contains(".wav", StringComparison.OrdinalIgnoreCase) || r.Contains("/monitor/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IResult> PostRemoverAtendente(RemoverAtendenteRequest req, IOptions<WavenApiOptions> opts, ILoggerFactory lf, WavenDbContext db)
    {
        var logger = lf.CreateLogger("WavenApi.Ami");
        var o = opts.Value.Ami;
        if (string.IsNullOrWhiteSpace(o.User))
            return Results.Problem("AMI nao configurado no servidor.", statusCode: 503);

        var sol = (req.RamalSolicitante ?? "").Trim();
        var alvoRamal = (req.RamalAlvo ?? "").Trim();
        RemoverAtendenteResponse Nao(string m) => new(false, m, false, true, "inalterada");
        if (!_reRamal.IsMatch(sol) || !_reRamal.IsMatch(alvoRamal)) return Results.BadRequest(Nao("Ramal inválido."));
        if (sol == alvoRamal) return Results.BadRequest(Nao("Para sair da ligação, desligue a sua chamada."));
        if (!await PodeRemoverAsync(o, db, sol))
        {
            logger.LogWarning("API_AMI_CONF_REMOVE_DENIED | solicitante={Sol} alvo={Alvo}", sol, alvoRamal);
            return Results.Json(Nao("Seu ramal não tem permissão para remover atendentes."), statusCode: 403);
        }

        try
        {
            var resposta = await ComSessaoAmiAsync(o, logger, async stream =>
            {
                var canais = await LerCanaisNaSessaoAsync(stream);
                var meu = canais.FirstOrDefault(c => EhCanalDoRamal(c.Channel, sol) &&
                    string.Equals(c.Application, "ConfBridge", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.BridgeId));
                if (meu == null) return Nao("Você não está em uma ligação em conferência. Nada foi alterado.");

                var naPonte = canais.Where(c => string.Equals(c.BridgeId, meu.BridgeId, StringComparison.OrdinalIgnoreCase) && !EhCanalDeServicoDaSala(c.Channel)).ToList();
                var alvo = naPonte.FirstOrDefault(c => EhCanalDoRamal(c.Channel, alvoRamal));
                if (alvo == null) return Nao($"O ramal {alvoRamal} não está mais nesta ligação.");

                var clientes = naPonte.Where(c => !_reCanalRamalP.IsMatch(c.Channel)).ToList();
                if (clientes.Count == 0) return Nao("Não há cliente nesta ligação. Nada foi alterado.");

                var sala = meu.ApplicationData.Split(',')[0].Trim();
                if (sala.Length == 0 || !sala.All(char.IsDigit)) return Nao("Não foi possível identificar a sala da ligação. Nada foi alterado.");

                // ── Gravação: garante que o restante da conversa continue gravado ──
                var gravacao = "continua";
                try
                {
                    var canalCliente = clientes.FirstOrDefault(c => !c.Channel.StartsWith("Local/", StringComparison.OrdinalIgnoreCase)) ?? clientes[0];
                    var clienteGrava = false;
                    foreach (var c in clientes)
                        if (await TemGravacaoAsync(stream, c.Channel, "WAVEN_RM_ML")) { clienteGrava = true; break; }
                    if (!clienteGrava && !await TemGravacaoAsync(stream, meu.Channel, "WAVEN_RM_MM"))
                    {
                        var agora = DateTime.Now;
                        var numero = new string((canalCliente.CallerIDNum ?? "").Where(char.IsDigit).ToArray());
                        // Continuação da gravação: o nome leva o LINKEDID da ligação + "-parteN", para o
                        // histórico encontrar e oferecer os dois arquivos (GET /api/cdr/recording-parts).
                        var lid = string.IsNullOrWhiteSpace(canalCliente.Linkedid) ? canalCliente.Uniqueid : canalCliente.Linkedid;
                        var parte = 2;
                        try
                        {
                            var pasta = Path.Combine(opts.Value.RecordingsPath.TrimEnd('/', '\\'), agora.ToString("yyyy"), agora.ToString("MM"), agora.ToString("dd"));
                            if (Directory.Exists(pasta)) parte = 2 + Directory.EnumerateFiles(pasta, $"*-{lid}-parte*.wav").Count();
                        }
                        catch { }
                        var arquivo = $"force-{numero}-{sol}-{agora:yyyyMMdd}-{agora:HHmmss}-{lid}-parte{parte}.wav";
                        await EnviarAsync(stream, $"Action: MixMonitor\r\nActionID: WAVEN_RM_MX\r\nChannel: {canalCliente.Channel}\r\nFile: {agora:yyyy}/{agora:MM}/{agora:dd}/{arquivo}\r\n\r\n");
                        var mm = await LerRespostaAsync(stream, "WAVEN_RM_MX", null, 4000);
                        gravacao = mm.Contains("Success", StringComparison.OrdinalIgnoreCase) ? "nova:" + arquivo : "nao_garantida";
                    }
                }
                catch (Exception exG) { gravacao = "nao_verificada"; logger.LogWarning("API_AMI_CONF_REMOVE_REC | {M}", exG.Message); }

                // ── Tira somente o canal do atendente da sala ──
                await EnviarAsync(stream, $"Action: ConfbridgeKick\r\nActionID: WAVEN_RM_KK\r\nConference: {sala}\r\nChannel: {alvo.Channel}\r\n\r\n");
                var kk = await LerRespostaAsync(stream, "WAVEN_RM_KK", null, 5000);
                if (!kk.Contains("Success", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("API_AMI_CONF_REMOVE_FAIL | solicitante={Sol} alvo={Canal} sala={Sala}", sol, alvo.Channel, sala);
                    return new RemoverAtendenteResponse(false, "O PABX recusou o pedido. Ninguém foi removido.", false, true, gravacao);
                }

                // ── Confere a saída real ──
                var saiu = false; var clienteOk = true; var euOk = true;
                for (var i = 0; i < 8 && !saiu; i++)
                {
                    await Task.Delay(400);
                    var depois = await LerCanaisNaSessaoAsync(stream);
                    saiu = !depois.Any(c => string.Equals(c.Channel, alvo.Channel, StringComparison.OrdinalIgnoreCase) &&
                                            string.Equals(c.BridgeId, meu.BridgeId, StringComparison.OrdinalIgnoreCase));
                    clienteOk = clientes.Any(cl => depois.Any(c => string.Equals(c.Channel, cl.Channel, StringComparison.OrdinalIgnoreCase) &&
                                                                   string.Equals(c.BridgeId, meu.BridgeId, StringComparison.OrdinalIgnoreCase)));
                    euOk = depois.Any(c => string.Equals(c.Channel, meu.Channel, StringComparison.OrdinalIgnoreCase));
                }
                logger.LogInformation("API_AMI_CONF_REMOVE | solicitante={Sol} alvo={Canal} sala={Sala} saiu={Saiu} cliente={Cli} solicitanteNaSala={Eu} gravacao={Grav}",
                    sol, alvo.Channel, sala, saiu, clienteOk, euOk, gravacao);

                if (!saiu) return new RemoverAtendenteResponse(false, $"O ramal {alvoRamal} ainda aparece na ligação. Tente novamente.", false, clienteOk, gravacao);
                var msg = clienteOk
                    ? $"O ramal {alvoRamal} saiu da ligação e não ouve mais a conversa. Você e o cliente continuam."
                    : $"O ramal {alvoRamal} saiu, mas o cliente não aparece mais na ligação.";
                return new RemoverAtendenteResponse(clienteOk, msg, true, clienteOk, gravacao);
            });
            return Results.Ok(resposta);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "API_AMI_CONF_REMOVE_ERROR | {Message}", ex.Message);
            return Results.Problem($"Falha ao remover o atendente: {ex.Message}", statusCode: 502);
        }
    }
}
