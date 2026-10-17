using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace WavenApi.Endpoints;

// WavenVoIP 2.5.1 — recuperação de desligamento.
//
//   POST /api/ami/hangup-own — o WavenVoIP desligou, mas o canal do PRÓPRIO ramal continuou no
//        PABX (BYE perdido). Encerra SOMENTE esse canal: tem que ser canal de ramal (SIP/<ramal>-),
//        do ramal que pediu, ainda existir, ter o mesmo LinkedID, falar com o mesmo número
//        (8 últimos dígitos, ou exato para ramal) e já existir antes do pedido (idade mínima —
//        uma ligação nova do mesmo ramal nunca casa). Tudo conferido na mesma sessão AMI, logo
//        antes do Hangup. Nunca atua sobre canal de tronco/cliente, Local/ ou de
//        outro ramal — conferência e transferência seguem para os demais participantes.
public static partial class AmiEndpoints
{
    public sealed record HangupOwnRequest(string? Ramal, string? Canal, string? Linkedid, string? Numero = null, int? IdadeMinSeg = null);
    public sealed record HangupOwnResponse(bool Ok, string Mensagem);

    // formato exato do Asterisk: SIP/100-0000a1b2 e linkedid 1792015152.13299 (nada além disso)
    private static readonly Regex _reCanalProprio = new(@"^(SIP|PJSIP)/(\d{2,5})-[0-9a-fA-F]{6,12}$", RegexOptions.Compiled);
    private static readonly Regex _reLinkedid = new(@"^\d{9,12}\.\d{1,9}$", RegexOptions.Compiled);

    private static void MapEncerrarEndpoints(RouteGroupBuilder g) => g.MapPost("/hangup-own", PostHangupOwn);

    private static async Task<IResult> PostHangupOwn(HangupOwnRequest req, IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Ami");
        var o = opts.Value.Ami;
        if (string.IsNullOrWhiteSpace(o.User))
            return Results.Problem("AMI nao configurado no servidor.", statusCode: 503);

        var ramal = (req.Ramal ?? "").Trim();
        var canal = (req.Canal ?? "").Trim();
        var linkedid = (req.Linkedid ?? "").Trim();
        if (!_reRamal.IsMatch(ramal)) return Results.BadRequest(new HangupOwnResponse(false, "Ramal inválido."));
        var mCanal = _reCanalProprio.Match(canal);
        if (!mCanal.Success || mCanal.Groups[2].Value != ramal || !EhCanalDoRamal(canal, ramal))
            return Results.BadRequest(new HangupOwnResponse(false, "Só é permitido encerrar o canal do próprio ramal."));
        if (linkedid.Length == 0) return Results.BadRequest(new HangupOwnResponse(false, "LinkedID obrigatório."));
        if (!_reLinkedid.IsMatch(linkedid)) return Results.BadRequest(new HangupOwnResponse(false, "LinkedID inválido."));
        var numero = new string(SemQuebras(req.Numero).Where(char.IsDigit).ToArray());
        if (numero.Length == 0) return Results.BadRequest(new HangupOwnResponse(false, "Número da ligação obrigatório."));
        var idadeMin = Math.Clamp(req.IdadeMinSeg ?? 0, 2, 86400);

        static bool MesmoNumero(string campo, string numero)
        {
            var d = new string((campo ?? "").Where(char.IsDigit).ToArray());
            if (d.Length == 0) return false;
            return numero.Length >= 8 && d.Length >= 8 ? d[^8..] == numero[^8..] : d == numero;
        }

        try
        {
            var resposta = await ComSessaoAmiAsync(o, logger, async stream =>
            {
                var antes = await LerCanaisNaSessaoAsync(stream);
                var alvo = antes.FirstOrDefault(c => string.Equals(c.Channel, canal, StringComparison.OrdinalIgnoreCase));
                if (alvo == null) return new HangupOwnResponse(true, "O canal já estava encerrado.");
                if (!string.Equals(alvo.Linkedid, linkedid, StringComparison.OrdinalIgnoreCase))
                    return new HangupOwnResponse(false, "O canal pertence a outra ligação. Nada foi alterado.");
                if (!MesmoNumero(alvo.ConnectedLineNum, numero) && !MesmoNumero(alvo.CallerIDNum, numero))
                    return new HangupOwnResponse(false, "O canal fala com outro número. Nada foi alterado.");
                if (alvo.DuracaoSeg < idadeMin)
                    return new HangupOwnResponse(false, "O canal é mais novo que a ligação encerrada. Nada foi alterado.");

                await EnviarAsync(stream, $"Action: Hangup\r\nActionID: WAVEN_HGOWN\r\nChannel: {canal}\r\nCause: 16\r\n\r\n");
                await LerRespostaAsync(stream, "WAVEN_HGOWN", null, 4000);
                await Task.Delay(500);
                var depois = await LerCanaisNaSessaoAsync(stream);
                var saiu = !depois.Any(c => string.Equals(c.Channel, canal, StringComparison.OrdinalIgnoreCase));
                return new HangupOwnResponse(saiu, saiu ? "Canal do ramal encerrado." : "O PABX não encerrou o canal.");
            });
            logger.LogInformation("API_AMI_HANGUP_OWN | ramal={Ramal} canal={Canal} linkedid={Linkedid} idadeMin={Idade} ok={Ok} msg={Msg}", ramal, canal, linkedid, idadeMin, resposta.Ok, resposta.Mensagem);
            return Results.Ok(resposta);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "API_AMI_HANGUP_OWN_ERROR | {Message}", ex.Message);
            return Results.Problem($"Falha ao encerrar o canal: {ex.Message}", statusCode: 502);
        }
    }
}
