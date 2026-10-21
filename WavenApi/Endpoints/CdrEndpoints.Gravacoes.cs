using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace WavenApi.Endpoints;

// v2.5.3 — download de gravação por link temporário assinado.
//
// O app pede o link com o Bearer (GET /api/cdr/recording-link?file=&date=&ramal=) e recebe
// "/api/rec/{token}", válido por poucos minutos e preso a um arquivo e a um ramal. O link serve
// tanto para o player (download para o cache) quanto para o botão Baixar (navegador, que não
// manda cabeçalho Authorization). Assim a pasta /gravacoes do Apache pode ser fechada para fora
// quando todos os PCs estiverem na 2.5.3, sem expor o token da API em URL.
public static partial class CdrEndpoints
{
    public const string RotaLinkGravacao = "/api/rec";

    private static readonly Regex _nomeGravacao = new(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,200}\.(wav|WAV|mp3|gsm|ogg)$", RegexOptions.Compiled);
    private static readonly Regex _ramalValido  = new(@"^\d{2,6}$", RegexOptions.Compiled);

    // Chave aleatória por processo quando ChaveLink não está configurada.
    private static readonly byte[] _chaveAleatoria = RandomNumberGenerator.GetBytes(32);

    private static byte[] ChaveLink(WavenApiOptions.GravacoesOptions g)
    {
        if (!string.IsNullOrWhiteSpace(g.ChaveLink))
        {
            try
            {
                var k = Convert.FromBase64String(g.ChaveLink.Trim());
                if (k.Length >= 32) return k;
            }
            catch (FormatException) { }
        }
        return _chaveAleatoria;
    }

    // ── GET /api/cdr/recording-link?file=&date=yyyy-MM-dd&ramal= ─────────────────
    private static async Task<IResult> GetRecordingLink(
        string? file, string? date, string? ramal,
        IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Gravacao");
        var o = opts.Value;
        var g = o.Gravacoes;

        var safeFile = Path.GetFileName(file ?? "");
        if (!_nomeGravacao.IsMatch(safeFile) || safeFile.Contains(".."))
            return Results.BadRequest(new { ok = false, error = "Nome de arquivo invalido" });
        var r = (ramal ?? "").Trim();
        if (!_ramalValido.IsMatch(r))
            return Results.BadRequest(new { ok = false, error = "Ramal invalido" });

        if (g.RamaisSemAcesso.Contains(r))
        {
            logger.LogWarning("API_REC_LINK_DENIED | ramal={Ramal} file={File} motivo=ramal_sem_acesso", r, safeFile);
            return Results.Json(new { ok = false, error = "Este ramal nao tem acesso as gravacoes" }, statusCode: 403);
        }

        var basePath = o.RecordingsPath.TrimEnd('/', '\\');
        var found = LocalizarGravacao(basePath, safeFile, date);
        if (found == null)
        {
            logger.LogWarning("API_REC_LINK_NOT_FOUND | ramal={Ramal} file={File} date={Date}", r, safeFile, date);
            return Results.NotFound(new { ok = false, error = "Gravacao nao encontrada", file = safeFile });
        }

        if (g.SomenteParticipantes && !g.RamaisQueOuvemTodas.Contains(r))
        {
            bool participou;
            try { participou = await RamalParticipouAsync(o, safeFile, date, r); }
            catch (Exception ex)
            {
                logger.LogError("API_REC_LINK_CHECK_FAIL | ramal={Ramal} file={File} erro={Erro}", r, safeFile, ex.Message);
                return Results.Json(new { ok = false, error = "Nao foi possivel conferir a permissao agora" }, statusCode: 503);
            }
            if (!participou)
            {
                logger.LogWarning("API_REC_LINK_DENIED | ramal={Ramal} file={File} motivo=nao_participou", r, safeFile);
                return Results.Json(new { ok = false, error = "Esta gravacao e de uma ligacao que nao passou pelo seu ramal" }, statusCode: 403);
            }
        }

        var rel = Path.GetRelativePath(basePath, found).Replace('\\', '/');
        var validade = Math.Clamp(g.ValidadeLinkSegundos, 30, 3600);
        var expira = DateTimeOffset.UtcNow.AddSeconds(validade);
        var token = CriarTokenLink(ChaveLink(g), rel, expira.ToUnixTimeSeconds(), r);

        logger.LogInformation("API_REC_LINK_ISSUED | ramal={Ramal} file={File} validade={Seg}s", r, rel, validade);
        return Results.Ok(new { ok = true, url = $"{RotaLinkGravacao}/{token}", expiraEm = expira.ToString("O"), file = safeFile });
    }

    // ── GET /api/rec/{token} (sem Bearer) ─────────────────────────────────────────
    private static IResult GetRecordingByLink(string token, HttpContext ctx, IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Gravacao");
        var o = opts.Value;
        var ip = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "";

        if (!LerTokenLink(ChaveLink(o.Gravacoes), token, out var rel, out var exp, out var ramal))
        {
            logger.LogWarning("API_REC_GET_DENIED | motivo=assinatura ip={Ip}", ip);
            return Results.Json(new { ok = false, error = "Link invalido" }, statusCode: 403);
        }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp)
        {
            logger.LogInformation("API_REC_GET_EXPIRED | ramal={Ramal} file={File} ip={Ip}", ramal, rel, ip);
            return Results.Json(new { ok = false, error = "Link expirado" }, statusCode: 410);
        }

        // O caminho vem de dentro do token assinado, mas ainda assim tem que cair dentro da pasta base.
        var basePath = Path.GetFullPath(o.RecordingsPath.TrimEnd('/', '\\') + Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(basePath, rel));
        if (!full.StartsWith(basePath, StringComparison.Ordinal) || !_nomeGravacao.IsMatch(Path.GetFileName(full)) || !File.Exists(full))
        {
            logger.LogWarning("API_REC_GET_NOT_FOUND | ramal={Ramal} file={File} ip={Ip}", ramal, rel, ip);
            return Results.NotFound(new { ok = false, error = "Gravacao nao encontrada" });
        }

        logger.LogInformation("API_REC_GET | ramal={Ramal} file={File} ip={Ip} range={Range}",
            ramal, rel, ip, ctx.Request.Headers.Range.ToString());
        ctx.Response.Headers.CacheControl = "private, no-store";
        var nome = Path.GetFileName(full);
        var contentType = nome.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav";
        return Results.File(full, contentType, nome, enableRangeProcessing: true);
    }

    // ── Token: base64url("v1|rel|exp|ramal") + "." + base64url(HMAC-SHA256) ───────
    internal static string CriarTokenLink(byte[] chave, string rel, long exp, string ramal)
    {
        var payload = Encoding.UTF8.GetBytes($"v1|{rel}|{exp}|{ramal}");
        var sig = HMACSHA256.HashData(chave, payload);
        return B64Url(payload) + "." + B64Url(sig);
    }

    internal static bool LerTokenLink(byte[] chave, string token, out string rel, out long exp, out string ramal)
    {
        rel = ""; exp = 0; ramal = "";
        var partes = (token ?? "").Split('.');
        if (partes.Length != 2) return false;
        byte[] payload, sig;
        try { payload = DeB64Url(partes[0]); sig = DeB64Url(partes[1]); }
        catch (FormatException) { return false; }
        if (!CryptographicOperations.FixedTimeEquals(sig, HMACSHA256.HashData(chave, payload))) return false;

        var campos = Encoding.UTF8.GetString(payload).Split('|');
        if (campos.Length != 4 || campos[0] != "v1" || !long.TryParse(campos[2], out exp)) return false;
        rel = campos[1]; ramal = campos[3];
        return rel.Length > 0 && !rel.Contains("..");
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DeB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    // O ramal aparece em alguma perna da ligação (mesmo linkedid) da gravação? Busca pelo nome do
    // arquivo na janela da data (índice de calldate); partes de gravação ("-{linkedid}-parteN")
    // usam o linkedid do próprio nome.
    private static async Task<bool> RamalParticipouAsync(WavenApiOptions o, string arquivo, string? date, string ramal)
    {
        var dia = DateTime.TryParse(date, out var d) ? d.Date : DateTime.Today;
        await using var conn = new MySqlConnection(BuildConnectionString(o.MySql));
        await conn.OpenAsync();
        var colunas = await ObterColunasAsync(conn, o.MySql.Database, o.MySql.Table);
        var chaveGrupo = colunas.Contains("linkedid", StringComparer.OrdinalIgnoreCase) ? "COALESCE(linkedid,uniqueid)" : "uniqueid";

        var linkedIds = new List<string>();
        var parte = Regex.Match(arquivo, @"-(\d{6,12}\.\d{1,8})-parte\d+\.", RegexOptions.IgnoreCase);
        if (parte.Success) linkedIds.Add(parte.Groups[1].Value);
        else
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT {chaveGrupo} FROM `{o.MySql.Table}` " +
                              "WHERE calldate >= @Ini AND calldate < @Fim AND recordingfile LIKE @Arq LIMIT 20";
            cmd.Parameters.AddWithValue("@Ini", dia.AddDays(-1));
            cmd.Parameters.AddWithValue("@Fim", dia.AddDays(2));
            cmd.Parameters.AddWithValue("@Arq", "%" + arquivo);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) if (!rd.IsDBNull(0)) linkedIds.Add(rd.GetString(0));
        }
        if (linkedIds.Count == 0) return false;

        await using var cmd2 = conn.CreateCommand();
        var nomes = new List<string>();
        for (var i = 0; i < linkedIds.Count; i++) { nomes.Add("@L" + i); cmd2.Parameters.AddWithValue("@L" + i, linkedIds[i]); }
        cmd2.CommandText = $"SELECT COUNT(*) FROM `{o.MySql.Table}` WHERE calldate >= @Ini AND calldate < @Fim " +
                           $"AND {chaveGrupo} IN ({string.Join(",", nomes)}) AND (src = @R OR dst = @R " +
                           "OR channel LIKE @C1 OR channel LIKE @C2 OR channel LIKE @C3 " +
                           "OR dstchannel LIKE @C1 OR dstchannel LIKE @C2 OR dstchannel LIKE @C3)";
        cmd2.Parameters.AddWithValue("@Ini", dia.AddDays(-1));
        cmd2.Parameters.AddWithValue("@Fim", dia.AddDays(2));
        cmd2.Parameters.AddWithValue("@R", ramal);
        cmd2.Parameters.AddWithValue("@C1", $"SIP/{ramal}-%");
        cmd2.Parameters.AddWithValue("@C2", $"PJSIP/{ramal}-%");
        cmd2.Parameters.AddWithValue("@C3", $"Local/{ramal}@%");
        return Convert.ToInt64(await cmd2.ExecuteScalarAsync()) > 0;
    }
}
