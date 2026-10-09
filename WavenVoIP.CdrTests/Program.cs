// Teste local do CDR incremental — NÃO grava histórico nem config (só lê e compara em memória).
// Uso: CdrTest <urlApiNova> <urlApiAntiga>
using System.Reflection;
using System.Text.Json;
using WavenVoIP;
using WavenVoIP.Models;
using WavenVoIP.Services;

var urlNova   = args[0];
var urlAntiga = args.Length > 1 ? args[1] : "";
int total = 0, falhas = 0;
void Check(string nome, bool ok, string detalhe = "")
{
    total++; if (!ok) falhas++;
    Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {nome} {detalhe}");
}

const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
var asm = typeof(SipConfig).Assembly;
var inc = asm.GetType("WavenVoIP.Services.CdrApiIncrementalService")!;
FieldInfo F(string n) => inc.GetField(n, Priv)!;
List<ApiCdrRow>? Linhas() => (List<ApiCdrRow>?)F("_linhas").GetValue(null);
string Cursor() => (string)F("_cursor").GetValue(null)!;

void UsarUrl(string url)
{
    SipConfig.CarregarSalva(); // garante cache carregado
    var inst = (SipConfig)typeof(SipConfig).GetField("_cacheInstancia", Priv)!.GetValue(null)!;
    inst.WavenApiUrl = url;   // só em memória, neste processo
}

string K(ApiCdrRow r) => $"{r.UniqueId}|{r.CallDate:s}|{r.Channel}|{r.DstChannel}|{r.LastApp}|{r.Duration}|{r.BillSec}|{r.Disposition}|{r.LinkedId}";
string Raw(IEnumerable<ApiCdrRow> l) => string.Join("\n", l.Select(K));
string RawSet(IEnumerable<ApiCdrRow> l) => string.Join("\n", l.Select(K).OrderBy(x => x, StringComparer.Ordinal));
string SemId(string j) => System.Text.RegularExpressions.Regex.Replace(j, "\"Id\":\"[0-9a-f]{32}\"", "\"Id\":\"\"");
string Ser(List<HistoricoLigacaoItem> l) => SemId(JsonSerializer.Serialize(l));
void Diff(List<HistoricoLigacaoItem> a, List<HistoricoLigacaoItem> b)
{
    var sa = a.Select(x => SemId(JsonSerializer.Serialize(x))).ToList(); var sb = b.Select(x => SemId(JsonSerializer.Serialize(x))).ToList();
    var soA = sa.Except(sb).ToList(); var soB = sb.Except(sa).ToList();
    Console.WriteLine($"       itens A={a.Count} B={b.Count} soEmA={soA.Count} soEmB={soB.Count} mesmaOrdem={sa.SequenceEqual(sb)}");
    foreach (var x in soA.Take(2)) Console.WriteLine("       A: " + x[..Math.Min(260, x.Length)]);
    foreach (var x in soB.Take(2)) Console.WriteLine("       B: " + x[..Math.Min(260, x.Length)]);
}

UsarUrl(urlNova);
var cfg = SipConfig.CarregarSalva()!;
var dias = cfg.HistoricoRetencaoDias;
Console.WriteLine($"ramal={cfg.Ramal} usarApi={cfg.UsarWavenApi} cdrAtivo={cfg.CdrAtivo} dias={dias} url={cfg.WavenApiUrl}");

// ── T1: sync completo (equivale ao botão "Atualizar CDR") ────────────────────────────
var sw = System.Diagnostics.Stopwatch.StartNew();
var A = await IssabelCdrService.SincronizarAsync(cfg, dias, forcarCompleto: true);
var msFull = sw.ElapsedMilliseconds;
var rawA = Raw(Linhas()!); var setA = RawSet(Linhas()!); var nA = Linhas()!.Count; var cursorA = Cursor();
Check("T1 sync completo popula cache e cursor", nA > 0 && cursorA.Length == 19, $"linhas={nA} itens={A.Count} cursor={cursorA} ms={msFull}");

// ── T2: ciclo automático logo depois = incremental, mesmo resultado ──────────────────
sw.Restart();
var B = await IssabelCdrService.SincronizarAsync(cfg, dias);
var msInc = sw.ElapsedMilliseconds;
Check("T2 incremental devolve o mesmo historico", Ser(A) == Ser(B), $"ms={msInc}");
if (Ser(A) != Ser(B)) Diff(A, B);
Check("T2 cache intacto (mesmas linhas, mesma ordem)", Raw(Linhas()!) == rawA, $"linhas={Linhas()!.Count}");
Check("T2 cursor avancou", string.CompareOrdinal(Cursor(), cursorA) >= 0, $"cursor={Cursor()}");

// tamanho transferido: completo x incremental
var rFull = await WavenApiService.GetCdrAsync(cfg.Ramal, dias, null);
var rInc  = await WavenApiService.GetCdrAsync(cfg.Ramal, dias, Cursor());
Console.WriteLine($"       bytes: completo={rFull!.TamanhoBytes} incremental={rInc!.TamanhoBytes} (incremental={rInc.Incremental}, linhas={rInc.Calls.Count})");
Check("T2 resposta incremental e' pequena", rInc.Incremental && rInc.TamanhoBytes < rFull.TamanhoBytes / 20);

// ── T3: cache defasado em N horas -> incremental recompõe exatamente ─────────────────
foreach (var horas in new[] { 1, 3, 8 })
{
    var agoraSrv = DateTime.ParseExact(Cursor(), "yyyy-MM-dd HH:mm:ss", null);
    var T = agoraSrv.AddHours(-horas);
    var linhas = Linhas()!;
    // remove linha a linha (não por grupo) tudo que encerrou perto/depois de T: simula grupos
    // parcialmente recebidos + chamadas ainda inexistentes no cache
    var removidas = linhas.RemoveAll(r => r.CallDate.AddSeconds(r.Duration) >= T.AddSeconds(-200));
    F("_cursor").SetValue(null, T.ToString("yyyy-MM-dd HH:mm:ss"));
    var C = await IssabelCdrService.SincronizarAsync(cfg, dias);
    var igual = Ser(C) == Ser(A);
    Check($"T3 defasado {horas}h: historico recomposto igual ao completo", igual, $"removidas={removidas}");
    if (!igual) Diff(A, C);
    Check($"T3 defasado {horas}h: cache com as mesmas linhas (sem perda/duplicidade)", RawSet(Linhas()!) == setA,
        $"linhas={Linhas()!.Count} esperado={nA} ordemIgual={Raw(Linhas()!) == rawA}");
}

// ── T3b: repetir o mesmo incremental não duplica ─────────────────────────────────────
for (var i = 0; i < 3; i++) await IssabelCdrService.SincronizarAsync(cfg, dias);
var distintas = Linhas()!.Select(K).Distinct().Count();
Check("T3b ciclos repetidos nao duplicam linhas", Linhas()!.Count == nA, $"linhas={Linhas()!.Count} distintas={distintas}");

// ── T4: pedido de 1 dia (pós-chamada) é servido do cache de 7 dias ───────────────────
var antesCompleto = (DateTime)F("_ultimoCompletoUtc").GetValue(null)!;
var D1 = await IssabelCdrService.SincronizarAsync(cfg, 1);
Check("T4 dias=1 nao dispara download completo", (DateTime)F("_ultimoCompletoUtc").GetValue(null)! == antesCompleto && Linhas()!.Count == nA);
var api1 = await WavenApiService.GetCdrAsync(cfg.Ramal, 1, null);
var srvNow = DateTime.ParseExact(Cursor(), "yyyy-MM-dd HH:mm:ss", null);
var borda = srvNow.AddDays(-1);
var recorte = Linhas()!.Where(r => r.CallDate >= borda).Select(K).ToHashSet();
var doServidor = api1!.Calls.Select(K).ToHashSet();
var difs = recorte.Except(doServidor).Concat(doServidor.Except(recorte)).Count();
Check("T4 recorte de 1 dia == consulta dias=1 no servidor", difs <= 2, $"cache={recorte.Count} servidor={doServidor.Count} difs={difs} itens={D1.Count}");

// ── T5: queda da API -> invalida -> recupera com completo ────────────────────────────
UsarUrl("http://127.0.0.1:59999");
var cfgFora = SipConfig.CarregarSalva()!;
var E = await IssabelCdrService.SincronizarAsync(cfgFora, dias);
Check("T5 API fora: sync devolve vazio sem excecao e invalida cache", E.Count == 0 && Linhas() == null && Cursor() == "");
UsarUrl(urlNova);
var G = await IssabelCdrService.SincronizarAsync(SipConfig.CarregarSalva()!, dias);
Check("T5 API voltou: primeiro ciclo baixa completo e iguala", Linhas() != null && Linhas()!.Count == nA && Ser(G) == Ser(A));
if (Ser(G) != Ser(A)) Diff(A, G);
var H = await IssabelCdrService.SincronizarAsync(SipConfig.CarregarSalva()!, dias);
Check("T5 ciclo seguinte volta ao incremental", Ser(H) == Ser(A) && Cursor().Length == 19);

// ── T6: API antiga (sem suporte a incremental) -> continua funcionando como antes ────
// A produção já roda a API nova, então a API antiga é SIMULADA por um servidor local que
// repassa a consulta à API real e se comporta como a v2.4.6 do servidor: ignora "desde" e
// devolve somente {"calls":[...]} (sem incremental/serverTime/windowStart).
{
    var stub = new ApiAntigaSimulada(urlNova);
    UsarUrl(stub.Url);
    var c2 = SipConfig.CarregarSalva()!;
    var O1 = await IssabelCdrService.SincronizarAsync(c2, dias);
    var semCursor1 = Cursor() == "";
    var O2 = await IssabelCdrService.SincronizarAsync(c2, dias);
    var O3 = await IssabelCdrService.SincronizarAsync(c2, dias);
    Check("T6 API antiga: resposta sem serverTime nao cria cursor", semCursor1 && Cursor() == "" && Linhas() != null, $"linhas={Linhas()?.Count}");
    Check("T6 API antiga: todo ciclo baixa a janela completa (3 ciclos = 3 consultas completas)", stub.Consultas == 3 && stub.ComDesde == 0, $"consultas={stub.Consultas} comDesde={stub.ComDesde}");
    Check("T6 API antiga: mesmo historico que a API nova", Ser(O1) == Ser(A) && Ser(O2) == Ser(A) && Ser(O3) == Ser(A));
    if (Ser(O1) != Ser(A)) Diff(A, O1);
    Check("T6 API antiga: sem duplicar linhas entre ciclos", Linhas()!.Count == nA, $"linhas={Linhas()!.Count} esperado={nA}");

    // Servidor atualizado no meio do uso: o cliente passa ao incremental sozinho.
    UsarUrl(urlNova);
    var N1 = await IssabelCdrService.SincronizarAsync(SipConfig.CarregarSalva()!, dias);
    var N2 = await IssabelCdrService.SincronizarAsync(SipConfig.CarregarSalva()!, dias);
    Check("T6 troca para API nova: volta a ter cursor e mantem o historico", Cursor().Length == 19 && Ser(N1) == Ser(A) && Ser(N2) == Ser(A));
    stub.Parar();
}

Console.WriteLine($"\nRESULTADO: {total - falhas}/{total} OK, {falhas} falha(s)");
await Task.Delay(1500);
return falhas == 0 ? 0 : 1;

// Simula a Waven API anterior à sincronização incremental (ver T6).
sealed class ApiAntigaSimulada
{
    private readonly System.Net.Sockets.TcpListener _l;
    private readonly string _real;
    private readonly System.Net.Http.HttpClient _http = new();
    private volatile bool _parar;
    public int Consultas, ComDesde;
    public string Url { get; }

    public ApiAntigaSimulada(string urlReal)
    {
        _real = urlReal.TrimEnd('/');
        _l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        _l.Start();
        Url = $"http://127.0.0.1:{((System.Net.IPEndPoint)_l.LocalEndpoint).Port}";
        _ = Task.Run(LoopAsync);
    }

    public void Parar() { _parar = true; try { _l.Stop(); } catch { } }

    private async Task LoopAsync()
    {
        while (!_parar)
        {
            System.Net.Sockets.TcpClient c;
            try { c = await _l.AcceptTcpClientAsync(); } catch { break; }
            _ = Task.Run(() => AtenderAsync(c));
        }
    }

    private async Task AtenderAsync(System.Net.Sockets.TcpClient c)
    {
        using (c)
        {
            var s = c.GetStream();
            var rd = new System.IO.StreamReader(s, System.Text.Encoding.ASCII);
            var linha = await rd.ReadLineAsync() ?? "";
            string auth = "", h;
            while (!string.IsNullOrEmpty(h = await rd.ReadLineAsync() ?? ""))
                if (h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) auth = h.Substring(14).Trim();
            var alvo = linha.Split(' ').ElementAtOrDefault(1) ?? "/";
            int status = 404; byte[] corpo = System.Text.Encoding.UTF8.GetBytes("{}");
            if (alvo.StartsWith("/api/cdr/calls"))
            {
                Interlocked.Increment(ref Consultas);
                var q = alvo.Contains('?') ? alvo.Substring(alvo.IndexOf('?') + 1).Split('&').ToList() : new List<string>();
                if (q.Any(p => p.StartsWith("desde="))) Interlocked.Increment(ref ComDesde);
                q.RemoveAll(p => p.StartsWith("desde="));                       // API antiga não conhece "desde"
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"{_real}/api/cdr/calls?{string.Join("&", q)}");
                if (auth.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", auth);
                using var resp = await _http.SendAsync(req);
                status = (int)resp.StatusCode;
                var json = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    json = "{\"calls\":" + doc.RootElement.GetProperty("calls").GetRawText() + "}";   // só o campo que a API antiga devolvia
                }
                corpo = System.Text.Encoding.UTF8.GetBytes(json);
            }
            var cab = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {corpo.Length}\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(cab); await s.WriteAsync(corpo); await s.FlushAsync();
        }
    }
}
