using System.ComponentModel;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WF = System.Windows.Forms;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class DialerShellWindow : Window
    {
        private readonly SipService _sipService;
        private bool _dndAtivo; // usado como Offline/Online visual
        private IncomingCallWindow? _incomingPopup;
        private CallWindow? _activeCallWindow;
        private ConferenceControlWindow? _conferenceControlWindow;
        private TaskCompletionSource<SaidaChamada?>? _routeSelectorTcs;
        private WF.NotifyIcon? _trayIcon;
        private bool _fechamentoReal;
        private readonly DispatcherTimer _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        private readonly DispatcherTimer _amiSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        private readonly DispatcherTimer _googleSyncTimer = new DispatcherTimer();
        private readonly DispatcherTimer _cdrSyncTimer = new DispatcherTimer();
        private string _historicoChamadaAtivaId = string.Empty;
        private DateTime _inicioHistoricoChamadaAtiva = DateTime.MinValue;
        private string _ultimaOrigemEntradaPendente = string.Empty;
        private bool _autoJoinFired;
        private RingtoneService? _testRingtoneService;

        // Dashboard / missed call state
        private int _badgePendentes = 0;
        private readonly System.Collections.Generic.HashSet<string> _perdidasJaMostradas = new(StringComparer.OrdinalIgnoreCase);
        private MissedCallPopup? _missedCallPopup;

        // ── System log panel ─────────────────────────────────────────────────────
        private readonly List<LogEntry>                     _allLogs  = new(2001);
        private readonly ObservableCollection<LogViewModel> _logItems = new();
        private string                                       _activeFilter = "ALL";

        private sealed class LogViewModel
        {
            public string Text   { get; init; } = "";
            public Brush  Color  { get; init; } = null!;
            public LogEntry Source { get; init; } = null!;
        }

        private void RegistrarUiDiagnostico(string mensagem)
        {
            try { Services.LogHelper.Info(mensagem); }
            catch { }
        }

        public DialerShellWindow(SipService sipService)
        {
            _sipService = sipService ?? throw new ArgumentNullException(nameof(sipService));
            InitializeComponent();
            Loaded += DialerShellWindow_Loaded;
            StateChanged += DialerShellWindow_StateChanged;
            ConfigurarBandeja();
            Closing += DialerShellWindow_Closing;
            ConfigurarReconexaoAutomatica();

            _sipService.StatusChanged += status =>
            {
                Dispatcher.Invoke(() => txtStatus.Text = status);
            };

            _sipService.IncomingCallReceived += caller =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_incomingPopup != null)
                        return;
                    if (!_sipService.PossuiChamadaRecebidaPendente)
                        return;

                    _ultimaOrigemEntradaPendente = _sipService.LastIncomingOrigin;
                    var displayCaller = ResolverDisplayChamada(caller);
                    var tela = new IncomingCallWindow(displayCaller);
                    _incomingPopup = tela;
                    bool acaoExecutada = false;

                    tela.AtenderSolicitado += async () =>
                    {
                        if (acaoExecutada) return;
                        acaoExecutada = true;

                        if (_sipService.PossuiChamadaRecebidaPendente)
                        {
                            bool atendeu = await _sipService.AtenderChamada();
                            if (atendeu)
                            {
                                var call = CriarTelaDeChamada(displayCaller, "Em chamada");
                                _activeCallWindow = call;
                                call.Closed += (_, __) => { if (ReferenceEquals(_activeCallWindow, call)) _activeCallWindow = null; };
                                call.IniciarContador();
                                call.Show();
                                IniciarControleHistoricoChamada(RegistrarHistorico(caller, TipoHistoricoLigacao.Recebida, "Em andamento", OrigemEntradaAtual()));
                            }
                        }
                    };

                    tela.RecusarSolicitado += () =>
                    {
                        if (acaoExecutada) return;
                        acaoExecutada = true;

                        if (_sipService.PossuiChamadaRecebidaPendente)
                        {
                            _sipService.RecusarChamada();
                            RegistrarHistorico(caller, TipoHistoricoLigacao.Perdida, "00:00", OrigemEntradaAtual());
                            MostrarNotificacaoChamadaPerdida(displayCaller);
                        }
                    };

                    tela.Closed += (_, __) =>
                    {
                        if (ReferenceEquals(_incomingPopup, tela))
                            _incomingPopup = null;

                        if (!acaoExecutada && !tela.EncerradaPeloSistema && _sipService.PossuiChamadaRecebidaPendente)
                        {
                            _sipService.IgnorarChamadaLocal();
                        }
                    };

                    tela.Show();
                    RegistrarUiDiagnostico($"EXTENSION_RING_POPUP_SHOWN caller={caller}");
                }));
            };

            _sipService.IncomingCallEnded += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (_incomingPopup != null)
                        {
                            _incomingPopup.FecharPorSistema();
                            _incomingPopup = null;
                        }
                    }
                    catch { }
                });
            };

            _sipService.CallEnded += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        AtualizarDuracaoHistoricoChamadaAtiva();
                        if (_activeCallWindow != null)
                        {
                            _activeCallWindow.DefinirStatus("Chamada encerrada");
                            _activeCallWindow.FecharPorSistema();
                            _activeCallWindow = null;
                        }
                        if (_conferenceControlWindow != null && _conferenceControlWindow.IsVisible)
                        {
                            RegistrarUiDiagnostico("CONF AUTO_CLOSE chamada encerrada");
                            _conferenceControlWindow.Close();
                            _conferenceControlWindow = null;
                        }
                        _autoJoinFired = false;
                        txtStatus.Text = "Chamada encerrada pelo Issabel/cliente.";
                    }
                    catch { }
                });

                // Sync CDR at 5 s, 15 s and 30 s after hangup — Asterisk/recording may take time to close
                foreach (var delay in new[] { 5000, 15000, 30000 })
                {
                    var d = delay;
                    _ = Task.Delay(d).ContinueWith(_ =>
                    {
                        try { Dispatcher.BeginInvoke(new Action(async () => await ExecutarSyncCdrAsync(silencioso: true, diasOverride: 1))); }
                        catch { }
                    });
                }
            };
        }

        private void DialerShellWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                AtualizarNavSelecionada();
                AtualizarPlaceholderNumero();
                txtNumero.TextChanged += (_, __) => AtualizarPlaceholderNumero();

                try
                {
                    var cfg = SipConfig.CarregarSalva();
                    try { AtualizarBotaoStatus(); } catch { }
                }
                catch { }

                CarregarPreferenciasNaTela();
                CarregarDispositivosAudioNaTela();
                AplicarRetencaoHistorico();
                CarregarDadosDasAbas();
                ConfigurarSincronizacaoAmi();
                ConfigurarSincronizacaoGoogle();
                ConfigurarSincronizacaoCdr();
                _ = SincronizarRamaisAmiAsync(false);
                _ = CarregarContatosGoogleCacheAsync();
                // Fix concatenated numbers on startup (fast, no HTTP validation)
                _ = ExecutarReprocessarAsync(silencioso: true, validarUrls: false);
                InicializarPainelLogs();
                IntegrationAutoReconnectService.ReconectarAgora += OnIntegracaoReconectarAgora;
                Closed += (_, _) => IntegrationAutoReconnectService.ReconectarAgora -= OnIntegracaoReconectarAgora;
            }
            catch (Exception ex)
            {
                try { txtStatus.Text = $"Falha ao carregar dados da tela: {ex.Message}"; } catch { }
            }
        }

        private void AtualizarPlaceholderNumero()
        {
            if (txtNumeroPH == null || txtNumero == null) return;
            txtNumeroPH.Visibility = string.IsNullOrEmpty(txtNumero.Text) ? Visibility.Visible : Visibility.Collapsed;
        }



        private void ConfigurarReconexaoAutomatica()
        {
            _reconnectTimer.Tick += (_, __) =>
            {
                try
                {
                    if (!_sipService.IsRegistered)
                    {
                        txtStatus.Text = "Reconectando ao Issabel...";
                        _sipService.Registrar();
                    }
                }
                catch { }
            };
            _reconnectTimer.Start();
        }

        private void ConfigurarSincronizacaoAmi()
        {
            try
            {
                var config = SipConfig.CarregarSalva() ?? new SipConfig();
                _amiSyncTimer.Tick -= AmiSyncTimer_Tick;
                _amiSyncTimer.Tick += AmiSyncTimer_Tick;

                var segundos = config.AmiSyncIntervalSeconds;
                if (config.AmiAtivo && segundos > 0)
                {
                    _amiSyncTimer.Interval = TimeSpan.FromSeconds(segundos);
                    _amiSyncTimer.Start();
                }
                else
                {
                    _amiSyncTimer.Stop();
                }
            }
            catch { }
        }

        private void AmiSyncTimer_Tick(object? sender, EventArgs e)
        {
            _ = SincronizarRamaisAmiAsync(false);
        }

        private void AtualizarStatusAmiContatos(string texto, string corHex)
        {
            try
            {
                if (txtAmiStatusContatos != null)
                    txtAmiStatusContatos.Text = texto;

                if (bolinhaAmiContatos != null)
                    bolinhaAmiContatos.Background = (SolidColorBrush)new BrushConverter().ConvertFromString(corHex)!;
            }
            catch { }
        }

        private SipConfig MontarConfigAmiAtual()
        {
            var config = SipConfig.CarregarSalva() ?? new SipConfig();

            try
            {
                if (chkAmiAtivo != null)
                    config.AmiAtivo = chkAmiAtivo.IsChecked == true;

                if (txtAmiHost != null)
                    config.AmiHost = txtAmiHost.Text?.Trim() ?? string.Empty;

                if (txtAmiPorta != null && int.TryParse(txtAmiPorta.Text?.Trim(), out var porta) && porta > 0)
                    config.AmiPorta = porta;

                if (txtAmiUsuario != null)
                    config.AmiUsuario = txtAmiUsuario.Text?.Trim() ?? string.Empty;

                if (txtAmiSenha != null)
                    config.AmiSenha = txtAmiSenha.Password?.Trim() ?? string.Empty;

                if (cmbAmiIntervalo?.SelectedItem is ComboBoxItem cmbItem && cmbItem.Tag is string cmbTag && int.TryParse(cmbTag, out var min))
                    config.AmiIntervaloMinutos = min;

                if (string.IsNullOrWhiteSpace(config.AmiHost))
                    config.AmiHost = config.ServerIp;

                if (config.AmiPorta <= 0)
                    config.AmiPorta = 5038;

                config.Salvar();
            }
            catch { }

            return config;
        }

        private async Task SincronizarRamaisAmiAsync(bool mostrarMensagem)
        {
            try
            {
                var config = MontarConfigAmiAtual();
                if (!config.AmiAtivo || string.IsNullOrWhiteSpace(config.AmiHost) || string.IsNullOrWhiteSpace(config.AmiUsuario) || string.IsNullOrWhiteSpace(config.AmiSenha))
                {
                    AtualizarStatusAmiContatos("AMI: configure host, usuário, senha e marque Ativar", "#94A3B8");
                    if (mostrarMensagem)
                        MessageBox.Show("Marque 'Ativar sincronização AMI' e preencha Host, Porta, Usuário e Senha.\n\nDepois clique em Testar AMI ou Sincronizar ramais AMI.", "Waven VoIP");
                    return;
                }

                txtStatus.Text = "Sincronizando ramais do Issabel via AMI...";
                AtualizarStatusAmiContatos($"AMI: conectando em {config.AmiHost}:{config.AmiPorta}...", "#F59E0B");
                var ramais = await AmiRamalSyncService.BuscarRamaisAsync(config);
                var alterados = ContatoStorageService.SincronizarRamaisIssabel(ramais);
                AtualizarContatosShell();
                txtStatus.Text = $"AMI sincronizado: {ramais.Count} ramais encontrados, {alterados} contatos atualizados.";
                AtualizarStatusAmiContatos($"AMI: online • {ramais.Count} ramais encontrados • {alterados} contatos atualizados", "#22C55E");
                IntegrationStatusService.Atualizar(IntegracaoNome.Ami, IntegracaoStatus.Conectado);
                if (txtAmiSyncStatus != null)
                    txtAmiSyncStatus.Text = $"AMI atualizado • {ramais.Count} ramais • {alterados} contatos";

                if (mostrarMensagem)
                    MessageBox.Show($"AMI funcionando!\n\nRamais encontrados: {ramais.Count}\nContatos atualizados: {alterados}", "Waven VoIP");
            }
            catch (Exception ex)
            {
                var falha = IntegrationFailureClassifier.Classificar(ex);
                txtStatus.Text = "Falha ao sincronizar ramais via AMI.";
                Services.LogHelper.Info($"[AMI_DISCONNECTED] falha={falha} {ex.Message}");

                if (falha == FalhaTipo.Temporaria)
                {
                    Services.LogHelper.Info($"[INTEGRATION_TEMPORARY_FAILURE] Ami — {ex.Message}");
                    IntegrationStatusService.Atualizar(IntegracaoNome.Ami, IntegracaoStatus.Reconectando);
                    AtualizarStatusAmiContatos("AMI: reconectando automaticamente…", "#F59E0B");
                    if (txtAmiSyncStatus != null) txtAmiSyncStatus.Text = "AMI: reconectando…";
                    if (!mostrarMensagem)
                    {
                        Services.LogHelper.Info($"[INTEGRATION_MODAL_SUPPRESSED_TEMPORARY] Ami");
                        IntegrationAutoReconnectService.AgendarReconexao(IntegracaoNome.Ami);
                    }
                    else
                        MessageBox.Show("Falha temporária ao conectar AMI:\n\n" + ex.Message +
                            "\n\nO sistema tentará reconectar automaticamente.",
                            "Waven VoIP - AMI", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    Services.LogHelper.Info($"[INTEGRATION_ACTION_REQUIRED] Ami — {ex.Message}");
                    Services.LogHelper.Info($"[INTEGRATION_MODAL_SHOWN_AUTH_FAILURE] Ami");
                    IntegrationStatusService.Atualizar(IntegracaoNome.Ami, IntegracaoStatus.Erro);
                    AtualizarStatusAmiContatos("AMI: ação necessária", "#EF4444");
                    if (txtAmiSyncStatus != null) txtAmiSyncStatus.Text = "AMI: ação necessária";
                    if (!mostrarMensagem)
                        MostrarModalIntegracao(IntegracaoNome.Ami, "AMI — Asterisk Manager",
                            "Ação necessária: verifique usuário, senha ou permissões do AMI.",
                            onReconectar: null);
                    else
                        MessageBox.Show("Falha de autenticação AMI:\n\n" + ex.Message +
                            "\n\nConfira usuário, senha e permissões (system, command, reporting).",
                            "Waven VoIP - AMI", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private async void BtnTestarAmiContatos_Click(object sender, RoutedEventArgs e)
        {
            await SincronizarRamaisAmiAsync(true);
        }

        private async void BtnSincronizarRamaisAmi_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            object? orig = null;
            if (btn != null) { orig = btn.Content; btn.IsEnabled = false; btn.Content = "Sincronizando..."; }
            if (txtAmiSyncStatus != null) txtAmiSyncStatus.Text = "Sincronizando...";
            try
            {
                await SincronizarRamaisAmiAsync(false);
            }
            finally
            {
                if (btn != null) { btn.IsEnabled = true; btn.Content = orig; }
            }
        }

        public Task ReconectarAmiPublicoAsync() => SincronizarRamaisAmiAsync(false);
        public Task ReconectarCdrPublicoAsync() => ExecutarSyncCdrAsync(silencioso: false);

        private async void BtnAtualizarContatos_Click(object sender, RoutedEventArgs e)
        {
            if (btnAtualizarContatos != null) { btnAtualizarContatos.IsEnabled = false; }
            if (txtBtnAtualizarLabel != null) txtBtnAtualizarLabel.Text = "Atualizando...";
            if (txtStatusAtualizacao != null) txtStatusAtualizacao.Text = string.Empty;
            RegistrarUiDiagnostico("CONTATOS_REFRESH_START");

            var amiRamais = 0;
            var googleImportados = 0;

            try
            {
                // AMI
                try
                {
                    var cfg = MontarConfigAmiAtual();
                    if (cfg.AmiAtivo && !string.IsNullOrWhiteSpace(cfg.AmiHost) &&
                        !string.IsNullOrWhiteSpace(cfg.AmiUsuario) && !string.IsNullOrWhiteSpace(cfg.AmiSenha))
                    {
                        var ramais = await AmiRamalSyncService.BuscarRamaisAsync(cfg);
                        ContatoStorageService.SincronizarRamaisIssabel(ramais);
                        amiRamais = ramais.Count;
                        RegistrarUiDiagnostico($"CONTATOS_REFRESH_AMI_OK quantidade={amiRamais}");
                        AtualizarStatusAmiContatos($"AMI: online • {amiRamais} ramais", "#22C55E");
                    }
                }
                catch (Exception ex) { RegistrarUiDiagnostico($"CONTATOS_REFRESH_AMI_FAIL erro={ex.Message}"); }

                // Google
                if (GoogleContactsService.EstaConectado())
                {
                    try
                    {
                        var (contatos, _) = await Task.Run(() => GoogleContactsService.SincronizarContatosAsync());
                        googleImportados = ContatoStorageService.SincronizarContatosGoogle(contatos);
                        AtualizarStatusGoogle();
                        RegistrarUiDiagnostico($"CONTATOS_REFRESH_GOOGLE_OK quantidade={googleImportados}");
                    }
                    catch (Exception ex) { RegistrarUiDiagnostico($"CONTATOS_REFRESH_GOOGLE_FAIL erro={ex.Message}"); }
                }

                AtualizarContatosShell();
                RegistrarUiDiagnostico("CONTATOS_REFRESH_DONE");

                var partes = new System.Collections.Generic.List<string> { "Atualizado agora" };
                if (amiRamais > 0) partes.Add($"AMI: {amiRamais} ramais");
                if (GoogleContactsService.EstaConectado()) partes.Add($"Google: {googleImportados} contatos");
                if (txtStatusAtualizacao != null) txtStatusAtualizacao.Text = string.Join(" • ", partes);
            }
            catch (Exception ex)
            {
                RegistrarUiDiagnostico($"CONTATOS_REFRESH_FAIL erro={ex.Message}");
                if (txtStatusAtualizacao != null) txtStatusAtualizacao.Text = "Falha ao atualizar contatos";
            }
            finally
            {
                if (txtBtnAtualizarLabel != null) txtBtnAtualizarLabel.Text = "Atualizar";
                if (btnAtualizarContatos != null) btnAtualizarContatos.IsEnabled = true;
            }
        }

        private void CmbGoogleSyncInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var minutos = 0;
                if (cmbGoogleSyncInterval?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                    int.TryParse(tag, out minutos);
                AplicarTimerGoogleSync(minutos);
            }
            catch { }
        }

        private void ConfigurarSincronizacaoGoogle()
        {
            try
            {
                var config = SipConfig.CarregarSalva() ?? new SipConfig();
                AplicarTimerGoogleSync(config.GoogleSyncIntervalSeconds);
            }
            catch { }
        }

        private void AplicarTimerGoogleSync(int segundos)
        {
            _googleSyncTimer.Stop();
            _googleSyncTimer.Tick -= GoogleSyncTimer_Tick;
            if (segundos > 0 && GoogleContactsService.EstaConectado())
            {
                _googleSyncTimer.Interval = TimeSpan.FromSeconds(segundos);
                _googleSyncTimer.Tick += GoogleSyncTimer_Tick;
                _googleSyncTimer.Start();
            }
        }

        private void GoogleSyncTimer_Tick(object? sender, EventArgs e)
            => _ = Task.Run(TentarSincronizarGoogleSilenciosoAsync);

        private async Task TentarSincronizarGoogleSilenciosoAsync()
        {
            try
            {
                var (contatos, _) = await GoogleContactsService.SincronizarSemBrowserAsync();
                ContatoStorageService.SincronizarContatosGoogle(contatos);
                IntegrationStatusService.Atualizar(IntegracaoNome.Google, IntegracaoStatus.Conectado);
                Services.LogHelper.Info("[INTEGRATION_AUTO_RECONNECT_SUCCESS] Google");
                Dispatcher.Invoke(() => { AtualizarContatosShell(); AtualizarStatusGoogle(); });
            }
            catch (UnauthorizedAccessException)
            {
                Services.LogHelper.Info("[INTEGRATION_ACTION_REQUIRED] Google — Token invalido/ausente");
                Services.LogHelper.Info("[INTEGRATION_MODAL_SHOWN_AUTH_FAILURE] Google");
                IntegrationStatusService.Atualizar(IntegracaoNome.Google, IntegracaoStatus.Erro);
                Dispatcher.Invoke(() =>
                {
                    _googleSyncTimer.Stop();
                    AtualizarStatusGoogle();
                    MostrarModalIntegracao(
                        IntegracaoNome.Google,
                        "Google Contacts",
                        "Ação necessária: o acesso ao Google Contacts foi revogado. Clique em Reconectar para reautenticar.",
                        () => _ = BtnConectarGoogleAsync());
                });
            }
            catch (Exception ex)
            {
                var falha = IntegrationFailureClassifier.Classificar(ex);
                Services.LogHelper.Info($"[GOOGLE_SYNC_FAIL] falha={falha} {ex.Message}");
                if (falha == FalhaTipo.Temporaria)
                {
                    Services.LogHelper.Info($"[INTEGRATION_TEMPORARY_FAILURE] Google — {ex.Message}");
                    Services.LogHelper.Info($"[INTEGRATION_MODAL_SUPPRESSED_TEMPORARY] Google");
                    IntegrationStatusService.Atualizar(IntegracaoNome.Google, IntegracaoStatus.Reconectando);
                    IntegrationAutoReconnectService.AgendarReconexao(IntegracaoNome.Google);
                    Dispatcher.Invoke(AtualizarStatusGoogle);
                }
                else
                {
                    Services.LogHelper.Info($"[INTEGRATION_ACTION_REQUIRED] Google — {ex.Message}");
                    Services.LogHelper.Info($"[INTEGRATION_MODAL_SHOWN_AUTH_FAILURE] Google");
                    IntegrationStatusService.Atualizar(IntegracaoNome.Google, IntegracaoStatus.Erro);
                    Dispatcher.Invoke(() =>
                    {
                        _googleSyncTimer.Stop();
                        AtualizarStatusGoogle();
                        MostrarModalIntegracao(
                            IntegracaoNome.Google,
                            "Google Contacts",
                            "Ação necessária: falha de autenticação com o Google. Clique em Reconectar para reautenticar.",
                            () => _ = BtnConectarGoogleAsync());
                    });
                }
            }
        }

        private async Task BtnConectarGoogleAsync()
        {
            try
            {
                if (btnConectarGoogle != null) btnConectarGoogle.IsEnabled = false;
                if (txtGoogleStatus != null) txtGoogleStatus.Text = "Google: autenticando...";

                var (contatos, total) = await Task.Run(() => GoogleContactsService.SincronizarContatosAsync());
                var importados = ContatoStorageService.SincronizarContatosGoogle(contatos);
                IntegrationStatusService.Atualizar(IntegracaoNome.Google, IntegracaoStatus.Conectado);
                AplicarTimerGoogleSync(SipConfig.CarregarSalva()?.GoogleSyncIntervalSeconds ?? 0);
                AtualizarContatosShell();
                AtualizarStatusGoogle();
                txtStatus.Text = $"Google conectado: {total} contatos encontrados, {importados} importados.";
            }
            catch (Exception ex)
            {
                IntegrationStatusService.Atualizar(IntegracaoNome.Google, IntegracaoStatus.Erro);
                AtualizarStatusGoogle();
                if (txtGoogleStatus != null) txtGoogleStatus.Text = "Google: falha na autenticação";
                TratarErroSemTravamento(ex, $"Falha ao conectar Google:\n{ex.Message}");
            }
            finally
            {
                if (btnConectarGoogle != null) btnConectarGoogle.IsEnabled = true;
            }
        }

        private void MostrarModalIntegracao(IntegracaoNome integracao, string nomeVisual, string mensagem, Action? onReconectar)
        {
            if (IntegrationStatusService.ModalJaMostrado(integracao)) return;
            IntegrationStatusService.MarcarModalMostrado(integracao);
            var overlay = new IntegrationDisconnectedOverlay(integracao, nomeVisual, mensagem, this, onReconectar);
            overlay.Show();
        }

        private void OnIntegracaoReconectarAgora(IntegracaoNome nome)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                switch (nome)
                {
                    case IntegracaoNome.Ami:
                        await SincronizarRamaisAmiAsync(false);
                        break;
                    case IntegracaoNome.Cdr:
                        await ExecutarSyncCdrAsync(silencioso: true);
                        break;
                    case IntegracaoNome.Google:
                        await TentarSincronizarGoogleSilenciosoAsync();
                        break;
                }
            }));
        }

        private void CarregarDadosDasAbas()
        {
            AtualizarContatosShell();
            AtualizarHistoricoShell();
        }

        private void AtualizarContatosShell()
        {
            try
            {
                if (gridContatosShell == null) return;
                var contatos = ContatoStorageService.Carregar();
                var busca = txtBuscaContatosShell?.Text?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(busca))
                {
                    contatos = contatos.Where(c =>
                        (c.Nome ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                        (c.Numero ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                        (c.Observacao ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                gridContatosShell.ItemsSource = null;
                gridContatosShell.ItemsSource = contatos;
            }
            catch { }
        }

        private void AtualizarHistoricoShell()
        {
            try
            {
                if (gridHistoricoShell == null) return;
                var itens = HistoricoStorageService.CarregarComRetencao(ObterDiasRetencaoHistorico());
                var busca = txtBuscaHistoricoShell?.Text?.Trim() ?? string.Empty;
                var canalFiltro = (cmbFiltroCanalHistorico?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos os canais";
                var tipoFiltro = (cmbFiltroTipoHistorico?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos os tipos";

                if (!string.Equals(canalFiltro, "Todos os canais", StringComparison.OrdinalIgnoreCase))
                    itens = itens.Where(i => string.Equals(i.OrigemSaidaVisual, canalFiltro, StringComparison.OrdinalIgnoreCase)).ToList();

                if (!string.Equals(tipoFiltro, "Todos os tipos", StringComparison.OrdinalIgnoreCase))
                {
                    itens = tipoFiltro switch
                    {
                        "Recebidas" => itens.Where(i => i.Tipo == Models.TipoHistoricoLigacao.Recebida).ToList(),
                        "Realizadas" => itens.Where(i => i.Tipo == Models.TipoHistoricoLigacao.Realizada).ToList(),
                        "Perdidas" => itens.Where(i => i.Tipo == Models.TipoHistoricoLigacao.Perdida).ToList(),
                        "Não atendidas" => itens.Where(i => i.Tipo == Models.TipoHistoricoLigacao.NaoAtendidaNesseRamal).ToList(),
                        _ => itens
                    };
                }

                if (!string.IsNullOrWhiteSpace(busca))
                {
                    itens = itens.Where(i =>
                        (i.NomeExibido ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                        (i.Nome ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                        (i.Numero ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                        (i.NumeroLimpoVisual ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                        (i.OrigemSaidaVisual ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                        (i.RamalExibido ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                        i.TipoTexto.Contains(busca, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                gridHistoricoShell.ItemsSource = null;
                gridHistoricoShell.ItemsSource = itens;
            }
            catch { }
        }

        private void CmbFiltroTipoHistorico_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (gridHistoricoShell != null) AtualizarHistoricoShell();
        }

        private void BtnAdicionarContato_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Views.SalvarContatoDialog("") { Owner = this };
            if (dlg.ShowDialog() == true)
                AtualizarContatosShell();
        }

        private void BtnEditarContatoShell_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is Contato contato)
            {
                var dlg = new Views.SalvarContatoDialog(contato) { Owner = this };
                if (dlg.ShowDialog() == true)
                    AtualizarContatosShell();
            }
        }

        private async void BtnLigarContatoShell_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string numero)
            {
                var numeroNorm = PhoneNumberNormalizer.NormalizeForDial(numero);
                RegistrarUiDiagnostico($"CLICK CONTATO numero={numero} norm={numeroNorm}");
                try
                {
                    MainTabs.SelectedIndex = 0;
                    AtualizarNavSelecionada();
                    txtNumero.Text = DialPlanService.RemoverDuplicacaoSequencial(numeroNorm);
                    txtNumero.CaretIndex = txtNumero.Text.Length;
                }
                catch { }

                await Dispatcher.Yield(DispatcherPriority.Background);
                RegistrarUiDiagnostico($"CONTATO iniciando fluxo de discagem numeroNorm={numeroNorm}");
                await IniciarLigacaoAsync(numeroNorm);
            }
        }

        private void BtnWhatsAppContatoShell_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string numero)
                AbrirTelaWhatsApp(numero, "contato");
        }

        private void BtnExcluirContatoShell_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is Contato contato)
            {
                var contatos = ContatoStorageService.Carregar();
                contatos.RemoveAll(c => c.Nome == contato.Nome && c.Numero == contato.Numero && c.Observacao == contato.Observacao);
                ContatoStorageService.Salvar(contatos);
                AtualizarContatosShell();
            }
        }

        private void TxtBuscaContatosShell_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (gridContatosShell != null) AtualizarContatosShell();
        }

        // ── Google Contacts ──────────────────────────────────────────────────────

        private async Task CarregarContatosGoogleCacheAsync()
        {
            try
            {
                if (!GoogleContactsService.EstaConectado()) { AtualizarStatusGoogle(); return; }
                var cache = await GoogleContactsService.CarregarCacheAsync();
                if (cache.Count > 0)
                {
                    ContatoStorageService.SincronizarContatosGoogle(cache);
                    Dispatcher.Invoke(AtualizarContatosShell);
                }
                Dispatcher.Invoke(AtualizarStatusGoogle);
            }
            catch { Dispatcher.Invoke(AtualizarStatusGoogle); }
        }

        private void AtualizarStatusGoogle()
        {
            try
            {
                var statusInt = IntegrationStatusService.ObterStatus(IntegracaoNome.Google);
                var conectado = GoogleContactsService.EstaConectado();

                System.Windows.Media.Color cor = statusInt switch
                {
                    IntegracaoStatus.Conectado    => System.Windows.Media.Color.FromRgb(34, 197, 94),
                    IntegracaoStatus.Reconectando => System.Windows.Media.Color.FromRgb(245, 158, 11),
                    IntegracaoStatus.Erro         => System.Windows.Media.Color.FromRgb(239, 68, 68),
                    _                             => System.Windows.Media.Color.FromRgb(148, 163, 184)
                };
                if (bolinhaGoogle != null)
                    bolinhaGoogle.Background = new System.Windows.Media.SolidColorBrush(cor);

                var cache = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WavenVoIP", "google_contacts_cache.json");
                var total = 0;
                if (System.IO.File.Exists(cache))
                {
                    try
                    {
                        var json  = System.IO.File.ReadAllText(cache);
                        var lista = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<Models.Contato>>(json);
                        total = lista?.Count ?? 0;
                    }
                    catch { }
                }

                if (txtGoogleStatus != null)
                    txtGoogleStatus.Text = statusInt switch
                    {
                        IntegracaoStatus.Conectado    => $"Google: conectado • {total} contatos importados",
                        IntegracaoStatus.Reconectando => "Google: reconectando automaticamente…",
                        IntegracaoStatus.Erro         => "Google: ação necessária",
                        _                             => "Google: desconectado"
                    };

                if (btnConectarGoogle    != null) btnConectarGoogle.IsEnabled    = !conectado;
                if (btnSincronizarGoogle != null) btnSincronizarGoogle.IsEnabled = conectado;
                if (btnDesconectarGoogle != null) btnDesconectarGoogle.IsEnabled = conectado;
            }
            catch { }
        }

        private async void BtnConectarGoogle_Click(object sender, RoutedEventArgs e)
            => await BtnConectarGoogleAsync();

        private async void BtnSincronizarGoogle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (btnSincronizarGoogle != null) btnSincronizarGoogle.IsEnabled = false;
                if (txtGoogleStatus != null) txtGoogleStatus.Text = "Google: sincronizando...";

                var (contatos, total) = await Task.Run(() =>
                    GoogleContactsService.SincronizarContatosAsync());

                var importados = ContatoStorageService.SincronizarContatosGoogle(contatos);
                AtualizarContatosShell();
                AtualizarStatusGoogle();
                txtStatus.Text = $"Google sincronizado: {total} contatos, {importados} importados.";
            }
            catch (Exception ex)
            {
                AtualizarStatusGoogle();
                TratarErroSemTravamento(ex, $"Falha ao sincronizar Google:\n{ex.Message}");
            }
        }

        private async void BtnDesconectarGoogle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await GoogleContactsService.LimparTokenAsync();
                // Remove Google contacts from local storage
                var todos = ContatoStorageService.Carregar();
                todos.RemoveAll(c => c.FonteGoogle);
                ContatoStorageService.Salvar(todos);
                AtualizarContatosShell();
                AtualizarStatusGoogle();
                txtStatus.Text = "Google desconectado. Contatos Google removidos.";
            }
            catch (Exception ex)
            {
                TratarErroSemTravamento(ex, $"Falha ao desconectar Google:\n{ex.Message}");
            }
        }

        private void TxtBuscaHistoricoShell_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (gridHistoricoShell != null) AtualizarHistoricoShell();
        }

        private void CmbFiltroCanalHistorico_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (gridHistoricoShell != null) AtualizarHistoricoShell();
        }

        // ── CDR / Histórico Issabel ───────────────────────────────────────────────

        private void ConfigurarSincronizacaoCdr()
        {
            try
            {
                var config = SipConfig.CarregarSalva() ?? new SipConfig();
                _cdrSyncTimer.Stop();
                _cdrSyncTimer.Tick -= CdrSyncTimer_Tick;

                var seconds = config.HistoricoSyncIntervalSeconds;
                // Migration: old configs stored minutes — apply only when new field wasn't set yet
                if (seconds <= 0 && config.HistoricoSyncIntervalMinutes > 0)
                    seconds = config.HistoricoSyncIntervalMinutes * 60;
                // 0 = Manual — do not start timer automatically

                if (config.CdrAtivo && seconds > 0)
                {
                    _cdrSyncTimer.Interval = TimeSpan.FromSeconds(seconds);
                    _cdrSyncTimer.Tick += CdrSyncTimer_Tick;
                    _cdrSyncTimer.Start();
                    RegistrarUiDiagnostico($"CDR_TIMER_INTERVAL_SET seconds={seconds}");
                    var label = seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m";
                    if (txtStatusCdrConfig != null)
                        txtStatusCdrConfig.Text = $"Atualização CDR: a cada {label}";
                }
                else if (config.CdrAtivo && txtStatusCdrConfig != null)
                {
                    txtStatusCdrConfig.Text = "Atualização CDR: manual";
                }
            }
            catch { }
        }

        private void CdrSyncTimer_Tick(object? sender, EventArgs e)
        {
            _ = ExecutarSyncCdrAsync(silencioso: true);
        }

        private async Task ExecutarSyncCdrAsync(bool silencioso, int? diasOverride = null)
        {
            RegistrarUiDiagnostico("CDR_SYNC_START");
            try
            {
                var config = SipConfig.CarregarSalva() ?? new SipConfig();
                if (!config.CdrAtivo || string.IsNullOrWhiteSpace(config.CdrHost) || string.IsNullOrWhiteSpace(config.CdrUsuario))
                {
                    if (!silencioso)
                        MessageBox.Show("CDR não configurado. Preencha Host, Usuário e Senha nas Configurações → CDR.", "Waven VoIP");
                    return;
                }

                var itens = await Task.Run(() => IssabelCdrService.SincronizarAsync(config, diasOverride ?? config.HistoricoRetencaoDias));
                var novos = HistoricoStorageService.MesclarCdr(itens);
                var gravacoes = itens.Count(i => !string.IsNullOrWhiteSpace(i.GravacaoUrl));
                RegistrarUiDiagnostico($"CDR_SYNC_DONE chamadas={itens.Count} novas={novos} gravacoes={gravacoes}");

                var itensCopia = itens.ToList();
                Dispatcher.Invoke(() =>
                {
                    AtualizarHistoricoShell();
                    if (txtStatusHistorico != null)
                        txtStatusHistorico.Text = $"CDR atualizado • {itens.Count} chamadas • {novos} novas • {gravacoes} gravações";
                    if (txtStatusCdrConfig != null)
                        txtStatusCdrConfig.Text = $"Sincronizado • {itens.Count} chamadas • {novos} novas • {gravacoes} gravações";
                    if (novos > 0) DetectarChamadasPerdidasNovas(itensCopia);
                    dashboardControl?.AtualizarDados();
                });

                IntegrationStatusService.Atualizar(IntegracaoNome.Cdr, IntegracaoStatus.Conectado);
                if (!silencioso)
                    Dispatcher.Invoke(() => txtStatus.Text = $"CDR sincronizado: {itens.Count} chamadas, {novos} novas, {gravacoes} gravações.");
            }
            catch (Exception ex)
            {
                var falha = IntegrationFailureClassifier.Classificar(ex);
                RegistrarUiDiagnostico($"CDR_SYNC_FAIL falha={falha} erro={ex.Message}");
                Services.LogHelper.Info($"[CDR_DISCONNECTED] falha={falha} {ex.Message}");

                if (falha == FalhaTipo.Temporaria)
                {
                    Services.LogHelper.Info($"[INTEGRATION_TEMPORARY_FAILURE] Cdr — {ex.Message}");
                    IntegrationStatusService.Atualizar(IntegracaoNome.Cdr, IntegracaoStatus.Reconectando);
                    if (!silencioso)
                        Dispatcher.Invoke(() => MessageBox.Show("Falha temporária ao conectar CDR:\n\n" + ex.Message +
                            "\n\nO sistema tentará reconectar automaticamente.",
                            "Waven VoIP - CDR", MessageBoxButton.OK, MessageBoxImage.Warning));
                    else
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (txtStatusHistorico != null) txtStatusHistorico.Text = "CDR: reconectando…";
                            if (txtStatusCdrConfig  != null) txtStatusCdrConfig.Text  = "CDR: reconectando automaticamente…";
                            Services.LogHelper.Info($"[INTEGRATION_MODAL_SUPPRESSED_TEMPORARY] Cdr");
                            IntegrationAutoReconnectService.AgendarReconexao(IntegracaoNome.Cdr);
                        });
                    }
                }
                else
                {
                    Services.LogHelper.Info($"[INTEGRATION_ACTION_REQUIRED] Cdr — {ex.Message}");
                    Services.LogHelper.Info($"[INTEGRATION_MODAL_SHOWN_AUTH_FAILURE] Cdr");
                    IntegrationStatusService.Atualizar(IntegracaoNome.Cdr, IntegracaoStatus.Erro);
                    if (!silencioso)
                        Dispatcher.Invoke(() => MessageBox.Show("Falha de autenticação CDR:\n\n" + ex.Message +
                            "\n\nVerifique usuário e senha MySQL.",
                            "Waven VoIP - CDR", MessageBoxButton.OK, MessageBoxImage.Warning));
                    else
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (txtStatusHistorico != null) txtStatusHistorico.Text = "CDR: ação necessária";
                            if (txtStatusCdrConfig  != null) txtStatusCdrConfig.Text  = "CDR: ação necessária";
                            MostrarModalIntegracao(IntegracaoNome.Cdr, "CDR — Histórico MySQL",
                                "Ação necessária: verifique usuário, senha ou banco de dados MySQL.",
                                onReconectar: null);
                        });
                    }
                }
            }
        }

        private async void BtnAtualizarHistoricoCdr_Click(object sender, RoutedEventArgs e)
        {
            if (btnAtualizarHistoricoCdr != null) btnAtualizarHistoricoCdr.IsEnabled = false;
            if (txtBtnHistoricoLabel != null) txtBtnHistoricoLabel.Text = "Sincronizando...";
            if (txtStatusHistorico != null) txtStatusHistorico.Text = string.Empty;
            try
            {
                // Reprocess local history first (fixes concatenated numbers, validates URLs)
                await ExecutarReprocessarAsync(silencioso: true, validarUrls: true);
                await ExecutarSyncCdrAsync(silencioso: false);
            }
            finally
            {
                if (txtBtnHistoricoLabel != null) txtBtnHistoricoLabel.Text = "Atualizar CDR";
                if (btnAtualizarHistoricoCdr != null) btnAtualizarHistoricoCdr.IsEnabled = true;
            }
        }

        private async void BtnReprocessarHistorico_Click(object sender, RoutedEventArgs e)
        {
            if (btnReprocessarHistorico != null) btnReprocessarHistorico.IsEnabled = false;
            if (txtBtnReprocessarLabel != null) txtBtnReprocessarLabel.Text = "Processando...";
            if (txtStatusHistorico != null) txtStatusHistorico.Text = string.Empty;
            try { await ExecutarReprocessarAsync(silencioso: false); }
            finally
            {
                if (txtBtnReprocessarLabel != null) txtBtnReprocessarLabel.Text = "Reprocessar";
                if (btnReprocessarHistorico != null) btnReprocessarHistorico.IsEnabled = true;
            }
        }

        private async Task ExecutarReprocessarAsync(bool silencioso, bool validarUrls = true)
        {
            try
            {
                RegistrarUiDiagnostico("REPROCESS_CDR_START");
                var (total, corrigidos, urlsRemovidas) = await Task.Run(
                    () => IssabelCdrService.ReprocessarHistoricoCdrLocalAsync(validarUrls));
                RegistrarUiDiagnostico($"REPROCESS_CDR_DONE total={total} corrigidos={corrigidos} urls404={urlsRemovidas}");

                Dispatcher.Invoke(() =>
                {
                    AtualizarHistoricoShell();
                    if (!silencioso && txtStatusHistorico != null)
                        txtStatusHistorico.Text =
                            $"Reprocessado: {total} registros • {corrigidos} números corrigidos • {urlsRemovidas} URLs 404 removidas";
                    else if (silencioso && (corrigidos > 0 || urlsRemovidas > 0) && txtStatusHistorico != null)
                        txtStatusHistorico.Text =
                            $"Auto-reprocessamento: {corrigidos} números corrigidos, {urlsRemovidas} URLs removidas";
                    dashboardControl?.AtualizarDados();
                });
            }
            catch (Exception ex)
            {
                RegistrarUiDiagnostico($"REPROCESS_CDR_FAIL erro={ex.Message}");
                if (!silencioso)
                    Dispatcher.Invoke(() => MessageBox.Show(
                        "Falha ao reprocessar histórico:\n\n" + ex.Message,
                        "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        }

        private async void BtnTestarConexaoCdr_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (txtStatusCdrConfig != null) txtStatusCdrConfig.Text = "Testando...";
                var config = MontarConfigCdrAtual();
                await Task.Run(() => IssabelCdrService.TestarConexaoAsync(config));
                if (txtStatusCdrConfig != null) txtStatusCdrConfig.Text = "Conexão OK";
                txtStatus.Text = "Conexão CDR estabelecida com sucesso.";
                RegistrarUiDiagnostico("CDR_CONNECTION_OK");
            }
            catch (Exception ex)
            {
                if (txtStatusCdrConfig != null) txtStatusCdrConfig.Text = "Falha";
                RegistrarUiDiagnostico($"CDR_CONNECTION_FAIL erro={ex.Message}");
                MessageBox.Show("Falha ao conectar ao CDR:\n\n" + ex.Message +
                    "\n\nVerifique host, porta, banco, usuário e senha.", "Waven VoIP - CDR", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnSincronizarCdrAgora_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            object? originalContent = null;
            if (btn != null) { originalContent = btn.Content; btn.IsEnabled = false; btn.Content = "Sincronizando..."; }
            if (txtStatusCdrConfig != null) txtStatusCdrConfig.Text = "Sincronizando...";
            try { await ExecutarSyncCdrAsync(silencioso: false); }
            finally { if (btn != null) { btn.IsEnabled = true; btn.Content = originalContent; } }
        }

        private static readonly System.Net.Http.HttpClient _recordingHttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

        // Resolves final playback URL: uses stored GravacaoUrl or rebuilds from recording filename + config + date
        private string ResolverUrlGravacaoParaPlay(Models.HistoricoLigacaoItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.GravacaoUrl))
                return item.GravacaoUrl;

            if (string.IsNullOrWhiteSpace(item.GravacaoArquivo))
                return string.Empty;

            var config = SipConfig.CarregarSalva() ?? new SipConfig();
            if (config.GravacaoTipoAcesso == "URL" && !string.IsNullOrWhiteSpace(config.GravacaoUrlBase))
            {
                var base_ = config.GravacaoUrlBase.TrimEnd('/');
                var datePath = $"{item.DataHora:yyyy}/{item.DataHora:MM}/{item.DataHora:dd}";
                var url = (item.GravacaoArquivo.Contains('/') || item.GravacaoArquivo.Contains('\\'))
                    ? $"{base_}/{item.GravacaoArquivo.Replace('\\', '/')}"
                    : $"{base_}/{datePath}/{item.GravacaoArquivo}";
                RegistrarUiDiagnostico($"RECORDING_URL_RESOLVED url={url}");
                return url;
            }

            if (config.GravacaoTipoAcesso == "Local" && !string.IsNullOrWhiteSpace(config.GravacaoCaminhoLocal))
                return Path.Combine(config.GravacaoCaminhoLocal, item.GravacaoArquivo);

            return string.Empty;
        }

        private async Task<string> BaixarParaCacheAsync(string url, Button? btn)
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WavenVoIP", "cache", "recordings");
            Directory.CreateDirectory(cacheDir);

            var urlSemQuery = url.Split('?')[0];
            var fileName = Path.GetFileName(urlSemQuery);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "gravacao.wav";
            var cacheFile = Path.Combine(cacheDir, fileName);

            if (File.Exists(cacheFile))
            {
                RegistrarUiDiagnostico($"RECORDING_CACHE_HIT local={cacheFile}");
                return cacheFile;
            }

            RegistrarUiDiagnostico($"RECORDING_DOWNLOAD_STARTED url={url}");
            if (btn != null) { btn.IsEnabled = false; btn.ToolTip = "Baixando..."; }
            var data = await _recordingHttpClient.GetByteArrayAsync(url);
            File.WriteAllBytes(cacheFile, data);
            RegistrarUiDiagnostico($"RECORDING_DOWNLOAD_DONE local={cacheFile} bytes={data.Length}");
            return cacheFile;
        }

        private async void BtnOuvirGravacao_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe && fe.Tag is Models.HistoricoLigacaoItem item)) return;

            var alvo = ResolverUrlGravacaoParaPlay(item);
            if (string.IsNullOrWhiteSpace(alvo)) return;

            RegistrarUiDiagnostico($"RECORDING_PLAY_STARTED arquivo={Path.GetFileName(alvo)}");
            var btn = sender as Button;
            try
            {
                string localFile;
                if (alvo.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    alvo.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    localFile = await BaixarParaCacheAsync(alvo, btn);
                }
                else
                {
                    localFile = alvo;
                }

                if (!File.Exists(localFile))
                {
                    MessageBox.Show($"Arquivo não encontrado:\n{localFile}", "Waven VoIP",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var player = new AudioPlayerWindow(localFile) { Owner = this };
                player.Show();
            }
            catch (Exception ex)
            {
                TratarErroSemTravamento(ex, $"Não foi possível abrir a gravação:\n{Path.GetFileName(alvo)}\n\n{ex.Message}");
            }
            finally
            {
                if (btn != null) { btn.IsEnabled = true; btn.ToolTip = "Ouvir gravação"; }
            }
        }

        private void BtnBaixarGravacao_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe && fe.Tag is Models.HistoricoLigacaoItem item)) return;

            var alvo = ResolverUrlGravacaoParaPlay(item);
            if (string.IsNullOrWhiteSpace(alvo)) return;

            RegistrarUiDiagnostico($"RECORDING_DOWNLOAD_STARTED arquivo={Path.GetFileName(alvo)}");
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(alvo) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                TratarErroSemTravamento(ex, $"Não foi possível abrir para download:\n{ex.Message}");
            }
        }

        private void BtnTestarAcessoGravacao_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var config = MontarConfigCdrAtual();
                var base_ = config.GravacaoUrlBase?.Trim();
                if (string.IsNullOrWhiteSpace(base_))
                {
                    MessageBox.Show("Configure a URL base ou caminho das gravações.", "Waven VoIP");
                    return;
                }
                if (config.GravacaoTipoAcesso == "Local")
                {
                    if (Directory.Exists(base_))
                        MessageBox.Show($"Pasta acessível:\n{base_}", "Waven VoIP");
                    else
                        MessageBox.Show($"Pasta não encontrada:\n{base_}", "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(base_) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                TratarErroSemTravamento(ex, "Falha ao testar acesso às gravações.");
            }
        }

        private SipConfig MontarConfigCdrAtual()
        {
            var config = SipConfig.CarregarSalva() ?? new SipConfig();
            try
            {
                config.CdrAtivo = chkCdrAtivo?.IsChecked == true;
                config.CdrHost = string.IsNullOrWhiteSpace(txtCdrHost?.Text) ? config.ServerIp : txtCdrHost.Text.Trim();
                if (int.TryParse(txtCdrPorta?.Text?.Trim(), out var p) && p > 0) config.CdrPorta = p;
                config.CdrBanco = string.IsNullOrWhiteSpace(txtCdrBanco?.Text) ? "asteriskcdrdb" : txtCdrBanco.Text.Trim();
                config.CdrTabela = string.IsNullOrWhiteSpace(txtCdrTabela?.Text) ? "cdr" : txtCdrTabela.Text.Trim();
                config.CdrUsuario = txtCdrUsuario?.Text?.Trim() ?? string.Empty;
                config.CdrSenha = txtCdrSenha?.Password?.Trim() ?? string.Empty;

                var modoItem = cmbHistoricoModo?.SelectedItem as ComboBoxItem;
                config.HistoricoModoExibicao = modoItem?.Tag as string ?? "MeuRamal";

                var syncItem = cmbHistoricoSyncInterval?.SelectedItem as ComboBoxItem;
                if (syncItem?.Tag is string st && int.TryParse(st, out var sm)) config.HistoricoSyncIntervalSeconds = sm;

                config.GravacaoAtiva = chkGravacaoAtiva?.IsChecked == true;
                var tipoGrav = cmbGravacaoTipo?.SelectedItem as ComboBoxItem;
                config.GravacaoTipoAcesso = tipoGrav?.Tag as string ?? "URL";
                config.GravacaoUrlBase = txtGravacaoUrlBase?.Text?.Trim() ?? string.Empty;
                config.GravacaoCaminhoLocal = config.GravacaoTipoAcesso == "Local" ? config.GravacaoUrlBase : string.Empty;
            }
            catch { }
            return config;
        }

        private async void BtnLigarHistoricoShell_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string numero)
            {
                var numeroNorm = PhoneNumberNormalizer.NormalizeForDial(numero);
                RegistrarUiDiagnostico($"CLICK HISTORICO numero={numero} norm={numeroNorm}");
                try
                {
                    MainTabs.SelectedIndex = 0;
                    AtualizarNavSelecionada();
                    txtNumero.Text = DialPlanService.RemoverDuplicacaoSequencial(numeroNorm);
                    txtNumero.CaretIndex = txtNumero.Text.Length;
                }
                catch { }

                await Dispatcher.Yield(DispatcherPriority.Background);
                RegistrarUiDiagnostico($"HISTORICO iniciando fluxo de discagem numeroNorm={numeroNorm}");
                await IniciarLigacaoDoHistoricoAsync(numeroNorm);
            }
        }

        private void BtnWhatsAppHistoricoShell_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string numero)
                AbrirTelaWhatsApp(numero, "historico");
        }

        private void BtnSalvarContatoHistoricoShell_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string numero && !string.IsNullOrWhiteSpace(numero))
                AbrirDialogSalvarContato(numero);
        }

        private void BtnMenuHistorico_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn && btn.Tag is Models.HistoricoLigacaoItem item)) return;

            var menuStyle = TryFindResource("ModernContextMenuStyle") as Style;
            var itemStyle = TryFindResource("ModernMenuItemStyle")     as Style;
            var sepStyle  = TryFindResource("ModernSeparatorStyle")    as Style;

            var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = btn };
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            if (menuStyle != null) menu.Style = menuStyle;

            void Add(string header, bool visible, System.Action acao)
            {
                var mi = new MenuItem { Header = header, Visibility = visible ? Visibility.Visible : Visibility.Collapsed };
                if (itemStyle != null) mi.Style = itemStyle;
                mi.Click += (_, _) => acao();
                menu.Items.Add(mi);
            }

            void AddSep()
            {
                var sep = new Separator();
                if (sepStyle != null) sep.Style = sepStyle;
                menu.Items.Add(sep);
            }

            Add("📞  Ligar", true, () => { _ = IniciarLigacaoDoHistoricoAsync(item.NumeroLimpoVisual); });
            Add("💬  Enviar WhatsApp", true, () => AbrirTelaWhatsApp(item.NumeroLimpoVisual, "historico"));
            AddSep();
            Add("✏️  Salvar contato", true, () => AbrirDialogSalvarContato(item.NumeroLimpoVisual));
            Add("▶  Reproduzir gravação", item.TemGravacao, () =>
            {
                _ = Task.Run(async () =>
                {
                    var alvo = ResolverUrlGravacaoParaPlay(item);
                    if (string.IsNullOrWhiteSpace(alvo)) return;
                    try
                    {
                        string localFile;
                        if (alvo.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || alvo.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            localFile = await BaixarParaCacheAsync(alvo, null);
                        else localFile = alvo;
                        Dispatcher.Invoke(() =>
                        {
                            if (!File.Exists(localFile)) { MessageBox.Show($"Arquivo não encontrado:\n{localFile}", "Waven VoIP"); return; }
                            new AudioPlayerWindow(localFile) { Owner = this }.Show();
                        });
                    }
                    catch (Exception ex) { Dispatcher.Invoke(() => TratarErroSemTravamento(ex, "Não foi possível abrir a gravação.")); }
                });
            });
            Add("⬇  Baixar gravação", item.TemGravacao, () =>
            {
                var alvo = ResolverUrlGravacaoParaPlay(item);
                if (!string.IsNullOrWhiteSpace(alvo))
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(alvo) { UseShellExecute = true }); } catch { }
            });
            AddSep();
            Add("📋  Copiar número", true, () =>
            {
                try
                {
                    var raw = !string.IsNullOrWhiteSpace(item.NumeroLimpoVisual) ? item.NumeroLimpoVisual : item.Numero;
                    var soDigitos = new string((raw ?? string.Empty).Where(char.IsDigit).ToArray());
                    // Remove leading country code 55 when result has 12-13 digits (55 + DDD + number)
                    if (soDigitos.Length >= 12 && soDigitos.StartsWith("55"))
                        soDigitos = soDigitos.Substring(2);
                    RegistrarUiDiagnostico($"HISTORY_COPY_NUMBER_VALUE numero={soDigitos} raw={raw}");
                    if (!string.IsNullOrWhiteSpace(soDigitos)) Clipboard.SetDataObject(soDigitos, true);
                }
                catch { }
            });
            Add("ℹ  Ver detalhes da chamada", true, () => MostrarDetalhesHistorico(item));
            AddSep();
            Add("🗑  Excluir do histórico", true, () =>
            {
                var todos = HistoricoStorageService.Carregar();
                todos.RemoveAll(i => string.Equals(i.Id, item.Id, StringComparison.OrdinalIgnoreCase));
                HistoricoStorageService.Salvar(todos);
                AtualizarHistoricoShell();
            });

            menu.IsOpen = true;
        }

        private void MostrarDetalhesHistorico(Models.HistoricoLigacaoItem item)
        {
            var ramal = string.IsNullOrWhiteSpace(item.RamalExibido) ? "—" : item.RamalExibido;
            var detalhes = $"Número:     {item.NumeroLimpoVisual}\n" +
                           $"Nome:       {(string.IsNullOrWhiteSpace(item.Nome) ? "—" : item.Nome)}\n" +
                           $"Tipo:       {item.TipoTexto}\n" +
                           $"Data/hora:  {item.DataHora:dd/MM/yyyy HH:mm:ss}\n" +
                           $"Duração:    {(string.IsNullOrWhiteSpace(item.Duracao) ? "—" : item.Duracao)}\n" +
                           $"Ramal:      {ramal}\n" +
                           $"Gravação:   {(item.TemGravacao ? "Sim" : "Não")}";
            MessageBox.Show(detalhes, "Detalhes da chamada", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AbrirDialogSalvarContato(string numero)
        {
            var numLimpo = new string((numero ?? "").Where(char.IsDigit).ToArray());
            if (ContatoStorageService.ExisteNumero(numLimpo))
            {
                var nomeExistente = ContatoStorageService.ResolverNomePorNumero(numLimpo);
                MessageBox.Show($"O número {numLimpo} já está salvo como '{nomeExistente}'.", "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Views.SalvarContatoDialog(numLimpo) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                AtualizarContatosShell();
                AtualizarHistoricoShell();
            }
        }

        private void RestaurarJanelas()
        {
            try
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();

                if (_activeCallWindow != null)
                {
                    try
                    {
                        _activeCallWindow.Show();
                        _activeCallWindow.WindowState = WindowState.Normal;
                        _activeCallWindow.Activate();
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void DialerShellWindow_StateChanged(object? sender, EventArgs e)
        {
            try
            {
                // Quando houver chamada ativa, minimizar o discador não deve deixar o usuário sem caminho de volta.
                // Mantemos o ícone na bandeja e restauramos também a CallWindow pelo menu/duplo clique.
                if (WindowState == WindowState.Minimized && _sipService.IsInCall)
                {
                    Hide();
                    _trayIcon?.ShowBalloonTip(1800, "Waven VoIP", "Chamada ativa. Dê duplo clique no ícone para voltar.", WF.ToolTipIcon.Info);
                }
            }
            catch { }
        }

                private void ConfigurarBandeja()
        {
            try
            {
                _trayIcon = new WF.NotifyIcon
                {
                    Text = "Waven VoIP",
                    Visible = true,
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty)
                };

                var menu = new WF.ContextMenuStrip();
                menu.Items.Add("Abrir", null, (_, __) => RestaurarJanelas());
                menu.Items.Add("Sair", null, (_, __) => { _fechamentoReal = true; _trayIcon?.Dispose(); Close(); });
                _trayIcon.ContextMenuStrip = menu;
                _trayIcon.DoubleClick += (_, __) => RestaurarJanelas();
            }
            catch { }
        }

        private void DialerShellWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_fechamentoReal) { try { _reconnectTimer.Stop(); } catch { } return; }
            e.Cancel = true;
            Hide();
            try { _trayIcon?.ShowBalloonTip(1800, "Waven VoIP", "Continuo em segundo plano para receber chamadas.", WF.ToolTipIcon.Info); } catch { }
        }

private void Tecla_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button botao)
            {
                var valor = botao.Tag?.ToString() ?? botao.Content?.ToString();
                if (!string.IsNullOrEmpty(valor))
                {
                    txtNumero.Text += valor;
                    txtNumero.CaretIndex = txtNumero.Text.Length;
                    txtNumero.ScrollToEnd();
                    txtNumero.Focus();
                }
            }
        }

        private async Task IniciarLigacaoAsync(string numeroDigitado) => await IniciarLigacaoAsync(numeroDigitado, false);
        private async Task IniciarLigacaoDoHistoricoAsync(string numeroHistorico) => await IniciarLigacaoAsync(numeroHistorico, true);

        private async Task IniciarLigacaoAsync(string numeroDigitado, bool veioDoHistorico)
        {
            try
            {
                RegistrarUiDiagnostico($"INICIAR_LIGACAO entrada={numeroDigitado} veioDoHistorico={veioDoHistorico}");
                numeroDigitado = DialPlanService.RemoverDuplicacaoSequencial(numeroDigitado?.Trim() ?? string.Empty);
                if (string.IsNullOrWhiteSpace(numeroDigitado)) throw new InvalidOperationException("Digite um número.");
                if (veioDoHistorico) numeroDigitado = DialPlanService.RemoverPrefixoDeRota(numeroDigitado);

                string numeroFinal;
                string origemSaida = "Ramal interno";
                if (DialPlanService.EhRamalInterno(numeroDigitado))
                {
                    numeroFinal = DialPlanService.NormalizarNumero(numeroDigitado);
                }
                else
                {
                    RegistrarUiDiagnostico($"ABRINDO SELETOR numero={numeroDigitado}");
                    var saida = await AbrirSeletorSaidaAsync(numeroDigitado);
                    RegistrarUiDiagnostico($"RETORNO SELETOR numero={numeroDigitado} saida={(saida.HasValue ? saida.Value.ToString() : "null")}");
                    if (saida == null) return;
                    origemSaida = DialPlanService.NomeSaida(saida.Value);
                    numeroFinal = DialPlanService.AplicarRegraDeDiscagem(numeroDigitado, saida.Value);
                    RegistrarUiDiagnostico($"NUMERO FINAL numeroDigitado={numeroDigitado} numeroFinal={numeroFinal} saida={origemSaida}");
                }

                RegistrarUiDiagnostico($"ABRINDO CALLWINDOW numeroFinal={numeroFinal} origem={origemSaida}");
                var call = CriarTelaDeChamada(ResolverDisplayChamada(numeroFinal), $"Ligando via {origemSaida}...");
                _activeCallWindow = call;
                call.Closed += (_, __) => { if (ReferenceEquals(_activeCallWindow, call)) _activeCallWindow = null; };
                call.Show();

                RegistrarUiDiagnostico($"CHAMANDO SIP Ligar numeroFinal={numeroFinal}");
                bool ok = await _sipService.Ligar(numeroFinal);
                RegistrarUiDiagnostico($"RETORNO SIP Ligar numeroFinal={numeroFinal} ok={ok} erro={_sipService.LastCallError}");
                if (ok)
                {
                    call.DefinirStatus($"Em chamada via {origemSaida}");
                    call.IniciarContador();
                    IniciarControleHistoricoChamada(RegistrarHistorico(numeroFinal, TipoHistoricoLigacao.Realizada, "Em andamento", origemSaida));
                }
                else
                {
                    // Mantém a tela aberta para o usuário ver que a tentativa foi feita.
                    // Antes ela fechava imediatamente quando o SIP/Issabel recusava ou falhava,
                    // dando a impressão de que a tela de chamada nem abriu.
                    var erro = string.IsNullOrWhiteSpace(_sipService.LastCallError) ? "Issabel/SIP não completou a chamada." : _sipService.LastCallError;
                    call.DefinirStatus("Falhou: " + erro);
                    try { Clipboard.SetText($"Número enviado: {numeroFinal}\nSaída: {origemSaida}\nDestino SIP: {_sipService.LastDialedDestination}\nErro: {erro}"); } catch { }
                }
            }
            catch (Exception ex)
            {
                TratarErroSemTravamento(ex, "Não foi possível iniciar a chamada.");
            }
        }



        private void NavDiscagem_Click(object sender, RoutedEventArgs e) { MainTabs.SelectedIndex = 0; AtualizarNavSelecionada(); AnimarAbaAtual(); }
        private void NavContatos_Click(object sender, RoutedEventArgs e) { MainTabs.SelectedIndex = 1; AtualizarContatosShell(); AtualizarNavSelecionada(); AnimarAbaAtual(); }
        private void NavHistorico_Click(object sender, RoutedEventArgs e) { MainTabs.SelectedIndex = 2; AtualizarHistoricoShell(); AtualizarNavSelecionada(); AnimarAbaAtual(); ResetarBadgePerdidas(); }
        private void NavConfiguracoes_Click(object sender, RoutedEventArgs e)
        {
            // Highlight Configurações nav while popup is open
            foreach (var b in new[] { btnNavDiscagem, btnNavContatos, btnNavHistorico, btnNavDashboard })
                if (b != null) b.Tag = null;
            if (btnNavConfiguracoes != null) btnNavConfiguracoes.Tag = "active";

            var win = new SettingsWindow { Owner = this };
            win.ShowDialog();

            // Reapply timers with values the user may have just changed
            try { ConfigurarSincronizacaoAmi(); }    catch { }
            try { ConfigurarSincronizacaoCdr(); }    catch { }
            try { ConfigurarSincronizacaoGoogle(); }  catch { }

            AtualizarNavSelecionada();
            try { AtualizarBotaoStatus(); } catch { }
        }
        private void NavDashboard_Click(object sender, RoutedEventArgs e) { MainTabs.SelectedIndex = 4; AtualizarNavSelecionada(); AnimarAbaAtual(); dashboardControl?.AtualizarDados(); RegistrarUiDiagnostico("DASHBOARD_REFRESH"); }

        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != MainTabs) return;
            AtualizarNavSelecionada();
            AnimarAbaAtual();
        }

        private void AtualizarBadgePerdidas(int count)
        {
            try
            {
                if (badgePerdidas == null || txtBadgePerdidas == null) return;
                _badgePendentes = count;
                badgePerdidas.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
                txtBadgePerdidas.Text = count > 9 ? "9+" : count.ToString();
            }
            catch { }
        }

        private void ResetarBadgePerdidas() => AtualizarBadgePerdidas(0);

        private void MostrarMissedCallPopup(Models.HistoricoLigacaoItem item)
        {
            try
            {
                RegistrarUiDiagnostico($"MISSED_CALL_POPUP_OPENED numero={item.Numero}");
                _missedCallPopup?.Close();

                var popup = new MissedCallPopup(item.NumeroLimpoVisual, item.NomeExibido);
                _missedCallPopup = popup;

                popup.RetornarSolicitado += async numero =>
                {
                    var numeroNorm = PhoneNumberNormalizer.NormalizeForDial(numero);
                    RegistrarUiDiagnostico($"MISSED_CALL_CALLBACK_STARTED numero={numero} norm={numeroNorm}");
                    _missedCallPopup = null;
                    RestaurarJanelas();
                    MainTabs.SelectedIndex = 0;
                    AtualizarNavSelecionada();
                    await IniciarLigacaoDoHistoricoAsync(numeroNorm);
                };

                popup.VerHistoricoSolicitado += () =>
                {
                    _missedCallPopup = null;
                    RestaurarJanelas();
                    MainTabs.SelectedIndex = 2;
                    AtualizarHistoricoShell();
                    AtualizarNavSelecionada();
                    ResetarBadgePerdidas();
                    try
                    {
                        if (txtBuscaHistoricoShell != null && !string.IsNullOrWhiteSpace(item.Numero))
                            txtBuscaHistoricoShell.Text = PhoneNumberNormalizer.NormalizeForDial(item.Numero);
                    }
                    catch { }
                };

                popup.Closed += (_, _) => { if (ReferenceEquals(_missedCallPopup, popup)) _missedCallPopup = null; };
                popup.Show();
            }
            catch { }
        }

        private void DetectarChamadasPerdidasNovas(System.Collections.Generic.List<Models.HistoricoLigacaoItem> itensCdr)
        {
            try
            {
                var perdidas = itensCdr
                    .Where(i => (i.Tipo == Models.TipoHistoricoLigacao.Perdida || i.Tipo == Models.TipoHistoricoLigacao.NaoAtendidaNesseRamal)
                             && !string.IsNullOrWhiteSpace(i.UniqueId)
                             && !_perdidasJaMostradas.Contains(i.UniqueId))
                    .OrderByDescending(i => i.DataHora)
                    .ToList();

                if (perdidas.Count == 0) return;

                foreach (var p in perdidas)
                    _perdidasJaMostradas.Add(p.UniqueId);

                _badgePendentes += perdidas.Count;
                AtualizarBadgePerdidas(_badgePendentes);
                RegistrarUiDiagnostico($"MISSED_CALL_DETECTED count={perdidas.Count}");

                MostrarMissedCallPopup(perdidas[0]);
            }
            catch { }
        }

        private void AtualizarNavSelecionada()
        {
            if (btnNavDiscagem == null || MainTabs == null) return;
            var botoes = new[] { btnNavDiscagem, btnNavContatos, btnNavHistorico, btnNavConfiguracoes, btnNavDashboard };

            var activeBg = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0xA7, 0x8B, 0xFA), 0),
                    new GradientStop(Color.FromRgb(0xC4, 0xB5, 0xFD), 1)
                },
                new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
            var activeFg     = new SolidColorBrush(Color.FromRgb(0x1E, 0x0A, 0x4B));
            var activeBorder = new SolidColorBrush(Color.FromRgb(0x6D, 0x28, 0xD9));

            for (int i = 0; i < botoes.Length; i++)
            {
                if (botoes[i] == null) continue;
                bool ativo = MainTabs.SelectedIndex == i;
                botoes[i].Tag = ativo ? "active" : null;
                if (ativo)
                {
                    botoes[i].Background      = activeBg;
                    botoes[i].Foreground      = activeFg;
                    botoes[i].BorderBrush     = activeBorder;
                    botoes[i].BorderThickness = new Thickness(2);
                    botoes[i].FontWeight      = FontWeights.Bold;
                }
                else
                {
                    botoes[i].ClearValue(Button.BackgroundProperty);
                    botoes[i].ClearValue(Button.ForegroundProperty);
                    botoes[i].ClearValue(Button.BorderBrushProperty);
                    botoes[i].ClearValue(Button.BorderThicknessProperty);
                    botoes[i].ClearValue(Button.FontWeightProperty);
                }
            }
        }

        private void AnimarAbaAtual()
        {
            if (MainTabs?.SelectedContent is not FrameworkElement conteudo) return;
            conteudo.Opacity = 0;
            conteudo.RenderTransform = new TranslateTransform(0, 10);
            conteudo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            conteudo.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(180)));
        }

        private async void GridHistoricoShell_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (gridHistoricoShell != null && gridHistoricoShell.SelectedItem is HistoricoLigacaoItem item && !string.IsNullOrWhiteSpace(item.Numero))
                await IniciarLigacaoDoHistoricoAsync(item.Numero);
        }

        private async void BtnLigar_Click(object sender, RoutedEventArgs e) => await IniciarLigacaoAsync(txtNumero.Text);

        private CallWindow CriarTelaDeChamada(string numero, string status)
        {
            var call = new CallWindow(numero, status);
            call.OnHangupRequested += () => _sipService.Desligar();
            call.OnBlindTransferRequested += BlindTransferRequested;
            call.OnAttendedTransferRequested += AttendedTransferRequested;
            call.OnAddParticipantRequested += AddParticipantRequested;
            call.OnRecordingToggleRequested += RecordingToggleRequested;
            call.OnDtmfKeyboardRequested += DtmfKeyboardRequested;
            call.OnHoldToggleRequested += HoldToggleRequested;
            call.OnAudioRouteRequested += AudioRouteRequested;
            call.OnOpenConferenceParticipantsRequested += OpenConferenceParticipantsRequested;
            call.OnWhatsAppRequested += numero => AbrirTelaWhatsApp(numero, "chamada");
            return call;
        }

        private async void BlindTransferRequested()
        {
            if (!_sipService.IsInCall) { MessageBox.Show("Não há chamada ativa para transferir.", "Waven VoIP"); return; }
            var prompt = new InputPromptWindow("Transferência cega: digite o ramal ou número externo de destino") { Owner = (_activeCallWindow as Window) ?? this };
            if (!AbrirPromptSeguro(prompt) || string.IsNullOrWhiteSpace(prompt.ValorDigitado)) return;

            var destinoFinal = await PrepararDestinoTransferenciaAsync(prompt.ValorDigitado, "transferência cega");
            if (string.IsNullOrWhiteSpace(destinoFinal)) return;

            var displayDestino = ResolverDisplayChamada(destinoFinal);
            var confirm = new BlindTransferWindow(displayDestino) { Owner = (_activeCallWindow as Window) ?? this };
            confirm.ShowDialog();
            if (!confirm.Confirmado) return;

            bool ok = _sipService.TransferenciaCega(destinoFinal);
            if (!ok) MessageBox.Show("Não foi possível completar a transferência cega.", "Waven VoIP");
        }

        private async void AttendedTransferRequested()
        {
            if (!_sipService.IsInCall) { MessageBox.Show("Não há chamada ativa para transferir.", "Waven VoIP"); return; }
            var prompt = new InputPromptWindow("Transferência assistida: digite o ramal ou número externo de destino") { Owner = (_activeCallWindow as Window) ?? this };
            if (!AbrirPromptSeguro(prompt) || string.IsNullOrWhiteSpace(prompt.ValorDigitado)) return;

            var destinoFinal = await PrepararDestinoTransferenciaAsync(prompt.ValorDigitado, "transferência assistida");
            if (string.IsNullOrWhiteSpace(destinoFinal)) return;

            bool ok = _sipService.TransferenciaAssistida(destinoFinal);
            if (!ok) { MessageBox.Show("Não foi possível iniciar a transferência assistida.", "Waven VoIP"); return; }
            AbrirControleTransferenciaAssistida(destinoFinal);
        }

        private async Task<string?> PrepararDestinoTransferenciaAsync(string destinoDigitado, string tipoTransferencia)
        {
            var destinoBruto = DialPlanService.NormalizarNumero(destinoDigitado);
            var destino = PhoneNumberNormalizer.NormalizeBrazilPhone(destinoBruto);
            if (!string.Equals(destino, destinoBruto, StringComparison.Ordinal))
                RegistrarUiDiagnostico($"TRANSFER_NUMBER_NORMALIZED tipo={tipoTransferencia} entrada={destinoBruto} saida={destino}");
            if (string.IsNullOrWhiteSpace(destino)) return null;

            if (DialPlanService.EhRamalInterno(destino))
                return destino;

            var config = SipConfig.CarregarSalva() ?? new SipConfig();
            config.AplicarPadroes();

            if (!config.PermitirTransferenciaExterna)
            {
                MessageBox.Show("A transferência para número externo está desativada nas configurações.\n\nUse apenas ramais internos ou ative a opção em Configurações.", "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            var saida = await AbrirSeletorSaidaAsync(destino);
            if (saida == null) return null;

            return DialPlanService.AplicarRegraDeDiscagem(destino, saida.Value);
        }

        private void AddParticipantRequested()
        {
            try
            {
                var config = SipConfig.CarregarSalva() ?? new SipConfig();
                var salaPadrao = string.IsNullOrWhiteSpace(config.SalaConferenciaIssabel) ? "800" : config.SalaConferenciaIssabel;

                if (_conferenceControlWindow != null && _conferenceControlWindow.IsVisible)
                {
                    _conferenceControlWindow.Activate();
                    return;
                }

                _conferenceControlWindow = new ConferenceControlWindow(salaPadrao)
                {
                    Owner = (_activeCallWindow as Window) ?? this
                };

                _conferenceControlWindow.OnAddParticipantAsync += async (numero, sala) =>
                {
                    var cfg = SipConfig.CarregarSalva() ?? new SipConfig();
                    var salaSistema = string.IsNullOrWhiteSpace(cfg.SalaConferenciaIssabel) ? "800" : cfg.SalaConferenciaIssabel.Trim();
                    RegistrarUiDiagnostico($"CONF UI ADD solicitado numero={numero} salaPadraoSistema={salaSistema}");
                    // sempreAbrirSeletor=true: garante que o usuário escolha a rota manualmente
                    // para cada participante externo, evitando uso automático de rota incorreta.
                    return await AdicionarParticipanteNaChamadaAsync(numero, false, sempreAbrirSeletor: true);
                };

                _conferenceControlWindow.OnRefreshRequested += () => RegistrarUiDiagnostico("CONF UI REFRESH solicitado");
                _conferenceControlWindow.OnMuteRequested += async numero =>
                {
                    RegistrarUiDiagnostico($"CONF UI MUTE_REQUEST numero={numero}");
                    var config = SipConfig.CarregarSalva() ?? new SipConfig();
                    var sala = string.IsNullOrWhiteSpace(config.SalaConferenciaIssabel) ? "800" : config.SalaConferenciaIssabel.Trim();
                    bool ok = await _sipService.MutarParticipanteConferenciaAsync(numero, true, sala);
                    RegistrarUiDiagnostico($"CONF UI MUTE_OK={ok} numero={numero} erro={_sipService.LastConferenceError}");
                };
                _conferenceControlWindow.OnRemoveRequested += async numero =>
                {
                    RegistrarUiDiagnostico($"CONF UI REMOVE_REQUEST numero={numero}");
                    bool ok = await _sipService.DesligarParticipanteConferenciaAsync(numero);
                    RegistrarUiDiagnostico($"CONF UI REMOVE_{(ok ? "OK" : "FAIL")} numero={numero} erro={_sipService.LastConferenceError}");
                    if (ok)
                        Dispatcher.Invoke(() => _conferenceControlWindow?.MarcarParticipanteRemovido(numero));
                };
                _conferenceControlWindow.OnJoinCurrentCallAsync += async sala =>
                {
                    RegistrarUiDiagnostico($"CONF UI JOIN chamada atual sala={sala}");
                    return await _sipService.MoverChamadaAtualParaConferenciaIssabelAsync(sala);
                };
                _conferenceControlWindow.OnMuteHostRequested += isMuted =>
                {
                    try
                    {
                        using var enumerator = new MMDeviceEnumerator();
                        using var mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                        mic.AudioEndpointVolume.Mute = isMuted;
                        RegistrarUiDiagnostico($"CONF HOST MUTE isMuted={isMuted}");
                    }
                    catch (Exception ex)
                    {
                        RegistrarUiDiagnostico($"CONF HOST MUTE ERRO {ex.Message}");
                    }
                };
                _autoJoinFired = false;
                _conferenceControlWindow.Closed += (_, __) => { _conferenceControlWindow = null; _autoJoinFired = false; };
                _conferenceControlWindow.Show();
            }
            catch (Exception ex)
            {
                RegistrarUiDiagnostico("CONF UI ERRO abrir janela=" + ex.Message);
                MessageBox.Show("Não foi possível abrir a janela de conferência:\n\n" + ex.Message, "Waven VoIP");
            }
        }

        private void AbrirControleTransferenciaAssistida(string destino)
        {
            var displayDestino = ResolverDisplayChamada(destino);
            var controle = new TransferControlWindow(displayDestino, _activeCallWindow?.NomeNumeroAtual) { Owner = this };
            controle.OnReturnRequested += () =>
            {
                bool voltou = _sipService.VoltarTransferenciaAssistida();
                MessageBox.Show(voltou ? "Comando para voltar à chamada original enviado." : "Não foi possível enviar o retorno. Tente * no teclado.", "Waven VoIP");
            };
            controle.OnCompleteRequested += () => _sipService.ConcluirTransferenciaAssistida();
            controle.OnStarRequested += () => _sipService.EnviarSequenciaDtmf("*");
            controle.Show();
        }

        private async Task<bool> AdicionarParticipanteNumeroFinalConferenciaAsync(string numeroFinal, string sala)
        {
            if (!_sipService.IsInCall) return false;
            numeroFinal = numeroFinal?.Trim() ?? string.Empty;
            sala = string.IsNullOrWhiteSpace(sala) ? "800" : sala.Trim();
            if (string.IsNullOrWhiteSpace(numeroFinal)) return false;

            RegistrarUiDiagnostico($"CONF ADD INICIO numeroFinal={numeroFinal} sala={sala}");
            bool ok = await _sipService.AdicionarParticipanteConferenciaIssabelAsync(numeroFinal, sala);
            RegistrarUiDiagnostico($"CONF ADD FIM numeroFinal={numeroFinal} sala={sala} ok={ok} erro={_sipService.LastConferenceError}");
            return ok;
        }

        private async Task<bool> AdicionarParticipanteNaChamadaAsync(string numero, bool mostrarMensagem = true, bool sempreAbrirSeletor = false)
        {
            if (!_sipService.IsInCall) { if (mostrarMensagem) MessageBox.Show("Não há chamada ativa para adicionar participante.", "Waven VoIP"); return false; }

            numero = numero?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(numero)) return false;

            // Strip country code 55 and add 9th digit for old mobiles before route selection
            var numeroBrasil = PhoneNumberNormalizer.NormalizeBrazilPhone(numero);
            if (!string.Equals(numeroBrasil, numero, StringComparison.Ordinal))
                RegistrarUiDiagnostico($"CONFERENCE_NUMBER_NORMALIZED origem=adicionar_participante entrada={numero} saida={numeroBrasil}");
            numero = numeroBrasil;

            string numeroFinal = numero;

            if (!DialPlanService.EhRamalInterno(numero))
            {
                // Para conferência (sempreAbrirSeletor=true), sempre abre o seletor mesmo se o número
                // tiver um prefixo de rota aparente. Isso evita que área codes começando com 1/2/3
                // sejam erroneamente detectados como prefixo de tronco, além de garantir que o
                // usuário SEMPRE escolha a rota manualmente para cada participante.
                var numSemPrefixo = sempreAbrirSeletor
                    ? DialPlanService.RemoverPrefixoDeRota(numero)
                    : numero;
                var jaTemPrefixo = !sempreAbrirSeletor &&
                                   DialPlanService.ObterSaidaPeloPrefixo(numero) != null &&
                                   numero.Length > 5;

                if (!jaTemPrefixo)
                {
                    RegistrarUiDiagnostico($"CONF ADD SELETOR_ABERTURA numOriginal={numero} numSemPrefixo={numSemPrefixo} sempreAbrirSeletor={sempreAbrirSeletor}");
                    var seletor = await AbrirSeletorSaidaAsync(numSemPrefixo);
                    if (seletor == null)
                    {
                        RegistrarUiDiagnostico($"CONF ADD CANCELADO numOriginal={numero} motivo=seletor_cancelado_pelo_usuario");
                        return false;
                    }
                    var tronco = DialPlanService.NomeSaida(seletor.Value);
                    numeroFinal = DialPlanService.AplicarRegraDeDiscagem(numSemPrefixo, seletor.Value);
                    RegistrarUiDiagnostico($"CONF ADD ROTA_ESCOLHIDA numOriginal={numero} tronco={tronco} numeroFinal={numeroFinal}");
                }
            }

            var config = SipConfig.CarregarSalva() ?? new SipConfig();
            var sala = string.IsNullOrWhiteSpace(config.SalaConferenciaIssabel) ? "800" : config.SalaConferenciaIssabel.Trim();

            RegistrarUiDiagnostico($"CONF ADD INICIO numOriginal={numero} numeroFinal={numeroFinal} sala={sala}");
            bool ok = await _sipService.AdicionarParticipanteConferenciaIssabelAsync(numeroFinal, sala);
            RegistrarUiDiagnostico($"CONF ADD FIM numeroFinal={numeroFinal} sala={sala} ok={ok} erro={_sipService.LastConferenceError}");

            if (ok && !_autoJoinFired)
            {
                _autoJoinFired = true;
                var salaAutoJoin = sala;
                var confWin = _conferenceControlWindow;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    RegistrarUiDiagnostico($"CONF AUTO_JOIN INICIO sala={salaAutoJoin}");
                    bool joinOk = await _sipService.MoverChamadaAtualParaConferenciaIssabelAsync(salaAutoJoin);
                    RegistrarUiDiagnostico($"CONF AUTO_JOIN FIM ok={joinOk} erro={_sipService.LastConferenceError}");
                    Dispatcher.Invoke(() => confWin?.AtualizarStatus(joinOk
                        ? $"Host unido à sala {salaAutoJoin} automaticamente."
                        : $"Auto-join falhou: {_sipService.LastConferenceError}"));
                });
            }

            if (mostrarMensagem)
            {
                MessageBox.Show(ok
                    ? $"Conferência enviada para a sala {sala}. Participante: {numeroFinal}."
                    : "Não foi possível montar a conferência no Issabel. Detalhe: " + _sipService.LastConferenceError,
                    "Waven VoIP");
            }
            return ok;
        }

        private void RecordingToggleRequested()
        {
            bool ok = _sipService.EnviarSequenciaDtmf("*1");
            MessageBox.Show(ok ? "Código *1 enviado para gravação em chamada." : "Não foi possível enviar o código *1.", "Waven VoIP");
        }

        private void DtmfKeyboardRequested()
        {
            if (!_sipService.IsInCall) { MessageBox.Show("Não há chamada ativa.", "Waven VoIP"); return; }
            var teclado = new DtmfPadWindow(digito => _sipService.EnviarSequenciaDtmf(digito)) { Owner = this };
            teclado.Show();
        }

        private bool HoldToggleRequested() => _sipService.TentarSegurarAlternar();
        private void AudioRouteRequested() { var win = new SettingsWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }; win.Show(); }

        private void OpenConferenceParticipantsRequested()
        {
            MessageBox.Show("Use o botão Adicionar para chamar ramais ou números externos para a conferência.", "Waven VoIP");
        }

        private void MostrarNotificacaoChamadaPerdida(string caller)
        {
            try
            {
                var toast = new MissedCallToast(caller);
                toast.Show();
            }
            catch
            {
                try { _trayIcon?.ShowBalloonTip(3500, "Chamada perdida", caller, WF.ToolTipIcon.Warning); } catch { }
            }
        }

        // Resolves a raw SIP/dialed number to a clean human-readable display string.
        // Strips route prefix (1/2/3), country code 55, resolves contact name.
        // Returns "Chamada do sistema" for non-numeric callers (SIP labels, PJSIP, etc.).
        private string ResolverDisplayChamada(string numero)
        {
            try
            {
                var semRota = DialPlanService.RemoverPrefixoDeRota(
                    DialPlanService.RemoverDuplicacaoSequencial(numero ?? string.Empty));
                var numDisplay = PhoneNumberNormalizer.NormalizeForDisplay(semRota);

                if (string.IsNullOrWhiteSpace(numDisplay))
                    return "Chamada do sistema";

                var nomeContato = ContatoStorageService.ResolverNomePorNumero(numDisplay);
                var display = string.Equals(nomeContato, numDisplay, StringComparison.OrdinalIgnoreCase)
                    ? numDisplay
                    : $"{nomeContato} ({numDisplay})";

                if (!string.Equals(numero, display, StringComparison.OrdinalIgnoreCase))
                    RegistrarUiDiagnostico($"PHONE_NORMALIZED entrada={numero} display={display}");

                return display;
            }
            catch { return numero ?? string.Empty; }
        }

        private string RegistrarHistorico(string numero, TipoHistoricoLigacao tipo, string duracao, string origemSaida = "")
        {
            var itens = HistoricoStorageService.Carregar();
            var numeroTratado = DialPlanService.RemoverDuplicacaoSequencial(numero);
            var numLimpo = DialPlanService.RemoverPrefixoDeRota(numeroTratado);
            var numBrasil = PhoneNumberNormalizer.NormalizeBrazilPhone(numLimpo);
            var nomeResolvido = ContatoStorageService.ResolverNomePorNumero(numBrasil);
            var nome = string.Equals(nomeResolvido, numBrasil, StringComparison.OrdinalIgnoreCase)
                ? numeroTratado
                : nomeResolvido;

            var item = new HistoricoLigacaoItem
            {
                Numero = numeroTratado,
                Nome = nome,
                Tipo = tipo,
                Duracao = duracao,
                OrigemSaida = origemSaida
            };
            itens.Insert(0, item);
            HistoricoStorageService.Salvar(itens);
            HistoricoStorageService.LimparAntigas(ObterDiasRetencaoHistorico());
            AtualizarHistoricoShell();
            return item.Id;
        }

        private void IniciarControleHistoricoChamada(string historicoId)
        {
            _historicoChamadaAtivaId = historicoId ?? string.Empty;
            _inicioHistoricoChamadaAtiva = DateTime.Now;
        }

        private void AtualizarDuracaoHistoricoChamadaAtiva()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_historicoChamadaAtivaId)) return;

                var duracao = CalcularDuracaoHistorico();
                var itens = HistoricoStorageService.Carregar();
                var item = itens.FirstOrDefault(i => string.Equals(i.Id, _historicoChamadaAtivaId, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    item.Duracao = duracao;
                    HistoricoStorageService.Salvar(itens);
                    AtualizarHistoricoShell();
                }
            }
            catch { }
            finally
            {
                _historicoChamadaAtivaId = string.Empty;
                _inicioHistoricoChamadaAtiva = DateTime.MinValue;
            }
        }

        private string CalcularDuracaoHistorico()
        {
            try
            {
                var tempo = TimeSpan.Zero;
                if (_inicioHistoricoChamadaAtiva != DateTime.MinValue)
                    tempo = DateTime.Now - _inicioHistoricoChamadaAtiva;

                if (tempo.TotalSeconds < 0) tempo = TimeSpan.Zero;
                return tempo.ToString(tempo.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
            }
            catch { return "00:00"; }
        }

        private string OrigemEntradaAtual()
        {
            var origem = !string.IsNullOrWhiteSpace(_sipService.LastIncomingOrigin)
                ? _sipService.LastIncomingOrigin
                : _ultimaOrigemEntradaPendente;

            if (string.IsNullOrWhiteSpace(origem) ||
                origem.Equals("Entrada não identificada", StringComparison.OrdinalIgnoreCase))
                return "Operadora";

            return origem;
        }

        private void BtnApagar_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(txtNumero.Text))
            {
                txtNumero.Text = txtNumero.Text.Substring(0, txtNumero.Text.Length - 1);
                txtNumero.CaretIndex = txtNumero.Text.Length;
            }
            txtNumero.Focus();
        }

        private void BtnLimparNumero_Click(object sender, RoutedEventArgs e)
        {
            txtNumero.Text = string.Empty;
            txtNumero.Focus();
        }

        private int ObterDiasRetencaoHistorico()
        {
            var config = SipConfig.CarregarSalva();
            var dias = config?.HistoricoRetencaoDias ?? 7;
            return dias <= 0 ? 7 : dias;
        }

        private void CarregarDispositivosAudioNaTela()
        {
            try
            {
                if (cmbCfgMicrofone == null || cmbCfgAltoFalante == null) return;

                var audio = ConfiguracaoAudioService.Carregar();
                cmbCfgMicrofone.Items.Clear();
                cmbCfgAltoFalante.Items.Clear();
                cmbCfgDispToque.Items.Clear();
                cmbCfgMicrofone.Items.Add("Padrão do sistema");
                cmbCfgAltoFalante.Items.Add("Padrão do sistema");
                cmbCfgDispToque.Items.Add(new RingDeviceItem("", "Automático (Realtek/Speaker)"));

                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    foreach (var mic in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                        cmbCfgMicrofone.Items.Add(mic.FriendlyName);
                    foreach (var speaker in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        cmbCfgAltoFalante.Items.Add(speaker.FriendlyName);
                        cmbCfgDispToque.Items.Add(new RingDeviceItem(speaker.ID, speaker.FriendlyName));
                    }
                }
                catch { }

                cmbCfgMicrofone.SelectedItem = cmbCfgMicrofone.Items.Cast<object>().FirstOrDefault(i => i?.ToString() == audio.Microfone) ?? "Padrão do sistema";
                cmbCfgAltoFalante.SelectedItem = cmbCfgAltoFalante.Items.Cast<object>().FirstOrDefault(i => i?.ToString() == audio.AltoFalante) ?? "Padrão do sistema";
                txtCfgToque.Text = audio.Toque;
                chkCfgInvadirTela.IsChecked = audio.TocarEmTelaCheia;
                if (chkToqueSpeakerPrincipal != null) chkToqueSpeakerPrincipal.IsChecked = audio.ToqueSempreNoSpeakerPrincipal;
                if (cmbCfgDispToque != null)
                {
                    cmbCfgDispToque.SelectedItem = string.IsNullOrEmpty(audio.DispositivoToqueId)
                        ? cmbCfgDispToque.Items[0]
                        : cmbCfgDispToque.Items.Cast<object>().FirstOrDefault(i => i is RingDeviceItem r && r.Id == audio.DispositivoToqueId)
                          ?? cmbCfgDispToque.Items[0];
                }

                if (cmbGoogleSyncInterval != null)
                {
                    var intervalo = audio.GoogleSyncIntervalMinutes;
                    var found = cmbGoogleSyncInterval.Items.Cast<ComboBoxItem>()
                        .FirstOrDefault(i => i.Tag is string t && int.TryParse(t, out var v) && v == intervalo);
                    cmbGoogleSyncInterval.SelectedItem = found ?? cmbGoogleSyncInterval.Items[0];
                }
            }
            catch { }
        }

        private void BtnTrocarToquePrincipal_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "Áudio (*.mp3;*.wav)|*.mp3;*.wav|Todos os arquivos (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                    txtCfgToque.Text = dlg.FileName;
            }
            catch (Exception ex)
            {
                TratarErroSemTravamento(ex, "Não foi possível selecionar o toque.");
            }
        }

        private void BtnTestarToque_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _testRingtoneService?.Parar();
                _testRingtoneService = new RingtoneService();

                var dispSelecionado = cmbCfgDispToque?.SelectedItem as RingDeviceItem;
                var deviceId = dispSelecionado?.Id ?? string.Empty;
                var deviceNome = dispSelecionado?.Nome ?? string.Empty;

                var path = string.IsNullOrWhiteSpace(txtCfgToque?.Text)
                    ? (ConfiguracaoAudioService.Carregar().Toque ?? "Assets\\toque_padrao.mp3")
                    : txtCfgToque.Text.Trim();
                var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
                if (!File.Exists(fullPath)) { MessageBox.Show("Arquivo de toque não encontrado:\n" + fullPath, "Waven VoIP"); return; }

                var svc = _testRingtoneService;
                svc.Tocar(fullPath, deviceId, deviceNome, "TestButton", loop: false);
                Task.Delay(4000).ContinueWith(_ => Dispatcher.Invoke(() => svc.Parar()));
            }
            catch (Exception ex)
            {
                TratarErroSemTravamento(ex, "Erro ao testar toque.");
            }
        }

        private void CarregarPreferenciasNaTela()
        {
            try
            {
                var config = SipConfig.CarregarSalva() ?? new SipConfig();
                config.RepairDefaults();

                txtHistoricoRetencaoDias.Text = ObterDiasRetencaoHistorico().ToString();

                txtCfgRamal.Text = config.Ramal ?? string.Empty;
                if (txtCfgRamalNome != null) txtCfgRamalNome.Text = config.RamalNome ?? string.Empty;
                txtCfgLogin.Text = config.Login ?? string.Empty;
                txtCfgSenha.Password = config.Senha ?? string.Empty;
                txtCfgServidor.Text = config.ServerIp ?? string.Empty;
                txtCfgPorta.Text = config.Port.ToString();
                txtCfgDominio.Text = config.Domain ?? string.Empty;
                txtCfgProxy.Text = config.ProxySip ?? string.Empty;
                if (chkPermitirTransferenciaExterna != null)
                    chkPermitirTransferenciaExterna.IsChecked = config.PermitirTransferenciaExterna;

                chkAmiAtivo.IsChecked = config.AmiAtivo;
                txtAmiHost.Text = string.IsNullOrWhiteSpace(config.AmiHost) ? config.ServerIp : config.AmiHost;
                txtAmiPorta.Text = (config.AmiPorta <= 0 ? 5038 : config.AmiPorta).ToString();
                txtAmiUsuario.Text = string.IsNullOrWhiteSpace(config.AmiUsuario) ? "waven" : config.AmiUsuario;
                txtAmiSenha.Password = string.IsNullOrWhiteSpace(config.AmiSenha) ? "Waven@2025" : config.AmiSenha;
                if (cmbAmiIntervalo != null)
                {
                    var alvo = config.AmiIntervaloMinutos;
                    var item = cmbAmiIntervalo.Items.Cast<ComboBoxItem>()
                        .FirstOrDefault(i => i.Tag is string t && int.TryParse(t, out var v) && v == alvo);
                    cmbAmiIntervalo.SelectedItem = item ?? cmbAmiIntervalo.Items[0];
                }
                try { AtualizarStatusAmiContatos(config.AmiAtivo ? "AMI: configurado • aguardando sincronização" : "AMI: desativado nas configurações", config.AmiAtivo ? "#F59E0B" : "#94A3B8"); } catch { }
                try { AtualizarBotaoStatus(); } catch { }

                txtCanalOperadora.Text = string.IsNullOrWhiteSpace(config.CanalOperadora) ? "6631998716;IN-BRDID-6631998716" : config.CanalOperadora;
                txtCanal0800.Text = string.IsNullOrWhiteSpace(config.Canal0800) ? "08001901900;VONO-0800-ENTRADA" : config.Canal0800;
                txtCanalTim.Text = string.IsNullOrWhiteSpace(config.CanalWhatsAppTim) ? "556684263277;WAVOIP-556684263277" : config.CanalWhatsAppTim;
                txtCanalVivo.Text = string.IsNullOrWhiteSpace(config.CanalWhatsAppVivo) ? "556696308630;WAVOIP-556696308630" : config.CanalWhatsAppVivo;

                var whats = WhatsAppConfigService.Carregar();
                if (txtWhatsApiUrl != null)
                    txtWhatsApiUrl.Text = string.IsNullOrWhiteSpace(whats.ApiUrl) ? "https://api.wavenchat.com.br/v2/api/external/SEU_API_ID" : whats.ApiUrl;
                if (txtWhatsBearerToken != null)
                    txtWhatsBearerToken.Password = whats.BearerToken ?? string.Empty;
                if (txtWhatsNumeroTeste != null)
                    txtWhatsNumeroTeste.Text = whats.NumeroTeste ?? string.Empty;
                if (txtWhatsMensagemTeste != null)
                    txtWhatsMensagemTeste.Text = string.IsNullOrWhiteSpace(whats.MensagemTeste) ? "Teste de envio pelo Waven VoIP." : whats.MensagemTeste;
                if (txtWhatsStatus != null)
                    txtWhatsStatus.Text = string.IsNullOrWhiteSpace(whats.BearerToken) ? "WhatsApp: configure URL e token" : "WhatsApp: configurado";

                // CDR
                if (chkCdrAtivo != null) chkCdrAtivo.IsChecked = config.CdrAtivo;
                if (txtCdrHost != null) txtCdrHost.Text = string.IsNullOrWhiteSpace(config.CdrHost) ? config.ServerIp : config.CdrHost;
                if (txtCdrPorta != null) txtCdrPorta.Text = (config.CdrPorta <= 0 ? 3306 : config.CdrPorta).ToString();
                if (txtCdrBanco != null) txtCdrBanco.Text = string.IsNullOrWhiteSpace(config.CdrBanco) ? "asteriskcdrdb" : config.CdrBanco;
                if (txtCdrTabela != null) txtCdrTabela.Text = string.IsNullOrWhiteSpace(config.CdrTabela) ? "cdr" : config.CdrTabela;
                if (txtCdrUsuario != null) txtCdrUsuario.Text = string.IsNullOrWhiteSpace(config.CdrUsuario) ? "waven" : config.CdrUsuario;
                if (txtCdrSenha != null) txtCdrSenha.Password = config.CdrSenha ?? string.Empty;

                if (cmbHistoricoModo != null)
                {
                    var modoTodos = string.Equals(config.HistoricoModoExibicao, "TodosRamais", StringComparison.OrdinalIgnoreCase);
                    cmbHistoricoModo.SelectedIndex = modoTodos ? 1 : 0;
                }
                if (cmbHistoricoSyncInterval != null)
                {
                    var sec = config.HistoricoSyncIntervalSeconds > 0
                        ? config.HistoricoSyncIntervalSeconds
                        : (config.HistoricoSyncIntervalMinutes > 0 ? config.HistoricoSyncIntervalMinutes * 60 : 5);
                    var found = cmbHistoricoSyncInterval.Items.Cast<ComboBoxItem>()
                        .FirstOrDefault(i => i.Tag is string t && int.TryParse(t, out var v) && v == sec);
                    cmbHistoricoSyncInterval.SelectedItem = found ?? cmbHistoricoSyncInterval.Items[0];
                }

                // Gravações
                if (chkGravacaoAtiva != null) chkGravacaoAtiva.IsChecked = config.GravacaoAtiva;
                if (cmbGravacaoTipo != null)
                    cmbGravacaoTipo.SelectedIndex = string.Equals(config.GravacaoTipoAcesso, "Local", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (txtGravacaoUrlBase != null)
                {
                    var urlVal = !string.IsNullOrWhiteSpace(config.GravacaoCaminhoLocal)
                        ? config.GravacaoCaminhoLocal : config.GravacaoUrlBase;
                    txtGravacaoUrlBase.Text = string.IsNullOrWhiteSpace(urlVal)
                        ? "http://pabx.almeidagas.com/gravacoes/monitor/"
                        : urlVal;
                }

            }
            catch
            {
                try { txtHistoricoRetencaoDias.Text = "7"; } catch { }
            }
        }

        private void AplicarRetencaoHistorico()
        {
            HistoricoStorageService.LimparAntigas(ObterDiasRetencaoHistorico());
        }

        private void BtnSalvarPreferencias_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!int.TryParse(txtHistoricoRetencaoDias.Text?.Trim(), out var dias) || dias < 1)
                    throw new InvalidOperationException("Informe uma quantidade de dias maior que zero.");

                var config = SipConfig.CarregarSalva() ?? new SipConfig();
                config.HistoricoRetencaoDias = dias;

                config.Ramal = txtCfgRamal.Text?.Trim() ?? string.Empty;
                config.RamalNome   = txtCfgRamalNome?.Text?.Trim() ?? string.Empty;
                config.NomeUsuario = config.RamalNome; // keep in sync
                config.Login = txtCfgLogin.Text?.Trim() ?? string.Empty;
                config.Senha = txtCfgSenha.Password?.Trim() ?? string.Empty;
                config.ServerIp = txtCfgServidor.Text?.Trim() ?? config.ServerIp;
                config.Domain = txtCfgDominio.Text?.Trim() ?? config.Domain;
                config.ProxySip = txtCfgProxy.Text?.Trim() ?? config.ProxySip;
                if (int.TryParse(txtCfgPorta.Text?.Trim(), out var porta) && porta > 0) config.Port = porta;
                config.PermitirTransferenciaExterna = chkPermitirTransferenciaExterna?.IsChecked == true;

                config.AmiAtivo = chkAmiAtivo?.IsChecked == true;
                config.AmiHost = txtAmiHost.Text?.Trim() ?? string.Empty;
                if (int.TryParse(txtAmiPorta.Text?.Trim(), out var amiPorta) && amiPorta > 0) config.AmiPorta = amiPorta;
                config.AmiUsuario = string.IsNullOrWhiteSpace(txtAmiUsuario.Text) ? "waven" : txtAmiUsuario.Text.Trim();
                config.AmiSenha = string.IsNullOrWhiteSpace(txtAmiSenha.Password) ? "Waven@2025" : txtAmiSenha.Password.Trim();
                if (cmbAmiIntervalo?.SelectedItem is ComboBoxItem amiItem && amiItem.Tag is string amiTag && int.TryParse(amiTag, out var amiMin))
                    config.AmiIntervaloMinutos = amiMin;
                AtualizarBotaoStatus();

                config.CanalOperadora = txtCanalOperadora.Text?.Trim() ?? string.Empty;
                config.Canal0800 = txtCanal0800.Text?.Trim() ?? string.Empty;
                config.CanalWhatsAppTim = txtCanalTim.Text?.Trim() ?? string.Empty;
                config.CanalWhatsAppVivo = txtCanalVivo.Text?.Trim() ?? string.Empty;

                WhatsAppConfigService.Salvar(new WhatsAppConfig
                {
                    ApiUrl = txtWhatsApiUrl?.Text?.Trim() ?? string.Empty,
                    BearerToken = txtWhatsBearerToken?.Password?.Trim() ?? string.Empty,
                    NumeroTeste = txtWhatsNumeroTeste?.Text?.Trim() ?? string.Empty,
                    MensagemTeste = txtWhatsMensagemTeste?.Text?.Trim() ?? "Teste de envio pelo Waven VoIP."
                });

                try
                {
                    var audio = ConfiguracaoAudioService.Carregar();
                    audio.Microfone = cmbCfgMicrofone?.SelectedItem?.ToString() ?? "Padrão do sistema";
                    audio.AltoFalante = cmbCfgAltoFalante?.SelectedItem?.ToString() ?? "Padrão do sistema";
                    audio.Toque = txtCfgToque?.Text?.Trim() ?? "Assets\\toque_padrao.mp3";
                    audio.TocarEmTelaCheia = chkCfgInvadirTela?.IsChecked == true;
                    audio.ToqueSempreNoSpeakerPrincipal = chkToqueSpeakerPrincipal?.IsChecked == true;
                    var dispSelecionado = cmbCfgDispToque?.SelectedItem as RingDeviceItem;
                    audio.DispositivoToqueId = dispSelecionado?.Id ?? string.Empty;
                    audio.DispositivoToqueNome = dispSelecionado?.Nome ?? string.Empty;

                    var intervaloItem = cmbGoogleSyncInterval?.SelectedItem as ComboBoxItem;
                    if (intervaloItem?.Tag is string tagStr && int.TryParse(tagStr, out var intervMin))
                        audio.GoogleSyncIntervalMinutes = intervMin;

                    ConfiguracaoAudioService.Salvar(audio);
                    ConfigurarSincronizacaoGoogle();
                }
                catch { }

                // CDR + Gravações
                var cdrCfg = MontarConfigCdrAtual();
                config.CdrAtivo = cdrCfg.CdrAtivo;
                config.CdrHost = cdrCfg.CdrHost;
                config.CdrPorta = cdrCfg.CdrPorta;
                config.CdrBanco = cdrCfg.CdrBanco;
                config.CdrTabela = cdrCfg.CdrTabela;
                config.CdrUsuario = cdrCfg.CdrUsuario;
                config.CdrSenha = cdrCfg.CdrSenha;
                config.HistoricoModoExibicao = cdrCfg.HistoricoModoExibicao;
                config.HistoricoSyncIntervalMinutes = cdrCfg.HistoricoSyncIntervalMinutes;
                config.HistoricoSyncIntervalSeconds = cdrCfg.HistoricoSyncIntervalSeconds;
                config.GravacaoAtiva = cdrCfg.GravacaoAtiva;
                config.GravacaoTipoAcesso = cdrCfg.GravacaoTipoAcesso;
                config.GravacaoUrlBase = cdrCfg.GravacaoUrlBase;
                config.GravacaoCaminhoLocal = cdrCfg.GravacaoCaminhoLocal;

                config.Salvar();
                ConfigurarSincronizacaoAmi();
                ConfigurarSincronizacaoCdr();
                AtualizarStatusAmiContatos(config.AmiAtivo ? "AMI: configurações salvas • clique em Sincronizar ramais AMI" : "AMI: desativado nas configurações", config.AmiAtivo ? "#F59E0B" : "#94A3B8");

                HistoricoStorageService.LimparAntigas(dias);
                AtualizarHistoricoShell();
                txtStatus.Text = "Configurações salvas. Reinicie o registro se alterar servidor/ramal.";
                MessageBox.Show("Configurações salvas.", "Waven VoIP");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


        private async void BtnEnviarTesteWhatsApp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var config = new WhatsAppConfig
                {
                    ApiUrl = txtWhatsApiUrl?.Text?.Trim() ?? string.Empty,
                    BearerToken = txtWhatsBearerToken?.Password?.Trim() ?? string.Empty,
                    NumeroTeste = txtWhatsNumeroTeste?.Text?.Trim() ?? string.Empty,
                    MensagemTeste = txtWhatsMensagemTeste?.Text?.Trim() ?? "Teste de envio pelo Waven VoIP."
                };

                WhatsAppConfigService.Salvar(config);

                if (string.IsNullOrWhiteSpace(config.NumeroTeste))
                {
                    MessageBox.Show("Informe um número de teste para enviar a mensagem.", "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                txtWhatsStatus.Text = "WhatsApp: enviando teste...";
                var resultado = await WhatsAppService.EnviarMensagemAsync(config.NumeroTeste, config.MensagemTeste, "teste_configuracao");
                txtWhatsStatus.Text = resultado.Sucesso ? $"WhatsApp: teste enviado • HTTP {resultado.HttpStatusCode}" : $"WhatsApp: falha • HTTP {resultado.HttpStatusCode}";

                MessageBox.Show(
                    $"HTTP: {resultado.HttpStatusCode}\nSucesso: {resultado.Sucesso}\n\nResposta:\n{resultado.RespostaBruta}\n\nDebug:\n{resultado.Debug}",
                    "Teste WhatsApp",
                    MessageBoxButton.OK,
                    resultado.Sucesso ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                if (txtWhatsStatus != null) txtWhatsStatus.Text = "WhatsApp: erro no teste";
                MessageBox.Show(ex.Message, "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


        private void BtnTrocarUsuario_Click(object sender, RoutedEventArgs e) => TrocarUsuario();

        private void BtnSairConta_Click(object sender, RoutedEventArgs e) => TrocarUsuario();

        public void TrocarUsuario()
        {
            var confirma = MessageBox.Show(
                "Trocar usuário?\n\n" +
                "Nome, Ramal, Login e Senha serão limpos.\n" +
                "Todas as configurações da empresa (servidor SIP, AMI, CDR, áudio, WhatsApp, histórico e contatos) serão mantidas.",
                "Waven VoIP — Trocar Usuário",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirma != MessageBoxResult.Yes) return;

            try
            {
                _reconnectTimer?.Stop();
                _sipService.Desligar();

                // Clear only user-specific fields — preserve all company settings
                var config = SipConfig.CarregarSalva() ?? new SipConfig();
                config.NomeUsuario   = string.Empty;
                config.RamalNome     = string.Empty;
                config.DisplayName   = string.Empty;
                config.Ramal         = string.Empty;
                config.Login         = string.Empty;
                config.Senha         = string.Empty;
                config.Salvar();
            }
            catch { }

            _fechamentoReal = true;
            try { _trayIcon?.Dispose(); } catch { }

            var setup = new SetupWindow();
            Application.Current.MainWindow = setup;
            setup.Show();
            Close();
        }

        private void BtnAlterarContaSip_Click(object sender, RoutedEventArgs e)
        {
            var setup = new SetupWindow(_sipService) { Owner = this };
            setup.ShowDialog();
        }

        private void BtnContatos_Click(object sender, RoutedEventArgs e)
        {
            var win = new ContactsWindow(async numero => await IniciarLigacaoAsync(numero), null, false, numero => AbrirTelaWhatsApp(numero, "contato"));
            AbrirDialogSeguro(win);
        }

        private void BtnHistorico_Click(object sender, RoutedEventArgs e)
        {
            var win = new HistoryWindow(async numero => await IniciarLigacaoDoHistoricoAsync(numero), numero => AbrirTelaWhatsApp(numero, "historico"));
            AbrirDialogSeguro(win);
        }

        private void BtnConfiguracoes_Click(object sender, RoutedEventArgs e) { var win = new SettingsWindow(); AbrirDialogSeguro(win); }


        private void AbrirTelaWhatsApp(string numero, string tipoEvento)
        {
            var numSem55 = PhoneNumberNormalizer.NormalizeForDial(numero ?? string.Empty);
            var numLimpo = DialPlanService.RemoverPrefixoDeRota(numSem55);
            if (!DialPlanService.IsTelefoneExternoValido(numLimpo))
            {
                MessageBox.Show(
                    "Este registro não possui telefone externo válido para WhatsApp.\n\n" +
                    $"Número recebido: {(string.IsNullOrWhiteSpace(numero) ? "(vazio)" : numero)}\n\n" +
                    "Apenas celulares e fixos externos (8+ dígitos, sem ramal/fila/URA) podem receber mensagens.",
                    "Waven VoIP — WhatsApp",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var nome = BuscarNomeContatoPorNumero(numLimpo);
            var win = new WhatsAppMessageWindow(numLimpo, string.Empty, tipoEvento, nome);
            AbrirDialogSeguro(win);
        }

        private static string BuscarNomeContatoPorNumero(string numero)
        {
            try
            {
                var key = PhoneNumberNormalizer.NormalizeForSearch(numero);
                return ContatoStorageService.Carregar()
                    .FirstOrDefault(c => PhoneNumberNormalizer.NormalizeForSearch(c.Numero) == key)?.Nome
                    ?? string.Empty;
            }
            catch { return string.Empty; }
        }




        private Task<SaidaChamada?> AbrirSeletorSaidaAsync(string numero)
        {
            try
            {
                numero = DialPlanService.RemoverDuplicacaoSequencial(numero ?? string.Empty);
                var seletor = new RouteSelectorWindow(numero)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true
                };

                // Só define Owner se a janela principal estiver realmente visível/ativa.
                // Isso evita que o seletor fique preso a uma janela que acabou de ser ocultada/fechada
                // no fluxo de Contatos/Histórico.
                try
                {
                    if (_activeCallWindow is Window cw && cw.IsVisible) seletor.Owner = cw;
                    else if (IsVisible) seletor.Owner = this;
                }
                catch { }

                seletor.ShowDialog();
                return Task.FromResult(seletor.Confirmado ? seletor.SaidaSelecionada : null);
            }
            catch (Exception ex)
            {
                TratarErroSemTravamento(ex, "Não foi possível abrir a seleção de saída.");
                return Task.FromResult<SaidaChamada?>(null);
            }
        }

        private void FecharSeletorSaida(SaidaChamada? saida)
        {
            try
            {
                routeSelectorOverlay.Visibility = Visibility.Collapsed;
                _routeSelectorTcs?.TrySetResult(saida);
                _routeSelectorTcs = null;
            }
            catch { }
        }

        private void RouteOperadora_Click(object sender, RoutedEventArgs e) => FecharSeletorSaida(SaidaChamada.Operadora);
        private void RouteWhatsAppTim_Click(object sender, RoutedEventArgs e) => FecharSeletorSaida(SaidaChamada.WhatsAppTim);
        private void RouteWhatsAppVivo_Click(object sender, RoutedEventArgs e) => FecharSeletorSaida(SaidaChamada.WhatsAppVivo);
        private void RouteCancelar_Click(object sender, RoutedEventArgs e) => FecharSeletorSaida(null);

        private bool AbrirPromptSeguro(InputPromptWindow prompt)
        {
            try
            {
                return prompt.ShowDialog() == true || prompt.Confirmado;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 0)
            {
                return prompt.Confirmado;
            }
            catch (InvalidOperationException ex)
            {
                TratarErroSemTravamento(ex, "Não foi possível abrir a tela de transferência.");
                return prompt.Confirmado;
            }
        }

        private bool? AbrirDialogSeguro(Window janela)
        {
            try
            {
                if (janela == null) return false;
                if (janela.Owner == null && janela != this) janela.Owner = this;
                janela.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                // Padrão seguro: abrir como janela normal. Evita travamentos de ShowDialog
                // quando o Windows/WPF acusa handle inválido.
                janela.Show();
                janela.Activate();
                return null;
            }
            catch (Exception ex)
            {
                TratarErroSemTravamento(ex, "Não foi possível abrir a janela solicitada.");
                return false;
            }
        }

        private void TratarErroSemTravamento(Exception ex, string mensagemPadrao)
        {
            if (ex is System.ComponentModel.Win32Exception w32 && w32.NativeErrorCode == 0)
                return;

            var msg = ex?.Message ?? string.Empty;
            if (msg.Contains("A operação foi concluída com êxito", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("The operation completed successfully", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                txtStatus.Text = mensagemPadrao;
            }
            catch { }

            MessageBox.Show(mensagemPadrao + "\n" + msg, "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void BtnDndToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private async void MenuOnline_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _dndAtivo = false;
                AtualizarBotaoStatus();
                txtStatus.Text = "Reconectando ao Issabel...";
                _sipService.Registrar();
                if (_sipService.IsInCall) return;
                await _sipService.ExecutarCodigoFuncao("*79");
            }
            catch (Exception ex) { TratarErroSemTravamento(ex, "Não foi possível ativar Online."); }
        }

        private async void MenuOffline_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool ok = await _sipService.ExecutarCodigoFuncao("*78");
                if (ok)
                {
                    _dndAtivo = true;
                    AtualizarBotaoStatus();
                    txtStatus.Text = "Offline — chamadas bloqueadas no Issabel.";
                }
                else MessageBox.Show("Não foi possível ativar o modo Offline no Issabel.", "Waven VoIP");
            }
            catch (Exception ex) { TratarErroSemTravamento(ex, "Não foi possível ativar Offline."); }
        }

        private void AtualizarBotaoStatus()
        {
            try
            {
                if (btnDndToggle == null) return;
                var config = SipConfig.CarregarSalva() ?? new SipConfig();
                var ramal  = config.Ramal?.Trim() ?? string.Empty;

                // Priority: NomeUsuario → RamalNome → DisplayName → contacts/AMI lookup
                var nome = config.NomeUsuario?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(nome))
                    nome = config.RamalNome?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(nome))
                    nome = config.DisplayName?.Trim() ?? string.Empty;
                // Strip ramal number from DisplayName if it equals the ramal (avoid "100 (100)")
                if (string.Equals(nome, ramal, StringComparison.OrdinalIgnoreCase))
                    nome = string.Empty;
                if (string.IsNullOrWhiteSpace(nome) && !string.IsNullOrWhiteSpace(ramal))
                {
                    var resolvido = ContatoStorageService.ResolverNomePorNumero(ramal);
                    if (!string.Equals(resolvido, ramal, StringComparison.OrdinalIgnoreCase))
                        nome = resolvido;
                }

                var label = !string.IsNullOrWhiteSpace(nome)
                    ? $"{nome} ({ramal})"
                    : (!string.IsNullOrWhiteSpace(ramal) ? $"Ramal {ramal}" : "Online");

                if (txtDndNomeRamal != null)
                    txtDndNomeRamal.Text = label;
                if (txtDndStatusLabel != null)
                {
                    txtDndStatusLabel.Text = _dndAtivo ? "Offline" : "Online";
                    txtDndStatusLabel.Foreground = new SolidColorBrush(_dndAtivo
                        ? Color.FromRgb(254, 202, 202)
                        : Color.FromRgb(187, 247, 208));
                }
                if (dotStatusBtn != null)
                    dotStatusBtn.Background = new SolidColorBrush(_dndAtivo
                        ? Color.FromRgb(254, 202, 202)
                        : Color.FromRgb(255, 255, 255));
                btnDndToggle.Background = new SolidColorBrush(_dndAtivo
                    ? Color.FromRgb(239, 68, 68)
                    : Color.FromRgb(34, 197, 94));
            }
            catch { }
        }


        // ── System log panel — real-time streaming ───────────────────────────────

        private void InicializarPainelLogs()
        {
            listSysLogs.ItemsSource = _logItems;
            LogHelper.LogWritten   += OnLogWritten;
            Closed += (_, _) => LogHelper.LogWritten -= OnLogWritten;
            AtualizarBotoesFiltroLogs(btnLogFiltroTodos);
            CarregarLogsExistentes();
        }

        private void OnLogWritten(LogEntry entry)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _allLogs.Add(entry);
                if (_allLogs.Count > 2000) _allLogs.RemoveAt(0);

                if (!LogPassaFiltro(entry)) return;

                _logItems.Add(new LogViewModel
                {
                    Text   = FormatarEntrada(entry),
                    Color  = CorLog(entry),
                    Source = entry
                });
                if (_logItems.Count > 2000) _logItems.RemoveAt(0);

                if (chkLogAutoScroll?.IsChecked == true)
                    listSysLogs.ScrollIntoView(_logItems[_logItems.Count - 1]);
            }));
        }

        private void BtnLogFiltro_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            _activeFilter = btn.Tag?.ToString() ?? "ALL";
            AtualizarBotoesFiltroLogs(btn);

            _logItems.Clear();
            foreach (var entry in _allLogs)
            {
                if (!LogPassaFiltro(entry)) continue;
                _logItems.Add(new LogViewModel { Text = FormatarEntrada(entry), Color = CorLog(entry), Source = entry });
                if (_logItems.Count >= 2000) break;
            }

            if (chkLogAutoScroll?.IsChecked == true && _logItems.Count > 0)
                listSysLogs.ScrollIntoView(_logItems[_logItems.Count - 1]);
        }

        private void AtualizarBotoesFiltroLogs(Button active)
        {
            var ativos   = new SolidColorBrush(Color.FromRgb(124, 58, 237));   ativos.Freeze();
            var inativos = new SolidColorBrush(Color.FromRgb(241, 245, 249));  inativos.Freeze();
            var txOn     = new SolidColorBrush(Colors.White);                  txOn.Freeze();
            var txOff    = new SolidColorBrush(Color.FromRgb(71, 85, 105));    txOff.Freeze();

            foreach (var btn in new[] { btnLogFiltroTodos, btnLogFiltroErros, btnLogFiltroUpdater,
                                        btnLogFiltroGoogle, btnLogFiltroAmi, btnLogFiltroWhatsApp, btnLogFiltroSip })
            {
                if (btn == null) continue;
                btn.Background  = btn == active ? ativos   : inativos;
                btn.Foreground  = btn == active ? txOn     : txOff;
                btn.BorderThickness = new Thickness(0);
            }
        }

        private bool LogPassaFiltro(LogEntry e) => _activeFilter switch
        {
            "ERROR"  => e.Level == LogLevel.ERROR,
            "update" => e.Channel.Contains("update", StringComparison.OrdinalIgnoreCase),
            "google" => e.Message.Contains("GOOGLE",  StringComparison.OrdinalIgnoreCase) ||
                        e.Caller.Contains("Google",   StringComparison.OrdinalIgnoreCase),
            "ami"    => e.Channel.Contains("ami",     StringComparison.OrdinalIgnoreCase) ||
                        e.Message.Contains("[AMI",    StringComparison.OrdinalIgnoreCase),
            "whats"  => e.Channel.Contains("whats",  StringComparison.OrdinalIgnoreCase) ||
                        e.Message.Contains("WHATS",  StringComparison.OrdinalIgnoreCase),
            "sip"    => e.Channel.Contains("sip",    StringComparison.OrdinalIgnoreCase),
            _        => true
        };

        private static string FormatarEntrada(LogEntry e)
            => $"{e.Timestamp:HH:mm:ss.fff} [{e.Level,-5}] [{e.Channel}] {e.Message}";

        private static Brush CorLog(LogEntry e)
        {
            Brush b;
            if (e.Level == LogLevel.ERROR)
            {
                b = new SolidColorBrush(Color.FromRgb(248, 113, 113)); b.Freeze(); return b;
            }
            if (e.Level == LogLevel.WARN)
            {
                b = new SolidColorBrush(Color.FromRgb(252, 211, 77));  b.Freeze(); return b;
            }
            var m = e.Message;
            if (m.Contains("_OK") || m.Contains("SUCCESS") || m.Contains("SUCESSO") ||
                m.Contains("Conectado") || m.Contains("CONNECTED") || m.Contains("✔") ||
                m.Contains("sincronizado") || m.Contains("funcionando") || m.Contains("online"))
            {
                b = new SolidColorBrush(Color.FromRgb(74, 222, 128)); b.Freeze(); return b;
            }
            b = new SolidColorBrush(Color.FromRgb(203, 213, 225)); b.Freeze(); return b;
        }

        private void BtnLimparLogsShell_Click(object sender, RoutedEventArgs e)
        {
            _logItems.Clear(); // visual only — _allLogs and disk files are untouched
        }

        private void BtnCopiarLogsShell_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (LogViewModel item in listSysLogs.Items) sb.AppendLine(item.Text);
                Clipboard.SetText(sb.ToString());
            }
            catch (Exception ex) { MessageBox.Show("Não foi possível copiar.\n\n" + ex.Message, "Waven VoIP"); }
        }

        private void BtnAbrirPastaLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var pasta = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WavenVoIP", "Logs");
                Directory.CreateDirectory(pasta);
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(pasta) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("Não foi possível abrir a pasta.\n\n" + ex.Message, "Waven VoIP"); }
        }

        private void CarregarLogsExistentes()
        {
            Task.Run(() =>
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WavenVoIP", "Logs");
                if (!Directory.Exists(logDir)) return;

                var entries  = new List<LogEntry>();
                var channels = new[] { "ui_flow", "sip_signal", "ami_sync", "cdr_sync", "update" };

                foreach (var ch in channels)
                {
                    var path = Path.Combine(logDir, $"{ch}.log");
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var lines = File.ReadLines(path)
                                        .Where(l => !string.IsNullOrWhiteSpace(l))
                                        .TakeLast(400)
                                        .ToList();
                        foreach (var line in lines)
                        {
                            var entry = ParsearLinhaLog(line, ch);
                            if (entry != null) entries.Add(entry);
                        }
                    }
                    catch { }
                }

                entries.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                if (entries.Count > 2000) entries = entries.GetRange(entries.Count - 2000, 2000);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var entry in entries)
                    {
                        _allLogs.Add(entry);
                        if (LogPassaFiltro(entry))
                            _logItems.Add(new LogViewModel
                            {
                                Text   = FormatarEntrada(entry),
                                Color  = CorLog(entry),
                                Source = entry
                            });
                    }
                    if (chkLogAutoScroll?.IsChecked == true && _logItems.Count > 0)
                        listSysLogs.ScrollIntoView(_logItems[_logItems.Count - 1]);
                }));
            });
        }

        // Format: "2026-05-24 10:30:45.123 [INFO ] [CallerName] message"
        private static LogEntry? ParsearLinhaLog(string line, string channel)
        {
            try
            {
                if (line.Length < 26) return null;
                var ts = DateTime.ParseExact(line[..23], "yyyy-MM-dd HH:mm:ss.fff",
                                             System.Globalization.CultureInfo.InvariantCulture);
                var rest = line[24..].TrimStart();

                var level = LogLevel.INFO;
                if (rest.StartsWith("[WARN"))  level = LogLevel.WARN;
                if (rest.StartsWith("[ERROR")) level = LogLevel.ERROR;

                var b1 = rest.IndexOf(']');
                var caller = "";
                var msg    = rest;
                if (b1 >= 0 && b1 + 1 < rest.Length)
                {
                    var rem = rest[(b1 + 1)..].TrimStart();
                    var b2  = rem.IndexOf(']');
                    if (b2 >= 0)
                    {
                        caller = rem[1..b2];
                        msg    = b2 + 1 < rem.Length ? rem[(b2 + 1)..].TrimStart() : "";
                    }
                }
                return new LogEntry { Timestamp = ts, Level = level, Channel = channel, Caller = caller, Message = msg };
            }
            catch { return null; }
        }

        private void BtnDesligarPrincipal_Click(object sender, RoutedEventArgs e)
        {
            _sipService.Desligar();
            txtStatus.Text = "Chamada encerrada.";
        }
    }

    internal sealed class RingDeviceItem
    {
        public string Id { get; }
        public string Nome { get; }
        public RingDeviceItem(string id, string nome) { Id = id; Nome = nome; }
        public override string ToString() => Nome;
    }
}
