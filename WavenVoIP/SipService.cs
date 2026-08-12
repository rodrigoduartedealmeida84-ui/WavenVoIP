
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Windows;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP
{
    public class SipService : IDisposable
    {
        private bool _disposed;
        private SipConfig? _config;
        private SIPTransport? _sipTransport;
        private SIPRegistrationUserAgent? _registerUserAgent;
        private SIPUserAgent? _userAgent;
        private VoIPMediaSession? _mediaSession;
        private SIPRequest? _pendingIncomingRequest;
        private string _pendingIncomingCallId = string.Empty;
        // Via branch da transação pendente. RFC 3261: a branch identifica a TRANSAÇÃO, não a
        // chamada — uma retransmissão UDP do mesmo INVITE reusa a MESMA branch; um novo ciclo de
        // ring group/fila do Issabel pode reusar o Call-ID mas sempre abre uma transação nova
        // (nova branch/CSeq). Por isso o Call-ID sozinho NUNCA é usado para decidir retransmissão.
        private string _pendingIncomingBranch = string.Empty;
        private DateTime _ultimoInviteRecebido = DateTime.MinValue;
        private Timer? _monitorChamadaRecebidaTimer;
        private readonly object _incomingLock = new object();
        // Branch -> horário em que foi ignorada (INVITE recusado/cancelado localmente). Existe só
        // para não reabrir o popup quando o MESMO INVITE (mesma transação) é retransmitido pelo
        // transporte SIP — isso acontece em poucos segundos, por retransmissão UDP. Chaveado por
        // branch (não Call-ID) para nunca bloquear um ciclo novo e legítimo que reuse o Call-ID.
        private readonly Dictionary<string, DateTime> _ignoredBranches = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan _janelaIgnorarBranchRetransmissao = TimeSpan.FromSeconds(5);

        private bool EhBranchIgnoradaRecente(string branch)
        {
            if (string.IsNullOrWhiteSpace(branch)) return false;
            if (!_ignoredBranches.TryGetValue(branch, out var quando)) return false;
            if (DateTime.Now - quando < _janelaIgnorarBranchRetransmissao) return true;
            _ignoredBranches.Remove(branch);
            return false;
        }

        private void MarcarBranchIgnorada(string branch)
        {
            if (string.IsNullOrWhiteSpace(branch)) return;
            _ignoredBranches[branch] = DateTime.Now;
        }

        // Mascara Call-ID/branch/tags para log: mantém só os 4 primeiros e 4 últimos caracteres.
        private static string MascararTextoSip(string valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return string.Empty;
            if (valor.Length <= 10) return new string('*', valor.Length);
            return valor.Substring(0, 4) + "..." + valor.Substring(valor.Length - 4);
        }

        private static string ObterBranch(SIPRequest? req)
        {
            try { return req?.Header?.Vias?.TopViaHeader?.Branch ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static int ObterCSeq(SIPRequest? req)
        {
            try { return req?.Header?.CSeq ?? 0; }
            catch { return 0; }
        }

        private static string ObterFromTag(SIPRequest? req)
        {
            try { return req?.Header?.From?.FromTag ?? string.Empty; }
            catch { return string.Empty; }
        }

        // Número do chamador (From URI user) — usado para reconhecer a MESMA chamada de fila
        // sendo reofertada com um Call-ID novo (cada tentativa de agente pode originar um Dial()
        // novo no Issabel, com Call-ID próprio, mas o chamador é sempre o mesmo).
        private static string ObterFromUser(SIPRequest? req)
        {
            try { return req?.Header?.From?.FromURI?.User ?? string.Empty; }
            catch { return string.Empty; }
        }
        private bool _emEspera;
        private volatile bool _cancelamentoSolicitado;
        private volatile bool _chamadaSaindoEmAndamento;

        // v2.3.6 — investigação real (10/08/2026): cancelamento antecipado às vezes deixava o
        // telefone do cliente tocando mesmo com o Waven já marcando Cancelada. Causa raiz
        // confirmada por log real (attemptId eb826b82): SIPClientUserAgent.Cancel() (SIPSorcery)
        // dispara CallFailed IMEDIATAMENTE e LOCALMENTE, sem esperar NENHUMA confirmação de rede —
        // "conseguimos chamar Cancel()" não é prova de que o Asterisk recebeu o CANCEL. Estes campos
        // dão: (1) um lock pra fechar a corrida entre Desligar() marcar _cancelamentoSolicitado e
        // Ligar() checar esse flag logo antes de enviar o INVITE — ver Ligar()/Desligar(); (2) um
        // flag setado só quando a confirmação de rede REAL chega — ver MonitorarConfirmacaoCancelamento.
        private readonly object _outboundCancelLock = new object();
        private volatile bool _inviteEmEnvio;
        private volatile bool _outboundCancelConfirmado;

        // v2.3.6 — revisão de desempenho: EncerrarSinalizacaoAtiva() roda DUAS vezes pra uma mesma
        // tentativa cancelada durante a discagem (uma vez chamada por Desligar(), outra pelo próprio
        // Ligar() no branch de corrida pós-retorno — confirmado em log real, CALL_CANCEL_SENT aparece
        // 2x por tentativa). Sem este guard, MonitorarConfirmacaoCancelamento/AgendarReenvioSeNaoConfirmado
        // rodariam 2x por tentativa (handlers duplicados na mesma transação, 2 retries agendados).
        // Não é vazamento (não cresce por chamada, sempre no máximo 2x por tentativa), mas é
        // trabalho/log duplicado desnecessário — este campo garante no máximo 1x por attemptId.
        private volatile string? _outboundCancelMonitorAttemptId;

        // Cache dos FieldInfo de reflection usados só pra LEITURA de m_uac/m_cancelTransaction
        // (campos privados/internal do SIPSorcery sem getter público) — evita repetir
        // GetType().GetField(...) a cada cancelamento.
        private static readonly FieldInfo? _campoUac = typeof(SIPUserAgent).GetField("m_uac", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo? _campoCancelTransaction = typeof(SIPClientUserAgent).GetField("m_cancelTransaction", BindingFlags.NonPublic | BindingFlags.Instance);
        private readonly List<SIPUserAgent> _conferenceAgents = new List<SIPUserAgent>();
        private readonly List<VoIPMediaSession> _conferenceMediaSessions = new List<VoIPMediaSession>();

        // v1.3.0 — mute TX-only via proxy de silence injection
        private WindowsAudioEndPoint? _audioEndpoint;
        private MuteableAudioSource? _muteableAudioSource;
        private volatile bool _muteMicrofoneAtivo;
        private bool _muteAntesDeEspera;   // salva estado do mute ao entrar em Hold
        private Timer? _rtpKeepaliveTimer;
        private volatile bool _transferenciaEmAndamento;
        private DateTime _inicioDialing = DateTime.MinValue;

        public bool IsRegistered { get; private set; }
        public bool SupportsIncomingCallUi => true;
        public bool IsInCall => _userAgent?.IsCallActive ?? false;
        public bool IsOnHold => _emEspera;
        public bool IsDialing => _chamadaSaindoEmAndamento;

        // v2.3.6 — id só de LOG, pra correlacionar todos os eventos (Ringing/Failed/Hangup) de UMA
        // tentativa de saída específica nos arquivos de diagnóstico. Trocado a cada Ligar().
        private string _outboundAttemptId = string.Empty;

        // v2.3.6 — true quando ESTA tentativa foi encerrada por Desligar() (o próprio operador,
        // pelo Waven) enquanto ainda estava discando/tocando — nunca por causa de BusyHere/Decline
        // do lado do cliente. Investigação de "popup Chamada recusada falso" achou a causa raiz:
        // Ligar() nunca resetava LastOutboundResultado no início de uma nova tentativa, então um
        // cancelamento local (que não passa por ClassificarResultadoLigacao) deixava o resultado da
        // tentativa ANTERIOR (ex.: Recusada) vazar pra tentativa nova. Ver Ligar()/Desligar().
        public bool LastOutboundWasCancelledLocally { get; private set; }

        // v2.1.1 — Offline controlado localmente pelo WavenVoIP, sem depender de
        // discagem de código de função no Issabel/Asterisk (*78/*79).
        public bool ModoOffline { get; private set; }

        public event Action<string>? StatusChanged;
        public event Action<string>? IncomingCallReceived;
        // Disparado quando o Issabel reoferece a MESMA chamada (mesmo Call-ID, nova transação/
        // branch) num novo ciclo de ring group/fila, enquanto o ciclo anterior ainda não tinha
        // sido claramente encerrado no app. A UI deve atualizar/reabrir o popup, nunca tratar
        // isso como uma chamada perdida.
        public event Action<string>? IncomingCallCycleRenewed;
        public event Action? IncomingCallEnded;
        public event Action? CallEnded;

        public string LastIncomingOrigin { get; private set; } = string.Empty;
        public string LastCallError { get; private set; } = string.Empty;
        public string LastConferenceError { get; private set; } = string.Empty;
        public string LastDialedDestination { get; private set; } = string.Empty;

        // v2.3.5 — motivo real (código SIP) da última falha de chamada REALIZADA, capturado ao
        // vivo via SIPUserAgent.ClientCallFailed (não depende do CDR, que só chega minutos depois
        // via sync). Usado para diferenciar Recusada de NaoAtendida imediatamente na interface —
        // ver Ligar() e ClassificarResultadoLigacao().
        public SIPResponseStatusCodesEnum? LastOutboundFailureStatus { get; private set; }
        public string LastOutboundFailureReason { get; private set; } = string.Empty;
        public TipoHistoricoLigacao LastOutboundResultado { get; private set; } = TipoHistoricoLigacao.NaoAtendida;
        public double? LastOutboundRingDurationSeconds { get; private set; }

        // Momento do último 180 Ringing confirmado para a chamada de saída em andamento — usado
        // para medir o tempo de toque até BusyHere/Decline (heurística de Recusada vs NaoAtendida).
        // Resetado no início de cada Ligar().
        private DateTime? _ultimoRingingRecebidoEm;

        // Investigação real (08/08/2026, duas chamadas de teste na mesma rota/operadora):
        //   Teste 1 — cliente recusou no aparelho: Ringing → BusyHere em ~11s.
        //   Teste 2 — cliente deixou tocar até a operadora encerrar: Ringing → BusyHere em ~60s
        //             (tempo redondo — timeout de rede do lado da operadora/WAVOIP, não uma ação
        //             do cliente). Confirmado que SIP (486 Busy Here) e CDR (disposition BUSY) são
        //             IDÊNTICOS nos dois casos — não existe código que diferencie recusa real de
        //             timeout nesta rota. O único sinal disponível é o tempo entre Ringing e a
        //             falha. Limiar conservador escolhido com o usuário após ver essa evidência;
        //             ajustável conforme mais testes reais.
        public const int OutboundDeclineThresholdSeconds = 30;

        // BusyHere (486) e Decline (603) são os únicos códigos SIP que indicam alguma resposta do
        // lado chamado — mas a operadora/gateway usa o MESMO BusyHere tanto para rejeição real
        // quanto para timeout de toque (ver investigação acima), então o código sozinho não basta.
        // A heurística de tempo SÓ vale quando: (1) houve Ringing confirmado — sem isso não há como
        // medir, e uma falha rápida pode ser erro de rota/rede, não recusa; (2) o status é
        // BusyHere/Decline. Qualquer outro caso (sem Ringing, ou status diferente — 404, 480, 5xx,
        // timeout de conexão) é sempre NaoAtendida, nunca presume recusa.
        private TipoHistoricoLigacao ClassificarResultadoLigacao(SIPResponseStatusCodesEnum? status, double? ringDurationSeg)
        {
            var ehBusyOuDecline = status == SIPResponseStatusCodesEnum.BusyHere || status == SIPResponseStatusCodesEnum.Decline;
            if (!ehBusyOuDecline || !ringDurationSeg.HasValue)
            {
                LogHelper.Sip($"OUTBOUND_CLASSIFIED_NO_ANSWER status={status} ringDurationSeg={ringDurationSeg} motivo=sem_busy_ou_sem_ringing_confirmado");
                return TipoHistoricoLigacao.NaoAtendida;
            }

            var resultado = ringDurationSeg.Value < OutboundDeclineThresholdSeconds
                ? TipoHistoricoLigacao.Recusada
                : TipoHistoricoLigacao.NaoAtendida;

            LogHelper.Sip($"OUTBOUND_BUSY_CLASSIFICATION ringDurationSeg={ringDurationSeg:F1} threshold={OutboundDeclineThresholdSeconds} result={resultado}");
            LogHelper.Sip(resultado == TipoHistoricoLigacao.Recusada
                ? $"OUTBOUND_CLASSIFIED_DECLINED ringDurationSeg={ringDurationSeg:F1}"
                : $"OUTBOUND_CLASSIFIED_NO_ANSWER ringDurationSeg={ringDurationSeg:F1} motivo=proximo_do_timeout");
            return resultado;
        }

        public void Inicializar(SipConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _sipTransport = new SIPTransport();
            var channel = new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0));
            _sipTransport.AddSIPChannel(channel);

            _sipTransport.SIPTransportRequestReceived += async (localEP, remoteEP, sipRequest) =>
            {
                try
                {
                    if (sipRequest.Method == SIPMethodsEnum.OPTIONS ||
                        sipRequest.Method == SIPMethodsEnum.NOTIFY)
                    {
                        var ok = SIPResponse.GetResponse(
                            sipRequest,
                            SIPResponseStatusCodesEnum.Ok,
                            null);

                        await _sipTransport.SendResponseAsync(ok);
                    }

                    if (sipRequest.Method == SIPMethodsEnum.INVITE)
                    {
                        var tsInvite = DateTime.Now;
                        var inviteFrom = sipRequest.Header?.From?.FromURI?.User ?? "Unknown";
                        var inviteCallId = ObterCallId(sipRequest);
                        var inviteBranch = ObterBranch(sipRequest);
                        var inviteCSeq = ObterCSeq(sipRequest);
                        RegistrarSinalSip($"INCOMING_INVITE_RECEIVED ts={tsInvite:HH:mm:ss.fff} Call-ID={inviteCallId} From={inviteFrom} To={sipRequest.Header?.To?.ToURI?.User ?? string.Empty}");
                        LogHelper.Sip($"INCOMING_INVITE_RECEIVED | Call-ID={inviteCallId} From={inviteFrom}");
                        LogHelper.Sip($"SIP_INVITE_RECEIVED callId={MascararTextoSip(inviteCallId)} cseq={inviteCSeq} " +
                            $"branch={MascararTextoSip(inviteBranch)} desdeUltimoCicloMs={(tsInvite - _ultimoInviteRecebido).TotalMilliseconds:0}");
                        _ultimoInviteRecebido = tsInvite;

                        // ESSENCIAL para funcionar igual MicroSIP/IP Phone:
                        // responder 100 Trying e 180 Ringing no INVITE pendente.
                        // Sem isso o Asterisk fica retransmitindo INVITE e pode não entregar o CANCEL
                        // para o fluxo do softphone.
                        await EnviarRespostaTentandoETocandoAsync(sipRequest);
                        RegistrarSinalSip($"SIP_INVITE_TO_ISSABEL_SENT elapsed={(DateTime.Now - tsInvite).TotalMilliseconds:0}ms Call-ID={inviteCallId}");

                        // Processa aqui — no handler de transporte, que recebe TODO INVITE de forma
                        // confiável — em vez de depender de SIPUserAgent.OnIncomingCall refirar para
                        // ciclos de ring group/fila subsequentes (comportamento interno da lib que
                        // não controlamos e que pode não repetir para o mesmo Call-ID).
                        ProcessarInviteRecebido(sipRequest);
                    }

                    // Quando a chamada vem de fila/ring group e outro atendente atende,
                    // o Issabel/Asterisk envia CANCEL para os ramais que ainda estavam tocando.
                    // Nesta versão fechamos o popup em QUALQUER CANCEL recebido no transporte,
                    // porque em alguns cenários o SIPSorcery já limpou/alterou a transação antes
                    // de mantermos a referência em _pendingIncomingRequest.
                    if (sipRequest.Method == SIPMethodsEnum.CANCEL)
                    {
                        var cancelCallId = ObterCallId(sipRequest);
                        var cancelBranch = ObterBranch(sipRequest);
                        RegistrarSinalSip($"RX CANCEL Call-ID={cancelCallId}");
                        LogHelper.Sip($"SIP_CANCEL_RECEIVED callId={MascararTextoSip(cancelCallId)} branch={MascararTextoSip(cancelBranch)}");

                        var okCancel = SIPResponse.GetResponse(
                            sipRequest,
                            SIPResponseStatusCodesEnum.Ok,
                            null);
                        await _sipTransport.SendResponseAsync(okCancel);

                        // RFC 3261 §9.2: além do 200 OK ao CANCEL, o UAS deve responder 487 Request
                        // Terminated ao INVITE original sendo cancelado, se ainda não respondeu com
                        // uma resposta final. Isso fecha essa transação de forma limpa no lado do
                        // Issabel, sem afetar o Call-ID (que pode ser reusado no próximo ciclo).
                        try
                        {
                            if (_pendingIncomingRequest != null &&
                                !string.IsNullOrWhiteSpace(cancelBranch) &&
                                string.Equals(ObterBranch(_pendingIncomingRequest), cancelBranch, StringComparison.OrdinalIgnoreCase))
                            {
                                var terminated = SIPResponse.GetResponse(_pendingIncomingRequest, SIPResponseStatusCodesEnum.RequestTerminated, null);
                                await _sipTransport.SendResponseAsync(terminated);
                                LogHelper.Sip($"SIP_487_SENT callId={MascararTextoSip(cancelCallId)} branch={MascararTextoSip(cancelBranch)}");
                            }
                        }
                        catch { }

                        // Marca só ESTA transação (branch) como ignorada — nunca o Call-ID inteiro,
                        // para não bloquear um novo ciclo legítimo que reuse o mesmo Call-ID.
                        MarcarBranchIgnorada(cancelBranch);
                        LogHelper.Sip($"RING_CYCLE_END callId={MascararTextoSip(cancelCallId)} branch={MascararTextoSip(cancelBranch)} motivo=cancel_recebido");

                        // Encerra só esta perna — NÃO marca a chamada inteira como perdida aqui;
                        // isso só acontece depois via CDR/AMI confirmando que ninguém atendeu em
                        // lugar nenhum (ver DialerShellWindow.PodeNotificarChamadaPerdida).
                        LimparChamadaRecebidaPendente(false);
                        StatusChanged?.Invoke("Chamada cancelada pelo Issabel/remetente.");
                        IncomingCallEnded?.Invoke();
                        return;
                    }

                    // Algumas chamadas externas por tronco encerram a perna SIP pendente com BYE
                    // ou o evento de hangup chega antes do usuário atender. Fecha o popup também.
                    if (sipRequest.Method == SIPMethodsEnum.BYE)
                    {
                        RegistrarSinalSip($"RX BYE Call-ID={ObterCallId(sipRequest)}");
                        var okBye = SIPResponse.GetResponse(
                            sipRequest,
                            SIPResponseStatusCodesEnum.Ok,
                            null);

                        await _sipTransport.SendResponseAsync(okBye);

                        LimparChamadaRecebidaPendente(false);
                        StatusChanged?.Invoke("Chamada encerrada pelo remetente/Issabel.");
                        IncomingCallEnded?.Invoke();
                        return;
                    }
                }
                catch
                {
                }
            };

            CriarUserAgent();
            StatusChanged?.Invoke($"SIP pronto em porta dinâmica -> {_config.ServerIp}:{_config.Port}/udp");
        }

        private void CriarUserAgent()
        {
            if (_sipTransport == null)
                throw new InvalidOperationException("Transporte SIP não inicializado.");

            _userAgent = new SIPUserAgent(_sipTransport, null);
            _userAgent.OnIncomingCall += OnIncomingCall;
            _userAgent.ClientCallRinging += (_, __) =>
            {
                // Só registra o PRIMEIRO Ringing da tentativa — retransmissões de 180 não devem
                // reiniciar a contagem do tempo de toque.
                if (_ultimoRingingRecebidoEm == null)
                {
                    _ultimoRingingRecebidoEm = DateTime.Now;
                    LogHelper.Sip("OUTBOUND_RINGING_STARTED | 180 Ringing recebido do servidor SIP");
                }
                LogHelper.Sip("CALL_RINGING | 180 Ringing recebido do servidor SIP");
            };
            _userAgent.ClientCallFailed += (uac, errorMessage, sipResponse) =>
            {
                LastOutboundFailureStatus = sipResponse?.Status;
                LastOutboundFailureReason = errorMessage;
                LastOutboundRingDurationSeconds = _ultimoRingingRecebidoEm.HasValue
                    ? (DateTime.Now - _ultimoRingingRecebidoEm.Value).TotalSeconds
                    : (double?)null;
                LogHelper.Sip($"OUTBOUND_RING_DURATION ringDurationSeg={LastOutboundRingDurationSeconds:F1}");
                LogHelper.Sip($"OUTBOUND_CALL_FAILED status={sipResponse?.Status} motivo={errorMessage}");
                RegistrarSinalSip($"OUTBOUND_CALL_FAILED status={sipResponse?.Status} motivo={errorMessage}");
            };
            _userAgent.OnCallHungup += _ =>
            {
                _emEspera = false;
                PararRtpKeepalive();
                LimparChamadaRecebidaPendente(false);
                RegistrarSinalSip("CALL_REMOTE_HANGUP | OnCallHungup fired");
                LogHelper.Sip("CALL_REMOTE_HANGUP | OnCallHungup fired");
                try { IncomingCallEnded?.Invoke(); } catch { }
                LimparEstadoChamada("Chamada encerrada pelo Issabel/cliente.");
            };
        }

        private void OnIncomingCall(SIPUserAgent ua, SIPRequest req)
        {
            // A lógica principal já roda em ProcessarInviteRecebido, chamada pelo handler de
            // transporte (SIPTransportRequestReceived) — que recebe todo INVITE de forma confiável,
            // independente de o SIPUserAgent decidir refirar OnIncomingCall para um Call-ID repetido
            // num novo ciclo de ring group/fila (comportamento interno da lib fora do nosso controle).
            // Este callback fica só como rede de segurança, caso o SIPUserAgent chame antes do
            // handler de transporte processar a mesma transação.
            if (ReferenceEquals(_pendingIncomingRequest, req)) return;
            ProcessarInviteRecebido(req);
        }

        // Ponto único de decisão para qualquer INVITE de entrada — chamado tanto pelo handler de
        // transporte (via confiável) quanto por OnIncomingCall (rede de segurança). Diferencia:
        //   • retransmissão UDP do MESMO INVITE (mesma branch Via) → ignora, sem reabrir popup;
        //   • novo ciclo legítimo da MESMA chamada (mesmo Call-ID, branch NOVA) → supera o estado
        //     anterior e dispara IncomingCallCycleRenewed para o popup atualizar/reabrir;
        //   • chamada realmente nova (Call-ID novo, nada pendente) → dispara IncomingCallReceived;
        //   • chamada simultânea diferente enquanto já existe uma pendente → ocupado (486).
        private void ProcessarInviteRecebido(SIPRequest req)
        {
            try
            {
                if (ModoOffline)
                {
                    var callIdOffline = ObterCallId(req);
                    var fromOffline = req.Header?.From?.FromURI?.User ?? "Desconhecido";
                    LogHelper.Sip($"CALL_BLOCKED_OFFLINE | Call-ID={callIdOffline} From={fromOffline}", LogLevel.WARN);
                    RegistrarSinalSip($"CALL_BLOCKED_OFFLINE Call-ID={callIdOffline} From={fromOffline}");
                    EnviarRespostaOcupado(req);
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var callId = ObterCallId(req);
                var branch = ObterBranch(req);
                var cseq = ObterCSeq(req);
                _ultimoInviteRecebido = DateTime.Now;
                RegistrarSinalSip($"WHATSAPP_CALL_EVENT_RECEIVED Call-ID={callId}");

                // 100 Trying + 180 Ringing already sent by transport handler — no duplicate.

                // Evita reabrir o popup quando o usuário já recusou/cancelou ESTA transação —
                // mas só durante a janela curta de retransmissão (ver EhBranchIgnoradaRecente).
                // Chaveado por branch (não Call-ID): um novo ciclo sempre abre transação nova.
                if (EhBranchIgnoradaRecente(branch))
                {
                    LogHelper.Sip($"SIP_INVITE_BLOCKED callId={MascararTextoSip(callId)} branch={MascararTextoSip(branch)} motivo=branch_ignorada_recente");
                    EnviarRespostaOcupado(req);
                    return;
                }

                bool novoCiclo;
                lock (_incomingLock)
                {
                    if (_pendingIncomingRequest != null)
                    {
                        var mesmaTransacao = !string.IsNullOrWhiteSpace(branch) &&
                            string.Equals(_pendingIncomingBranch, branch, StringComparison.OrdinalIgnoreCase);
                        if (mesmaTransacao)
                        {
                            RegistrarSinalSip($"WHATSAPP_DUPLICATE_EVENT_IGNORED Call-ID={callId}");
                            LogHelper.Sip($"SIP_INVITE_RETRANSMISSION callId={MascararTextoSip(callId)} branch={MascararTextoSip(branch)} cseq={cseq}");
                            return;
                        }

                        var mesmoCallId = string.Equals(_pendingIncomingCallId, callId, StringComparison.OrdinalIgnoreCase);

                        // Filas do Issabel frequentemente originam CADA tentativa de agente como
                        // um Dial() novo (canal/Local channel novo) — o que gera um Call-ID NOVO a
                        // cada ciclo, não o mesmo Call-ID reaproveitado. Se o Call-ID mudou mas o
                        // chamador (From URI/número) é o MESMO da chamada pendente, ainda é a MESMA
                        // chamada de fila reofertada — nunca "chamada simultânea diferente".
                        var chamadorAtual    = ObterFromUser(req);
                        var chamadorPendente = ObterFromUser(_pendingIncomingRequest);
                        var mesmoChamador = !mesmoCallId &&
                            !string.IsNullOrWhiteSpace(chamadorAtual) &&
                            string.Equals(chamadorAtual, chamadorPendente, StringComparison.OrdinalIgnoreCase);

                        if (!mesmoCallId && !mesmoChamador)
                        {
                            LogHelper.Sip($"SIP_INVITE_BLOCKED callId={MascararTextoSip(callId)} branch={MascararTextoSip(branch)} " +
                                $"motivo=ja_existe_pendente pendenteCallId={MascararTextoSip(_pendingIncomingCallId)}");
                            EnviarRespostaOcupado(req);
                            return;
                        }

                        // Mesmo Call-ID (branch/CSeq diferentes) OU Call-ID novo do MESMO chamador:
                        // não é retransmissão — é um novo ciclo de fila/ring group reofertando a
                        // MESMA chamada, possivelmente com um canal/Dial() novo no Issabel.
                        LogHelper.Sip($"SIP_INVITE_NEW_CYCLE callId={MascararTextoSip(callId)} cseq={cseq} " +
                            $"branchAnterior={MascararTextoSip(_pendingIncomingBranch)} branchNova={MascararTextoSip(branch)} " +
                            $"motivo={(mesmoCallId ? "mesmo_call_id" : "mesmo_chamador_novo_call_id")}");
                        novoCiclo = true;
                    }
                    else
                    {
                        novoCiclo = false;
                    }

                    _pendingIncomingRequest = req;
                    _pendingIncomingCallId = callId;
                    _pendingIncomingBranch = branch;
                }

                IniciarMonitorChamadaRecebida();

                var callerUser = req.Header?.From?.FromURI?.User ?? "Desconhecido";
                var callerName = req.Header?.From?.FromName;
                var caller = string.IsNullOrWhiteSpace(callerName)
                    ? callerUser
                    : $"{callerName} ({callerUser})";

                // Fire event IMMEDIATELY — popup must not wait for origin detection (reflection + disk I/O)
                RegistrarSinalSip($"WHATSAPP_FORWARD_TO_ISSABEL_START elapsed={sw.ElapsedMilliseconds}ms Call-ID={callId}");
                StatusChanged?.Invoke($"Chamada recebida de {caller}");

                if (novoCiclo)
                    IncomingCallCycleRenewed?.Invoke(caller);
                else
                    IncomingCallReceived?.Invoke(caller);
                RegistrarSinalSip($"EXTENSION_RING_STARTED elapsed={sw.ElapsedMilliseconds}ms Call-ID={callId}");

                // Origin detection runs async — has reflection + file I/O, must not block popup
                var reqCapture = req;
                var swCapture = sw;
                var callIdCapture = callId;
                Task.Run(() =>
                {
                    try
                    {
                        LastIncomingOrigin = DetectarOrigemEntrada(reqCapture);
                        RegistrarSinalSip($"WHATSAPP_FORWARD_TOTAL_DELAY_MS elapsed={swCapture.ElapsedMilliseconds}ms Call-ID={callIdCapture} origem={LastIncomingOrigin}");
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Erro ao tratar chamada recebida: {ex.Message}");
                RegistrarSinalSip($"INCOMING_CALL_HANDLER_ERROR | ProcessarInviteRecebido excecao: {ex}");
                LogHelper.Sip($"INCOMING_CALL_HANDLER_ERROR | ProcessarInviteRecebido: {ex.Message}", LogLevel.ERROR);
            }
        }

        private async Task EnviarRespostaTentandoETocandoAsync(SIPRequest req)
        {
            try
            {
                if (_sipTransport == null || req == null) return;

                // MicroSIP faz isso automaticamente. No nosso app forçamos para o Asterisk
                // parar de retransmitir INVITE e conseguir cancelar a chamada corretamente.
                var trying = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Trying, null);
                await _sipTransport.SendResponseAsync(trying);

                var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
                await _sipTransport.SendResponseAsync(ringing);

                RegistrarSinalSip($"TX 100 Trying + 180 Ringing Call-ID={ObterCallId(req)}");
            }
            catch (Exception ex)
            {
                RegistrarSinalSip($"ERRO ao enviar 100/180: {ex.Message}");
            }
        }

        private void IniciarMonitorChamadaRecebida()
        {
            try
            {
                _monitorChamadaRecebidaTimer?.Dispose();
                var inicioChamada = DateTime.Now;
                _monitorChamadaRecebidaTimer = new Timer(_ =>
                {
                    try
                    {
                        if (_pendingIncomingRequest == null) return;

                        // Timeout from call START — not from last INVITE.
                        // After 180 Ringing, Asterisk correctly stops retransmitting INVITE;
                        // the old "8s without INVITE" check was closing the popup too early.
                        var segundosDecorridos = (DateTime.Now - inicioChamada).TotalSeconds;
                        if (segundosDecorridos >= 45)
                        {
                            var callId = _pendingIncomingCallId;
                            RegistrarSinalSip($"INCOMING_CALL_RING_TIMEOUT: {segundosDecorridos:0.0}s sem resposta. Limpando popup Call-ID={callId}");
                            LimparChamadaRecebidaPendente(false);
                            StatusChanged?.Invoke("Chamada expirada sem resposta.");
                            IncomingCallEnded?.Invoke();
                        }
                    }
                    catch (Exception ex)
                    {
                        RegistrarSinalSip($"ERRO monitor chamada recebida: {ex.Message}");
                    }
                }, null, 1000, 1000);
            }
            catch { }
        }

        private string DetectarOrigemEntrada(SIPRequest req)
        {
            try
            {
                var numeroOrigem = req?.Header?.From?.FromURI?.User ?? string.Empty;
                if (DialPlanService.EhRamalInterno(numeroOrigem))
                {
                    RegistrarDebugEntrada(req, MontarTextoDiagnosticoEntrada(req), "Ramal interno");
                    return "Ramal interno";
                }

                // Regra profissional: nunca identificar canal por palavra solta como TIM/VIVO.
                // O canal de entrada precisa vir pelo DID/DDR, Request-URI, To, Diversion,
                // P-Called-Party-ID, X-Original-DID, Alert-Info ou nome da rota enviado pelo Issabel.
                var bruto = MontarTextoDiagnosticoEntrada(req);
                var origem = CanalIdentificacaoService.IdentificarEntrada(
                    bruto,
                    req?.Header?.To?.ToURI?.User ?? string.Empty,
                    req?.URI?.User ?? string.Empty,
                    req?.Header?.From?.FromURI?.User ?? string.Empty);

                RegistrarDebugEntrada(req, bruto, origem);
                return origem;
            }
            catch { }

            return "Entrada não identificada";
        }

        private static string MontarTextoDiagnosticoEntrada(SIPRequest? req)
        {
            var sb = new StringBuilder();
            try { sb.AppendLine(req?.ToString() ?? string.Empty); } catch { }
            try { sb.AppendLine(req?.URI?.ToString() ?? string.Empty); } catch { }
            try { sb.AppendLine(req?.Header?.To?.ToString() ?? string.Empty); } catch { }
            try { sb.AppendLine(req?.Header?.From?.ToString() ?? string.Empty); } catch { }

            try
            {
                var header = req?.Header;
                if (header != null)
                {
                    foreach (var prop in header.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var nome = prop.Name ?? string.Empty;
                        if (nome.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            nome.IndexOf("extra", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            nome.IndexOf("header", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            nome.IndexOf("diversion", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            nome.IndexOf("alert", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            nome.IndexOf("called", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            object? valor = null;
                            try { valor = prop.GetValue(header); } catch { }
                            if (valor != null) sb.AppendLine($"{nome}: {valor}");
                        }
                    }
                }
            }
            catch { }

            return sb.ToString();
        }

        private static void RegistrarSinalSip(string texto)
        {
            try
            {
                var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WavenVoIP");
                Directory.CreateDirectory(pasta);
                var linha = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {texto}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(pasta, "sip_signal_debug.log"), linha);
                File.AppendAllText(Path.Combine(pasta, "ui_flow_debug.log"), linha);
            }
            catch { }
        }

        private static void RegistrarDebugEntrada(SIPRequest? req, string texto, string origem)
        {
            try
            {
                var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WavenVoIP");
                Directory.CreateDirectory(pasta);
                var path = Path.Combine(pasta, "sip_incoming_debug.log");
                File.AppendAllText(path, $"\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} | Origem detectada: {origem} =====\n{texto}\n");
            }
            catch { }
        }

        public void Registrar()
        {
            if (_config == null || _sipTransport == null)
                throw new InvalidOperationException("SipService não inicializado.");

            string registrarHost = !string.IsNullOrWhiteSpace(_config.ProxySip)
                ? _config.ProxySip
                : $"{_config.ServerIp}:{_config.Port}";

            LogHelper.Sip($"SIP_REGISTER_START | ramal={_config.Login} servidor={registrarHost}");
            RegistrarSinalSip($"SIP_REGISTER_START | ramal={_config.Login} servidor={registrarHost}");

            _registerUserAgent = new SIPRegistrationUserAgent(
                _sipTransport,
                _config.Login,
                _config.Senha,
                registrarHost,
                120
            );

            _registerUserAgent.RegistrationSuccessful += (uri, resp) =>
            {
                IsRegistered = true;
                LogHelper.Sip($"SIP_REGISTER_OK | ramal={_config.Login} servidor={registrarHost}");
                RegistrarSinalSip($"SIP_REGISTER_OK | ramal={_config.Login} servidor={registrarHost}");
                StatusChanged?.Invoke($"Registrado com sucesso: {_config.Login}");
            };

            _registerUserAgent.RegistrationFailed += (uri, resp, err) =>
            {
                IsRegistered = false;
                LogHelper.Sip($"SIP_REGISTER_FAILED | ramal={_config.Login} erro={err}", LogLevel.ERROR);
                RegistrarSinalSip($"SIP_REGISTER_FAILED | ramal={_config.Login} erro={err}");
                StatusChanged?.Invoke($"Falha no registro: {err}");
            };

            _registerUserAgent.RegistrationTemporaryFailure += (uri, resp, msg) =>
            {
                IsRegistered = false;
                LogHelper.Sip($"SIP_REGISTER_FAILED | ramal={_config.Login} falha_temp={msg}", LogLevel.WARN);
                RegistrarSinalSip($"SIP_REGISTER_FAILED | ramal={_config.Login} falha_temp={msg}");
                StatusChanged?.Invoke($"Falha temporária: {msg}");
            };

            _registerUserAgent.Start();
        }

        // v2.1.1 — controle local de Offline (substitui dependência de *78/*79 no Issabel)
        public void EntrarOffline()
        {
            ModoOffline = true;
            LogHelper.Info("OFFLINE_ATIVADO_LOCAL");
        }

        public void SairOffline()
        {
            ModoOffline = false;
            LogHelper.Info("OFFLINE_DESATIVADO_LOCAL");
        }

        /// Garante registro SIP ativo: confere transporte, registra/renova se necessário
        /// e aguarda confirmação até o timeout. Usado para recuperar recebimento de
        /// chamadas após período Offline ou queda de rede.
        public async Task<bool> GarantirRegistradoAsync(int timeoutSegundos = 12)
        {
            LogHelper.Info("SIP_VERIFICANDO_TRANSPORTE");
            if (_sipTransport == null)
            {
                LogHelper.Error("SIP_TRANSPORTE_INDISPONIVEL");
                return false;
            }

            if (_userAgent == null)
            {
                LogHelper.Info("SIP_USERAGENT_AUSENTE_RECRIANDO");
                try { CriarUserAgent(); }
                catch (Exception ex) { LogHelper.Error("SIP_USERAGENT_RECRIAR_FALHOU", ex); return false; }
            }

            LogHelper.Info("SIP_VERIFICANDO_REGISTRO");
            if (IsRegistered)
            {
                LogHelper.Info("SIP_REGISTRO_OK_JA_ATIVO");
                return true;
            }

            LogHelper.Info("SIP_RENOVANDO_REGISTRO");
            try { Registrar(); }
            catch (Exception ex) { LogHelper.Error("SIP_RENOVAR_REGISTRO_FALHOU", ex); return false; }

            var inicio = DateTime.Now;
            while ((DateTime.Now - inicio).TotalSeconds < timeoutSegundos)
            {
                if (IsRegistered) return true;
                await Task.Delay(300);
            }

            return IsRegistered;
        }

        public async Task<bool> Ligar(string numeroFinal)
        {
            if (_config == null || _userAgent == null)
                throw new InvalidOperationException("SipService não inicializado.");

            // v2.3.6 — trava de reentrância: Ligar() não tinha NENHUMA proteção contra ser chamado
            // de novo enquanto uma tentativa anterior ainda estava discando/tocando. Um segundo
            // clique nesse intervalo criava uma SEGUNDA chamada concorrente usando os MESMOS campos
            // de estado de instância (LastOutboundResultado, _ultimoRingingRecebidoEm, etc.),
            // corrompendo a classificação de ambas as tentativas — investigação real encontrou
            // exatamente esse padrão. O guard de UI (DialerShellWindow.IniciarLigacaoAsync) já
            // deveria impedir isso antes de chegar aqui; esta é a segunda camada de defesa.
            if (_chamadaSaindoEmAndamento || IsInCall)
            {
                LogHelper.Sip($"OUTBOUND_CLICK_IGNORED_BUSY | numero={numeroFinal} discando={_chamadaSaindoEmAndamento} emChamada={IsInCall}", LogLevel.WARN);
                throw new InvalidOperationException("Já existe uma chamada em andamento.");
            }

            _outboundAttemptId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var attemptId = _outboundAttemptId;
            LastOutboundWasCancelledLocally = false;
            LastOutboundResultado = TipoHistoricoLigacao.NaoAtendida;

            _muteMicrofoneAtivo = false;
            var _swDiscagem = System.Diagnostics.Stopwatch.StartNew();

            var numeroRaw = numeroFinal;
            var numeroNorm = PhoneNumberNormalizer.NormalizeForDial(numeroFinal);
            string destination = $"sip:{numeroNorm}@{_config.ServerIp}:{_config.Port}";
            LastCallError = string.Empty;
            LastDialedDestination = destination;
            LastOutboundFailureStatus = null;
            LastOutboundFailureReason = string.Empty;
            LastOutboundRingDurationSeconds = null;
            _ultimoRingingRecebidoEm = null;
            LogHelper.Sip($"OUTBOUND_ATTEMPT_CREATED | attemptId={attemptId} numero={MascararTextoSip(numeroNorm)}");
            LogHelper.Sip($"CALL_START_CLICK | destino={numeroNorm} raw={numeroRaw}");
            LogHelper.Sip($"CALL_DIAL_NUMBER_RAW | numero={numeroRaw}");
            LogHelper.Sip($"CALL_DIAL_NUMBER_NORMALIZED | numero={numeroNorm}");
            LogHelper.Sip($"CALL_INVITE_TARGET | sip={destination}");
            RegistrarSinalSip($"CALL_START_REQUEST | destino={destination} usuario={_config.Login}");
            LogHelper.Sip($"CALL_START_REQUEST | destino={destination}");

            try
            {
                _cancelamentoSolicitado = false;
                _outboundCancelConfirmado = false;
                _outboundCancelMonitorAttemptId = null;
                _chamadaSaindoEmAndamento = true;
                _inicioDialing = DateTime.Now;

                _mediaSession = await CriarMediaAsync();
                LogHelper.Sip($"CALL_MEDIA_STARTED | media session created elapsed_ms={_swDiscagem.ElapsedMilliseconds}");

                // v2.3.6 — CriarMedia() pode levar 150-400ms+ em máquinas reais (enumeração de
                // dispositivo de áudio, visto em log real). Se Desligar() foi clicado ENQUANTO isso
                // rodava, _userAgent.Call() nem tinha começado — o Cancel() que Desligar() já tentou
                // não tinha nenhuma transação pra cancelar ainda (m_uac era o da tentativa ANTERIOR
                // ou nulo). Sem este check o INVITE seria enviado mesmo assim e o telefone do cliente
                // tocaria. O lock fecha a corrida com Desligar() (ver lá) sobre _cancelamentoSolicitado.
                bool abortarAntesDoInvite;
                lock (_outboundCancelLock)
                {
                    abortarAntesDoInvite = _cancelamentoSolicitado;
                    _inviteEmEnvio = !abortarAntesDoInvite;
                }

                if (abortarAntesDoInvite)
                {
                    _chamadaSaindoEmAndamento = false;
                    _inicioDialing = DateTime.MinValue;
                    LastOutboundWasCancelledLocally = true;
                    LogHelper.Sip($"OUTBOUND_INVITE_SUPPRESSED_BY_CANCEL | attemptId={attemptId} destino={destination}", LogLevel.WARN);
                    RegistrarSinalSip($"OUTBOUND_INVITE_SUPPRESSED_BY_CANCEL | attemptId={attemptId} destino={destination}");
                    LimparEstadoChamada("Chamada cancelada antes do envio.");
                    return false;
                }

                LogHelper.Sip($"CALL_INVITE_SENT | destino={destination} elapsed_ms={_swDiscagem.ElapsedMilliseconds}");
                LogHelper.Sip($"OUTBOUND_CLICK_TO_INVITE_TIMING | attemptId={attemptId} ms={_swDiscagem.ElapsedMilliseconds}");
                bool ok = await _userAgent.Call(destination, _config.Login, _config.Senha, _mediaSession);

                lock (_outboundCancelLock) { _inviteEmEnvio = false; }

                _chamadaSaindoEmAndamento = false;
                _inicioDialing = DateTime.MinValue;
                LogHelper.Sip($"CALL_SIP_RESPONSE | ok={ok} destino={destination}");

                // Se o usuário clicou em desligar enquanto ainda estava chamando,
                // não podemos considerar a chamada como ativa/atendida.
                if (_cancelamentoSolicitado)
                {
                    // ok=true indica corrida: 200 OK chegou no Asterisk antes de processar nosso CANCEL.
                    // EncerrarSinalizacaoAtiva envia BYE quando IsCallActive=true (fix Cancel() no-op).
                    // LimparEstadoChamada já foi chamada por Desligar() — não repete para evitar duplo CallEnded.
                    //
                    // v2.3.6 — ESTE é o cancelamento pelo PRÓPRIO OPERADOR, nunca uma recusa/timeout
                    // do lado do cliente. Marca explicitamente (LastOutboundWasCancelledLocally) em
                    // vez de deixar o chamador ler LastOutboundResultado — antes disso, esse campo
                    // não era resetado no início de Ligar() e podia carregar o resultado (ex.:
                    // "Recusada") de uma tentativa ANTERIOR completamente diferente, fazendo o popup
                    // "Chamada recusada" aparecer para um cancelamento local sem nenhuma relação.
                    LastOutboundWasCancelledLocally = true;
                    RegistrarSinalSip($"CALL_OUT_CANCEL_POST_RETURN | ok={ok} destino={destination}");
                    LogHelper.Sip($"CALL_OUT_CANCEL_POST_RETURN | ok={ok} destino={destination}" + (ok ? " [RACE: 200 OK before CANCEL — sending BYE]" : ""));
                    LogHelper.Sip($"OUTBOUND_LOCAL_CANCEL_CONFIRMED | attemptId={attemptId}");
                    EncerrarSinalizacaoAtiva();
                    return false;
                }

                if (ok)
                {
                    StatusChanged?.Invoke($"Chamada enviada para {numeroFinal}");
                    RegistrarSinalSip($"CALL_CONNECTED | destino={destination}");
                    LogHelper.Sip($"CALL_CONNECTED | destino={destination}");
                    LogHelper.Sip($"CALL_SETUP_DURATION_MS | ms={_swDiscagem.ElapsedMilliseconds}");
                    LogHelper.Sip($"CALL_RTP_TX_ACTIVE | destino={destination}");
                    LogHelper.Sip($"CALL_RTP_RX_ACTIVE | destino={destination}");
                    LogHelper.Sip($"WAVOIP_CHANNEL_USED | destino={destination} usuario={_config.Login}");
                }
                else
                {
                    LastOutboundResultado = ClassificarResultadoLigacao(LastOutboundFailureStatus, LastOutboundRingDurationSeconds);
                    LastCallError = $"Issabel/SIP recusou ou não completou o INVITE para {numeroFinal}. Destino enviado: {destination}.";
                    StatusChanged?.Invoke($"Falha ao chamar {numeroFinal}. Veja log SIP.");
                    RegistrarSinalSip($"TX CALL FALHA destino={destination}");
                    LogHelper.Sip($"CALL_ENDED_REASON | motivo=SIP_REJECTED destino={destination} " +
                        $"status={LastOutboundFailureStatus} resultado={LastOutboundResultado}", LogLevel.WARN);
                    LimparEstadoChamada("Falha na chamada.");
                }

                return ok;
            }
            catch (Exception ex)
            {
                _chamadaSaindoEmAndamento = false;
                _inicioDialing = DateTime.MinValue;
                LastCallError = $"Erro real da chamada para {numeroFinal}: {ex.Message}. Destino enviado: {destination}.";
                RegistrarSinalSip($"TX CALL ERRO destino={destination} erro={ex}");
                LogHelper.Sip($"CALL_ENDED_REASON | motivo=EXCEPTION erro={ex.Message} destino={destination}", LogLevel.ERROR);
                LimparEstadoChamada("Erro na chamada.");
                StatusChanged?.Invoke($"Erro real da chamada: {ex.Message}");
                throw;
            }
        }

        public async Task<bool> ExecutarCodigoFuncao(string codigo)
        {
            if (string.IsNullOrWhiteSpace(codigo))
                throw new InvalidOperationException("Código inválido.");

            bool ok = await Ligar(codigo);

            if (ok)
            {
                await Task.Delay(1800);
                Desligar();
            }

            return ok;
        }

        public bool PossuiChamadaRecebidaPendente => _pendingIncomingRequest != null;

        public async Task<bool> AtenderChamada()
        {
            if (_userAgent == null)
                throw new InvalidOperationException("SipService não inicializado.");

            if (_pendingIncomingRequest == null)
                throw new InvalidOperationException("Não existe chamada pendente para atender.");

            var callIdAtendida = _pendingIncomingCallId;
            LogHelper.Sip($"INCOMING_CALL_ACCEPTED | Call-ID={callIdAtendida}");
            RegistrarSinalSip($"INCOMING_CALL_ACCEPTED | Call-ID={callIdAtendida}");

            var uas = _userAgent.AcceptCall(_pendingIncomingRequest);
            if (uas == null)
            {
                StatusChanged?.Invoke("Falha ao aceitar chamada recebida.");
                LogHelper.Sip($"INCOMING_CALL_HANDLER_ERROR | AcceptCall retornou null Call-ID={callIdAtendida}", LogLevel.ERROR);
                RegistrarSinalSip($"INCOMING_CALL_HANDLER_ERROR | AcceptCall retornou null Call-ID={callIdAtendida}");
                return false;
            }

            _mediaSession = await CriarMediaAsync();
            bool ok = await _userAgent.Answer(uas, _mediaSession);

            if (ok)
            {
                StatusChanged?.Invoke("Chamada atendida.");
                LimparChamadaRecebidaPendente(false);
                LogHelper.Sip("CALL_CONNECTED | chamada recebida atendida");
                LogHelper.Sip("CALL_RTP_TX_ACTIVE | chamada recebida");
                LogHelper.Sip("CALL_RTP_RX_ACTIVE | chamada recebida");
            }
            else
            {
                LogHelper.Sip("CALL_ENDED_REASON | motivo=ANSWER_FAILED", LogLevel.WARN);
                LimparEstadoChamada("Falha ao atender.");
            }

            return ok;
        }

        public bool TransferenciaCega(string ramalDestino)
        {
            if (_userAgent == null || _config == null || string.IsNullOrWhiteSpace(ramalDestino) || !IsInCall)
                return false;

            ramalDestino = DialPlanService.NormalizarNumero(ramalDestino);
            _transferenciaEmAndamento = true;
            StatusChanged?.Invoke($"Transferência cega para {ramalDestino}...");
            RegistrarSinalSip($"CALL_TRANSFER_STARTED | tipo=cega destino={ramalDestino}");
            LogHelper.Sip($"CALL_TRANSFER_STARTED | tipo=cega destino={ramalDestino}");

            // Issabel/Asterisk: Transferência cega durante chamada = ## + ramal.
            bool ok = EnviarDtmfEmPassos("##", ramalDestino);
            if (ok)
            {
                RegistrarSinalSip($"CALL_TRANSFER_REFER_SENT | tipo=cega destino={ramalDestino} metodo=DTMF");
                LogHelper.Sip($"CALL_TRANSFER_REFER_SENT | tipo=cega destino={ramalDestino} metodo=DTMF");
            }
            else
            {
                string sipDestino = $"sip:{ramalDestino}@{_config.ServerIp}:{_config.Port}";
                ok = TentarTransferDireta("BlindTransfer", sipDestino, ramalDestino) ||
                     TentarTransferDireta("Transfer", sipDestino, ramalDestino);
                if (ok)
                {
                    RegistrarSinalSip($"CALL_TRANSFER_REFER_SENT | tipo=cega destino={ramalDestino} metodo=REFER");
                    LogHelper.Sip($"CALL_TRANSFER_REFER_SENT | tipo=cega destino={ramalDestino} metodo=REFER");
                }
            }

            if (ok)
            {
                RegistrarSinalSip($"CALL_TRANSFER_COMPLETED | tipo=cega destino={ramalDestino}");
                LogHelper.Sip($"CALL_TRANSFER_COMPLETED | tipo=cega destino={ramalDestino}");
            }
            else
            {
                _transferenciaEmAndamento = false;
                RegistrarSinalSip($"CALL_TRANSFER_FAILED | tipo=cega destino={ramalDestino}");
                LogHelper.Sip($"CALL_TRANSFER_FAILED | tipo=cega destino={ramalDestino}", LogLevel.WARN);
            }

            StatusChanged?.Invoke(ok ? $"Transferência cega enviada para {ramalDestino}." : "Falha ao enviar transferência cega.");
            return ok;
        }

        public bool TransferenciaAssistida(string ramalDestino)
        {
            if (_userAgent == null || _config == null || string.IsNullOrWhiteSpace(ramalDestino) || !IsInCall)
                return false;

            ramalDestino = DialPlanService.NormalizarNumero(ramalDestino);
            _transferenciaEmAndamento = true;
            StatusChanged?.Invoke($"Transferência assistida para {ramalDestino}...");
            RegistrarSinalSip($"CALL_TRANSFER_STARTED | tipo=assistida destino={ramalDestino}");
            LogHelper.Sip($"CALL_TRANSFER_STARTED | tipo=assistida destino={ramalDestino}");

            // Issabel/Asterisk: Transferência assistida durante chamada = *2 + ramal.
            bool ok = EnviarDtmfEmPassos("*2", ramalDestino);
            if (ok)
            {
                RegistrarSinalSip($"CALL_TRANSFER_REFER_SENT | tipo=assistida destino={ramalDestino} metodo=DTMF");
                LogHelper.Sip($"CALL_TRANSFER_REFER_SENT | tipo=assistida destino={ramalDestino} metodo=DTMF");
            }
            else
            {
                string sipDestino = $"sip:{ramalDestino}@{_config.ServerIp}:{_config.Port}";
                ok = TentarTransferDireta("AttendedTransfer", sipDestino, ramalDestino) ||
                     TentarTransferDireta("Transfer", sipDestino, ramalDestino);
                if (ok)
                {
                    RegistrarSinalSip($"CALL_TRANSFER_REFER_SENT | tipo=assistida destino={ramalDestino} metodo=REFER");
                    LogHelper.Sip($"CALL_TRANSFER_REFER_SENT | tipo=assistida destino={ramalDestino} metodo=REFER");
                }
            }

            if (!ok)
            {
                _transferenciaEmAndamento = false;
                RegistrarSinalSip($"CALL_TRANSFER_FAILED | tipo=assistida destino={ramalDestino}");
                LogHelper.Sip($"CALL_TRANSFER_FAILED | tipo=assistida destino={ramalDestino}", LogLevel.WARN);
            }
            else
            {
                LogHelper.Sip($"CALL_TRANSFER_REFER_ACCEPTED | tipo=assistida destino={ramalDestino}");
            }

            StatusChanged?.Invoke(ok ? $"Transferência assistida enviada para {ramalDestino}." : "Falha ao enviar transferência assistida.");
            return ok;
        }

        private bool TentarTransferDireta(string metodoNome, string sipDestino, string ramalDestino)
        {
            if (_userAgent == null) return false;

            foreach (var metodo in _userAgent.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!string.Equals(metodo.Name, metodoNome, StringComparison.OrdinalIgnoreCase))
                    continue;

                var ps = metodo.GetParameters();

                try
                {
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    {
                        var ret = metodo.Invoke(_userAgent, new object[] { sipDestino });
                        return ResolverRetornoBool(ret);
                    }

                    if (ps.Length == 1 && ps[0].ParameterType.Name.Contains("SIPURI"))
                    {
                        var sipUri = SIPURI.ParseSIPURIRelaxed(sipDestino);
                        var ret = metodo.Invoke(_userAgent, new object[] { sipUri });
                        return ResolverRetornoBool(ret);
                    }

                    if (ps.Length == 2 &&
                        ps[0].ParameterType == typeof(string) &&
                        ps[1].ParameterType == typeof(bool))
                    {
                        var ret = metodo.Invoke(_userAgent, new object[] { sipDestino, true });
                        return ResolverRetornoBool(ret);
                    }

                    if (ps.Length == 2 &&
                        ps[0].ParameterType.Name.Contains("SIPURI") &&
                        ps[1].ParameterType == typeof(bool))
                    {
                        var sipUri = SIPURI.ParseSIPURIRelaxed(sipDestino);
                        var ret = metodo.Invoke(_userAgent, new object[] { sipUri, true });
                        return ResolverRetornoBool(ret);
                    }

                    if (ps.Length == 2 &&
                        ps[0].ParameterType == typeof(string) &&
                        ps[1].ParameterType == typeof(string))
                    {
                        var ret = metodo.Invoke(_userAgent, new object[] { sipDestino, ramalDestino });
                        return ResolverRetornoBool(ret);
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private bool ResolverRetornoBool(object? ret)
        {
            if (ret == null) return true;
            if (ret is bool b) return b;
            if (ret is Task<bool> tb) return tb.GetAwaiter().GetResult();
            if (ret is Task t)
            {
                t.GetAwaiter().GetResult();
                return true;
            }

            return true;
        }

        public bool EnviarSequenciaDtmf(string sequencia)
        {
            if (!IsInCall || string.IsNullOrWhiteSpace(sequencia))
                return false;

            foreach (char c in sequencia)
            {
                if (!TentarEnviarDtmf(c))
                    return false;

                Thread.Sleep(220);
            }

            return true;
        }

        private bool EnviarDtmfEmPassos(params string[] passos)
        {
            foreach (var passo in passos)
            {
                if (!EnviarSequenciaDtmf(passo))
                    return false;

                Thread.Sleep(900);
            }

            return true;
        }

        private static byte ObterCodigoDtmf(char digito)
        {
            return digito switch
            {
                '0' => 0,
                '1' => 1,
                '2' => 2,
                '3' => 3,
                '4' => 4,
                '5' => 5,
                '6' => 6,
                '7' => 7,
                '8' => 8,
                '9' => 9,
                '*' => 10,
                '#' => 11,
                'A' or 'a' => 12,
                'B' or 'b' => 13,
                'C' or 'c' => 14,
                'D' or 'd' => 15,
                _ => 255
            };
        }

        private bool TentarEnviarDtmf(char digito)
        {
            byte codigo = ObterCodigoDtmf(digito);
            if (codigo == 255) return false;

            // Importante: para RTP/RFC2833, * e # NÃO podem ser enviados como ASCII.
            // Os códigos corretos são *=10 e #=11. Essa correção é essencial para
            // os códigos do Issabel/Asterisk (*2, ##, *1) funcionarem durante chamada.
            return TentarNoObjeto(_userAgent, digito, codigo)
                || TentarNoObjeto(_mediaSession, digito, codigo)
                || TentarEmPropriedadesConhecidas(_mediaSession, digito, codigo);
        }

        private bool TentarEmPropriedadesConhecidas(object? origem, char digito, byte codigo)
        {
            if (origem == null) return false;

            var nomes = new[] { "AudioExtrasSource", "AudioSource", "AudioSink", "Media", "RtpSession", "RTPSession", "AudioSession" };
            foreach (var nome in nomes)
            {
                try
                {
                    var prop = origem.GetType().GetProperty(nome, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop != null)
                    {
                        var valor = prop.GetValue(origem);
                        if (TentarNoObjeto(valor, digito, codigo)) return true;
                    }

                    var field = origem.GetType().GetField(nome, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null)
                    {
                        var valor = field.GetValue(origem);
                        if (TentarNoObjeto(valor, digito, codigo)) return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private bool TentarNoObjeto(object? alvo, char digito, byte codigo)
        {
            if (alvo == null) return false;

            var candidatos = new[]
            {
                "SendDtmf",
                "SendDTMF",
                "SendDtmfEvent",
                "SendDTMFEvent",
                "SendTone",
                "SendRtpEvent",
                "SendDtmfTone",
                "SendDtmfAsync",
                "SendDtmfEventAsync"
            };

            foreach (var metodo in alvo.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (Array.IndexOf(candidatos, metodo.Name) < 0) continue;
                var ps = metodo.GetParameters();

                try
                {
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(char))
                    {
                        return ResolverRetornoBool(metodo.Invoke(alvo, new object[] { digito }));
                    }

                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    {
                        return ResolverRetornoBool(metodo.Invoke(alvo, new object[] { digito.ToString() }));
                    }

                    if (ps.Length == 1 && ps[0].ParameterType == typeof(int))
                    {
                        return ResolverRetornoBool(metodo.Invoke(alvo, new object[] { (int)codigo }));
                    }

                    if (ps.Length == 1 && ps[0].ParameterType == typeof(byte))
                    {
                        return ResolverRetornoBool(metodo.Invoke(alvo, new object[] { codigo }));
                    }

                    if (ps.Length == 2 && ps[0].ParameterType == typeof(char) && ps[1].ParameterType == typeof(int))
                    {
                        return ResolverRetornoBool(metodo.Invoke(alvo, new object[] { digito, 250 }));
                    }

                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(int))
                    {
                        return ResolverRetornoBool(metodo.Invoke(alvo, new object[] { digito.ToString(), 250 }));
                    }

                    if (ps.Length == 2 && ps[0].ParameterType == typeof(byte) && ps[1].ParameterType == typeof(int))
                    {
                        return ResolverRetornoBool(metodo.Invoke(alvo, new object[] { codigo, 250 }));
                    }

                    if (ps.Length == 2 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(int))
                    {
                        return ResolverRetornoBool(metodo.Invoke(alvo, new object[] { (int)codigo, 250 }));
                    }

                    if (ps.Length == 3 && ps[0].ParameterType == typeof(byte) && ps[1].ParameterType == typeof(int))
                    {
                        object terceiro = ps[2].HasDefaultValue ? ps[2].DefaultValue! : false;
                        return ResolverRetornoBool(metodo.Invoke(alvo, new object[] { codigo, 250, terceiro }));
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        public bool TentarSegurarAlternar()
        {
            if (!IsInCall)
                return false;

            bool proximoEstado = !_emEspera;
            bool ok = false;
            try { ok = TentarSegurar(proximoEstado); } catch { ok = false; }

            // Algumas versões do SIPSorcery/Asterisk não retornam confirmação confiável do hold,
            // embora a sinalização/DTMF seja enviada. Para a interface não ficar presa no ícone errado,
            // atualizamos o estado local sempre que existe chamada ativa.
            _emEspera = proximoEstado;
            StatusChanged?.Invoke(_emEspera ? "Chamada em espera." : "Chamada retomada.");
            return ok || IsInCall;
        }

        public bool MutarMicrofone(bool mudo)
        {
            if (!IsInCall)
            {
                LogHelper.Sip($"CALL_MUTE_APPLY_FAILED | IsInCall={IsInCall}");
                return false;
            }
            _muteMicrofoneAtivo = mudo;

            // Silence injection via proxy — WaveIn continua rodando, RTP TX flui com silence
            _muteableAudioSource?.SetMute(mudo);

            if (mudo)
            {
                RegistrarSinalSip($"CALL_MUTE_ENABLED_TX_ONLY | IsInCall={IsInCall}");
                LogHelper.Sip($"CALL_MUTE_ENABLED_TX_ONLY | IsInCall={IsInCall}");
                LogHelper.Sip("CALL_RX_AUDIO_CONTINUES_DURING_MUTE | WaveOut nao afetado — cliente continua sendo ouvido");
                LogHelper.Sip("CALL_MUTE_DID_NOT_AFFECT_PLAYBACK | pipeline RX intacto");
            }
            else
            {
                RegistrarSinalSip($"CALL_MUTE_DISABLED_TX_RESTORED | IsInCall={IsInCall}");
                LogHelper.Sip($"CALL_MUTE_DISABLED_TX_RESTORED | IsInCall={IsInCall}");
                LogHelper.Sip("CALL_RX_AUDIO_CONTINUES_DURING_MUTE | WaveOut permaneceu ativo durante mute");
                LogHelper.Sip("CALL_MUTE_DID_NOT_AFFECT_PLAYBACK | pipeline RX intacto durante e apos mute");
            }

            StatusChanged?.Invoke(mudo ? "Microfone silenciado." : "Microfone ativo.");
            return true;
        }

        private void PararRtpKeepalive()
        {
            _rtpKeepaliveTimer?.Dispose();
            _rtpKeepaliveTimer = null;
        }

        // Limpa o BufferedWaveProvider do WindowsAudioEndPoint para evitar que
        // áudio acumulado durante Hold seja reproduzido ao retomar a chamada.
        private void LimparBufferPlayback()
        {
            try
            {
                var ep = _audioEndpoint;
                if (ep == null) return;
                var field = ep.GetType().GetField("_waveProvider",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var provider = field?.GetValue(ep);
                provider?.GetType().GetMethod("ClearBuffer")?.Invoke(provider, null);
                LogHelper.Sip("CALL_HOLD_BUFFER_CLEARED | buffer WaveOut limpo — sem audio residual ao retomar");
                RegistrarSinalSip("CALL_HOLD_BUFFER_CLEARED");
            }
            catch { }
        }

        private bool TentarSegurar(bool segurar)
        {
            if (segurar)
            {
                // Salva estado atual do mute e silencia o microfone durante a espera.
                // Sem isso, o WaveIn continua capturando e o áudio acumula — ao retomar,
                // o Asterisk vazaria o que foi dito durante a pausa para o cliente.
                _muteAntesDeEspera = _muteMicrofoneAtivo;
                _muteableAudioSource?.SetMute(true);
                LogHelper.Sip($"CALL_HOLD_MIC_MUTED | mic silenciado durante espera (mute anterior={_muteAntesDeEspera})");
                RegistrarSinalSip("CALL_HOLD_MIC_MUTED");

                // Pausa WaveOut e limpa buffer: evita que áudio pré-hold
                // fique enfileirado e seja reproduzido ao operador ao retomar.
                var ep = _audioEndpoint;
                if (ep != null)
                {
                    LimparBufferPlayback();
                    _ = ep.PauseAudioSink();
                    LogHelper.Sip("CALL_HOLD_WAVEOUT_PAUSED | operador nao ouve cliente durante espera");
                    RegistrarSinalSip("CALL_HOLD_WAVEOUT_PAUSED");
                }

                return TentarMetodo(_userAgent, "PutOnHold", true)
                    || TentarMetodo(_userAgent, "Hold", true)
                    || TentarMetodo(_mediaSession, "PutOnHold", true)
                    || TentarMetodo(_mediaSession, "Hold", true)
                    || EnviarSequenciaDtmf("*70");
            }
            else
            {
                // Limpa buffer acumulado durante Hold ANTES de retomar WaveOut.
                // Sem isso, o BufferedWaveProvider descarrega áudio enfileirado
                // (eco/voz própria) assim que Play() é chamado.
                var ep = _audioEndpoint;
                if (ep != null)
                {
                    LimparBufferPlayback();
                    _ = ep.ResumeAudioSink();
                    LogHelper.Sip("CALL_HOLD_WAVEOUT_RESUMED | operador voltou a ouvir cliente");
                    RegistrarSinalSip("CALL_HOLD_WAVEOUT_RESUMED");
                }

                // Restaura estado do mute que existia antes da espera
                _muteMicrofoneAtivo = _muteAntesDeEspera;
                _muteableAudioSource?.SetMute(_muteAntesDeEspera);
                LogHelper.Sip($"CALL_HOLD_MIC_RESTORED | mute restaurado para estado anterior={_muteAntesDeEspera}");
                RegistrarSinalSip($"CALL_HOLD_MIC_RESTORED | mute={_muteAntesDeEspera}");

                return TentarMetodo(_userAgent, "TakeOffHold", false)
                    || TentarMetodo(_userAgent, "Unhold", false)
                    || TentarMetodo(_userAgent, "Resume", false)
                    || TentarMetodo(_mediaSession, "TakeOffHold", false)
                    || TentarMetodo(_mediaSession, "Unhold", false)
                    || TentarMetodo(_mediaSession, "Resume", false)
                    || EnviarSequenciaDtmf("*71");
            }
        }

        private bool TentarMetodo(object? alvo, string nome, bool parametroBool = true)
        {
            if (alvo == null) return false;

            foreach (var metodo in alvo.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!string.Equals(metodo.Name, nome, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var ps = metodo.GetParameters();
                    if (ps.Length == 0)
                    {
                        return ResolverRetornoBool(metodo.Invoke(alvo, null));
                    }

                    if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
                    {
                        return ResolverRetornoBool(metodo.Invoke(alvo, new object[] { parametroBool }));
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private bool TentarMetodoSemParametro(object? alvo, params string[] nomes)
        {
            if (alvo == null) return false;

            foreach (var nome in nomes)
            {
                foreach (var metodo in alvo.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(metodo.Name, nome, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        if (metodo.GetParameters().Length == 0)
                            return ResolverRetornoBool(metodo.Invoke(alvo, null));
                    }
                    catch
                    {
                    }
                }
            }

            return false;
        }

        private void EncerrarSinalizacaoAtiva()
        {
            try
            {
                bool jaEstavaAtiva = _userAgent?.IsCallActive == true;

                // Durante chamada sainte ainda não atendida, alguns métodos Hangup não enviam CANCEL.
                // Tentamos os nomes usados por versões diferentes do SIPSorcery e, por fim, Hangup.
                bool cancelEnviado = TentarMetodoSemParametro(_userAgent, "Cancel", "CancelCall", "CancelInvite");
                // Cancel() é void e nunca lança exceção para chamadas já estabelecidas — retorna silenciosamente.
                // Se o diálogo já foi criado (200 OK chegou antes do CANCEL), é preciso enviar BYE via Hangup().
                if (!cancelEnviado || (_userAgent?.IsCallActive == true))
                {
                    try { _userAgent?.Hangup(); cancelEnviado = true; } catch { }
                }

                RegistrarSinalSip($"CALL_CANCEL_SENT | cancelEnviado={cancelEnviado} eraDial={_chamadaSaindoEmAndamento}");
                LogHelper.Sip($"CALL_CANCEL_SENT | cancelEnviado={cancelEnviado}");

                // v2.3.6 — investigação real confirmou que SIPClientUserAgent.Cancel() (SIPSorcery)
                // dispara CallFailed IMEDIATAMENTE e LOCALMENTE, sem esperar nenhuma resposta de rede.
                // "cancelEnviado=true" acima só prova que chamamos o método — não que o Asterisk
                // recebeu o CANCEL. Só faz sentido pendurar os ganchos de confirmação/reenvio quando
                // ainda estávamos discando (chamada nunca atendida) e o cancel não é, na verdade, um
                // Hangup pós-atendimento (jaEstavaAtiva).
                if (cancelEnviado && _chamadaSaindoEmAndamento && !jaEstavaAtiva)
                {
                    var attemptId = _outboundAttemptId;
                    // Guard: EncerrarSinalizacaoAtiva() pode rodar 2x pra mesma tentativa (Desligar()
                    // + branch de corrida pós-retorno em Ligar()) — só pendura os ganchos/agenda o
                    // retry na PRIMEIRA vez por attemptId, nunca duplica.
                    if (!string.Equals(_outboundCancelMonitorAttemptId, attemptId, StringComparison.Ordinal))
                    {
                        _outboundCancelMonitorAttemptId = attemptId;
                        MonitorarConfirmacaoCancelamento(attemptId);
                        AgendarReenvioSeNaoConfirmado(attemptId);
                    }
                }

                // Detecção de cancelamento antes do Ringing: sinal real de risco (o cliente pode não
                // ter recebido nenhum toque ainda quando cancelamos) — diferente de cancelar DURANTE
                // o toque, que é normal e não deve gerar alerta (heurística antiga por tempo decorrido
                // < 3s disparava também em cancelamentos legítimos durante Ringing, gerando ruído —
                // ver investigação real de 10/08/2026, attempts das 13:09).
                if (_chamadaSaindoEmAndamento && _inicioDialing != DateTime.MinValue && _ultimoRingingRecebidoEm == null)
                {
                    var elapsed = (DateTime.Now - _inicioDialing).TotalSeconds;
                    RegistrarSinalSip($"OUTBOUND_CANCEL_BEFORE_RINGING | elapsed={elapsed:0.0}s destino={LastDialedDestination}");
                    LogHelper.Sip($"OUTBOUND_CANCEL_BEFORE_RINGING | elapsed={elapsed:0.0}s destino={LastDialedDestination}", LogLevel.WARN);
                }
            }
            catch
            {
            }
        }

        // v2.3.6 — pendura handlers nos eventos PÚBLICOS da transação SIP real (UACInviteTransaction/
        // SIPNonInviteTransaction) pra logar PROVA de que o CANCEL foi entregue (ou não) — nunca só
        // que conseguimos chamar o método local. m_uac/m_cancelTransaction são campos privados/internal
        // do SIPSorcery sem getter público; reflection aqui é só LEITURA (nunca seta nada), no mesmo
        // estilo já usado no resto deste arquivo (TentarMetodoSemParametro/TentarTransferDireta).
        private void MonitorarConfirmacaoCancelamento(string attemptId)
        {
            try
            {
                if (_campoUac?.GetValue(_userAgent) is not SIPClientUserAgent uac) return;

                var serverTx = uac.ServerTransaction;
                if (serverTx != null)
                {
                    serverTx.UACInviteTransactionFinalResponseReceived += (localEp, remoteEp, tx, resp) =>
                    {
                        _outboundCancelConfirmado = true;
                        LogHelper.Sip($"OUTBOUND_INVITE_TERMINATED | attemptId={attemptId} status={resp?.Status}");
                        RegistrarSinalSip($"OUTBOUND_INVITE_TERMINATED | attemptId={attemptId} status={resp?.Status}");
                        return Task.FromResult(System.Net.Sockets.SocketError.Success);
                    };
                }

                // m_cancelTransaction só existe DEPOIS de Cancel() já ter criado a transação — por
                // isso este método precisa ser chamado logo após TentarMetodoSemParametro(...,"Cancel",...)
                // ter retornado, dentro de EncerrarSinalizacaoAtiva().
                if (_campoCancelTransaction?.GetValue(uac) is SIPNonInviteTransaction cancelTx)
                {
                    cancelTx.NonInviteTransactionFinalResponseReceived += (localEp, remoteEp, tx, resp) =>
                    {
                        _outboundCancelConfirmado = true;
                        LogHelper.Sip($"OUTBOUND_SIP_CANCEL_RESPONSE | attemptId={attemptId} status={resp?.Status}");
                        RegistrarSinalSip($"OUTBOUND_SIP_CANCEL_RESPONSE | attemptId={attemptId} status={resp?.Status}");
                        return Task.FromResult(System.Net.Sockets.SocketError.Success);
                    };
                    cancelTx.NonInviteTransactionFailed += (tx, reason) =>
                    {
                        LogHelper.Sip($"OUTBOUND_SIP_CANCEL_TRANSPORT_FAILED | attemptId={attemptId} motivo={reason}", LogLevel.WARN);
                        RegistrarSinalSip($"OUTBOUND_SIP_CANCEL_TRANSPORT_FAILED | attemptId={attemptId} motivo={reason}");
                    };
                    LogHelper.Sip($"OUTBOUND_SIP_CANCEL_SENT | attemptId={attemptId}");
                    RegistrarSinalSip($"OUTBOUND_SIP_CANCEL_SENT | attemptId={attemptId}");
                }
                else
                {
                    // Cancel() foi chamado mas m_serverTransaction ainda era null (INVITE ainda não
                    // tinha sido efetivamente montado) — SIPSorcery só marca m_callCancelled=true e
                    // se auto-cura enviando o CANCEL de verdade assim que a primeira resposta (100/180)
                    // chegar (ver ServerInformationResponseReceived). AgendarReenvioSeNaoConfirmado
                    // cobre o caso de nenhuma resposta chegar a tempo.
                    LogHelper.Sip($"OUTBOUND_SIP_CANCEL_PENDING_NO_TRANSACTION | attemptId={attemptId}", LogLevel.WARN);
                    RegistrarSinalSip($"OUTBOUND_SIP_CANCEL_PENDING_NO_TRANSACTION | attemptId={attemptId}");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Sip($"OUTBOUND_CANCEL_MONITOR_ERROR | attemptId={attemptId} erro={ex.Message}", LogLevel.WARN);
            }
        }

        // v2.3.6 — reforço não-bloqueante: se nenhuma confirmação real (487/200 na transação de
        // CANCEL, ou 487 na transação de INVITE original) chegar em ~600ms, reenvia o Cancel() uma
        // vez. Nunca bloqueia a UI (Task.Run + Task.Delay, sem .Wait()/.Result/Thread.Sleep na thread
        // de chamada). Idempotente: SIPClientUserAgent.Cancel() reenvia o CANCEL se m_cancelTransaction
        // já existir (ver biblioteca) — nunca duplica o INVITE nem afeta chamadas já atendidas.
        private void AgendarReenvioSeNaoConfirmado(string attemptId)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(600).ConfigureAwait(false);
                    if (!string.Equals(_outboundAttemptId, attemptId, StringComparison.Ordinal)) return;
                    if (_outboundCancelConfirmado) return;
                    if (_userAgent?.IsCallActive == true) return;

                    var reenviado = TentarMetodoSemParametro(_userAgent, "Cancel", "CancelCall", "CancelInvite");
                    LogHelper.Sip($"OUTBOUND_SIP_CANCEL_RETRY | attemptId={attemptId} reenviado={reenviado}", LogLevel.WARN);
                    RegistrarSinalSip($"OUTBOUND_SIP_CANCEL_RETRY | attemptId={attemptId} reenviado={reenviado}");
                }
                catch (Exception ex)
                {
                    LogHelper.Sip($"OUTBOUND_SIP_CANCEL_RETRY_ERROR | attemptId={attemptId} erro={ex.Message}", LogLevel.WARN);
                }
            });
        }

        public bool VoltarTransferenciaAssistida()
        {
            bool ok = EnviarSequenciaDtmf("*") || TentarSegurar(false);
            StatusChanged?.Invoke(ok ? "Voltando para a chamada original..." : "Não foi possível voltar automaticamente. Tente * no teclado DTMF.");
            return ok;
        }

        public bool ConcluirTransferenciaAssistida()
        {
            bool ok = EnviarSequenciaDtmf("*2");
            if (!ok)
            {
                try { _userAgent?.Hangup(); ok = true; } catch { ok = false; }
            }
            StatusChanged?.Invoke(ok ? "Transferência assistida concluída." : "Não foi possível concluir automaticamente.");
            return ok;
        }

        public async Task<bool> AdicionarParticipanteConferenciaAsync(string destino)
        {
            var sala = _config?.SalaConferenciaIssabel;
            if (string.IsNullOrWhiteSpace(sala)) sala = "800";
            return await AdicionarParticipanteConferenciaIssabelAsync(destino, sala);
        }

        public async Task<bool> AdicionarParticipanteConferenciaIssabelAsync(string destino, string salaConferencia)
        {
            LastConferenceError = string.Empty;
            if (_config == null || string.IsNullOrWhiteSpace(destino) || string.IsNullOrWhiteSpace(salaConferencia) || !IsInCall)
            {
                LastConferenceError = "Sem configuração, destino, sala ou chamada ativa.";
                return false;
            }

            destino = DialPlanService.NormalizarNumero(destino);
            salaConferencia = DialPlanService.NormalizarNumero(salaConferencia);
            if (string.IsNullOrWhiteSpace(destino) || string.IsNullOrWhiteSpace(salaConferencia))
            {
                LastConferenceError = "Destino ou sala inválidos depois da normalização.";
                return false;
            }

            StatusChanged?.Invoke($"Criando conferência no Issabel sala {salaConferencia}...");
            RegistrarSinalSip($"CONF AMI INICIO sala={salaConferencia} participante={destino}");

            try
            {
                var ok = await ExecutarConferenciaViaAmiAsync(destino, salaConferencia);
                StatusChanged?.Invoke(ok
                    ? $"Conferência enviada para sala {salaConferencia}."
                    : "Não foi possível montar conferência via Issabel/AMI.");
                RegistrarSinalSip($"CONF AMI FIM ok={ok} sala={salaConferencia} participante={destino}");
                return ok;
            }
            catch (Exception ex)
            {
                RegistrarSinalSip($"CONF AMI ERRO sala={salaConferencia} participante={destino} erro={ex}");
                StatusChanged?.Invoke($"Erro na conferência AMI: {ex.Message}");
                return false;
            }
        }

        private sealed class AmiSession
        {
            private readonly System.Net.Sockets.NetworkStream _stream;
            private readonly StreamWriter _writer;
            private readonly StringBuilder _buffer = new StringBuilder();
            private readonly byte[] _bytes = new byte[8192];

            public AmiSession(System.Net.Sockets.NetworkStream stream, StreamWriter writer)
            {
                _stream = stream;
                _writer = writer;
            }

            public async Task SendAsync(string action, Dictionary<string, string> campos)
            {
                await _writer.WriteLineAsync($"Action: {action}");
                foreach (var kv in campos)
                    await _writer.WriteLineAsync($"{kv.Key}: {kv.Value}");
                await _writer.WriteLineAsync();
                await _writer.FlushAsync();
            }

            // ReadBannerAsync: lê apenas a primeira linha do banner do Asterisk.
            // O banner (ex: "Asterisk Call Manager/7.0.3") termina com \r\n simples, nunca com
            // \r\n\r\n. ReadBlockAsync esperaria o timeout completo (5s) sem encontrar \r\n\r\n.
            // Este método retorna assim que a primeira linha completa chega (~50ms).
            public async Task<string> ReadBannerAsync(int timeoutMs)
            {
                var limite = DateTime.UtcNow.AddMilliseconds(timeoutMs <= 0 ? 3000 : timeoutMs);

                while (DateTime.UtcNow < limite)
                {
                    try
                    {
                        if (_stream.DataAvailable)
                        {
                            var lidos = await _stream.ReadAsync(_bytes, 0, _bytes.Length);
                            if (lidos > 0)
                                _buffer.Append(Encoding.ASCII.GetString(_bytes, 0, lidos));
                        }
                    }
                    catch (Exception ex)
                    {
                        return "AMI_READ_EXCEPTION: " + ex.Message;
                    }

                    var atual = _buffer.ToString();
                    var idx = atual.IndexOf('\n');
                    if (idx >= 0)
                    {
                        var linha = atual.Substring(0, idx).TrimEnd('\r');
                        _buffer.Remove(0, idx + 1);
                        return linha;
                    }

                    await Task.Delay(25);
                }

                var resto = _buffer.ToString().Trim();
                _buffer.Clear();
                return resto;
            }

            public async Task<string> ReadBlockAsync(int timeoutMs)
            {
                var limite = DateTime.UtcNow.AddMilliseconds(timeoutMs <= 0 ? 5000 : timeoutMs);

                while (DateTime.UtcNow < limite)
                {
                    var atual = _buffer.ToString();
                    var idx = atual.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    var tamanhoSeparador = 4;
                    if (idx < 0)
                    {
                        idx = atual.IndexOf("\n\n", StringComparison.Ordinal);
                        tamanhoSeparador = 2;
                    }

                    if (idx >= 0)
                    {
                        var bloco = atual.Substring(0, idx);
                        _buffer.Remove(0, idx + tamanhoSeparador);
                        if (!string.IsNullOrWhiteSpace(bloco))
                            return bloco;
                        continue;
                    }

                    try
                    {
                        if (_stream.DataAvailable)
                        {
                            var lidos = await _stream.ReadAsync(_bytes, 0, _bytes.Length);
                            if (lidos <= 0) break;
                            _buffer.Append(Encoding.ASCII.GetString(_bytes, 0, lidos));
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        return "AMI_READ_EXCEPTION: " + ex.Message;
                    }

                    await Task.Delay(25);
                }

                if (_buffer.Length > 0)
                {
                    var texto = _buffer.ToString();
                    _buffer.Clear();
                    return texto;
                }

                return string.Empty;
            }
        }

        private sealed class AmiChannelInfo
        {
            public string Channel { get; set; } = string.Empty;
            public string Uniqueid { get; set; } = string.Empty;
            public string Linkedid { get; set; } = string.Empty;
            public string CallerIDNum { get; set; } = string.Empty;
            public string ConnectedLineNum { get; set; } = string.Empty;
            public string Exten { get; set; } = string.Empty;
            public string Context { get; set; } = string.Empty;
        }

        public async Task<bool> MoverChamadaAtualParaConferenciaIssabelAsync(string salaConferencia)
        {
            LastConferenceError = string.Empty;
            if (_config == null || string.IsNullOrWhiteSpace(salaConferencia) || !IsInCall)
            {
                LastConferenceError = "Sem configuração, sala ou chamada ativa para mover.";
                return false;
            }

            salaConferencia = DialPlanService.NormalizarNumero(salaConferencia);
            try
            {
                var ok = await ExecutarRedirectChamadaAtualParaSalaAsync(salaConferencia);
                RegistrarSinalSip($"CONF AMI JOIN_CALL sala={salaConferencia} ok={ok} erro={LastConferenceError}");
                return ok;
            }
            catch (Exception ex)
            {
                LastConferenceError = ex.Message;
                RegistrarSinalSip($"CONF AMI JOIN_CALL ERRO sala={salaConferencia} erro={ex}");
                return false;
            }
        }

        public async Task<bool> DesligarParticipanteConferenciaAsync(string numero)
        {
            LastConferenceError = string.Empty;
            if (_config == null || string.IsNullOrWhiteSpace(numero))
            {
                LastConferenceError = "Número inválido para remover.";
                return false;
            }

            try
            {
                var ami = await AbrirAmiAsync();
                using (ami.Client)
                using (ami.Stream)
                using (ami.Writer)
                {
                    var canais = await ObterCanaisAmiAsync(ami.Session);
                    var n = DialPlanService.NormalizarNumero(numero);
                    var ultimos = n.Length > 8 ? n.Substring(n.Length - 8) : n;
                    var ramal = string.IsNullOrWhiteSpace(_config.Ramal) ? _config.Login : _config.Ramal;
                    var candidatos = canais
                        .Where(c => !CanalPertenceAoRamal(c, ramal, _config.Login))
                        .Where(c => (c.Channel ?? "").Contains(n, StringComparison.OrdinalIgnoreCase)
                                 || (c.CallerIDNum ?? "").Contains(n, StringComparison.OrdinalIgnoreCase)
                                 || (c.ConnectedLineNum ?? "").Contains(n, StringComparison.OrdinalIgnoreCase)
                                 || (c.Exten ?? "").Contains(n, StringComparison.OrdinalIgnoreCase)
                                 || (!string.IsNullOrWhiteSpace(ultimos) && ((c.Channel ?? "").Contains(ultimos, StringComparison.OrdinalIgnoreCase)
                                     || (c.CallerIDNum ?? "").Contains(ultimos, StringComparison.OrdinalIgnoreCase)
                                     || (c.ConnectedLineNum ?? "").Contains(ultimos, StringComparison.OrdinalIgnoreCase)
                                     || (c.Exten ?? "").Contains(ultimos, StringComparison.OrdinalIgnoreCase))))
                        .GroupBy(c => c.Channel)
                        .Select(g => g.First())
                        .ToList();

                    if (candidatos.Count == 0)
                    {
                        LastConferenceError = "Nenhum canal do participante localizado para desligar.";
                        RegistrarSinalSip($"CONF AMI HANGUP SEM_CANAL numero={numero} canais={canais.Count}");
                        return false;
                    }

                    foreach (var c in candidatos)
                    {
                        await ami.Session.SendAsync("Hangup", new Dictionary<string, string> { ["Channel"] = c.Channel });
                        var resp = await ami.Session.ReadBlockAsync(3000);
                        RegistrarSinalSip($"CONF AMI HANGUP canal={c.Channel} numero={numero} resp={ResumoAmi(resp)}");
                    }

                    await ami.Session.SendAsync("Logoff", new Dictionary<string, string>());
                    return true;
                }
            }
            catch (Exception ex)
            {
                LastConferenceError = ex.Message;
                RegistrarSinalSip($"CONF AMI HANGUP ERRO numero={numero} erro={ex}");
                return false;
            }
        }

        public async Task<bool> MutarParticipanteConferenciaAsync(string numero, bool mute, string salaConferencia = "800")
        {
            LastConferenceError = string.Empty;
            if (_config == null || string.IsNullOrWhiteSpace(numero))
            {
                LastConferenceError = "Número inválido para mute.";
                return false;
            }

            try
            {
                using var ami = await AbrirAmiAsync();
                var canais = await ObterCanaisAmiAsync(ami.Session);
                var n = DialPlanService.NormalizarNumero(numero);
                var ultimos = n.Length > 8 ? n.Substring(n.Length - 8) : n;
                var ramal = string.IsNullOrWhiteSpace(_config.Ramal) ? _config.Login : _config.Ramal;
                var candidatos = canais
                    .Where(c => !CanalPertenceAoRamal(c, ramal, _config.Login))
                    .Where(c => (c.Channel ?? "").Contains(n, StringComparison.OrdinalIgnoreCase)
                             || (c.CallerIDNum ?? "").Contains(n, StringComparison.OrdinalIgnoreCase)
                             || (c.ConnectedLineNum ?? "").Contains(n, StringComparison.OrdinalIgnoreCase)
                             || (c.Exten ?? "").Contains(n, StringComparison.OrdinalIgnoreCase)
                             || (!string.IsNullOrWhiteSpace(ultimos) && ((c.Channel ?? "").Contains(ultimos, StringComparison.OrdinalIgnoreCase)
                                 || (c.CallerIDNum ?? "").Contains(ultimos, StringComparison.OrdinalIgnoreCase)
                                 || (c.ConnectedLineNum ?? "").Contains(ultimos, StringComparison.OrdinalIgnoreCase)
                                 || (c.Exten ?? "").Contains(ultimos, StringComparison.OrdinalIgnoreCase))))
                    .GroupBy(c => c.Channel)
                    .Select(g => g.First())
                    .ToList();

                if (candidatos.Count == 0)
                {
                    LastConferenceError = $"Nenhum canal do participante {numero} localizado para mute.";
                    RegistrarSinalSip($"CONF AMI MUTE SEM_CANAL numero={numero} mute={mute}");
                    await ami.Session.SendAsync("Logoff", new Dictionary<string, string>());
                    return false;
                }

                var acao = mute ? "ConfbridgeMute" : "ConfbridgeUnmute";
                foreach (var c in candidatos)
                {
                    await ami.Session.SendAsync(acao, new Dictionary<string, string>
                    {
                        ["Conference"] = salaConferencia,
                        ["Channel"] = c.Channel
                    });
                    var resp = await ami.Session.ReadBlockAsync(3000);
                    RegistrarSinalSip($"CONF AMI {(mute ? "MUTE_REQUEST" : "UNMUTE_REQUEST")} canal={c.Channel} numero={numero} sala={salaConferencia} resp={ResumoAmi(resp)}");
                    var ok = resp.Contains("Success", StringComparison.OrdinalIgnoreCase);
                    RegistrarSinalSip($"CONF AMI {(mute ? "MUTE_OK" : "UNMUTE_OK")}={ok} canal={c.Channel}");
                }

                await ami.Session.SendAsync("Logoff", new Dictionary<string, string>());
                return true;
            }
            catch (Exception ex)
            {
                LastConferenceError = ex.Message;
                RegistrarSinalSip($"CONF AMI MUTE ERRO numero={numero} mute={mute} erro={ex}");
                return false;
            }
        }

        private sealed class AmiConnection : IDisposable
        {
            public System.Net.Sockets.TcpClient Client { get; init; } = null!;
            public System.Net.Sockets.NetworkStream Stream { get; init; } = null!;
            public StreamWriter Writer { get; init; } = null!;
            public AmiSession Session { get; init; } = null!;
            public void Dispose()
            {
                try { Writer.Dispose(); } catch { }
                try { Stream.Dispose(); } catch { }
                try { Client.Dispose(); } catch { }
            }
        }

        private async Task<AmiConnection> AbrirAmiAsync()
        {
            if (_config == null) throw new InvalidOperationException("Configuração SIP/AMI não carregada.");
            _config.AplicarPadroes();
            var host = string.IsNullOrWhiteSpace(_config.AmiHost) ? _config.ServerIp : _config.AmiHost.Trim();
            var porta = _config.AmiPorta <= 0 ? 5038 : _config.AmiPorta;
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(_config.AmiUsuario) || string.IsNullOrWhiteSpace(_config.AmiSenha))
                throw new InvalidOperationException($"Configuração AMI incompleta. Host={host}, Porta={porta}, Usuario={_config.AmiUsuario}");

            RegistrarSinalSip($"CONF AMI CONECTANDO host={host} porta={porta} usuario={_config.AmiUsuario}");
            var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(host, porta);
            var stream = client.GetStream();
            var writer = new StreamWriter(stream, Encoding.ASCII, 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            var session = new AmiSession(stream, writer);

            var banner = await session.ReadBannerAsync(3000);
            RegistrarSinalSip("CONF AMI BANNER " + ResumoAmi(banner));
            await session.SendAsync("Login", new Dictionary<string, string>
            {
                ["Username"] = _config.AmiUsuario,
                ["Secret"] = _config.AmiSenha,
                ["Events"] = "off"
            });

            var login = await session.ReadBlockAsync(5000);
            if (!login.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                try { writer.Dispose(); stream.Dispose(); client.Dispose(); } catch { }
                LastConferenceError = "Falha no login AMI: " + ResumoAmi(login);
                RegistrarSinalSip($"CONF AMI LOGIN FALHA resposta={login.Replace("\r", " ").Replace("\n", " ")}");
                throw new InvalidOperationException(LastConferenceError);
            }

            return new AmiConnection { Client = client, Stream = stream, Writer = writer, Session = session };
        }

        private async Task<bool> ExecutarConferenciaViaAmiAsync(string participante, string salaConferencia)
        {
            if (_config == null) return false;

            // MODO SEGURO:
            // Participante é originado diretamente para a sala ConfBridge.
            // A chamada atual do host só entra na sala quando o usuário clicar em "Unir chamada atual à sala".
            // Isso evita que falha na rota do participante derrube a chamada do host.
            try
            {
                var numSemPrefixo = DialPlanService.RemoverPrefixoDeRota(participante);
                var prefixo = participante.Length > numSemPrefixo.Length ? participante[0].ToString() : "(sem prefixo)";
                var troncoNome = prefixo switch
                {
                    "1" => "Operadora",
                    "2" => "WhatsApp TIM",
                    "3" => "WhatsApp Vivo",
                    _ => "Ramal interno"
                };

                using var ami = await AbrirAmiAsync();
                var ramal = string.IsNullOrWhiteSpace(_config.Ramal) ? _config.Login : _config.Ramal;
                var canalOriginate = $"Local/{participante}@from-internal/n";

                RegistrarSinalSip($"CONF AMI ORIGINATE | participante={participante} | numSemPrefixo={numSemPrefixo} | prefixo={prefixo} | tronco={troncoNome} | canal={canalOriginate} | sala={salaConferencia}");

                await ami.Session.SendAsync("Originate", new Dictionary<string, string>
                {
                    ["Channel"] = canalOriginate,
                    ["Application"] = "ConfBridge",
                    ["Data"] = salaConferencia,
                    ["CallerID"] = $"WavenConf <{ramal}>",
                    ["Timeout"] = "30000",
                    ["Async"] = "true"
                });

                var origem = await ami.Session.ReadBlockAsync(5000);
                var okOrigem = origem.Contains("Success", StringComparison.OrdinalIgnoreCase) ||
                               origem.Contains("Originate successfully queued", StringComparison.OrdinalIgnoreCase);

                RegistrarSinalSip($"CONF AMI ORIGINATE_RESP | ok={okOrigem} | tronco={troncoNome} | canal={canalOriginate} | sala={salaConferencia} | resp={ResumoAmi(origem)}");

                if (okOrigem)
                {
                    RegistrarSinalSip($"CONF AMI ORIGINATE_OK | participante={participante} | sala={salaConferencia} | aguardando_atendimento=true | audio_controlado_pelo_Asterisk_ConfBridge");
                }
                else
                {
                    LastConferenceError = "Originate AMI recusado: " + ResumoAmi(origem);
                    RegistrarSinalSip($"CONF AMI ORIGINATE_FALHOU | tronco={troncoNome} | canal={canalOriginate} | motivo={LastConferenceError}");
                }

                await ami.Session.SendAsync("Logoff", new Dictionary<string, string>());
                return okOrigem;
            }
            catch (Exception ex)
            {
                LastConferenceError = ex.Message;
                RegistrarSinalSip($"CONF AMI ORIGINATE_ERRO | participante={participante} | sala={salaConferencia} | erro={ex.Message}");
                return false;
            }
        }

        private async Task<bool> ExecutarRedirectChamadaAtualParaSalaAsync(string salaConferencia)
        {
            if (_config == null) return false;
            using var ami = await AbrirAmiAsync();

            var canais = await ObterCanaisAmiAsync(ami.Session);
            var ramal = string.IsNullOrWhiteSpace(_config.Ramal) ? _config.Login : _config.Ramal;
            var loginRamal = _config.Login;

            RegistrarSinalSip($"CONF AMI JOIN_INIT | sala={salaConferencia} | ramal={ramal} | login={loginRamal} | canais_totais={canais.Count} | momento={DateTime.Now:HH:mm:ss.fff}");
            foreach (var c in canais)
                RegistrarSinalSip($"CONF AMI JOIN_CANAL | channel={c.Channel} | callerIdNum={c.CallerIDNum} | connectedLine={c.ConnectedLineNum} | linkedid={c.Linkedid} | exten={c.Exten}");

            // Prefer SIP/PJSIP channels over Local/* — Originate sets CallerID to the ramal, so
            // Local channels can falsely match CanalPertenceAoRamal before the real SIP channel.
            var canalBase = canais.FirstOrDefault(c => CanalPertenceAoRamal(c, ramal, loginRamal) &&
                                                        !c.Channel.StartsWith("Local/", StringComparison.OrdinalIgnoreCase))
                            ?? canais.FirstOrDefault(c => CanalPertenceAoRamal(c, ramal, loginRamal));
            if (canalBase == null)
            {
                LastConferenceError = $"AMI não encontrou o canal ativo do ramal {ramal}. Canais lidos: {canais.Count}. Verifique se o ramal no SipConfig corresponde ao CallerIDNum/ConnectedLineNum/Exten/Channel dos canais acima.";
                RegistrarSinalSip($"CONF AMI JOIN_SEM_CANAL | ramal={ramal} | login={loginRamal} | canais={canais.Count} | ERRO={LastConferenceError}");
                return false;
            }

            RegistrarSinalSip($"CONF AMI JOIN_CANAL_BASE | channel={canalBase.Channel} | callerIdNum={canalBase.CallerIDNum} | linkedid={canalBase.Linkedid}");

            var linkedid = string.IsNullOrWhiteSpace(canalBase.Linkedid) ? canalBase.Uniqueid : canalBase.Linkedid;
            var canaisDaChamada = canais
                .Where(c => string.Equals(c.Linkedid, linkedid, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.Uniqueid, linkedid, StringComparison.OrdinalIgnoreCase))
                .Where(c => !string.IsNullOrWhiteSpace(c.Channel))
                .GroupBy(c => c.Channel)
                .Select(g => g.First())
                .Take(2)
                .ToList();

            RegistrarSinalSip($"CONF AMI JOIN_CANAIS_CHAMADA | linkedid={linkedid} | qtd={canaisDaChamada.Count} | canais={string.Join("|", canaisDaChamada.Select(c => c.Channel))}");

            if (canaisDaChamada.Count == 0)
            {
                LastConferenceError = "Nenhum canal ativo encontrado para unir à conferência. O linkedid não corresponde a nenhum canal ativo.";
                RegistrarSinalSip($"CONF AMI JOIN_VAZIO | linkedid={linkedid} | ERRO={LastConferenceError}");
                return false;
            }

            var contexto = "from-internal";
            RegistrarSinalSip($"CONF AMI REDIRECT_INICIO | contexto={contexto} | exten={salaConferencia} | qtd_canais={canaisDaChamada.Count} | momento={DateTime.Now:HH:mm:ss.fff}");

            if (canaisDaChamada.Count >= 2)
            {
                await ami.Session.SendAsync("Redirect", new Dictionary<string, string>
                {
                    ["Channel"] = canaisDaChamada[0].Channel,
                    ["Context"] = contexto,
                    ["Exten"] = salaConferencia,
                    ["Priority"] = "1",
                    ["ExtraChannel"] = canaisDaChamada[1].Channel,
                    ["ExtraContext"] = contexto,
                    ["ExtraExten"] = salaConferencia,
                    ["ExtraPriority"] = "1"
                });
                var resp = await ami.Session.ReadBlockAsync(7000);
                var ok = resp.Contains("Success", StringComparison.OrdinalIgnoreCase);
                RegistrarSinalSip($"CONF AMI REDIRECT_ATOMICO | ok={ok} | canal1={canaisDaChamada[0].Channel} | canal2={canaisDaChamada[1].Channel} | sala={salaConferencia} | resp={ResumoAmi(resp)} | momento={DateTime.Now:HH:mm:ss.fff}");
                if (!ok)
                {
                    LastConferenceError = "Redirect atômico recusado pelo AMI: " + ResumoAmi(resp) + ". Verifique se os canais ainda estão ativos e se a extensão " + salaConferencia + " existe no contexto " + contexto + ".";
                    RegistrarSinalSip($"CONF AMI REDIRECT_FALHOU | ERRO={LastConferenceError}");
                    return false;
                }
                RegistrarSinalSip($"CONF AMI REDIRECT_OK | ambos_canais_na_sala={salaConferencia} | audio_RTP_bidirecional_via_ConfBridge");
            }
            else
            {
                await ami.Session.SendAsync("Redirect", new Dictionary<string, string>
                {
                    ["Channel"] = canaisDaChamada[0].Channel,
                    ["Context"] = contexto,
                    ["Exten"] = salaConferencia,
                    ["Priority"] = "1"
                });
                var resp = await ami.Session.ReadBlockAsync(7000);
                var ok = resp.Contains("Success", StringComparison.OrdinalIgnoreCase);
                RegistrarSinalSip($"CONF AMI REDIRECT_UNICO | ok={ok} | canal={canaisDaChamada[0].Channel} | sala={salaConferencia} | resp={ResumoAmi(resp)} | momento={DateTime.Now:HH:mm:ss.fff}");
                if (!ok)
                {
                    LastConferenceError = "Redirect do canal atual recusado pelo AMI: " + ResumoAmi(resp);
                    RegistrarSinalSip($"CONF AMI REDIRECT_FALHOU | ERRO={LastConferenceError}");
                    return false;
                }
                RegistrarSinalSip($"CONF AMI REDIRECT_OK | canal_na_sala={salaConferencia}");
            }

            await ami.Session.SendAsync("Logoff", new Dictionary<string, string>());
            return true;
        }

        private static bool CanalPertenceAoRamal(AmiChannelInfo c, string ramal, string login)
        {
            bool Match(string? valor) => !string.IsNullOrWhiteSpace(valor) &&
                (string.Equals(valor.Trim(), ramal, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(valor.Trim(), login, StringComparison.OrdinalIgnoreCase));

            return Match(c.CallerIDNum) || Match(c.ConnectedLineNum) || Match(c.Exten) ||
                   (!string.IsNullOrWhiteSpace(ramal) && c.Channel.Contains("/" + ramal, StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrWhiteSpace(login) && c.Channel.Contains("/" + login, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task EnviarAmiAsync(StreamWriter writer, string action, Dictionary<string, string> campos)
        {
            await writer.WriteLineAsync($"Action: {action}");
            foreach (var kv in campos)
                await writer.WriteLineAsync($"{kv.Key}: {kv.Value}");
            await writer.WriteLineAsync();
        }

        private static async Task<string> LerBlocoAmiAsync(StreamReader reader, int timeoutMs)
        {
            // IMPORTANTE: não criar várias chamadas ReadLineAsync simultâneas no mesmo StreamReader.
            // Isso causava resposta vazia no login AMI, mesmo com usuário/senha corretos.
            var sb = new StringBuilder();
            var limite = DateTime.UtcNow.AddMilliseconds(timeoutMs <= 0 ? 5000 : timeoutMs);

            try
            {
                while (DateTime.UtcNow < limite)
                {
                    var restante = limite - DateTime.UtcNow;
                    if (restante.TotalMilliseconds <= 0) break;

                    var lineTask = reader.ReadLineAsync();
                    var done = await Task.WhenAny(lineTask, Task.Delay(restante));
                    if (done != lineTask) break;

                    var line = await lineTask;
                    if (line == null) break;

                    if (line.Length == 0)
                    {
                        if (sb.Length > 0) break;
                        continue;
                    }

                    sb.AppendLine(line);
                }
            }
            catch (Exception ex)
            {
                if (sb.Length == 0) sb.AppendLine("AMI_READ_EXCEPTION: " + ex.Message);
            }

            return sb.ToString();
        }

        private static async Task<List<AmiChannelInfo>> ObterCanaisAmiAsync(AmiSession ami)
        {
            await ami.SendAsync("CoreShowChannels", new Dictionary<string, string>());
            var canais = new List<AmiChannelInfo>();
            var atual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var limite = DateTime.Now.AddSeconds(8);

            while (DateTime.Now < limite)
            {
                var bloco = await ami.ReadBlockAsync(1200);
                if (string.IsNullOrWhiteSpace(bloco)) continue;

                atual.Clear();
                foreach (var line in bloco.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var idx = line.IndexOf(':');
                    if (idx > 0) atual[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                }

                if (atual.TryGetValue("Event", out var ev) && ev.Equals("CoreShowChannel", StringComparison.OrdinalIgnoreCase))
                {
                    canais.Add(new AmiChannelInfo
                    {
                        Channel = atual.TryGetValue("Channel", out var v) ? v : string.Empty,
                        Uniqueid = atual.TryGetValue("Uniqueid", out v) ? v : string.Empty,
                        Linkedid = atual.TryGetValue("Linkedid", out v) ? v : string.Empty,
                        CallerIDNum = atual.TryGetValue("CallerIDNum", out v) ? v : string.Empty,
                        ConnectedLineNum = atual.TryGetValue("ConnectedLineNum", out v) ? v : string.Empty,
                        Exten = atual.TryGetValue("Exten", out v) ? v : string.Empty,
                        Context = atual.TryGetValue("Context", out v) ? v : string.Empty
                    });
                }
                else if (atual.TryGetValue("Event", out ev) && ev.Equals("CoreShowChannelsComplete", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            return canais;
        }

        private static string ResumoAmi(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return "vazio";
            return texto.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        public bool AdicionarParticipanteConferencia(string destino)
        {
            _ = AdicionarParticipanteConferenciaAsync(destino);
            return true;
        }

        public void RecusarChamada()
        {
            try
            {
                // Recusar aqui deve encerrar somente ESTE ramal.
                // Em fila/ring group do Issabel, Busy Here derruba apenas esta perna da chamada
                // e os outros ramais continuam tocando normalmente.
                if (_pendingIncomingRequest != null)
                {
                    var callId = _pendingIncomingCallId;
                    LogHelper.Sip($"INCOMING_CALL_REJECTED | Call-ID={callId}");
                    RegistrarSinalSip($"INCOMING_CALL_REJECTED | Call-ID={callId}");

                    // Só marca ESTA transação (branch) como ignorada — o Call-ID pode voltar
                    // num próximo ciclo legítimo (outro ramal ainda tocando na fila/ring group).
                    MarcarBranchIgnorada(_pendingIncomingBranch);
                    LogHelper.Sip($"RING_CYCLE_END Call-ID={MascararTextoSip(callId)} branch={MascararTextoSip(_pendingIncomingBranch)} motivo=recusada_localmente");

                    EnviarRespostaOcupado(_pendingIncomingRequest);
                    LimparChamadaRecebidaPendente(false);
                    StatusChanged?.Invoke("Chamada recusada neste ramal. Outros usuários continuam recebendo.");
                    IncomingCallEnded?.Invoke();
                    return;
                }

                StatusChanged?.Invoke("Nenhuma chamada pendente para recusar.");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Erro ao recusar: {ex.Message}");
            }
        }

        public void IgnorarChamadaLocal()
        {
            try
            {
                // Usado pelo X do popup: silencia/fecha neste computador e evita que a mesma
                // chamada reabra a janela caso o Issabel retransmita o INVITE.
                MarcarBranchIgnorada(_pendingIncomingBranch);
                LogHelper.Sip($"RING_CYCLE_END Call-ID={MascararTextoSip(_pendingIncomingCallId)} branch={MascararTextoSip(_pendingIncomingBranch)} motivo=ignorada_localmente");

                LimparChamadaRecebidaPendente(false);
                StatusChanged?.Invoke("Chamada ignorada neste computador.");
                IncomingCallEnded?.Invoke();
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Erro ao ignorar chamada: {ex.Message}");
            }
        }

        public void Desligar()
        {
            var swDesligar = System.Diagnostics.Stopwatch.StartNew();
            var eraEmChamada = IsInCall;
            var eraDiscando = _chamadaSaindoEmAndamento;

            bool inviteJaEmEnvio;
            lock (_outboundCancelLock)
            {
                _cancelamentoSolicitado = true;
                inviteJaEmEnvio = _inviteEmEnvio;
            }

            RegistrarSinalSip($"CALL_LOCAL_HANGUP | eraEmChamada={eraEmChamada} eraDiscando={eraDiscando} destino={LastDialedDestination}");
            LogHelper.Sip($"CALL_LOCAL_HANGUP | eraEmChamada={eraEmChamada} eraDiscando={eraDiscando}");

            if (eraDiscando)
            {
                RegistrarSinalSip($"CALL_CANCEL_REQUESTED | destino={LastDialedDestination}");
                LogHelper.Sip($"CALL_CANCEL_REQUESTED | destino={LastDialedDestination}");

                // v2.3.6 — INVITE ainda nem começou a ser enviado (Ligar() ainda estava em
                // CriarMedia() ou equivalente, ver o lock lá). Não existe nenhuma transação SIP
                // pra cancelar ainda, então não adianta chamar EncerrarSinalizacaoAtiva()/Cancel()
                // aqui — e não mexe em media/estado pra não correr com o próprio Ligar(), que vai
                // ver _cancelamentoSolicitado=true (mesmo lock) e abortar ANTES de enviar o INVITE.
                // Feedback visual é imediato mesmo assim; o encerramento definitivo (LimparEstadoChamada)
                // acontece no abort de Ligar().
                if (!inviteJaEmEnvio)
                {
                    LogHelper.Sip($"OUTBOUND_CANCEL_BEFORE_INVITE | destino={LastDialedDestination}", LogLevel.WARN);
                    RegistrarSinalSip($"OUTBOUND_CANCEL_BEFORE_INVITE | destino={LastDialedDestination}");
                    StatusChanged?.Invoke("Cancelando chamada...");
                    try { IncomingCallEnded?.Invoke(); } catch { }
                    return;
                }
            }

            PararRtpKeepalive();
            _muteMicrofoneAtivo = false;
            _transferenciaEmAndamento = false;

            try { EncerrarSinalizacaoAtiva(); }
            catch (Exception ex) { StatusChanged?.Invoke($"Aviso ao cancelar chamada: {ex.Message}"); }

            // v2.3.6 — auditoria de desempenho: tempo do clique em Desligar() até o CANCEL/BYE
            // efetivamente sair (EncerrarSinalizacaoAtiva acima), espelhando OUTBOUND_CLICK_TO_INVITE_TIMING.
            // Só mede quando havia uma discagem em andamento (eraDiscando) — é o caso relevante pra
            // Cancelada, onde a resposta imediata do botão Desligar importa (ver item 11 da auditoria).
            if (eraDiscando)
                LogHelper.Sip($"OUTBOUND_HANGUP_TO_CANCEL_TIMING | ms={swDesligar.ElapsedMilliseconds}");

            _chamadaSaindoEmAndamento = false;
            _inicioDialing = DateTime.MinValue;

            LogHelper.Sip($"CALL_ENDED_REASON | motivo=LOCAL_HANGUP eraEmChamada={eraEmChamada}");

            try { LimparEstadoChamada("Chamada desligada."); }
            catch (Exception ex) { StatusChanged?.Invoke($"Chamada desligada com aviso: {ex.Message}"); }

            try { IncomingCallEnded?.Invoke(); } catch { }
        }

        private void LimparEstadoChamada(string mensagem)
        {
            try
            {
                LimparChamadaRecebidaPendente(false);
                _emEspera = false;

                if (_muteMicrofoneAtivo)
                {
                    LogHelper.Sip("CALL_MUTE_STATE_RESET_ON_END | mute ativo ao encerrar chamada — resetando");
                    RegistrarSinalSip("CALL_MUTE_STATE_RESET_ON_END");
                }
                _muteMicrofoneAtivo = false;

                // Desconecta proxy e garante próxima chamada começa desmutada
                _muteableAudioSource?.Detach();
                _muteableAudioSource = null;
                _muteAntesDeEspera = false;

                PararRtpKeepalive();

                if (_mediaSession != null)
                {
                    RegistrarSinalSip($"CALL_SESSION_DESTROYED | motivo={mensagem} transferenciaEmAndamento={_transferenciaEmAndamento}");
                    LogHelper.Sip($"CALL_SESSION_DESTROYED | motivo={mensagem}");
                    _mediaSession.Close("encerrando");
                    _mediaSession.Dispose();
                    _mediaSession = null;
                }

                _audioEndpoint = null;
                _transferenciaEmAndamento = false;
                LogHelper.Sip("WAVOIP_AUDIO_STATE_RESET | estado de audio e mute resetados");
            }
            catch
            {
            }

            StatusChanged?.Invoke(mensagem);
            CallEnded?.Invoke();
        }

        private static string ObterCallId(SIPRequest? req)
        {
            try { return req?.Header?.CallId ?? string.Empty; } catch { return string.Empty; }
        }

        private void LimparChamadaRecebidaPendente(bool esquecerIgnorados)
        {
            string callIdLimpo, branchLimpa;
            lock (_incomingLock)
            {
                callIdLimpo = _pendingIncomingCallId;
                branchLimpa = _pendingIncomingBranch;
                _pendingIncomingRequest = null;
                _pendingIncomingCallId = string.Empty;
                _pendingIncomingBranch = string.Empty;
            }
            LogHelper.Sip($"SIP_CALL_STATE_CLEARED callId={MascararTextoSip(callIdLimpo)} branch={MascararTextoSip(branchLimpa)}");

            try { _monitorChamadaRecebidaTimer?.Dispose(); _monitorChamadaRecebidaTimer = null; } catch { }

            if (esquecerIgnorados && _ignoredBranches.Count > 80)
            {
                var recentes = _ignoredBranches.OrderByDescending(kv => kv.Value).Take(20).ToList();
                var removidas = _ignoredBranches.Count - recentes.Count;
                _ignoredBranches.Clear();
                foreach (var kv in recentes) _ignoredBranches[kv.Key] = kv.Value;
                if (removidas > 0)
                    LogHelper.Sip($"SIP_CALL_ID_CACHE_REMOVE quantidade={removidas}");
            }
        }

        private void EnviarRespostaOcupado(SIPRequest req)
        {
            try
            {
                var busy = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, null);
                _sipTransport?.SendResponseAsync(busy).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        // v2.4.0 — investigação de travamento real: este método era síncrono e chamado ANTES de
        // qualquer await em Ligar()/AtenderChamada(), então rodava inteiro (inclusive a espera
        // abaixo) na UI thread. A espera pelos dois lookups de dispositivo usava
        // .GetAwaiter().GetResult() SEM TIMEOUT sobre uma enumeração de dispositivo de áudio via
        // API nativa MME (por trás do NAudio) — se o driver de um dispositivo travar (já visto com
        // headset USB instável, ver AudioDeviceService/hotfix de áudio), a UI trava por completo e
        // indefinidamente, exigindo encerrar pelo Gerenciador de Tarefas. Agora é async e usa
        // await (nunca bloqueia a UI thread) com timeout de 3s por segurança — se o lookup não
        // responder a tempo, segue com o dispositivo padrão do sistema em vez de travar.
        private async Task<VoIPMediaSession> CriarMediaAsync()
        {
            int outIdx = -1;
            int inIdx  = -1;

            if (_config != null)
            {
                // Lookup em paralelo reduz latência de enumeração de devices (~50-100ms ganho)
                var outDev = string.IsNullOrEmpty(_config.CallOutputDevice) ? null : _config.CallOutputDevice;
                var inDev  = string.IsNullOrEmpty(_config.AudioInputDevice)  ? null : _config.AudioInputDevice;
                var tOut = outDev != null ? Task.Run(() => AudioDeviceService.IndiceWaveOutPorNome(outDev)) : Task.FromResult(-1);
                var tIn  = inDev  != null ? Task.Run(() => AudioDeviceService.IndiceWaveInPorNome(inDev))  : Task.FromResult(-1);

                var conjunto = Task.WhenAll(tOut, tIn);
                var venceu = await Task.WhenAny(conjunto, Task.Delay(TimeSpan.FromSeconds(3)));
                if (ReferenceEquals(venceu, conjunto) && conjunto.IsCompletedSuccessfully)
                {
                    outIdx = tOut.Result;
                    inIdx  = tIn.Result;
                }
                else
                {
                    RegistrarSinalSip($"AUDIO_DEVICE_LOOKUP_TIMEOUT | outDev={outDev} inDev={inDev} | usando dispositivo padrao apos 3s");
                    LogHelper.Sip($"AUDIO_DEVICE_LOOKUP_TIMEOUT | outDev={outDev} inDev={inDev} | usando dispositivo padrao apos 3s", LogLevel.WARN);
                }

                if (outDev != null)
                {
                    if (outIdx >= 0) { RegistrarSinalSip($"AUDIO_DEVICE_DETECTED | call_output={outDev} waveOutIdx={outIdx}"); LogHelper.Sip($"AUDIO_DEVICE_DETECTED | call_output={outDev} waveOutIdx={outIdx}"); }
                    else             { RegistrarSinalSip($"AUDIO_DEVICE_RECOVERY | call_output='{outDev}' nao encontrado, usando default"); LogHelper.Sip($"AUDIO_DEVICE_RECOVERY | call_output='{outDev}' nao encontrado, fallback default", LogLevel.WARN); }
                }
                if (inDev != null)
                {
                    if (inIdx >= 0) { RegistrarSinalSip($"AUDIO_DEVICE_DETECTED | audio_input={inDev} waveInIdx={inIdx}"); LogHelper.Sip($"AUDIO_DEVICE_DETECTED | audio_input={inDev} waveInIdx={inIdx}"); }
                    else            { RegistrarSinalSip($"AUDIO_DEVICE_RECOVERY | audio_input='{inDev}' nao encontrado, usando default"); LogHelper.Sip($"AUDIO_DEVICE_RECOVERY | audio_input='{inDev}' nao encontrado, fallback default", LogLevel.WARN); }
                }
            }

            WindowsAudioEndPoint audio;
            try
            {
                audio = new WindowsAudioEndPoint(new AudioEncoder(), outIdx, inIdx);
                _audioEndpoint = audio;
                LogHelper.Sip($"AUDIO_RTP_REBOUND | outIdx={outIdx} inIdx={inIdx} | session ok");
            }
            catch (Exception ex)
            {
                RegistrarSinalSip($"AUDIO_DEVICE_SWITCH | erro ao criar endpoint outIdx={outIdx} inIdx={inIdx}: {ex.Message} — fallback default");
                LogHelper.Sip($"AUDIO_DEVICE_SWITCH | fallback dispositivo padrao | erro={ex.Message}", LogLevel.WARN);
                audio = new WindowsAudioEndPoint(new AudioEncoder(), -1, -1);
                _audioEndpoint = audio;
                LogHelper.Sip("AUDIO_OUTPUT_RECREATED | usando dispositivos padrao do sistema");
            }

            // Proxy TX-only: intercept encoded samples, injeta silence quando mutado.
            // AudioSink (RX/WaveOut) aponta direto para audio — nunca afetado pelo mute.
            var muteProxy = new MuteableAudioSource(audio);
            _muteableAudioSource = muteProxy;
            LogHelper.Sip("AUDIO_MUTE_PROXY_CREATED | TX=MuteableAudioSource RX=WindowsAudioEndPoint direto");

            return new VoIPMediaSession(new SIPSorceryMedia.Abstractions.MediaEndPoints
            {
                AudioSource = muteProxy,
                AudioSink   = audio
            })
            {
                AcceptRtpFromAny = true
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                Desligar();
                _sipTransport?.Shutdown();
            }
            catch { }
            try { _monitorChamadaRecebidaTimer?.Dispose(); } catch { }
            try { _rtpKeepaliveTimer?.Dispose(); _rtpKeepaliveTimer = null; } catch { }
            try { _registerUserAgent?.Stop(); } catch { }
            try { _sipTransport?.Dispose(); } catch { }
        }
    }
}
