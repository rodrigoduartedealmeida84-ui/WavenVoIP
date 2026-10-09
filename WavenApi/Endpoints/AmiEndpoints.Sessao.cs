using System.Net.Sockets;
using System.Text;

namespace WavenApi.Endpoints;

// Sessão AMI compartilhada + cache curto de resultado.
//
// Antes, cada GET em /peers, /queues-live e /extensions abria um TCP novo e fazia
// Login/Logoff no AMI — com vários WavenVoIP consultando, eram milhares de logins por hora.
// Agora existe UMA sessão AMI autenticada, reaproveitada por todas as requisições (uma por
// vez), e o resultado de cada endpoint fica válido por alguns segundos para quem chegar junto.
public static partial class AmiEndpoints
{
    private static readonly TimeSpan TtlAoVivo   = TimeSpan.FromMilliseconds(2500);
    // /extensions só devolve ramal + nome cadastrado (não é estado ao vivo).
    private static readonly TimeSpan TtlCadastro = TimeSpan.FromSeconds(30);

    private static readonly CacheCurto<List<Models.AmiPeer>>      _cachePeers      = new();
    private static readonly CacheCurto<List<Models.AmiQueue>>     _cacheFilas      = new();
    private static readonly CacheCurto<List<Models.AmiExtension>> _cacheExtensions = new();

    private static readonly SemaphoreSlim _sessaoGate = new(1, 1);
    private static TcpClient?     _sessaoClient;
    private static NetworkStream? _sessaoStream;
    private static string         _sessaoChave = string.Empty;
    private static long           _pingSeq;

    // Incrementado por LerRespostaAsync quando o tempo acaba sem a resposta completa: a
    // sessão pode ter ficado com resto de resposta no socket e não é reaproveitada.
    private static int _leiturasIncompletas;

    /// <summary>
    /// Guarda o último resultado por <c>ttl</c>; requisições simultâneas esperam a mesma busca
    /// em vez de dispararem uma cada. Falha também é guardada (por 1 s) para a fila de espera
    /// não repetir uma a uma o timeout de um AMI fora do ar.
    /// </summary>
    private sealed class CacheCurto<T> where T : class
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private T?         _valor;
        private Exception? _erro;
        private long       _quando;

        public async Task<(T valor, bool doCache)> ObterAsync(TimeSpan ttl, Func<Task<T>> buscar)
        {
            if (TentarLer(ttl, out var v)) return (v, true);

            await _gate.WaitAsync();
            try
            {
                if (TentarLer(ttl, out v)) return (v, true);
                try
                {
                    v = await buscar();
                    _erro   = null;
                    _valor  = v;
                    _quando = Environment.TickCount64;
                    return (v, false);
                }
                catch (Exception ex)
                {
                    _valor  = null;
                    _erro   = ex;
                    _quando = Environment.TickCount64;
                    throw;
                }
            }
            finally { _gate.Release(); }
        }

        private bool TentarLer(TimeSpan ttl, out T valor)
        {
            valor = null!;
            var idade = Environment.TickCount64 - Volatile.Read(ref _quando);
            var erro  = Volatile.Read(ref _erro);
            if (erro != null)
            {
                if (idade < 1000) throw new InvalidOperationException(erro.Message, erro);
                return false;
            }
            var v = Volatile.Read(ref _valor);
            if (v == null || idade >= ttl.TotalMilliseconds) return false;
            valor = v;
            return true;
        }
    }

    /// <summary>
    /// Executa <paramref name="acao"/> na sessão AMI compartilhada. Se a sessão reaproveitada
    /// estiver morta (Asterisk reiniciou, socket caiu), reconecta e tenta mais uma vez.
    /// </summary>
    private static async Task<T> ComSessaoAmiAsync<T>(
        WavenApiOptions.AmiOptions o, ILogger logger, Func<NetworkStream, Task<T>> acao)
    {
        await _sessaoGate.WaitAsync();
        try
        {
            for (var tentativa = 0; ; tentativa++)
            {
                var reaproveitada = false;
                try
                {
                    var chave = $"{o.Host}:{o.Port}:{o.User}";
                    if (_sessaoStream != null && _sessaoChave == chave && await SessaoVivaAsync(_sessaoStream))
                    {
                        reaproveitada = true;
                    }
                    else
                    {
                        FecharSessao();
                        await AbrirSessaoAsync(o, chave);
                        logger.LogInformation("API_AMI_SESSION_OPEN | host={Host}:{Port}", o.Host, o.Port);
                    }

                    var incompletasAntes = Volatile.Read(ref _leiturasIncompletas);
                    var resultado = await acao(_sessaoStream!);
                    if (Volatile.Read(ref _leiturasIncompletas) != incompletasAntes)
                    {
                        logger.LogWarning("API_AMI_SESSION_RESET | resposta incompleta — sessao sera reaberta na proxima consulta");
                        FecharSessao();
                    }
                    return resultado;
                }
                catch (Exception ex) when (reaproveitada && tentativa == 0)
                {
                    logger.LogWarning("API_AMI_SESSION_RETRY | sessao reaproveitada falhou ({Message}) — reconectando", ex.Message);
                    FecharSessao();
                }
                catch
                {
                    FecharSessao();
                    throw;
                }
            }
        }
        finally { _sessaoGate.Release(); }
    }

    private static async Task AbrirSessaoAsync(WavenApiOptions.AmiOptions o, string chave)
    {
        var client = new TcpClient();
        try
        {
            using var cto = new CancellationTokenSource(o.ConnectTimeoutMs);
            await client.ConnectAsync(o.Host, o.Port, cto.Token);
            client.ReceiveTimeout = 12000;
            client.SendTimeout    = 5000;
            var stream = client.GetStream();

            await LerDisponivelAsync(stream, 700); // banner "Asterisk Call Manager/x.y"

            await EnviarAsync(stream,
                $"Action: Login\r\nUsername: {o.User}\r\nSecret: {o.Password}\r\n" +
                "Events: off\r\nActionID: WAVEN_LOGIN\r\n\r\n");
            var login = await LerRespostaAsync(stream, "WAVEN_LOGIN", null, 5000);
            if (login.IndexOf("Success", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("AMI recusou login — verifique usuario, senha e permissoes.");

            _sessaoClient = client;
            _sessaoStream = stream;
            _sessaoChave  = chave;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // Antes de reaproveitar: descarta qualquer resto no socket e confirma com um Ping que o
    // Asterisk ainda responde nesta sessão.
    private static async Task<bool> SessaoVivaAsync(NetworkStream stream)
    {
        try
        {
            if (_sessaoClient is not { Connected: true }) return false;

            var lixo = new byte[4096];
            while (stream.DataAvailable)
                if (await stream.ReadAsync(lixo) <= 0) return false;

            var id = "WAVEN_PING_" + Interlocked.Increment(ref _pingSeq);
            await EnviarAsync(stream, $"Action: Ping\r\nActionID: {id}\r\n\r\n");
            var incompletasAntes = Volatile.Read(ref _leiturasIncompletas);
            var resp = await LerRespostaAsync(stream, id, null, 1500);
            return Volatile.Read(ref _leiturasIncompletas) == incompletasAntes
                && resp.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    private static void FecharSessao()
    {
        try { _sessaoStream?.Dispose(); } catch { }
        try { _sessaoClient?.Dispose(); } catch { }
        _sessaoStream = null;
        _sessaoClient = null;
        _sessaoChave  = string.Empty;
    }

    /// <summary>
    /// Lê a resposta de UMA ação AMI até o fim dela, consumindo o bloco final inteiro (o que
    /// sobrasse no socket apareceria na leitura seguinte de uma sessão reaproveitada).
    /// <para><paramref name="eventoFim"/> != null: ação de lista — termina no evento
    /// "...Complete" ou num "Response: Error" (ação/módulo indisponível).</para>
    /// <para><paramref name="eventoFim"/> == null: ação de resposta única (Login, Ping,
    /// Command). No Asterisk 14+ a saída de Command vem em linhas "Output:" dentro do próprio
    /// bloco, sem o "--END COMMAND--" das versões antigas ("Response: Follows").</para>
    /// </summary>
    private static async Task<string> LerRespostaAsync(
        NetworkStream s, string actionId, string? eventoFim, int timeoutMs)
    {
        const string fimBloco = "\r\n\r\n";
        var sb  = new StringBuilder();
        var buf = new byte[8192];
        var limite = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < limite)
        {
            var leu = false;
            while (s.DataAvailable)
            {
                var n = await s.ReadAsync(buf);
                if (n <= 0) throw new IOException("AMI fechou a conexao.");
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                leu = true;
            }

            if (leu)
            {
                var txt = sb.ToString();

                if (eventoFim != null)
                {
                    var f = txt.IndexOf(eventoFim, StringComparison.OrdinalIgnoreCase);
                    if (f >= 0 && txt.IndexOf(fimBloco, f, StringComparison.Ordinal) >= 0) return txt;
                }

                var a = txt.IndexOf("ActionID: " + actionId, StringComparison.OrdinalIgnoreCase);
                var fim = a >= 0 ? txt.IndexOf(fimBloco, a, StringComparison.Ordinal) : -1;
                if (fim >= 0)
                {
                    var ini = txt.LastIndexOf(fimBloco, a, StringComparison.Ordinal);
                    var bloco = txt.Substring(ini < 0 ? 0 : ini, fim - (ini < 0 ? 0 : ini));
                    var ehErro = bloco.IndexOf("Response: Error", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (eventoFim != null)
                    {
                        if (ehErro) return txt;
                    }
                    else if (bloco.IndexOf("Response: Follows", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var e = txt.IndexOf("--END COMMAND--", StringComparison.OrdinalIgnoreCase);
                        if (e >= 0 && txt.IndexOf(fimBloco, e, StringComparison.Ordinal) >= 0) return txt;
                    }
                    else if (bloco.IndexOf("Response:", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return txt;
                    }
                }
            }

            await Task.Delay(20);
        }

        Interlocked.Increment(ref _leiturasIncompletas);
        return sb.ToString();
    }
}
