using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WavenApi.Models;

namespace WavenApi.Endpoints;

public static class AmiEndpoints
{
    public static void MapAmiEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/ami");
        g.MapGet("/extensions", GetExtensions);
        g.MapGet("/status",     GetStatus);
        g.MapPost("/test",      TestAmi);
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
            client.SendTimeout = 3000;
            using var stream = client.GetStream();

            // Ler banner
            await LerDisponivelAsync(stream, 700);

            // Login
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

    // ── Logica AMI (espelho do AmiRamalSyncService do cliente) ───────────────

    private static async Task<List<AmiExtension>> BuscarRamaisAmiAsync(
        WavenApiOptions.AmiOptions o, ILogger logger)
    {
        var resultado = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var client = new TcpClient();
        var ct = new CancellationTokenSource(o.ConnectTimeoutMs);
        await client.ConnectAsync(o.Host, o.Port, ct.Token);

        client.ReceiveTimeout = 7000;
        client.SendTimeout = 5000;
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
        var ampuser = await LerAteAsync(stream, "--END COMMAND--", 7000);
        ParseDatabaseAmpuser(ampuser, resultado);

        await EnviarAsync(stream,
            "Action: SIPpeers\r\nActionID: WAVEN_SIPPEERS\r\n\r\n");
        var peers = await LerAteAsync(stream, "PeerlistComplete", 7000);
        ParseSipPeers(peers, resultado);

        await EnviarAsync(stream,
            "Action: PJSIPShowEndpoints\r\nActionID: WAVEN_PJSIP\r\n\r\n");
        var pjsip = await LerAteAsync(stream, "EndpointListComplete", 7000);
        ParsePjsipEndpoints(pjsip, resultado);

        await EnviarAsync(stream, "Action: Logoff\r\nActionID: WAVEN_LOGOFF\r\n\r\n");

        return resultado
            .Where(kv => EhRamalValido(kv.Key))
            .Select(kv => new AmiExtension
            {
                Ramal = kv.Key,
                Nome  = string.IsNullOrWhiteSpace(kv.Value) ? kv.Key : kv.Value
            })
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

    private static bool EhRamalValido(string r) =>
        !string.IsNullOrWhiteSpace(r) && r.All(char.IsDigit) && r.Length >= 2 && r.Length <= 6;

    private static async Task EnviarAsync(NetworkStream s, string texto)
    {
        var b = Encoding.ASCII.GetBytes(texto);
        await s.WriteAsync(b);
        await s.FlushAsync();
    }

    private static async Task<string> LerAteAsync(NetworkStream s, string marcador, int timeoutMs)
    {
        var sb = new StringBuilder();
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
        var sb = new StringBuilder();
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
