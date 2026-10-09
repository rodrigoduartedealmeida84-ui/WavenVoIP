using Microsoft.Extensions.Options;
using MySqlConnector;
using WavenApi.Models;

namespace WavenApi.Endpoints;

public static class CdrEndpoints
{
    public static void MapCdrEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/cdr");
        g.MapGet("/calls",       GetCalls);
        g.MapGet("/test",        TestConnection);
        g.MapGet("/recordings",  GetRecording);
    }

    // ── GET /api/cdr/calls?ramal=100&dias=7[&desde=yyyy-MM-dd HH:mm:ss] ──────
    //
    // Sem "desde": devolve a janela inteira (comportamento original).
    // Com "desde" (o serverTime devolvido pela resposta anterior): devolve somente os grupos
    // de chamada (mesmo linkedid) que tiveram alguma linha ENCERRADA depois do cursor, sempre
    // com o grupo COMPLETO — o cliente substitui o grupo inteiro no cache local dele, então a
    // classificação por linkedid enxerga exatamente as mesmas linhas da consulta completa.
    //
    // O CDR não tem coluna de "inserido em": a linha é gravada no fim da chamada com
    // calldate = início, então "calldate + duration" é a melhor aproximação do momento da
    // gravação. A margem de sobreposição cobre atraso de gravação (disco/ODBC lentos); o
    // cliente ainda refaz a consulta completa periodicamente como reconciliação.

    private const int MargemIncrementalSegundos = 300;
    private const int JanelaMaxChamadaHoras     = 12;
    private const string FormatoCursor          = "yyyy-MM-dd HH:mm:ss";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _colunasCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static async Task<IResult> GetCalls(
        string? ramal, int? dias, string? desde,
        IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Cdr");
        var o = opts.Value;

        if (string.IsNullOrWhiteSpace(o.MySql.User))
        {
            logger.LogError("API_CDR_QUERY_ERROR | MySql.User nao configurado em appsettings.Production.json");
            return Results.Problem("CDR nao configurado no servidor.", statusCode: 503);
        }

        var retencao = Math.Max(1, dias ?? 7);
        // Capturado ANTES das consultas: vira o cursor do cliente, então qualquer linha gravada
        // durante a consulta cai na próxima rodada em vez de se perder.
        var agora      = DateTime.Now;
        var dataInicio = agora.AddDays(-retencao);

        var incremental = DateTime.TryParseExact(desde, FormatoCursor,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var cursor);

        // Só a consulta completa é logada sempre — a incremental roda a cada poucos segundos
        // por ramal e só loga quando traz alguma coisa.
        if (!incremental)
            logger.LogInformation("API_CDR_QUERY_START | ramal={Ramal} dias={Dias} desde={Desde:yyyy-MM-dd}",
                ramal ?? "todos", retencao, dataInicio);

        try
        {
            var cs = BuildConnectionString(o.MySql);
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();

            var colunas = await ObterColunasAsync(conn, o.MySql.Database, o.MySql.Table);
            var temRecordingFile = colunas.Contains("recordingfile", StringComparer.OrdinalIgnoreCase);
            var temLinkedId      = colunas.Contains("linkedid",      StringComparer.OrdinalIgnoreCase);
            var temLastData      = colunas.Contains("lastdata",      StringComparer.OrdinalIgnoreCase);

            var sel = new System.Text.StringBuilder(
                "calldate, src, dst, channel, dstchannel, lastapp, duration, billsec, disposition, uniqueid, " +
                "COALESCE(clid,'') AS clid, COALESCE(dcontext,'') AS dcontext");

            sel.Append(temLastData      ? ", COALESCE(lastdata,'') AS lastdata"                      : ", '' AS lastdata");
            sel.Append(temRecordingFile ? ", COALESCE(recordingfile,'') AS recordingfile"            : ", '' AS recordingfile");
            sel.Append(temLinkedId      ? ", COALESCE(linkedid,uniqueid) AS linkedid"                : ", uniqueid AS linkedid");

            var chaveGrupo = temLinkedId ? "COALESCE(linkedid,uniqueid)" : "uniqueid";

            await using var cmd = conn.CreateCommand();
            cmd.Parameters.AddWithValue("@DataInicio", dataInicio);

            if (incremental)
            {
                // Passo 1 (barato, quase sempre vazio): grupos com linha encerrada desde o cursor.
                var chaves = new List<string>();
                var janela = cursor.AddHours(-JanelaMaxChamadaHoras);
                if (janela < dataInicio) janela = dataInicio;

                await using (var cmdChaves = conn.CreateCommand())
                {
                    cmdChaves.CommandText =
                        $"SELECT DISTINCT {chaveGrupo} FROM `{o.MySql.Table}` " +
                        "WHERE calldate >= @Janela AND DATE_ADD(calldate, INTERVAL duration SECOND) >= @Desde LIMIT 2000";
                    cmdChaves.Parameters.AddWithValue("@Janela", janela);
                    cmdChaves.Parameters.AddWithValue("@Desde", cursor.AddSeconds(-MargemIncrementalSegundos));
                    await using var rc = await cmdChaves.ExecuteReaderAsync();
                    while (await rc.ReadAsync())
                        if (!rc.IsDBNull(0)) chaves.Add(rc.GetString(0));
                }

                if (chaves.Count == 0)
                    return Results.Ok(MontarResposta(new List<CdrRow>(), agora, dataInicio, incremental: true));

                // Passo 2: os grupos inteiros dentro da janela de retenção.
                var nomes = new List<string>(chaves.Count);
                for (var i = 0; i < chaves.Count; i++)
                {
                    nomes.Add("@k" + i);
                    cmd.Parameters.AddWithValue("@k" + i, chaves[i]);
                }
                cmd.CommandText =
                    $"SELECT {sel} FROM `{o.MySql.Table}` WHERE calldate >= @DataInicio " +
                    $"AND {chaveGrupo} IN ({string.Join(",", nomes)}) ORDER BY calldate DESC LIMIT 5000";
            }
            else
            {
                cmd.CommandText =
                    $"SELECT {sel} FROM `{o.MySql.Table}` WHERE calldate >= @DataInicio ORDER BY calldate DESC LIMIT 5000";
                logger.LogInformation("API_CDR_SQL | {Sql}", cmd.CommandText);
            }

            var rows = new List<CdrRow>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new CdrRow
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

            if (incremental)
                logger.LogInformation("API_CDR_INCR_OK | ramal={Ramal} desde={Desde} rows={Count}",
                    ramal ?? "todos", desde, rows.Count);
            else
                logger.LogInformation("API_CDR_QUERY_OK | rows={Count}", rows.Count);

            return Results.Ok(MontarResposta(rows, agora, dataInicio, incremental));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "API_CDR_QUERY_ERROR | {Message}", ex.Message);
            return Results.Problem($"Falha ao consultar CDR: {ex.Message}", statusCode: 502);
        }
    }

    // "calls" continua sendo o único campo que clientes antigos leem; os demais são aditivos.
    private static object MontarResposta(List<CdrRow> rows, DateTime agora, DateTime dataInicio, bool incremental) => new
    {
        calls       = rows,
        incremental,
        serverTime  = agora.ToString(FormatoCursor, System.Globalization.CultureInfo.InvariantCulture),
        windowStart = dataInicio.ToString(FormatoCursor, System.Globalization.CultureInfo.InvariantCulture)
    };

    // ── GET /api/cdr/test ─────────────────────────────────────────────────────

    private static async Task<IResult> TestConnection(
        IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Cdr");
        var o = opts.Value;

        if (string.IsNullOrWhiteSpace(o.MySql.User))
            return Results.Ok(new { ok = false, message = "MySql.User nao configurado" });

        try
        {
            var cs = BuildConnectionString(o.MySql);
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM `{o.MySql.Table}` LIMIT 1";
            var count = await cmd.ExecuteScalarAsync();
            logger.LogInformation("API_CDR_TEST_OK | tabela={Table} count={Count}", o.MySql.Table, count);
            return Results.Ok(new { ok = true, message = $"Conexao OK — {o.MySql.Host}:{o.MySql.Port}/{o.MySql.Database}" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "API_CDR_TEST_FAIL | {Message}", ex.Message);
            return Results.Ok(new { ok = false, message = ex.Message });
        }
    }

    // ── GET /api/cdr/recordings?file={filename}&date={yyyy-MM-dd} ─────────────

    private static IResult GetRecording(
        string file, string? date,
        IOptions<WavenApiOptions> opts, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Cdr");

        if (string.IsNullOrWhiteSpace(file))
            return Results.BadRequest(new { error = "Parametro 'file' obrigatorio" });

        // Previne path traversal
        var safeFile = Path.GetFileName(file);
        if (string.IsNullOrWhiteSpace(safeFile) || safeFile.Contains(".."))
            return Results.BadRequest(new { error = "Nome de arquivo invalido" });

        var basePath = opts.Value.RecordingsPath.TrimEnd('/', '\\');

        // Tenta localizar o arquivo: com subdiretorio de data ou direto
        string? found = null;

        if (!string.IsNullOrWhiteSpace(date) &&
            DateTime.TryParse(date, out var dt))
        {
            var withDate = Path.Combine(basePath,
                dt.Year.ToString(), dt.Month.ToString("D2"), dt.Day.ToString("D2"), safeFile);
            if (File.Exists(withDate)) found = withDate;
        }

        if (found == null)
        {
            var direct = Path.Combine(basePath, safeFile);
            if (File.Exists(direct)) found = direct;
        }

        if (found == null)
        {
            logger.LogWarning("API_CDR_RECORDING_NOT_FOUND | file={File} base={Base}", safeFile, basePath);
            return Results.NotFound(new { error = "Gravacao nao encontrada", file = safeFile });
        }

        logger.LogInformation("API_CDR_RECORDING_SERVE | file={File}", found);

        var contentType = safeFile.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            ? "audio/mpeg"
            : "audio/wav";

        return Results.File(found, contentType, safeFile, enableRangeProcessing: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildConnectionString(WavenApiOptions.MySqlOptions m) =>
        $"Server={m.Host};Port={m.Port};Database={m.Database};" +
        $"User ID={m.User};Password={m.Password};" +
        $"Connection Timeout={m.ConnectionTimeoutSeconds};Allow Zero Datetime=True;Convert Zero Datetime=True;";

    // A lista de colunas não muda com o serviço no ar — consultar INFORMATION_SCHEMA a cada
    // requisição era uma segunda ida ao MySQL por chamada. Só guarda resultado não vazio.
    private static async Task<List<string>> ObterColunasAsync(MySqlConnection conn, string db, string tabela)
    {
        var chave = db + "." + tabela;
        if (_colunasCache.TryGetValue(chave, out var emCache)) return emCache;

        var cols = new List<string>();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS " +
                              "WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@tabela";
            cmd.Parameters.AddWithValue("@db", db);
            cmd.Parameters.AddWithValue("@tabela", tabela);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) cols.Add(r.GetString(0));
        }
        catch { }
        if (cols.Count > 0) _colunasCache[chave] = cols;
        return cols;
    }
}
