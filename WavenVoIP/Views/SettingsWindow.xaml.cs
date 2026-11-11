using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using MySqlConnector;
using NAudio.CoreAudioApi;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class SettingsWindow : Window
    {
        private ConfiguracaoAudio _config;
        private WhatsAppConfig _whatsConfig;
        private SipConfig _sipConfig;
        private RingtoneService? _testRing;

        public SettingsWindow()
        {
            InitializeComponent();
            // v2.6.0 — cabe na tela do notebook e amplia em monitor grande (só apresentação)
            LayoutResponsivo.Ativar(this, new LayoutResponsivo.Opcoes());
            Closed += (_, _) => PararTesteMicrofone();
            _config     = ConfiguracaoAudioService.Carregar();
            _whatsConfig = WhatsAppConfigService.Carregar();
            _sipConfig  = SipConfig.CarregarSalva() ?? new SipConfig();
            _sipConfig.RepairDefaults();

            // Auto-detect audio devices and fill empty fields
            AudioDeviceService.AplicarSelecaoAutomatica(_sipConfig);

            LogHelper.Sip($"CONFIG_LOADED_PATH: {SipConfig.ConfigFilePath}");
            LogHelper.Sip($"CONFIG_AFTER_REPAIR: ServerIp={_sipConfig.ServerIp} Port={_sipConfig.Port} Domain={_sipConfig.Domain} AmiHost={_sipConfig.AmiHost} AmiPorta={_sipConfig.AmiPorta} AmiUsuario={_sipConfig.AmiUsuario}");

            CarregarDispositivos();

            sldVolumeToque.ValueChanged += (_, _) =>
            {
                if (txtVolumeToqueVal != null)
                    txtVolumeToqueVal.Text = ((int)sldVolumeToque.Value).ToString();
            };

            Closed += (_, _) => _testRing?.Dispose();

            // Áudio
            txtToque.Text            = _config.Toque;
            chkInvadirTela.IsChecked = _config.TocarEmTelaCheia;
            chkIniciarWindows.IsChecked = EstaConfiguradoParaIniciarComWindows();

            // WhatsApp
            txtWhatsApiUrl.Text       = _whatsConfig.ApiUrl;
            txtWhatsToken.Password    = _whatsConfig.BearerToken;
            txtWhatsNumeroTeste.Text  = _whatsConfig.NumeroTeste;
            txtWhatsMensagemTeste.Text = _whatsConfig.MensagemTeste;

            // SIP
            txtSipServidor.Text  = _sipConfig.ServerIp;
            txtSipPorta.Text     = _sipConfig.Port.ToString();
            txtSipDominio.Text   = _sipConfig.Domain;
            txtSipAmiHost.Text   = _sipConfig.AmiHost;
            txtSipAmiPorta.Text  = _sipConfig.AmiPorta.ToString();
            txtSipAmiUsuario.Text = _sipConfig.AmiUsuario;
            txtSipAmiSenha.Password = _sipConfig.AmiSenha;

            // Waven API
            chkUsarWavenApi.IsChecked  = _sipConfig.UsarWavenApi;
            txtWavenApiUrl.Text        = _sipConfig.WavenApiUrl;
            pwdWavenApiToken.Password  = _sipConfig.WavenApiToken;
            chkUsarWavenApi.Checked   += (_, _) => AtualizarLabelsApiMode();
            chkUsarWavenApi.Unchecked += (_, _) => AtualizarLabelsApiMode();
            AtualizarLabelsApiMode();

            // CDR
            chkCdrAtivo.IsChecked = _sipConfig.CdrAtivo;
            txtCdrHost.Text    = string.IsNullOrWhiteSpace(_sipConfig.CdrHost) ? _sipConfig.ServerIp : _sipConfig.CdrHost;
            txtCdrPorta.Text   = _sipConfig.CdrPorta > 0 ? _sipConfig.CdrPorta.ToString() : "3306";
            txtCdrBanco.Text   = _sipConfig.CdrBanco;
            txtCdrTabela.Text  = _sipConfig.CdrTabela;
            txtCdrUsuario.Text = _sipConfig.CdrUsuario;
            txtCdrSenha.Password = _sipConfig.CdrSenha;

            // Sync intervals
            SelecionarComboBoxPorTag(cmbAmiSyncInterval,  _sipConfig.AmiSyncIntervalSeconds.ToString());
            SelecionarComboBoxPorTag(cmbCdrSyncInterval,  _sipConfig.HistoricoSyncIntervalSeconds.ToString());
            SelecionarComboBoxPorTag(cmbGoogleSyncInterval, _sipConfig.GoogleSyncIntervalSeconds.ToString());
            SelecionarComboBoxPorTag(cmbHistoricoModo, _sipConfig.HistoricoModoExibicao);

            // Atualizações
            chkAutoUpdate.IsChecked = _sipConfig.AutoUpdateEnabled;
            txtUpdateVerLocal.Text  = VersionService.Versao;
            AtualizarPainelUpdate(UpdateService.EstadoAtual);
            UpdateService.ProgressoChanged += OnUpdateProgresso;
            Closed += (_, _) => UpdateService.ProgressoChanged -= OnUpdateProgresso;

            // Logs e Diagnóstico
            chkLogAtivo.IsChecked      = _sipConfig.LogEnabled;
            chkLogDetalhado.IsChecked  = _sipConfig.LogDetailedEnabled;
            AtualizarStatusLog();

            // Status das integrações
            IntegrationStatusService.StatusChanged += OnIntegrationStatusChanged;
            Closed += (_, _) => IntegrationStatusService.StatusChanged -= OnIntegrationStatusChanged;
            AtualizarStatusIntegracoes();

            if (UpdateService.TemFalhaFlag())
            {
                borderUpdateFailed.Visibility    = Visibility.Visible;
                btnLimparFlagUpdate.Visibility   = Visibility.Visible;
            }

            LogHelper.Sip($"CONFIG_UI_BIND_START");
            LogHelper.Sip($"CONFIG_UI_FIELD ServidorSip={_sipConfig.ServerIp}");
            LogHelper.Sip($"CONFIG_UI_FIELD AmiHost={_sipConfig.AmiHost}");
            LogHelper.Sip($"CONFIG_UI_FIELD Toque={_config.Toque}");
        }

        private void CarregarDispositivos()
        {
            cmbMicrofone.Items.Clear();
            cmbAltoFalante.Items.Clear();
            cmbDispToque.Items.Clear();

            cmbMicrofone.Items.Add("Automático");
            cmbAltoFalante.Items.Add("Automático");
            cmbDispToque.Items.Add(new RingDeviceItem("", "Automático", "Automático (alto-falante do computador)"));

            try
            {
                using var enumerator = new MMDeviceEnumerator();

                foreach (var mic in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    cmbMicrofone.Items.Add(mic.FriendlyName);

                foreach (var speaker in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    cmbAltoFalante.Items.Add(speaker.FriendlyName);
            }
            catch { }

            // v2.6.2 — saídas do toque com o tipo (headset fica identificado na lista)
            foreach (var s in AudioDeviceService.ListarSaidasParaToque())
            {
                var tipo = AudioDeviceService.ClassificarSaida(s);
                var rotulo = tipo == AudioDeviceService.TipoSaida.Headset ? $"{s.Nome}  — headset" : s.Nome;
                cmbDispToque.Items.Add(new RingDeviceItem(s.Id, s.Nome, rotulo));
            }

            // Selecionar a partir do SipConfig (novos campos)
            // v2.6.1 — o mesmo aparelho em outra porta USB ("3- …") é reconhecido; e um aparelho
            // configurado que está desconectado continua visível e selecionado (antes a tela mostrava
            // "Automático" e, ao salvar qualquer outra coisa, a escolha do headset era perdida).
            SelecionarDispositivo(cmbMicrofone, _sipConfig.AudioInputDevice, _sipConfig.AudioInputDeviceId, saida: false);
            SelecionarDispositivo(cmbAltoFalante, _sipConfig.CallOutputDevice, _sipConfig.CallOutputDeviceId, saida: true);

            SelecionarDispositivoToque();

            sldVolumeToque.Value = _sipConfig.RingVolume > 0 ? _sipConfig.RingVolume : 80;
            if (txtVolumeToqueVal != null)
                txtVolumeToqueVal.Text = ((int)sldVolumeToque.Value).ToString();
        }

        private void BtnTrocarToque_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Áudio (*.mp3;*.wav)|*.mp3;*.wav|Todos os arquivos (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
                txtToque.Text = dlg.FileName;
        }

        // v2.6.2 — reencontra o dispositivo salvo pelo ID, nome ou nome sem a numeração da porta
        // USB; se estiver desconectado, continua visível e selecionado (não vira "Automático").
        private void SelecionarDispositivoToque()
        {
            try
            {
                var (nome, id) = AudioDeviceService.ConfiguracaoToque(_sipConfig, _config);
                if (nome.Length == 0) { cmbDispToque.SelectedItem = cmbDispToque.Items[0]; return; }
                var saidas = cmbDispToque.Items.OfType<RingDeviceItem>().Where(r => !r.Automatico)
                    .Select(r => new AudioDeviceService.SaidaAudio(r.Id, r.Nome, true, -1, string.Empty, false)).ToList();
                var (esc, motivo) = AudioDeviceService.EscolherSaidaToque(nome, id, saidas);
                if (esc != null && motivo.StartsWith("configurado", StringComparison.Ordinal) && !motivo.StartsWith("configurado_ausente", StringComparison.Ordinal))
                {
                    cmbDispToque.SelectedItem = cmbDispToque.Items.OfType<RingDeviceItem>().First(r => r.Id == esc.Id);
                    return;
                }
                var desc = new RingDeviceItem(id, nome, desconectado: true);
                cmbDispToque.Items.Add(desc);
                cmbDispToque.SelectedItem = desc;
            }
            catch { cmbDispToque.SelectedItem = cmbDispToque.Items[0]; }
        }

        private System.Threading.CancellationTokenSource? _testeToqueCts;
        private const int TesteToqueSegundos = 5;

        private async void BtnTestarToque_Click(object sender, RoutedEventArgs e)
        {
            // Clicar de novo durante o teste = parar
            if (_testeToqueCts != null) { PararTesteToque(); return; }
            try
            {
                var dispSelecionado = cmbDispToque?.SelectedItem as RingDeviceItem;
                string deviceId, deviceNome;
                if (dispSelecionado == null || dispSelecionado.Automatico) { deviceId = string.Empty; deviceNome = string.Empty; }
                else { deviceId = dispSelecionado.Id; deviceNome = dispSelecionado.Nome; }
                var volume = sldVolumeToque != null ? (float)sldVolumeToque.Value / 100f : 0.8f;

                var path = string.IsNullOrWhiteSpace(txtToque?.Text)
                    ? "Assets\\toque_padrao.mp3"
                    : txtToque.Text.Trim();
                var fullPath = System.IO.Path.IsPathRooted(path)
                    ? path
                    : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);

                if (!System.IO.File.Exists(fullPath))
                {
                    MostrarResultadoTesteToque(false, "Arquivo de toque não encontrado:\n" + fullPath);
                    return;
                }

                _testRing ??= new RingtoneService();
                var svc = _testRing;
                var cts = new System.Threading.CancellationTokenSource();
                _testeToqueCts = cts;
                btnTestarToque.Content = "Parar";
                MostrarResultadoTesteToque(null, "Procurando a saída do toque…");

                // Enumerar e abrir o áudio fora da interface (driver USB lento não trava a tela)
                var r = await System.Threading.Tasks.Task.Run(() => svc.Tocar(fullPath, deviceId, deviceNome, "TestButton", loop: false, volume: volume));
                if (cts.IsCancellationRequested) return;
                if (!r.Tocando)
                {
                    FinalizarTesteToque(cts);
                    MostrarResultadoTesteToque(false, "O toque não saiu em nenhum dispositivo. " + (r.Erro ?? string.Empty));
                    return;
                }
                var aviso = r.Motivo switch
                {
                    var m when m.StartsWith("configurado_ausente") => $"O dispositivo escolhido não está conectado. Tocando em: {r.Dispositivo}",
                    "padrao_windows" => $"Tocando no padrão do Windows: {r.Dispositivo}",
                    _ => $"Tocando em: {r.Dispositivo}"
                };
                bool ok = !r.Motivo.StartsWith("configurado_ausente") && r.Motivo != "padrao_windows";
                // v2.6.2 — tocar no dispositivo certo não basta: mudo/volume 0 no Windows = sem som
                var silencio = r.Estado?.Aviso(volume);
                if (silencio != null)
                {
                    MostrarResultadoTesteToque(false, $"⚠ SEM SOM — {silencio} Corrija no ícone de som do Windows (ou no Mixer de volume) e teste de novo. ({aviso})");
                    txtTesteToqueResultado.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
                }
                else MostrarResultadoTesteToque(ok, aviso);

                try { await System.Threading.Tasks.Task.Delay(TesteToqueSegundos * 1000, cts.Token); } catch (OperationCanceledException) { return; }
                FinalizarTesteToque(cts);
                _ = System.Threading.Tasks.Task.Run(() => { try { svc.Parar(); } catch { } });
            }
            catch (Exception ex)
            {
                PararTesteToque();
                MostrarResultadoTesteToque(false, "Erro ao testar toque: " + ex.Message);
            }
        }

        private void FinalizarTesteToque(System.Threading.CancellationTokenSource cts)
        {
            if (!ReferenceEquals(_testeToqueCts, cts)) return;
            _testeToqueCts = null;
            try { cts.Dispose(); } catch { }
            try { btnTestarToque.Content = "Testar toque"; } catch { }
        }

        private void PararTesteToque()
        {
            var cts = _testeToqueCts;
            _testeToqueCts = null;
            try { cts?.Cancel(); cts?.Dispose(); } catch { }
            try { btnTestarToque.Content = "Testar toque"; } catch { }
            var svc = _testRing;
            if (svc != null) _ = System.Threading.Tasks.Task.Run(() => { try { svc.Parar(); } catch { } });
        }

        // ok: true = verde, false = vermelho/amarelo, null = neutro
        private void MostrarResultadoTesteToque(bool? ok, string texto)
        {
            try
            {
                txtTesteToqueResultado.Visibility = Visibility.Visible;
                txtTesteToqueResultado.Text = texto;
                txtTesteToqueResultado.Foreground = ok switch
                {
                    true => new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D)),
                    false => new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)),
                    _ => new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55))
                };
            }
            catch { }
        }


        private async void BtnEnviarTesteWhatsApp_Click(object sender, RoutedEventArgs e)
        {
            _whatsConfig.ApiUrl = txtWhatsApiUrl.Text?.Trim() ?? string.Empty;
            _whatsConfig.BearerToken = txtWhatsToken.Password?.Trim() ?? string.Empty;
            _whatsConfig.NumeroTeste = txtWhatsNumeroTeste.Text?.Trim() ?? string.Empty;
            _whatsConfig.MensagemTeste = txtWhatsMensagemTeste.Text?.Trim() ?? string.Empty;
            WhatsAppConfigService.Salvar(_whatsConfig);

            btnEnviarTesteWhatsApp.IsEnabled = false;
            txtWhatsResultado.Text = "Enviando teste...";
            try
            {
                var resultado = await WhatsAppService.EnviarMensagemAsync(_whatsConfig.NumeroTeste, _whatsConfig.MensagemTeste, "teste_configuracao");
                txtWhatsResultado.Text = $"HTTP {resultado.HttpStatusCode} | Número: {resultado.NumeroNormalizado} | Sucesso: {resultado.Sucesso}\n{resultado.RespostaBruta}\n{resultado.Debug}";
            }
            catch (System.Exception ex)
            {
                txtWhatsResultado.Text = ex.Message;
            }
            finally
            {
                btnEnviarTesteWhatsApp.IsEnabled = true;
            }
        }

        private void BtnSalvar_Click(object sender, RoutedEventArgs e)
        {
            // Áudio — salva nos campos novos do SipConfig
            var selMic     = cmbMicrofone.SelectedItem?.ToString() ?? string.Empty;
            var selSpeaker = cmbAltoFalante.SelectedItem?.ToString() ?? string.Empty;
            var dispToque  = cmbDispToque?.SelectedItem as RingDeviceItem;

            PararTesteMicrofone();
            (_sipConfig.AudioInputDevice, _sipConfig.AudioInputDeviceId) = LerDispositivoSelecionado(cmbMicrofone, _sipConfig.AudioInputDevice, _sipConfig.AudioInputDeviceId, saida: false);
            (_sipConfig.CallOutputDevice, _sipConfig.CallOutputDeviceId) = LerDispositivoSelecionado(cmbAltoFalante, _sipConfig.CallOutputDevice, _sipConfig.CallOutputDeviceId, saida: true);
            LogHelper.Sip($"CONFIG_AUDIO_SALVO | microfone=\"{_sipConfig.AudioInputDevice}\" id_mic={(_sipConfig.AudioInputDeviceId.Length > 0 ? "sim" : "nao")} audio=\"{_sipConfig.CallOutputDevice}\" (selecao mic=\"{selMic}\" audio=\"{selSpeaker}\")");
            // v2.6.2 — nome + ID; "Automático" grava vazio; desconectado mantém o configurado
            if (dispToque == null || dispToque.Automatico)
            {
                _sipConfig.RingOutputDevice = string.Empty;
                _sipConfig.RingOutputDeviceId = string.Empty;
            }
            else if (!dispToque.Desconectado)
            {
                _sipConfig.RingOutputDevice = dispToque.Nome;
                _sipConfig.RingOutputDeviceId = dispToque.Id;
            }
            else
            {
                var (nomeCfg, idCfg) = AudioDeviceService.ConfiguracaoToque(_sipConfig, _config);
                _sipConfig.RingOutputDevice = nomeCfg;
                _sipConfig.RingOutputDeviceId = idCfg;
            }
            LogHelper.Sip($"CONFIG_RING_SALVO | toque=\"{(_sipConfig.RingOutputDevice.Length == 0 ? "Automático" : _sipConfig.RingOutputDevice)}\" id={(_sipConfig.RingOutputDeviceId.Length > 0 ? "sim" : "nao")}");
            _sipConfig.RingVolume = sldVolumeToque != null ? (int)sldVolumeToque.Value : 80;

            // Preenche campos ainda vazios com auto-detecção
            AudioDeviceService.AplicarSelecaoAutomatica(_sipConfig);

            // Mantém compatibilidade com audio-config.json legado
            _config.Microfone        = string.IsNullOrEmpty(_sipConfig.AudioInputDevice) ? "Padrão do sistema" : _sipConfig.AudioInputDevice;
            _config.AltoFalante      = string.IsNullOrEmpty(_sipConfig.CallOutputDevice) ? "Padrão do sistema" : _sipConfig.CallOutputDevice;
            _config.DispositivoToqueNome = _sipConfig.RingOutputDevice;
            _config.DispositivoToqueId   = _sipConfig.RingOutputDeviceId;
            _config.Toque            = string.IsNullOrWhiteSpace(txtToque.Text?.Trim()) ? "Assets\\toque_padrao.mp3" : txtToque.Text.Trim();
            _config.TocarEmTelaCheia = chkInvadirTela.IsChecked == true;
            ConfiguracaoAudioService.Salvar(_config);

            // WhatsApp
            _whatsConfig.ApiUrl        = txtWhatsApiUrl.Text?.Trim() ?? string.Empty;
            _whatsConfig.BearerToken   = txtWhatsToken.Password?.Trim() ?? string.Empty;
            _whatsConfig.NumeroTeste   = txtWhatsNumeroTeste.Text?.Trim() ?? string.Empty;
            _whatsConfig.MensagemTeste = txtWhatsMensagemTeste.Text?.Trim() ?? string.Empty;
            WhatsAppConfigService.Salvar(_whatsConfig);

            // SIP — merge visible fields into existing config (never wipe hidden fields)
            var srv = txtSipServidor.Text.Trim();
            if (!string.IsNullOrWhiteSpace(srv)) _sipConfig.ServerIp = srv;
            if (int.TryParse(txtSipPorta.Text.Trim(), out var sipPorta) && sipPorta > 0) _sipConfig.Port = sipPorta;
            var dom = txtSipDominio.Text.Trim();
            if (!string.IsNullOrWhiteSpace(dom)) _sipConfig.Domain = dom;
            var amiHost = txtSipAmiHost.Text.Trim();
            if (!string.IsNullOrWhiteSpace(amiHost)) _sipConfig.AmiHost = amiHost;
            if (int.TryParse(txtSipAmiPorta.Text.Trim(), out var amiPorta) && amiPorta > 0) _sipConfig.AmiPorta = amiPorta;
            var amiUser = txtSipAmiUsuario.Text.Trim();
            if (!string.IsNullOrWhiteSpace(amiUser)) _sipConfig.AmiUsuario = amiUser;
            var amiPwd = txtSipAmiSenha.Password;
            if (!string.IsNullOrWhiteSpace(amiPwd)) _sipConfig.AmiSenha = amiPwd;
            _sipConfig.ProxySip = $"{_sipConfig.ServerIp}:{_sipConfig.Port}";

            // CDR
            _sipConfig.CdrAtivo  = chkCdrAtivo.IsChecked == true;
            var cdrHost = txtCdrHost.Text.Trim();
            if (!string.IsNullOrWhiteSpace(cdrHost)) _sipConfig.CdrHost = cdrHost;
            if (int.TryParse(txtCdrPorta.Text.Trim(), out var cdrPorta) && cdrPorta > 0) _sipConfig.CdrPorta = cdrPorta;
            var cdrBanco = txtCdrBanco.Text.Trim();
            if (!string.IsNullOrWhiteSpace(cdrBanco)) _sipConfig.CdrBanco = cdrBanco;
            var cdrTabela = txtCdrTabela.Text.Trim();
            if (!string.IsNullOrWhiteSpace(cdrTabela)) _sipConfig.CdrTabela = cdrTabela;
            var cdrUser = txtCdrUsuario.Text.Trim();
            if (!string.IsNullOrWhiteSpace(cdrUser)) _sipConfig.CdrUsuario = cdrUser;
            var cdrPwd = txtCdrSenha.Password;
            if (!string.IsNullOrWhiteSpace(cdrPwd)) _sipConfig.CdrSenha = cdrPwd;

            // Sync intervals
            if (TagDaCombo(cmbAmiSyncInterval)   is string amiTag   && int.TryParse(amiTag,   out var amiSec))    _sipConfig.AmiSyncIntervalSeconds       = amiSec;
            if (TagDaCombo(cmbCdrSyncInterval)   is string cdrTag   && int.TryParse(cdrTag,   out var cdrSec))    _sipConfig.HistoricoSyncIntervalSeconds  = cdrSec;
            if (TagDaCombo(cmbGoogleSyncInterval) is string gTag    && int.TryParse(gTag,     out var gSec))      _sipConfig.GoogleSyncIntervalSeconds     = gSec;
            if (TagDaCombo(cmbHistoricoModo)     is string modoTag && !string.IsNullOrWhiteSpace(modoTag))        _sipConfig.HistoricoModoExibicao         = modoTag;

            // Waven API
            _sipConfig.UsarWavenApi  = chkUsarWavenApi.IsChecked == true;
            var apiUrl = txtWavenApiUrl.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(apiUrl)) _sipConfig.WavenApiUrl = apiUrl;
            var apiToken = pwdWavenApiToken.Password?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(apiToken)) _sipConfig.WavenApiToken = apiToken;

            _sipConfig.AutoUpdateEnabled    = chkAutoUpdate.IsChecked    == true;
            _sipConfig.LogEnabled           = chkLogAtivo.IsChecked      == true;
            _sipConfig.LogDetailedEnabled   = chkLogDetalhado.IsChecked  == true;
            LogHelper.ConfigurarDeSettings(_sipConfig);
            _sipConfig.Salvar();

            ConfigurarInicializacaoWindows(chkIniciarWindows.IsChecked == true);
            MessageBox.Show("Configurações salvas.", "Waven VoIP");
            Close();
        }
        private static bool EstaConfiguradoParaIniciarComWindows()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("WavenVoIP") != null;
            }
            catch { return false; }
        }

        private void BtnTrocarUsuario_Click(object sender, RoutedEventArgs e)
        {
            // Delegate to DialerShellWindow so SIP/timer shutdown happens there
            if (Owner is DialerShellWindow shell)
            {
                Close();
                shell.TrocarUsuario();
                return;
            }

            // Fallback: no owner — do the clear here and relaunch
            var confirma = MessageBox.Show(
                "Trocar usuário?\n\nNome, Ramal, Login e Senha serão limpos.\nConfigurações da empresa serão mantidas.",
                "Waven VoIP — Trocar Usuário",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirma != MessageBoxResult.Yes) return;

            try
            {
                var config = SipConfig.CarregarSalva() ?? new SipConfig();
                config.NomeUsuario = string.Empty;
                config.RamalNome   = string.Empty;
                config.DisplayName = string.Empty;
                config.Ramal       = string.Empty;
                config.Login       = string.Empty;
                config.Senha       = string.Empty;
                config.Salvar();
            }
            catch { }

            var setup = new SetupWindow();
            System.Windows.Application.Current.MainWindow = setup;
            setup.Show();
            Close();
        }

        private void AtualizarLabelsApiMode()
        {
            var usandoApi = chkUsarWavenApi?.IsChecked == true &&
                            !string.IsNullOrWhiteSpace(txtWavenApiUrl?.Text);

            if (btnTestarAmi != null)
                btnTestarAmi.Content = usandoApi ? "Testar AMI via API" : "Testar AMI";

            if (btnTestarCdr != null)
                btnTestarCdr.Content = usandoApi ? "Testar CDR via API" : "Testar CDR / MySQL";

            if (usandoApi)
            {
                if (txtAmiResultado != null && string.IsNullOrWhiteSpace(txtAmiResultado.Text))
                    SetResultado(txtAmiResultado,
                        "Waven API ativa — AMI será acessado via https://api.almeidagas.com. As configurações diretas abaixo são usadas apenas como fallback.", null);

                if (txtCdrResultado != null && string.IsNullOrWhiteSpace(txtCdrResultado.Text))
                    SetResultado(txtCdrResultado,
                        "Waven API ativa — CDR será acessado via https://api.almeidagas.com. As configurações diretas abaixo são usadas apenas como fallback.", null);
            }
        }

        private async void BtnTestarAmi_Click(object sender, RoutedEventArgs e)
        {
            btnTestarAmi.IsEnabled = false;

            // Modo API: não conecta direto no AMI
            if (_sipConfig.UsarWavenApi && !string.IsNullOrWhiteSpace(_sipConfig.WavenApiUrl) && !string.IsNullOrWhiteSpace(_sipConfig.WavenApiToken))
            {
                SetResultado(txtAmiResultado, "Testando AMI via Waven API...", null);
                try
                {
                    var (ok, msg) = await WavenApiService.TestarAmiAsync();
                    SetResultado(txtAmiResultado, ok ? $"✔ {msg}" : $"✘ {msg}", ok);
                    LogHelper.Ami($"AMI_TEST_VIA_API ok={ok} msg={msg}");
                }
                catch (Exception ex) { SetResultado(txtAmiResultado, $"✘ {ex.Message}", false); }
                finally { btnTestarAmi.IsEnabled = true; }
                return;
            }

            // Modo direto: conecta via TCP
            SetResultado(txtAmiResultado, "Testando AMI...", null);
            var host  = txtSipAmiHost.Text.Trim();
            var porta = int.TryParse(txtSipAmiPorta.Text.Trim(), out var p) ? p : 5038;
            var user  = txtSipAmiUsuario.Text.Trim();
            var pwd   = txtSipAmiSenha.Password;
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, porta);
                var done = await Task.WhenAny(connectTask, Task.Delay(5000));
                if (!client.Connected) throw new TimeoutException("Conexão TCP expirou em 5 s.");
                await connectTask;
                using var stream = client.GetStream();
                stream.ReadTimeout = 3000;
                var buf = new byte[512];
                var n = await stream.ReadAsync(buf, 0, buf.Length);
                var banner = Encoding.ASCII.GetString(buf, 0, n);
                var login = $"Action: Login\r\nUsername: {user}\r\nSecret: {pwd}\r\nEvents: off\r\nActionID: WTEST\r\n\r\n";
                var lb = Encoding.ASCII.GetBytes(login);
                await stream.WriteAsync(lb, 0, lb.Length);
                n = 0;
                var sb = new StringBuilder();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    if (stream.DataAvailable)
                    {
                        n = await stream.ReadAsync(buf, 0, buf.Length);
                        sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                        if (sb.ToString().Contains("ActionID: WTEST")) break;
                    }
                    else await Task.Delay(80);
                }
                var resp = sb.ToString();
                if (resp.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetResultado(txtAmiResultado, "✔ AMI OK — login aceito", true);
                else
                    SetResultado(txtAmiResultado, $"✘ AMI recusou login: {resp.Replace("\r\n", " ")}", false);
                LogHelper.Ami($"AMI_TEST host={host}:{porta} resultado={resp.Substring(0, Math.Min(200, resp.Length))}");
            }
            catch (Exception ex)
            {
                SetResultado(txtAmiResultado, $"✘ {ex.Message}", false);
                LogHelper.Ami($"AMI_TEST_ERROR {ex.Message}", LogLevel.ERROR);
            }
            finally { btnTestarAmi.IsEnabled = true; }
        }

        private async void BtnTestarCdr_Click(object sender, RoutedEventArgs e)
        {
            btnTestarCdr.IsEnabled = false;

            // Modo API: não conecta direto no MySQL
            if (_sipConfig.UsarWavenApi && !string.IsNullOrWhiteSpace(_sipConfig.WavenApiUrl) && !string.IsNullOrWhiteSpace(_sipConfig.WavenApiToken))
            {
                SetResultado(txtCdrResultado, "Testando CDR via Waven API...", null);
                try
                {
                    var (ok, msg) = await WavenApiService.TestarCdrAsync();
                    SetResultado(txtCdrResultado, ok ? $"✔ {msg}" : $"✘ {msg}", ok);
                    LogHelper.Cdr($"CDR_TEST_VIA_API ok={ok} msg={msg}");
                }
                catch (Exception ex) { SetResultado(txtCdrResultado, $"✘ {ex.Message}", false); }
                finally { btnTestarCdr.IsEnabled = true; }
                return;
            }

            // Modo direto: conecta via MySQL
            SetResultado(txtCdrResultado, "Testando MySQL...", null);
            var cs = new MySqlConnectionStringBuilder
            {
                Server          = txtCdrHost.Text.Trim(),
                Port            = uint.TryParse(txtCdrPorta.Text.Trim(), out var pr) ? pr : 3306,
                Database        = txtCdrBanco.Text.Trim(),
                UserID          = txtCdrUsuario.Text.Trim(),
                Password        = txtCdrSenha.Password,
                ConnectionTimeout = 8,
                AllowZeroDateTime = true,
                ConvertZeroDateTime = true
            };
            try
            {
                await using var conn = new MySqlConnection(cs.ConnectionString);
                await conn.OpenAsync();
                var tabela = txtCdrTabela.Text.Trim();
                long count = 0;
                if (!string.IsNullOrWhiteSpace(tabela))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) FROM `{tabela}` LIMIT 1";
                    count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
                }
                SetResultado(txtCdrResultado, $"✔ MySQL OK — {count:N0} registros na tabela", true);
                LogHelper.Cdr($"CDR_TEST OK count={count}");
            }
            catch (Exception ex)
            {
                SetResultado(txtCdrResultado, $"✘ {ex.Message}", false);
                LogHelper.Cdr($"CDR_TEST_ERROR {ex.Message}", LogLevel.ERROR);
            }
            finally { btnTestarCdr.IsEnabled = true; }
        }

        private async void BtnTestarGoogle_Click(object sender, RoutedEventArgs e)
        {
            btnTestarGoogle.IsEnabled = false;
            SetResultado(txtGoogleResultado, "Verificando Google...", null);
            try
            {
                if (!GoogleContactsService.EstaConectado())
                {
                    SetResultado(txtGoogleResultado, "Não autenticado — abrirá o navegador para login.", null);
                    var contatos = await GoogleContactsService.SincronizarContatosAsync();
                    SetResultado(txtGoogleResultado, $"✔ Google OK — {contatos.Contatos.Count} contatos sincronizados", true);
                }
                else
                {
                    var cache = await GoogleContactsService.CarregarCacheAsync();
                    SetResultado(txtGoogleResultado, $"✔ Autenticado — {cache.Count} contatos em cache", true);
                }
                LogHelper.Info("GOOGLE_TEST OK");
            }
            catch (Exception ex)
            {
                SetResultado(txtGoogleResultado, $"✘ {ex.Message}", false);
                LogHelper.Error($"GOOGLE_TEST_ERROR {ex.Message}", ex);
            }
            finally { btnTestarGoogle.IsEnabled = true; }
        }

        private async void BtnTestarWhatsConexao_Click(object sender, RoutedEventArgs e)
        {
            btnTestarWhatsConexao.IsEnabled = false;
            SetResultado(txtWhatsConexaoResultado, "Testando API...", null);
            var url   = txtWhatsApiUrl.Text.Trim();
            var token = txtWhatsToken.Password.Trim();
            try
            {
                if (string.IsNullOrWhiteSpace(url)) throw new Exception("URL da API não informada.");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                if (!string.IsNullOrWhiteSpace(token))
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var resp = await http.GetAsync(url);
                var status = (int)resp.StatusCode;
                if (status < 500)
                    SetResultado(txtWhatsConexaoResultado, $"✔ API respondeu HTTP {status}", true);
                else
                    SetResultado(txtWhatsConexaoResultado, $"✘ API retornou HTTP {status}", false);
                LogHelper.Info($"WHATS_CONN_TEST HTTP {status}");
            }
            catch (Exception ex)
            {
                SetResultado(txtWhatsConexaoResultado, $"✘ {ex.Message}", false);
                LogHelper.Error($"WHATS_CONN_TEST_ERROR {ex.Message}", ex);
            }
            finally { btnTestarWhatsConexao.IsEnabled = true; }
        }

        private static void SetResultado(System.Windows.Controls.TextBlock tb, string msg, bool? sucesso)
        {
            tb.Text = msg;
            tb.Foreground = sucesso switch
            {
                true  => new SolidColorBrush(Color.FromRgb(21, 128, 61)),
                false => new SolidColorBrush(Color.FromRgb(185, 28, 28)),
                null  => new SolidColorBrush(Color.FromRgb(100, 116, 139))
            };
        }

        private static void SelecionarComboBoxPorTag(System.Windows.Controls.ComboBox cmb, string tag)
        {
            foreach (System.Windows.Controls.ComboBoxItem item in cmb.Items)
            {
                if (item.Tag?.ToString() == tag) { cmb.SelectedItem = item; return; }
            }
            if (cmb.Items.Count > 0) cmb.SelectedIndex = 0;
        }

        private static string? TagDaCombo(System.Windows.Controls.ComboBox cmb)
            => (cmb.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();

        private void BtnLimparFlagUpdate_Click(object sender, RoutedEventArgs e)
        {
            UpdateService.LimparFlagFalha();
            borderUpdateFailed.Visibility  = Visibility.Collapsed;
            btnLimparFlagUpdate.Visibility = Visibility.Collapsed;
            txtUpdateStatus.Text = "Falha de atualização limpa. Atualizações automáticas reativadas.";
            txtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
        }

        private async void BtnVerificarUpdate_Click(object sender, RoutedEventArgs e)
        {
            btnVerificarUpdate.IsEnabled = false;
            progressUpdate.Visibility = Visibility.Visible;

            var result = await UpdateService.VerificarManualAsync();

            progressUpdate.Visibility = Visibility.Collapsed;
            btnVerificarUpdate.IsEnabled = true;

            switch (result)
            {
                case UpdateCheckResult.EmDia:
                    MessageBox.Show($"Você já está na versão mais recente ({VersionService.Versao}).",
                        "Waven VoIP — Sem atualizações", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;

                case UpdateCheckResult.AtualizacaoIniciada when UpdateService.TemAtualizacaoPendente:
                    var versaoNova = UpdateService.VersaoPendente;
                    var confirmar  = MessageBox.Show(
                        $"Nova versão disponível: {versaoNova}\n\nDeseja instalar agora?\n\nO aplicativo será reiniciado durante a atualização.",
                        "Waven VoIP — Atualização Disponível",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);
                    if (confirmar == MessageBoxResult.Yes)
                    {
                        btnVerificarUpdate.IsEnabled = false;
                        await UpdateService.LancarPendenteAsync();
                    }
                    break;

                case UpdateCheckResult.JaVerificando:
                    MessageBox.Show("Uma verificação já está em andamento.",
                        "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;

                case UpdateCheckResult.Erro:
                    var erro = UpdateService.EstadoAtual.ErrorMessage ?? "Erro desconhecido";
                    MessageBox.Show($"Não foi possível verificar atualizações.\n\n{erro}",
                        "Waven VoIP — Erro de Atualização", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        }

        private void OnUpdateProgresso(UpdateProgress p)
        {
            Dispatcher.Invoke(() => AtualizarPainelUpdate(p));
        }

        private void AtualizarPainelUpdate(UpdateProgress p)
        {
            if (string.IsNullOrEmpty(p.Message))
            {
                txtUpdateStatus.Text = string.Empty;
            }
            else
            {
                txtUpdateStatus.Text = p.Message;
                txtUpdateStatus.Foreground = p.Stage switch
                {
                    UpdateStage.Error     => new SolidColorBrush(Color.FromRgb(185, 28, 28)),
                    UpdateStage.UpToDate  => new SolidColorBrush(Color.FromRgb(21, 128, 61)),
                    UpdateStage.UpdateAvailable => new SolidColorBrush(Color.FromRgb(14, 116, 144)),
                    _ => (System.Windows.Media.Brush)(FindResource("MutedBrush") ?? new SolidColorBrush(Color.FromRgb(100, 116, 139)))
                };
            }

            progressUpdate.IsIndeterminate = true;
            progressUpdate.Visibility      = p.Stage == UpdateStage.Checking ? Visibility.Visible : Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(p.RemoteVersion))
                txtUpdateVerRemota.Text = p.RemoteVersion;

            if (p.CheckedAt.HasValue)
                txtUpdateUltimaVer.Text = p.CheckedAt.Value.ToString("dd/MM/yyyy HH:mm:ss");
        }

        // ── Integration status section ──────────────────────────────────────────

        private void OnIntegrationStatusChanged(IntegracaoNome nome, IntegracaoStatus status)
            => Dispatcher.Invoke(AtualizarStatusIntegracoes);

        private void AtualizarStatusIntegracoes()
        {
            AtualizarDotLinha(IntegracaoNome.Google,   dotGoogle,   txtIntGoogle,
                "Google conectado", "Google desconectado", "Google: erro de autenticação");
            AtualizarDotLinha(IntegracaoNome.WhatsApp, dotWhatsApp, txtIntWhatsApp,
                "WhatsApp conectado", "WhatsApp desconectado", "WhatsApp: erro");
            AtualizarDotLinha(IntegracaoNome.Ami, dotAmi, txtIntAmi,
                "AMI conectado", "AMI desconectado", "AMI: erro de conexão");
            AtualizarDotLinha(IntegracaoNome.Cdr, dotCdr, txtIntCdr,
                "CDR conectado", "CDR desconectado", "CDR indisponível");

            var googleConectado = IntegrationStatusService.ObterStatus(IntegracaoNome.Google) == IntegracaoStatus.Conectado;
            btnIntConectarGoogle.Visibility    = googleConectado ? Visibility.Collapsed : Visibility.Visible;
            btnIntDesconectarGoogle.Visibility = googleConectado ? Visibility.Visible   : Visibility.Collapsed;
            btnIntDesconectarGoogle.IsEnabled  = googleConectado;
        }

        private static void AtualizarDotLinha(IntegracaoNome nome, Ellipse dot, System.Windows.Controls.TextBlock txt,
            string textoConectado, string textoDesconectado, string textoErro)
        {
            var status = IntegrationStatusService.ObterStatus(nome);
            Brush fill;
            string texto;
            switch (status)
            {
                case IntegracaoStatus.Conectado:
                    fill  = new SolidColorBrush(Color.FromRgb(34, 197, 94));   // green
                    texto = textoConectado;
                    break;
                case IntegracaoStatus.Reconectando:
                    fill  = new SolidColorBrush(Color.FromRgb(245, 158, 11));  // amber
                    texto = "Reconectando automaticamente…";
                    break;
                case IntegracaoStatus.Erro:
                    fill  = new SolidColorBrush(Color.FromRgb(239, 68, 68));   // red
                    texto = textoErro;
                    break;
                default:
                    fill  = new SolidColorBrush(Color.FromRgb(148, 163, 184)); // gray
                    texto = textoDesconectado;
                    break;
            }
            dot.Fill = fill;
            txt.Text = texto;
        }

        private async void BtnIntConectarGoogle_Click(object sender, RoutedEventArgs e)
        {
            btnIntConectarGoogle.IsEnabled = false;
            txtIntGoogle.Text = "Aguardando autenticação Google...";
            try
            {
                var resultado = await GoogleContactsService.SincronizarContatosAsync();
                IntegrationStatusService.Atualizar(IntegracaoNome.Google, IntegracaoStatus.Conectado);
                AtualizarStatusIntegracoes();
                txtIntGoogle.Text = $"Google conectado — {resultado.Total} contatos";
            }
            catch (Exception ex)
            {
                IntegrationStatusService.Atualizar(IntegracaoNome.Google, IntegracaoStatus.Erro);
                AtualizarStatusIntegracoes();
                txtIntGoogle.Text = $"Erro: {ex.Message}";
            }
            finally { btnIntConectarGoogle.IsEnabled = true; }
        }

        private async void BtnIntDesconectarGoogle_Click(object sender, RoutedEventArgs e)
        {
            btnIntDesconectarGoogle.IsEnabled = false;
            try
            {
                await GoogleContactsService.LimparTokenAsync();
                LogHelper.Info("[GOOGLE_DISCONNECTED] Usuário desconectou Google via configurações");
                IntegrationStatusService.Atualizar(IntegracaoNome.Google, IntegracaoStatus.Desconectado);
                AtualizarStatusIntegracoes();
            }
            finally { btnIntDesconectarGoogle.IsEnabled = true; }
        }

        private async void BtnIntTestarWhatsApp_Click(object sender, RoutedEventArgs e)
        {
            btnIntTestarWhatsApp.IsEnabled = false;
            txtIntWhatsApp.Text = "Testando WhatsApp...";
            var config = WhatsAppConfigService.Carregar();
            try
            {
                if (string.IsNullOrWhiteSpace(config.ApiUrl))
                {
                    IntegrationStatusService.Atualizar(IntegracaoNome.WhatsApp, IntegracaoStatus.Desconectado);
                    AtualizarStatusIntegracoes();
                    txtIntWhatsApp.Text = "WhatsApp: URL da API não configurada";
                    return;
                }
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                if (!string.IsNullOrWhiteSpace(config.BearerToken))
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.BearerToken);
                var resp   = await http.GetAsync(config.ApiUrl);
                var status = (int)resp.StatusCode;
                if (status < 500)
                {
                    IntegrationStatusService.Atualizar(IntegracaoNome.WhatsApp, IntegracaoStatus.Conectado);
                    AtualizarStatusIntegracoes();
                    txtIntWhatsApp.Text = $"WhatsApp conectado — HTTP {status}";
                }
                else
                {
                    IntegrationStatusService.Atualizar(IntegracaoNome.WhatsApp, IntegracaoStatus.Erro);
                    AtualizarStatusIntegracoes();
                    txtIntWhatsApp.Text = $"WhatsApp: erro HTTP {status}";
                }
                LogHelper.Info($"WHATS_INT_TEST HTTP {status}");
            }
            catch (Exception ex)
            {
                IntegrationStatusService.Atualizar(IntegracaoNome.WhatsApp, IntegracaoStatus.Erro);
                AtualizarStatusIntegracoes();
                txtIntWhatsApp.Text = $"WhatsApp: {ex.Message}";
                LogHelper.Error($"WHATS_INT_TEST_ERROR {ex.Message}", ex);
            }
            finally { btnIntTestarWhatsApp.IsEnabled = true; }
        }

        private async void BtnIntReconectarAmi_Click(object sender, RoutedEventArgs e)
        {
            if (!IntegrationStatusService.PermitirReconexao(IntegracaoNome.Ami))
            {
                MessageBox.Show("Reconexão AMI já iniciada recentemente. Aguarde 10 minutos antes de tentar novamente.", "Waven VoIP");
                return;
            }
            IntegrationStatusService.RegistrarTentativa(IntegracaoNome.Ami);
            btnIntReconectarAmi.IsEnabled = false;
            try
            {
                if (Owner is DialerShellWindow shell)
                    await shell.ReconectarAmiPublicoAsync();
                AtualizarStatusIntegracoes();
            }
            finally { btnIntReconectarAmi.IsEnabled = true; }
        }

        private async void BtnIntReconectarCdr_Click(object sender, RoutedEventArgs e)
        {
            if (!IntegrationStatusService.PermitirReconexao(IntegracaoNome.Cdr))
            {
                MessageBox.Show("Reconexão CDR já iniciada recentemente. Aguarde 10 minutos antes de tentar novamente.", "Waven VoIP");
                return;
            }
            IntegrationStatusService.RegistrarTentativa(IntegracaoNome.Cdr);
            btnIntReconectarCdr.IsEnabled = false;
            try
            {
                if (Owner is DialerShellWindow shell)
                    await shell.ReconectarCdrPublicoAsync();
                AtualizarStatusIntegracoes();
            }
            finally { btnIntReconectarCdr.IsEnabled = true; }
        }

        // ── Logs e Diagnóstico ──────────────────────────────────────────────────

        private void AtualizarStatusLog()
        {
            bool ativo = chkLogAtivo.IsChecked == true;
            ellLogStatus.Fill = ativo
                ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
                : new SolidColorBrush(Color.FromRgb(148, 163, 184));
            txtLogStatus.Text = ativo ? "Logs ativos" : "Logs desativados";
        }

        private void BtnAbrirPastaLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = LogHelper.LogDir;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                txtLogResultado.Text = $"Erro ao abrir pasta: {ex.Message}";
                txtLogResultado.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            }
        }

        private void BtnCopiarDiagnostico_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"=== WavenVoIP Diagnóstico — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                sb.AppendLine($"Versão: {VersionService.VersaoCompleta}");
                sb.AppendLine($"OS: {Environment.OSVersion}");
                sb.AppendLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
                sb.AppendLine($"Logs ativos: {_sipConfig.LogEnabled}  Detalhados: {_sipConfig.LogDetailedEnabled}");
                sb.AppendLine($"SIP: {_sipConfig.ServerIp}:{_sipConfig.Port} ({_sipConfig.Transport})");
                sb.AppendLine($"AMI: {_sipConfig.AmiHost}:{_sipConfig.AmiPorta}");
                sb.AppendLine($"CDR: {(_sipConfig.CdrAtivo ? _sipConfig.CdrHost : "desativado")}");
                sb.AppendLine();

                var logDir = LogHelper.LogDir;
                if (Directory.Exists(logDir))
                {
                    foreach (var f in Directory.GetFiles(logDir, "*.log"))
                    {
                        sb.AppendLine($"--- {System.IO.Path.GetFileName(f)} (últimas 30 linhas) ---");
                        try
                        {
                            var lines = File.ReadAllLines(f);
                            var tail = lines.Skip(Math.Max(0, lines.Length - 30));
                            foreach (var l in tail) sb.AppendLine(l);
                        }
                        catch { sb.AppendLine("[erro ao ler arquivo]"); }
                        sb.AppendLine();
                    }
                }

                Clipboard.SetText(sb.ToString());
                txtLogResultado.Text = "Diagnóstico copiado para a área de transferência.";
                txtLogResultado.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
            }
            catch (Exception ex)
            {
                txtLogResultado.Text = $"Erro: {ex.Message}";
                txtLogResultado.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            }
        }

        private void BtnLimparLogs_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Limpar todos os arquivos de log?\n\nEsta ação não pode ser desfeita.",
                "Waven VoIP — Limpar Logs",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                var logDir = LogHelper.LogDir;
                if (Directory.Exists(logDir))
                {
                    foreach (var f in Directory.GetFiles(logDir, "*.log"))
                        try { File.Delete(f); } catch { }
                }
                txtLogResultado.Text = "Logs limpos com sucesso.";
                txtLogResultado.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
            }
            catch (Exception ex)
            {
                txtLogResultado.Text = $"Erro ao limpar logs: {ex.Message}";
                txtLogResultado.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            }
        }

        private void BtnExportarDiagnostico_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Filter   = "ZIP (*.zip)|*.zip",
                    FileName = $"WavenVoIP_Diagnostico_{DateTime.Now:yyyyMMdd_HHmm}.zip",
                    Title    = "Exportar diagnóstico"
                };
                if (dlg.ShowDialog() != true) return;

                using var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create);

                // Summary file
                var sb = new StringBuilder();
                sb.AppendLine($"WavenVoIP Diagnóstico — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Versão: {VersionService.VersaoCompleta}");
                sb.AppendLine($"OS: {Environment.OSVersion}");
                sb.AppendLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
                sb.AppendLine($"ProcessorCount: {Environment.ProcessorCount}");
                sb.AppendLine($"Logs ativos: {_sipConfig.LogEnabled}  Detalhados: {_sipConfig.LogDetailedEnabled}");
                sb.AppendLine($"SIP: {_sipConfig.ServerIp}:{_sipConfig.Port} ({_sipConfig.Transport})");
                sb.AppendLine($"AMI: {_sipConfig.AmiHost}:{_sipConfig.AmiPorta}");
                sb.AppendLine($"CDR ativo: {_sipConfig.CdrAtivo}");
                sb.AppendLine($"AutoUpdate: {_sipConfig.AutoUpdateEnabled}");

                var entry = zip.CreateEntry("diagnostico.txt");
                using (var w = new StreamWriter(entry.Open()))
                    w.Write(sb.ToString());

                // Log files
                var logDir = LogHelper.LogDir;
                if (Directory.Exists(logDir))
                {
                    foreach (var f in Directory.GetFiles(logDir, "*.log"))
                    {
                        try { zip.CreateEntryFromFile(f, $"logs/{System.IO.Path.GetFileName(f)}"); } catch { }
                    }
                }

                txtLogResultado.Text = $"ZIP exportado: {dlg.FileName}";
                txtLogResultado.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                LogHelper.Info($"DIAG_EXPORTED path={dlg.FileName}");
            }
            catch (Exception ex)
            {
                txtLogResultado.Text = $"Erro ao exportar: {ex.Message}";
                txtLogResultado.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                LogHelper.Error("DIAG_EXPORT_ERROR", ex);
            }
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e) => Close();

        private static void ConfigurarInicializacaoWindows(bool ativo)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;

                if (ativo)
                {
                    // Environment.ProcessPath e mais confiavel que MainModule.FileName em .NET 6+
                    var exe = Environment.ProcessPath
                              ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exe))
                    {
                        // /autostart permite detectar e atrasar o registro SIP ate a rede estar pronta
                        key.SetValue("WavenVoIP", $"\"{exe}\" /autostart");
                        WavenVoIP.Services.LogHelper.Info($"AUTOSTART_REGISTERED | path={exe}");
                    }
                }
                else
                {
                    key.DeleteValue("WavenVoIP", false);
                    WavenVoIP.Services.LogHelper.Info("AUTOSTART_UNREGISTERED");
                }
            }
            catch (Exception ex)
            {
                WavenVoIP.App.LogCrash("AUTOSTART_CONFIG_FAILED", "ConfigurarInicializacaoWindows falhou", ex);
            }
        }

        // ── Waven API ─────────────────────────────────────────────────────────

        private async void BtnTestarWavenApi_Click(object sender, RoutedEventArgs e)
        {
            var url   = txtWavenApiUrl.Text?.Trim() ?? string.Empty;
            var token = pwdWavenApiToken.Password?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(url))
            {
                txtWavenApiStatus.Text = "Informe a URL da API.";
                return;
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                txtWavenApiStatus.Text = "Informe o token de acesso.";
                return;
            }

            // Salva temporariamente para o WavenApiService ler
            _sipConfig.WavenApiUrl   = url;
            _sipConfig.WavenApiToken = token;
            _sipConfig.Salvar();

            btnTestarWavenApi.IsEnabled = false;
            txtWavenApiStatus.Text = "Testando...";
            try
            {
                var (ok, versao, erro) = await WavenVoIP.Services.WavenApiService.TestarAsync().ConfigureAwait(true);
                txtWavenApiStatus.Text = ok
                    ? $"API online — versão {versao}"
                    : $"Falha: {erro}";
            }
            finally
            {
                btnTestarWavenApi.IsEnabled = true;
            }
        }

        private async void BtnTestarRamaisAoVivo_Click(object sender, RoutedEventArgs e)
        {
            var url   = txtWavenApiUrl.Text?.Trim() ?? string.Empty;
            var token = pwdWavenApiToken.Password?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(url))
            {
                txtRamaisAoVivoStatus.Text      = "Informe a URL da API.";
                txtRamaisAoVivoStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
                return;
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                txtRamaisAoVivoStatus.Text      = "Informe o token de acesso.";
                txtRamaisAoVivoStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
                return;
            }

            // Salva temporariamente para WavenApiService ler
            _sipConfig.WavenApiUrl   = url;
            _sipConfig.WavenApiToken = token;
            _sipConfig.Salvar();

            btnTestarRamaisAoVivo.IsEnabled = false;
            txtRamaisAoVivoStatus.Text      = "Testando endpoint de Ramais ao Vivo...";
            txtRamaisAoVivoStatus.Foreground = FindResource("MutedBrush") as System.Windows.Media.Brush
                                               ?? System.Windows.Media.Brushes.Gray;
            try
            {
                var (ok, msg) = await WavenVoIP.Services.WavenApiService.TestarRamaisAoVivoAsync().ConfigureAwait(true);
                txtRamaisAoVivoStatus.Text      = (ok ? "✔ " : "✘ ") + msg;
                txtRamaisAoVivoStatus.Foreground = ok
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 38, 38));
                LogHelper.Info($"RAMAIS_AO_VIVO_TEST ok={ok} msg={msg}");
            }
            finally
            {
                btnTestarRamaisAoVivo.IsEnabled = true;
            }
        }

        private async void BtnSincronizarAgora_Click(object sender, RoutedEventArgs e)
        {
            var cfg = SipConfig.CarregarSalva();
            if (cfg?.UsarWavenApi != true ||
                string.IsNullOrWhiteSpace(cfg.WavenApiToken))
            {
                txtWavenApiStatus.Text = "Ative e configure a API antes de sincronizar.";
                return;
            }

            btnSincronizarAgora.IsEnabled = false;
            txtWavenApiStatus.Text = "Sincronizando...";
            try
            {
                var svc = new WavenVoIP.Services.WavenApiSyncService(cfg.Ramal);
                await svc.SincronizarAsync().ConfigureAwait(true);
                svc.Dispose();
                txtWavenApiStatus.Text = "Sincronização concluída.";
            }
            catch (Exception ex)
            {
                txtWavenApiStatus.Text = $"Erro: {ex.Message}";
            }
            finally
            {
                btnSincronizarAgora.IsEnabled = true;
            }
        }

        // ═══ v2.6.1 — dispositivo configurado (inclusive desconectado) e teste do microfone ═══

        // Item da lista para um aparelho configurado que não está conectado agora: mantém a
        // escolha do operador (não vira "Automático" sem ele saber).
        private sealed class DispositivoDesconectadoItem
        {
            public string Nome { get; }
            public string Id { get; }
            public DispositivoDesconectadoItem(string nome, string id) { Nome = nome; Id = id; }
            public override string ToString() => $"⚠ {Nome} (desconectado)";
        }

        private static void SelecionarDispositivo(System.Windows.Controls.ComboBox cmb, string nome, string id, bool saida)
        {
            try
            {
                if (string.IsNullOrEmpty(nome) && string.IsNullOrEmpty(id)) { cmb.SelectedItem = cmb.Items[0]; return; }
                var (sit, esc, _) = MicrofoneService.Escolher(nome, id, MicrofoneService.ListarEndpoints(saida));
                if (esc != null && sit is SituacaoDispositivo.Ok or SituacaoDispositivo.Renomeado)
                {
                    var item = cmb.Items.Cast<object>().FirstOrDefault(i => i is string s && string.Equals(s, esc.Nome, StringComparison.OrdinalIgnoreCase));
                    if (item != null) { cmb.SelectedItem = item; return; }
                }
                var desc = new DispositivoDesconectadoItem(string.IsNullOrEmpty(nome) ? "(dispositivo salvo)" : nome, id);
                cmb.Items.Add(desc);
                cmb.SelectedItem = desc;
            }
            catch { cmb.SelectedItem = cmb.Items[0]; }
        }

        private static (string nome, string id) LerDispositivoSelecionado(System.Windows.Controls.ComboBox cmb, string nomeAtual, string idAtual, bool saida)
        {
            var sel = cmb.SelectedItem;
            if (sel is DispositivoDesconectadoItem d) return (nomeAtual, idAtual);          // mantém o configurado
            var nome = sel?.ToString() ?? string.Empty;
            if (nome.Length == 0 || nome == "Automático") return (string.Empty, string.Empty);
            try
            {
                var ep = MicrofoneService.ListarEndpoints(saida).FirstOrDefault(e => e.Ativo && string.Equals(e.Nome, nome, StringComparison.OrdinalIgnoreCase));
                return (nome, ep?.Id ?? string.Empty);
            }
            catch { return (nome, string.Empty); }
        }

        private NAudio.Wave.WaveInEvent? _testeMicWaveIn;
        private System.Windows.Threading.DispatcherTimer? _testeMicTimer;
        private const int TesteMicSegundos = 8;
        private readonly object _testeMicLock = new();

        private async void BtnTestarMicrofone_Click(object sender, RoutedEventArgs e)
        {
            if (_testeMicWaveIn != null) { PararTesteMicrofone(); return; }
            try
            {
                var (nomeSel, idSel) = LerDispositivoSelecionado(cmbMicrofone, _sipConfig.AudioInputDevice, _sipConfig.AudioInputDeviceId, saida: false);
                var (foneSel, idFone) = LerDispositivoSelecionado(cmbAltoFalante, _sipConfig.CallOutputDevice, _sipConfig.CallOutputDeviceId, saida: true);
                painelTesteMic.Visibility = Visibility.Visible;
                txtTesteMicResultado.Text = "Verificando o microfone no Windows…";
                txtTesteMicResultado.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
                txtTesteMicNivel.Text = string.Empty;
                btnTestarMicrofone.IsEnabled = false;

                var saude = await MicrofoneService.ComLimiteDeTempo<SaudeMicrofone>(() => MicrofoneService.VerificarMicrofone(nomeSel, idSel, foneSel, idFone));
                btnTestarMicrofone.IsEnabled = true;
                if (saude == null)
                {
                    txtTesteMicResultado.Text = "O Windows não respondeu a tempo. Tente de novo.";
                    return;
                }
                var res = saude.Resolucao;
                if (res.Escolhido == null)
                {
                    MostrarResultadoTesteMic(false, saude.TextoParaOperador());
                    return;
                }
                var idx = MicrofoneService.IndiceWave(res.Escolhido, saida: false);
                var avisosWindows = saude.Ok ? string.Empty : saude.TextoParaOperador();

                var maxDb = -120.0; double segVoz = 0, segTotal = 0, somaVoz = 0; long blocosVoz = 0; bool algumSinal = false;
                var nivelAtual = -120.0;
                var w = new NAudio.Wave.WaveInEvent { DeviceNumber = idx, WaveFormat = new NAudio.Wave.WaveFormat(8000, 16, 1), BufferMilliseconds = 50 };
                w.DataAvailable += (_, a) =>
                {
                    try
                    {
                        var n = a.BytesRecorded / 2;
                        if (n <= 0) return;
                        var pcm = new short[n];
                        Buffer.BlockCopy(a.Buffer, 0, pcm, 0, n * 2);
                        var (db, zero) = DetectorVozMicrofone.Medir(pcm);
                        Array.Clear(pcm);                                    // nada de áudio guardado
                        var seg = n / 8000.0;
                        lock (_testeMicLock)
                        {
                            segTotal += seg;
                            if (!zero) algumSinal = true;
                            if (db > maxDb) maxDb = db;
                            if (db > -45) { segVoz += seg; somaVoz += db; blocosVoz++; }
                            nivelAtual = db;
                        }
                    }
                    catch { }
                };
                _testeMicWaveIn = w;
                w.StartRecording();
                LogHelper.Sip($"MIC_TESTE_INICIO | microfone=\"{res.NomeEmUso}\" idx={idx} {saude.Resumo()}");

                var inicio = DateTime.Now;
                btnTestarMicrofone.Content = "Parar teste";
                _testeMicTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _testeMicTimer.Tick += (_, _) =>
                {
                    double nv, mx; lock (_testeMicLock) { nv = nivelAtual; mx = maxDb; }
                    var largura = Math.Max(0, ((FrameworkElement)barraTesteMic.Parent).ActualWidth);
                    barraTesteMic.Width = largura * Math.Clamp((nv + 60.0) / 50.0, 0.0, 1.0);
                    barraTesteMic.Background = nv > -50 ? Brushes.LimeGreen : Brushes.Silver;
                    var resta = TesteMicSegundos - (DateTime.Now - inicio).TotalSeconds;
                    txtTesteMicNivel.Text = $"Microfone: {res.NomeEmUso}   •   nível agora {(nv <= -119 ? "sem sinal" : nv.ToString("F0") + " dBFS")}   •   pico {(mx <= -119 ? "-" : mx.ToString("F0") + " dBFS")}   •   {Math.Max(0, resta):F0}s";
                    txtTesteMicResultado.Text = string.IsNullOrEmpty(avisosWindows) ? "Fale agora…" : avisosWindows;
                    if (resta <= 0)
                    {
                        PararTesteMicrofone();
                        double vz, tot, med, mxf; bool sinal; lock (_testeMicLock) { vz = segVoz; tot = segTotal; med = blocosVoz > 0 ? somaVoz / blocosVoz : -120; mxf = maxDb; sinal = algumSinal; }
                        string texto; bool ok;
                        if (tot < 1) { ok = false; texto = "O microfone não entregou áudio. Ele pode estar desconectado ou em uso exclusivo por outro programa."; }
                        else if (!sinal) { ok = false; texto = "Nenhum som chegou do microfone (sinal zerado). Verifique se o headset está conectado, se o microfone não está no mudo do Windows e se o Windows permite o acesso ao microfone."; }
                        else if (mxf < -50) { ok = false; texto = "Sua voz quase não chega (sinal muito baixo). Aproxime o microfone da boca ou aumente o volume do microfone no Windows."; }
                        else if (vz < 0.6) { ok = false; texto = "Pouca voz detectada. Fale durante o teste e repita."; }
                        else if (med < -38) { ok = true; texto = $"Funciona, mas a voz está baixa (média {med:F0} dBFS). Aproxime o microfone ou aumente o volume no Windows."; }
                        else { ok = true; texto = $"Microfone OK — sua voz está chegando bem (média {med:F0} dBFS)."; }
                        if (!string.IsNullOrEmpty(avisosWindows)) { ok = false; texto = avisosWindows + " " + texto; }
                        MostrarResultadoTesteMic(ok, texto);
                        LogHelper.Sip(FormattableString.Invariant($"MIC_TESTE_FIM | microfone=\"{res.NomeEmUso}\" ok={ok} voz_s={vz:F1} total_s={tot:F1} media_dbfs={med:F1} pico_dbfs={mxf:F1} sinal={sinal}"));
                    }
                };
                _testeMicTimer.Start();
            }
            catch (Exception ex)
            {
                PararTesteMicrofone();
                MostrarResultadoTesteMic(false, $"Não foi possível abrir o microfone: {ex.Message}");
            }
        }

        private void MostrarResultadoTesteMic(bool ok, string texto)
        {
            painelTesteMic.Visibility = Visibility.Visible;
            txtTesteMicResultado.Text = texto;
            txtTesteMicResultado.Foreground = new SolidColorBrush(ok ? Color.FromRgb(0x15, 0x80, 0x3D) : Color.FromRgb(0xB9, 0x1C, 0x1C));
        }

        private void PararTesteMicrofone()
        {
            try { _testeMicTimer?.Stop(); } catch { }
            _testeMicTimer = null;
            var w = _testeMicWaveIn;
            _testeMicWaveIn = null;
            if (w != null)
            {
                try { w.StopRecording(); } catch { }
                try { w.Dispose(); } catch { }
            }
            try { btnTestarMicrofone.Content = "Testar microfone"; btnTestarMicrofone.IsEnabled = true; } catch { }
        }
    }
}
