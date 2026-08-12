using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    /// <summary>
    /// Long-lived AMI TCP connection that monitors ramal status in real time.
    /// Updates are always dispatched to the UI thread.
    /// Usage: call Start(config) once; call Stop() or Dispose() on shutdown.
    /// </summary>
    public sealed class AmiMonitorService : IDisposable
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static AmiMonitorService? _current;
        public static AmiMonitorService? Current => _current;

        public static AmiMonitorService Iniciar(SipConfig config)
        {
            _current?.Stop();
            _current = new AmiMonitorService();
            _current.Start(config);
            return _current;
        }

        public static void PararCurrent() { _current?.Stop(); _current = null; }

        // ── Public state ──────────────────────────────────────────────────────
        public ObservableCollection<RamalStatusItem> Ramais { get; } = new();
        public ObservableCollection<FilaStatusItem>  Filas  { get; } = new();
        public bool Conectado { get; private set; }
        public string ErroConexao { get; private set; } = string.Empty;
        public event Action? EstadoChanged;
        public event Action? FilasChanged;

        // ── Internal ─────────────────────────────────────────────────────────
        private CancellationTokenSource _cts = new();
        private readonly Dictionary<string, RamalStatusItem> _ramalState = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FilaStatusItem>  _filaState  = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ChannelInfo> _channels = new(StringComparer.OrdinalIgnoreCase);
        // bridge → (channel1, channel2)
        private readonly Dictionary<string, (string Ch1, string Ch2)> _bridges = new(StringComparer.OrdinalIgnoreCase);
        // trigger para forçar poll imediato (botão Atualizar)
        private readonly System.Threading.SemaphoreSlim _pollTrigger = new(0, 1);

        private class ChannelInfo
        {
            public string Channel { get; set; } = string.Empty;
            public string Ramal { get; set; } = string.Empty;
            public string RemoteNum { get; set; } = string.Empty;
            public string RemoteName { get; set; } = string.Empty;
            public bool IsInbound { get; set; }
            public int State { get; set; }
            public DateTime Created { get; set; } = DateTime.Now;
            public string BridgeId { get; set; } = string.Empty;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────
        private void Start(SipConfig config)
        {
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            bool usarApi = config.UsarWavenApi &&
                           !string.IsNullOrWhiteSpace(config.WavenApiUrl) &&
                           !string.IsNullOrWhiteSpace(config.WavenApiToken);

            LogHelper.Info($"AMI_MONITOR_START | modoApi={usarApi} url={config.WavenApiUrl}");

            if (usarApi)
                Task.Run(() => ApiMonitorLoopAsync(ct), ct);
            else
                Task.Run(() => MonitorLoopAsync(config, ct), ct);
        }

        public void Stop()
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }

        public void Dispose() => Stop();

        /// Força um poll imediato no próximo ciclo (usado pelo botão Atualizar).
        public void ForcePoll()
        {
            try { _pollTrigger.Release(); } catch (System.Threading.SemaphoreFullException) { }
        }

        // ── API polling loop (Waven API mode — no direct TCP to AMI) ─────────
        private async Task ApiMonitorLoopAsync(CancellationToken ct)
        {
            var startTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            // NOTA: modoBasico NÃO é declarado aqui — é local a cada iteração do loop.
            // Antes estava fora do while, tornando-se permanente após primeiro 404.

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // ── modoBasico é LOCAL por iteração — nunca fica permanente ──
                    bool modoBasico = false;
                    List<Models.ApiAmiPeer>? peers = null;
                    string erroMsg = string.Empty;

                    LogHelper.Info("AMI_API_POLL_START | endpoint=/api/ami/peers");

                    // ── Sempre tenta /api/ami/peers primeiro ─────────────────
                    var (p, httpStatus, err) = await WavenApiService.GetAmiPeersSafeAsync().ConfigureAwait(false);

                    if (p != null)
                    {
                        peers = p;
                        int cOnline  = peers.Count(x => x.Status == "online");
                        int cEmLig   = peers.Count(x => x.Status is "emligacao" or "tocando" or "chamando");
                        int cOffline = peers.Count(x => x.Status == "offline");
                        int cIndisp  = peers.Count(x => x.Status is "indisponivel" or "desconhecido");
                        LogHelper.Info($"AMI_API_PEERS_OK | total={peers.Count} online={cOnline} emLig={cEmLig} offline={cOffline} indisp={cIndisp} | badge=Conectado");
                    }
                    else if (httpStatus == 404)
                    {
                        // Endpoint não existe nesta versão da API — fallback básico APENAS para esta iteração
                        modoBasico = true;
                        LogHelper.Info("AMI_API_PEERS_404 | endpoint /api/ami/peers não existe — fallback /api/ami/extensions (só esta iteração)");
                    }
                    else if (httpStatus == 401 || httpStatus == 403)
                    {
                        erroMsg = $"Token da Waven API inválido (HTTP {httpStatus})";
                        LogHelper.Info($"AMI_API_POLL_AUTH_ERROR | httpStatus={httpStatus}");
                    }
                    else if (httpStatus == 0)
                    {
                        erroMsg = "Waven API offline — " + err;
                        LogHelper.Info($"AMI_API_POLL_OFFLINE | err={err}");
                    }
                    else if (httpStatus >= 500)
                    {
                        erroMsg = $"AMI indisponível no servidor (HTTP {httpStatus})";
                        LogHelper.Info($"AMI_API_POLL_SERVER_ERROR | httpStatus={httpStatus}");
                    }
                    else
                    {
                        erroMsg = $"Waven API: HTTP {httpStatus}";
                        LogHelper.Info($"AMI_API_POLL_HTTP_ERROR | httpStatus={httpStatus}");
                    }

                    // ── Fallback: /api/ami/extensions (somente quando /api/ami/peers retorna 404) ──
                    if (modoBasico && peers == null)
                    {
                        LogHelper.Info("AMI_API_EXTENSIONS_START | modo básico — /api/ami/peers não deployado");
                        var extensions = await WavenApiService.GetAmiExtensionsAsync().ConfigureAwait(false);
                        if (extensions != null && extensions.Count > 0)
                        {
                            peers = extensions
                                .Select(e => new Models.ApiAmiPeer { Ramal = e.Ramal, Nome = e.Nome, Status = "indisponivel", RawStatus = "fallback/extensions" })
                                .ToList();
                            erroMsg = string.Empty;
                            LogHelper.Info($"AMI_API_EXTENSIONS_OK | count={peers.Count} | badge=ModoBasico");
                        }
                        else if (extensions != null)
                        {
                            peers   = new List<Models.ApiAmiPeer>();
                            erroMsg = string.Empty;
                            LogHelper.Info("AMI_API_EXTENSIONS_OK | count=0 | badge=ModoBasico");
                        }
                        else
                        {
                            erroMsg = "Waven API offline ou não configurada";
                            LogHelper.Info("AMI_API_EXTENSIONS_FAIL | resultado nulo");
                        }
                    }

                    // ── Poll de filas (só no modo API, paralelo ao poll de ramais) ─────
                    if (!modoBasico)
                    {
                        try
                        {
                            var (filasList, _, _) = await WavenApiService.GetAmiQueuesAsync().ConfigureAwait(false);
                            if (filasList != null)
                            {
                                var filasCapture = filasList;
                                Dispatch(() =>
                                {
                                    var responseSet = new HashSet<string>(filasCapture.Select(f => f.Fila), StringComparer.OrdinalIgnoreCase);
                                    var toRemove = _filaState.Keys.Where(k => !responseSet.Contains(k)).ToList();
                                    foreach (var k in toRemove)
                                    {
                                        if (_filaState.TryGetValue(k, out var dead)) Filas.Remove(dead);
                                        _filaState.Remove(k);
                                    }
                                    foreach (var fq in filasCapture)
                                    {
                                        if (!_filaState.TryGetValue(fq.Fila, out var item))
                                        {
                                            item = new FilaStatusItem { Fila = fq.Fila, Nome = fq.Fila };
                                            _filaState[fq.Fila] = item;
                                            Filas.Add(item);
                                        }
                                        item.Nome                 = string.IsNullOrWhiteSpace(fq.Nome) ? fq.Fila : fq.Nome;
                                        item.Aguardando           = fq.Aguardando;
                                        item.Atendidas            = fq.Atendidas;
                                        item.Abandonadas          = fq.Abandonadas;
                                        item.TempoMedioEspera     = fq.TempoMedioEspera;
                                        item.AgentesOnline        = fq.AgentesOnline;
                                        item.AgentesEmAtendimento = fq.AgentesEmAtendimento;
                                        item.AgentesPausados      = fq.AgentesPausados;
                                        item.AgentesOffline       = fq.AgentesOffline;
                                        item.AgentesTotal         = fq.AgentesTotal;

                                        item.Agentes.Clear();
                                        foreach (var a in fq.Agentes)
                                            item.Agentes.Add(a);
                                        item.Clientes.Clear();
                                        foreach (var c in fq.Clientes)
                                            item.Clientes.Add(c);
                                    }
                                    FilasChanged?.Invoke();
                                });
                                LogHelper.Info($"AMI_API_QUEUES_OK | total={filasList.Count}");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogHelper.Info($"AMI_API_QUEUES_EXCEPTION | {ex.Message}");
                        }
                    }

                    // ── Processar resultado de ramais ──────────────────────────
                    if (peers != null)
                    {
                        var peersCapture = peers;
                        var modoBasicoCapture = modoBasico;
                        Dispatch(() =>
                        {
                            foreach (var peer in peersCapture)
                            {
                                // Filtrar troncos SIP (ex: "08002") — 4+ dígitos começando com "0"
                                if (peer.Ramal.Length >= 4 && peer.Ramal.Length > 0 && peer.Ramal[0] == '0') continue;

                                var newStatus = MapearStatusApi(peer.Status);
                                bool ativo    = newStatus is StatusRamal.EmLigacao or StatusRamal.Tocando or StatusRamal.Chamando;

                                if (ativo)
                                { if (!startTimes.ContainsKey(peer.Ramal)) startTimes[peer.Ramal] = DateTime.Now; }
                                else
                                { startTimes.Remove(peer.Ramal); }

                                if (!_ramalState.TryGetValue(peer.Ramal, out var item))
                                {
                                    var resolvedNome = peer.Nome;
                                    if (string.IsNullOrWhiteSpace(resolvedNome) || resolvedNome == peer.Ramal)
                                    {
                                        try
                                        {
                                            var local = ContatoStorageService.ResolverNomePorNumero(peer.Ramal);
                                            if (!string.IsNullOrWhiteSpace(local) &&
                                                !string.Equals(local, peer.Ramal, StringComparison.OrdinalIgnoreCase))
                                                resolvedNome = local;
                                        }
                                        catch { }
                                    }
                                    item = new RamalStatusItem { Ramal = peer.Ramal, Nome = resolvedNome };
                                    _ramalState[peer.Ramal] = item;
                                    Ramais.Add(item);
                                }
                                else if (!string.IsNullOrWhiteSpace(peer.Nome) && peer.Nome != peer.Ramal &&
                                         (string.IsNullOrWhiteSpace(item.Nome) || item.Nome == item.Ramal))
                                {
                                    item.Nome = peer.Nome;
                                }

                                item.Status         = newStatus;
                                item.NumeroRemoto   = ativo ? (peer.NumeroRemoto ?? string.Empty) : string.Empty;
                                item.NomeRemoto     = ativo ? (peer.NomeRemoto   ?? string.Empty) : string.Empty;
                                item.DirecaoEntrada = ativo && string.Equals(peer.Direcao, "entrada", StringComparison.OrdinalIgnoreCase);
                                item.Canal          = ativo ? (peer.Canal        ?? string.Empty) : string.Empty;
                                item.LinhaUsada     = ativo ? (peer.LinhaUsada   ?? string.Empty) : string.Empty;
                                item.NomeCanal      = ativo ? (peer.NomeCanal    ?? string.Empty) : string.Empty;
                                item.TipoChamada    = ativo ? (peer.TipoChamada  ?? string.Empty) : string.Empty;
                                item.InicioLigacao  = ativo && startTimes.TryGetValue(peer.Ramal, out var st) ? st : null;
                            }

                            // Remover ramais ausentes da resposta
                            var responseSet = new HashSet<string>(peersCapture.Select(p => p.Ramal), StringComparer.OrdinalIgnoreCase);
                            var toRemove    = _ramalState.Keys.Where(r => !responseSet.Contains(r)).ToList();
                            foreach (var r in toRemove)
                            {
                                if (_ramalState.TryGetValue(r, out var dead)) Ramais.Remove(dead);
                                _ramalState.Remove(r);
                                startTimes.Remove(r);
                            }

                            // Badge: "Conectado" quando /api/ami/peers respondeu OK
                            //        "Modo básico" somente quando /api/ami/peers retornou 404
                            Conectado   = true;
                            ErroConexao = modoBasicoCapture
                                ? "Modo básico: implante waven-api-linux-x64.zip para status em tempo real"
                                : string.Empty;
                            EstadoChanged?.Invoke();
                        });

                        await WaitAsync(10_000, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        Dispatch(() =>
                        {
                            Conectado   = false;
                            ErroConexao = erroMsg;
                            EstadoChanged?.Invoke();
                        });
                        await WaitAsync(15_000, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    LogHelper.Info($"AMI_API_POLL_EXCEPTION | {msg}");
                    Dispatch(() => { Conectado = false; ErroConexao = msg; EstadoChanged?.Invoke(); });
                    await WaitAsync(15_000, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task WaitAsync(int ms, CancellationToken ct)
        {
            try
            {
                // Acorda com timeout normal OU com ForcePoll() — o que vier primeiro
                await Task.WhenAny(
                    Task.Delay(ms, ct),
                    _pollTrigger.WaitAsync(ct)
                ).ConfigureAwait(false);
                // Drena semáforo caso ForcePoll tenha disparado
                while (_pollTrigger.CurrentCount > 0)
                    _pollTrigger.Wait(0);
            }
            catch (OperationCanceledException) { }
        }

        private static StatusRamal MapearStatusApi(string s) => s?.ToLowerInvariant() switch
        {
            "online"       => StatusRamal.Online,
            "offline"      => StatusRamal.Offline,
            "emligacao"    => StatusRamal.EmLigacao,
            "tocando"      => StatusRamal.Tocando,
            "chamando"     => StatusRamal.Chamando,
            "indisponivel" => StatusRamal.Indisponivel,
            _              => StatusRamal.Desconhecido
        };

        // ── Duration update (called by UI timer every second) ─────────────────
        public void AtualizarDuracoes()
        {
            foreach (var r in Ramais)
                if (r.EstaEmLigacao) r.NotificarDuracao();
        }

        // ── Monitor loop ──────────────────────────────────────────────────────
        private async Task MonitorLoopAsync(SipConfig config, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Dispatch(() => { Conectado = false; ErroConexao = string.Empty; EstadoChanged?.Invoke(); });

                try
                {
                    await ConnectAndMonitorAsync(config, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    Dispatch(() => { ErroConexao = msg; EstadoChanged?.Invoke(); });
                }

                if (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(12_000, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task ConnectAndMonitorAsync(SipConfig config, CancellationToken ct)
        {
            var host = string.IsNullOrWhiteSpace(config.AmiHost) ? config.ServerIp : config.AmiHost.Trim();
            var port = config.AmiPorta <= 0 ? 5038 : config.AmiPorta;

            if (string.IsNullOrWhiteSpace(host) ||
                string.IsNullOrWhiteSpace(config.AmiUsuario) ||
                string.IsNullOrWhiteSpace(config.AmiSenha))
            {
                Dispatch(() => { ErroConexao = "AMI não configurado (host/usuário/senha ausentes)."; EstadoChanged?.Invoke(); });
                await Task.Delay(60_000, ct).ConfigureAwait(false);
                return;
            }

            using var client = new TcpClient();
            client.SendTimeout    = 8_000;
            client.ReceiveTimeout = 0; // blocking read — cancellation handles timeout

            var connectTask = client.ConnectAsync(host, port, ct).AsTask();
            var timeoutTask = Task.Delay(8_000, ct);
            if (await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false) == timeoutTask)
                throw new TimeoutException($"Timeout ao conectar ao AMI em {host}:{port}");
            await connectTask; // propagate exception if connect failed

            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, 8192, leaveOpen: true) { AutoFlush = true };

            // Read AMI banner (Asterisk Call Manager/x.x)
            await Task.Delay(250, ct).ConfigureAwait(false);

            // Login with events ON from the start
            await writer.WriteAsync(
                $"Action: Login\r\n" +
                $"Username: {config.AmiUsuario}\r\n" +
                $"Secret: {config.AmiSenha}\r\n" +
                "Events: on\r\n" +
                "ActionID: WVMON_LOGIN\r\n\r\n").ConfigureAwait(false);

            // Keep-alive ping task
            var pingSrc = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = PingLoopAsync(writer, pingSrc.Token);

            bool loginOk = false;
            bool peersRequested = false;

            while (!ct.IsCancellationRequested)
            {
                var bloco = await LerBlocoAsync(reader, ct).ConfigureAwait(false);
                if (bloco == null) break; // connection closed

                // ── Login response ────────────────────────────────────────────
                if (bloco.TryGetValue("ActionID", out var aid) && aid == "WVMON_LOGIN")
                {
                    loginOk = bloco.TryGetValue("Response", out var resp) &&
                              resp.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!loginOk)
                    {
                        var msg2 = bloco.TryGetValue("Message", out var m) ? m : "login recusado";
                        throw new InvalidOperationException($"AMI recusou login: {msg2}");
                    }
                    Dispatch(() => { Conectado = true; EstadoChanged?.Invoke(); });

                    // Request initial state
                    await writer.WriteAsync(
                        "Action: SIPpeers\r\nActionID: WVMON_PEERS\r\n\r\n" +
                        "Action: PJSIPShowEndpoints\r\nActionID: WVMON_PJSIP\r\n\r\n" +
                        "Action: CoreShowChannels\r\nActionID: WVMON_CHAN\r\n\r\n").ConfigureAwait(false);
                    peersRequested = true;
                    continue;
                }

                // ── Pong (Ping response) ──────────────────────────────────────
                if (bloco.TryGetValue("Response", out var r2) &&
                    r2.Equals("Success", StringComparison.OrdinalIgnoreCase) &&
                    bloco.TryGetValue("Ping", out _))
                    continue;

                if (!loginOk || !peersRequested) continue;

                // ── Skip our own action response headers ──────────────────────
                if (bloco.TryGetValue("ActionID", out var actId) &&
                    (actId.StartsWith("WVMON_", StringComparison.OrdinalIgnoreCase)))
                    continue;

                ProcessarBloco(bloco);
            }

            pingSrc.Cancel();
        }

        private static async Task PingLoopAsync(StreamWriter writer, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(30_000, ct).ConfigureAwait(false);
                    if (!ct.IsCancellationRequested)
                        await writer.WriteAsync("Action: Ping\r\nActionID: WVMON_PING\r\n\r\n").ConfigureAwait(false);
                }
            }
            catch { }
        }

        // ── Block reader ──────────────────────────────────────────────────────
        private static async Task<Dictionary<string, string>?> LerBlocoAsync(StreamReader reader, CancellationToken ct)
        {
            var d = new Dictionary<string, string>(8, StringComparer.OrdinalIgnoreCase);
            while (!ct.IsCancellationRequested)
            {
                string? linha;
                try { linha = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
                catch { return null; }
                if (linha is null) return null;
                if (linha.Length == 0) return d;
                int col = linha.IndexOf(':');
                if (col > 0)
                    d[linha[..col].Trim()] = linha[(col + 1)..].Trim();
            }
            return null;
        }

        // ── Event dispatcher ──────────────────────────────────────────────────
        private void ProcessarBloco(Dictionary<string, string> e)
        {
            if (!e.TryGetValue("Event", out var evt)) return;
            switch (evt)
            {
                case "PeerEntry":         ProcessarPeerEntry(e);      break;
                case "PeerStatus":        ProcessarPeerStatus(e);     break;
                case "ContactStatus":     ProcessarContactStatus(e);  break;
                case "EndpointList":      ProcessarEndpointList(e);   break;
                case "Newchannel":        ProcessarNewchannel(e);     break;
                case "Newstate":          ProcessarNewstate(e);       break;
                case "DialBegin":         ProcessarDialBegin(e);      break;
                case "BridgeEnter":       ProcessarBridgeEnter(e);    break;
                case "BridgeLeave":       ProcessarBridgeLeave(e);    break;
                case "Hangup":            ProcessarHangup(e);         break;
                case "CoreShowChannel":   ProcessarCoreShowChannel(e); break;
            }
        }

        // ── SIPpeers initial state ────────────────────────────────────────────
        private void ProcessarPeerEntry(Dictionary<string, string> e)
        {
            var ramal = SomenteDigitos(
                e.GetValueOrDefault("ObjectName") ??
                e.GetValueOrDefault("Peer") ?? string.Empty);
            if (!EhRamalValido(ramal)) return;

            e.TryGetValue("Status", out var statusRaw);
            var status = MapearStatusPeer(statusRaw ?? string.Empty);
            var nome = e.GetValueOrDefault("Description") ?? e.GetValueOrDefault("Callerid") ?? string.Empty;
            nome = LimparNome(nome);

            EnsureRamal(ramal, nome, status);
        }

        // ── Real-time peer status ─────────────────────────────────────────────
        private void ProcessarPeerStatus(Dictionary<string, string> e)
        {
            var peer = e.GetValueOrDefault("Peer") ?? string.Empty;
            var ramal = SomenteDigitos(peer.Contains('/') ? peer.Split('/')[1] : peer);
            if (!EhRamalValido(ramal)) return;

            e.TryGetValue("PeerStatus", out var ps);
            StatusRamal status;
            if (ps == null) return;
            status = ps switch
            {
                "Registered"   => StatusRamal.Online,
                "Reachable"    => StatusRamal.Online,
                "Unregistered" => StatusRamal.Offline,
                "Unreachable"  => StatusRamal.Offline,
                "Lagged"       => StatusRamal.Online,
                _              => StatusRamal.Desconhecido
            };
            if (status == StatusRamal.Desconhecido) return;

            var item = EnsureRamal(ramal, string.Empty, status);
            // Only change status if ramal is not currently in a call
            if (!item.EstaEmLigacao)
                DispatchItemUpdate(item, status, null, null, null, null);
        }

        // ── PJSIP contact status ──────────────────────────────────────────────
        private void ProcessarContactStatus(Dictionary<string, string> e)
        {
            var uri = e.GetValueOrDefault("URI") ?? string.Empty;
            // sip:101@host or sip:101
            var m = Regex.Match(uri, @"sip:(\d{2,6})[@;]");
            if (!m.Success) m = Regex.Match(uri, @"sip:(\d{2,6})$");
            if (!m.Success) return;
            var ramal = m.Groups[1].Value;

            e.TryGetValue("ContactStatus", out var cs);
            if (cs == null) return;
            var status = cs switch
            {
                "Reachable"    => StatusRamal.Online,
                "NonReachable" => StatusRamal.Offline,
                "Removed"      => StatusRamal.Offline,
                "Created"      => StatusRamal.Online,
                _              => StatusRamal.Desconhecido
            };
            if (status == StatusRamal.Desconhecido) return;

            var item = EnsureRamal(ramal, string.Empty, status);
            if (!item.EstaEmLigacao)
                DispatchItemUpdate(item, status, null, null, null, null);
        }

        // ── PJSIP endpoint list ───────────────────────────────────────────────
        private void ProcessarEndpointList(Dictionary<string, string> e)
        {
            var objectName = SomenteDigitos(e.GetValueOrDefault("ObjectName") ?? string.Empty);
            if (!EhRamalValido(objectName)) return;

            e.TryGetValue("DeviceState", out var ds);
            var status = ds switch
            {
                "Not in use" => StatusRamal.Online,
                "Unavailable" => StatusRamal.Offline,
                "In use" => StatusRamal.EmLigacao,
                "Ringing" => StatusRamal.Tocando,
                _ => StatusRamal.Desconhecido
            };
            EnsureRamal(objectName, string.Empty, status == StatusRamal.Desconhecido ? StatusRamal.Desconhecido : status);
        }

        // ── New channel ───────────────────────────────────────────────────────
        private void ProcessarNewchannel(Dictionary<string, string> e)
        {
            var channel = e.GetValueOrDefault("Channel") ?? string.Empty;
            var ramal = RamalDoCanal(channel);
            if (string.IsNullOrWhiteSpace(ramal)) ramal = SomenteDigitos(e.GetValueOrDefault("CallerIDNum") ?? string.Empty);
            if (!EhRamalValido(ramal)) return;

            if (!int.TryParse(e.GetValueOrDefault("ChannelState") ?? "0", out var state)) state = 0;
            var callerNum = e.GetValueOrDefault("CallerIDNum") ?? string.Empty;
            var exten = e.GetValueOrDefault("Exten") ?? string.Empty;
            var isInbound = !string.Equals(callerNum, ramal, StringComparison.OrdinalIgnoreCase);

            var info = new ChannelInfo
            {
                Channel = channel,
                Ramal = ramal,
                State = state,
                IsInbound = isInbound,
                RemoteNum = isInbound ? callerNum : exten
            };
            lock (_channels) _channels[channel] = info;

            var item = EnsureRamal(ramal, string.Empty, StatusRamal.Desconhecido);
            if (!item.EstaEmLigacao && state >= 4)
            {
                var newStatus = isInbound ? StatusRamal.Tocando : StatusRamal.Chamando;
                DispatchItemUpdate(item, newStatus, info.RemoteNum, null, isInbound, null);
            }
        }

        // ── Channel state change ──────────────────────────────────────────────
        private void ProcessarNewstate(Dictionary<string, string> e)
        {
            var channel = e.GetValueOrDefault("Channel") ?? string.Empty;
            if (!int.TryParse(e.GetValueOrDefault("ChannelState") ?? "0", out var state)) state = 0;
            var connectedNum = e.GetValueOrDefault("ConnectedLineNum") ?? string.Empty;
            var connectedName = e.GetValueOrDefault("ConnectedLineName") ?? string.Empty;

            ChannelInfo? info;
            lock (_channels)
            {
                if (!_channels.TryGetValue(channel, out info)) return;
                info.State = state;
                if (!string.IsNullOrWhiteSpace(connectedNum) && connectedNum != "<unknown>")
                    info.RemoteNum = connectedNum;
                if (!string.IsNullOrWhiteSpace(connectedName) && connectedName != "<unknown>")
                    info.RemoteName = connectedName;
            }

            var ramal = info.Ramal;
            if (!EhRamalValido(ramal)) return;
            var item = EnsureRamal(ramal, string.Empty, StatusRamal.Desconhecido);

            StatusRamal newStatus;
            DateTime? inicio = null;
            if (state == 6) // Up = active call
            {
                newStatus = StatusRamal.EmLigacao;
                if (item.InicioLigacao == null) inicio = DateTime.Now;
            }
            else if (state == 5) // Ringing
                newStatus = info.IsInbound ? StatusRamal.Tocando : StatusRamal.Chamando;
            else if (state >= 4)
                newStatus = info.IsInbound ? StatusRamal.Tocando : StatusRamal.Chamando;
            else
                return;

            var remoteNum  = !string.IsNullOrWhiteSpace(info.RemoteNum) && info.RemoteNum != "<unknown>" ? info.RemoteNum : null;
            var remoteName = !string.IsNullOrWhiteSpace(info.RemoteName) && info.RemoteName != "<unknown>" ? info.RemoteName : null;
            DispatchItemUpdate(item, newStatus, remoteNum, remoteName, info.IsInbound, inicio ?? item.InicioLigacao);
        }

        // ── Dial begin ────────────────────────────────────────────────────────
        private void ProcessarDialBegin(Dictionary<string, string> e)
        {
            var callerCh  = e.GetValueOrDefault("Channel") ?? string.Empty;
            var destCh    = e.GetValueOrDefault("DestChannel") ?? string.Empty;
            var callerNum = e.GetValueOrDefault("CallerIDNum") ?? string.Empty;
            var destNum   = e.GetValueOrDefault("DestCallerIDNum") ?? e.GetValueOrDefault("DestExten") ?? string.Empty;

            var ramalCaller = RamalDoCanal(callerCh);
            if (string.IsNullOrWhiteSpace(ramalCaller)) ramalCaller = SomenteDigitos(callerNum);

            var ramalDest = RamalDoCanal(destCh);
            if (string.IsNullOrWhiteSpace(ramalDest)) ramalDest = SomenteDigitos(destNum);

            ChannelInfo? infoCaller;
            lock (_channels)
            {
                if (!_channels.TryGetValue(callerCh, out infoCaller))
                {
                    infoCaller = new ChannelInfo { Channel = callerCh, Ramal = ramalCaller, IsInbound = false };
                    _channels[callerCh] = infoCaller;
                }
                infoCaller.RemoteNum = destNum;
            }

            if (EhRamalValido(ramalCaller))
            {
                var item = EnsureRamal(ramalCaller, string.Empty, StatusRamal.Desconhecido);
                if (!item.EstaEmLigacao)
                    DispatchItemUpdate(item, StatusRamal.Chamando, destNum, null, false, null);
            }

            // Callee side — mark as Tocando
            if (EhRamalValido(ramalDest))
            {
                ChannelInfo? infoDest;
                lock (_channels)
                {
                    if (!_channels.TryGetValue(destCh, out infoDest))
                    {
                        infoDest = new ChannelInfo { Channel = destCh, Ramal = ramalDest, IsInbound = true };
                        _channels[destCh] = infoDest;
                    }
                    infoDest.RemoteNum  = callerNum;
                    infoDest.RemoteName = e.GetValueOrDefault("CallerIDName") ?? string.Empty;
                }

                var itemDest = EnsureRamal(ramalDest, string.Empty, StatusRamal.Desconhecido);
                if (!itemDest.EstaEmLigacao)
                    DispatchItemUpdate(itemDest, StatusRamal.Tocando, callerNum, infoDest.RemoteName, true, null);
            }
        }

        // ── Bridge enter ──────────────────────────────────────────────────────
        private void ProcessarBridgeEnter(Dictionary<string, string> e)
        {
            var channel   = e.GetValueOrDefault("Channel") ?? string.Empty;
            var bridgeId  = e.GetValueOrDefault("BridgeUniqueid") ?? string.Empty;
            var connNum   = e.GetValueOrDefault("ConnectedLineNum") ?? string.Empty;
            var connName  = e.GetValueOrDefault("ConnectedLineName") ?? string.Empty;

            ChannelInfo? info;
            lock (_channels)
            {
                if (!_channels.TryGetValue(channel, out info)) return;
                info.BridgeId = bridgeId;
                if (!string.IsNullOrWhiteSpace(connNum) && connNum != "<unknown>") info.RemoteNum  = connNum;
                if (!string.IsNullOrWhiteSpace(connName) && connName != "<unknown>") info.RemoteName = connName;

                // Link bridge
                if (!string.IsNullOrWhiteSpace(bridgeId))
                {
                    if (_bridges.TryGetValue(bridgeId, out var br))
                        _bridges[bridgeId] = (br.Ch1, channel);
                    else
                        _bridges[bridgeId] = (channel, string.Empty);
                }
            }

            var ramal = info.Ramal;
            if (!EhRamalValido(ramal)) return;
            var item = EnsureRamal(ramal, string.Empty, StatusRamal.Desconhecido);
            var inicio = item.EstaEmLigacao ? (DateTime?)null : DateTime.Now;
            DispatchItemUpdate(item, StatusRamal.EmLigacao,
                info.RemoteNum, info.RemoteName, info.IsInbound, inicio ?? item.InicioLigacao);
        }

        // ── Bridge leave ──────────────────────────────────────────────────────
        private void ProcessarBridgeLeave(Dictionary<string, string> e)
        {
            var channel = e.GetValueOrDefault("Channel") ?? string.Empty;
            ChannelInfo? info;
            lock (_channels) _channels.TryGetValue(channel, out info);
            if (info == null) return;

            var ramal = info.Ramal;
            if (!EhRamalValido(ramal)) return;
            var item = EnsureRamal(ramal, string.Empty, StatusRamal.Desconhecido);
            // Don't clear — Hangup will follow shortly
        }

        // ── Hangup ────────────────────────────────────────────────────────────
        private void ProcessarHangup(Dictionary<string, string> e)
        {
            var channel = e.GetValueOrDefault("Channel") ?? string.Empty;
            ChannelInfo? info;
            lock (_channels) { _channels.TryGetValue(channel, out info); _channels.Remove(channel); }

            var ramal = info?.Ramal ?? RamalDoCanal(channel);
            if (string.IsNullOrWhiteSpace(ramal)) ramal = SomenteDigitos(e.GetValueOrDefault("CallerIDNum") ?? string.Empty);
            if (!EhRamalValido(ramal)) return;

            // Check if ramal still has other active channels
            bool hasCalls;
            lock (_channels) hasCalls = _channels.Values.Any(c =>
                string.Equals(c.Ramal, ramal, StringComparison.OrdinalIgnoreCase));

            if (!hasCalls)
            {
                // Determine if still registered: keep as Online if was previously Online
                var item = EnsureRamal(ramal, string.Empty, StatusRamal.Desconhecido);
                var backStatus = item.Status == StatusRamal.Offline ? StatusRamal.Offline : StatusRamal.Online;
                DispatchItemUpdate(item, backStatus, null, null, null, null);
            }
        }

        // ── CoreShowChannels initial active calls ─────────────────────────────
        private void ProcessarCoreShowChannel(Dictionary<string, string> e)
        {
            var channel = e.GetValueOrDefault("Channel") ?? string.Empty;
            var ramal = RamalDoCanal(channel);
            if (string.IsNullOrWhiteSpace(ramal))
                ramal = SomenteDigitos(e.GetValueOrDefault("CallerIDNum") ?? string.Empty);
            if (!EhRamalValido(ramal)) return;

            if (!int.TryParse(e.GetValueOrDefault("ChannelState") ?? "0", out var state)) state = 0;
            var connNum  = e.GetValueOrDefault("ConnectedLineNum") ?? string.Empty;
            var connName = e.GetValueOrDefault("ConnectedLineName") ?? string.Empty;
            var callerNum = e.GetValueOrDefault("CallerIDNum") ?? string.Empty;
            var isInbound = !string.Equals(callerNum, ramal, StringComparison.OrdinalIgnoreCase);

            var info = new ChannelInfo
            {
                Channel   = channel,
                Ramal     = ramal,
                State     = state,
                IsInbound = isInbound,
                RemoteNum = connNum != "<unknown>" ? connNum : string.Empty,
                RemoteName = connName != "<unknown>" ? connName : string.Empty
            };
            lock (_channels) _channels.TryAdd(channel, info);

            StatusRamal status;
            DateTime? inicio = null;
            if (state == 6) { status = StatusRamal.EmLigacao; inicio = DateTime.Now.AddSeconds(-30); }
            else if (state >= 4) status = isInbound ? StatusRamal.Tocando : StatusRamal.Chamando;
            else return;

            var item = EnsureRamal(ramal, string.Empty, StatusRamal.Desconhecido);
            DispatchItemUpdate(item, status, info.RemoteNum, info.RemoteName, isInbound, inicio);
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private RamalStatusItem EnsureRamal(string ramal, string nome, StatusRamal status)
        {
            lock (_ramalState)
            {
                if (_ramalState.TryGetValue(ramal, out var existing))
                {
                    if (!string.IsNullOrWhiteSpace(nome) && string.IsNullOrWhiteSpace(existing.Nome))
                        Dispatch(() => existing.Nome = nome);
                    return existing;
                }
            }

            // v2.4.0 — investigação de desempenho: a resolução de nome (ResolverNomePorNumero pode
            // ler contatos.json do disco em cache-miss, a cada ~300s) rodava DENTRO do lock acima,
            // serializando o processamento de TODOS os eventos AMI atrás dessa leitura sempre que
            // um ramal novo aparecia no meio de uma rajada (ex.: reconexão, CoreShowChannels
            // listando todos os canais de uma vez). Agora a resolução roda FORA do lock — o lock
            // volta a proteger só a leitura/inserção do dicionário em memória.
            var resolvedName = nome;
            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                try { resolvedName = ContatoStorageService.ResolverNomePorNumero(ramal); }
                catch { }
                if (string.Equals(resolvedName, ramal, StringComparison.OrdinalIgnoreCase))
                    resolvedName = ramal;
            }

            lock (_ramalState)
            {
                // outra thread pode ter inserido este ramal enquanto o nome era resolvido acima.
                if (_ramalState.TryGetValue(ramal, out var existing))
                {
                    if (!string.IsNullOrWhiteSpace(nome) && string.IsNullOrWhiteSpace(existing.Nome))
                        Dispatch(() => existing.Nome = nome);
                    return existing;
                }

                var item = new RamalStatusItem { Ramal = ramal, Nome = resolvedName };
                if (status != StatusRamal.Desconhecido) item.Status = status;
                _ramalState[ramal] = item;
                Dispatch(() => Ramais.Add(item));
                return item;
            }
        }

        private void DispatchItemUpdate(RamalStatusItem item, StatusRamal status,
            string? numRemoto, string? nomeRemoto, bool? direcaoEntrada, DateTime? inicioLigacao)
        {
            Dispatch(() =>
            {
                if (status != StatusRamal.Desconhecido) item.Status = status;
                if (numRemoto != null)    item.NumeroRemoto   = numRemoto;
                if (nomeRemoto != null)   item.NomeRemoto     = nomeRemoto;
                if (direcaoEntrada != null) item.DirecaoEntrada = direcaoEntrada.Value;
                if (inicioLigacao != null) item.InicioLigacao = inicioLigacao;

                // Clear call info when going back to idle
                if (status == StatusRamal.Online || status == StatusRamal.Offline)
                {
                    item.NumeroRemoto  = string.Empty;
                    item.NomeRemoto    = string.Empty;
                    item.LinhaUsada    = string.Empty;
                    item.NomeCanal     = string.Empty;
                    item.TipoChamada   = string.Empty;
                    item.InicioLigacao = null;
                }

                EstadoChanged?.Invoke();
            });
        }

        private static void Dispatch(Action a)
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null || disp.CheckAccess())
                a();
            else
                disp.BeginInvoke(a, DispatcherPriority.Background);
        }

        private static string RamalDoCanal(string channel)
        {
            var m = Regex.Match(channel, @"(?:SIP|PJSIP)/(\d{2,6})-", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        private static StatusRamal MapearStatusPeer(string raw)
        {
            if (raw.StartsWith("OK", StringComparison.OrdinalIgnoreCase)) return StatusRamal.Online;
            if (raw.Equals("UNREACHABLE", StringComparison.OrdinalIgnoreCase)) return StatusRamal.Offline;
            if (raw.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)) return StatusRamal.Desconhecido;
            if (raw.Equals("Unmonitored", StringComparison.OrdinalIgnoreCase)) return StatusRamal.Desconhecido;
            return StatusRamal.Desconhecido;
        }

        private static string LimparNome(string nome)
        {
            nome = (nome ?? string.Empty).Trim().Trim('"');
            var m = Regex.Match(nome, "\"([^\"]+)\"");
            if (m.Success) nome = m.Groups[1].Value;
            nome = Regex.Replace(nome, @"<[^>]+>", string.Empty).Trim();
            return nome;
        }

        private static string SomenteDigitos(string s)
            => new string((s ?? string.Empty).Where(char.IsDigit).ToArray());

        private static bool EhRamalValido(string r)
            => !string.IsNullOrWhiteSpace(r) && r.All(char.IsDigit) && r.Length >= 2 && r.Length <= 6;
    }
}
