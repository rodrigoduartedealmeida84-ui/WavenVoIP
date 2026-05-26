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
            cmbDispToque.Items.Add(new RingDeviceItem("", "Automático"));

            try
            {
                using var enumerator = new MMDeviceEnumerator();

                foreach (var mic in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    cmbMicrofone.Items.Add(mic.FriendlyName);

                foreach (var speaker in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    cmbAltoFalante.Items.Add(speaker.FriendlyName);
                    cmbDispToque.Items.Add(new RingDeviceItem(speaker.ID, speaker.FriendlyName));
                }
            }
            catch { }

            // Selecionar a partir do SipConfig (novos campos)
            cmbMicrofone.SelectedItem = string.IsNullOrEmpty(_sipConfig.AudioInputDevice)
                ? cmbMicrofone.Items[0]
                : cmbMicrofone.Items.Cast<object>().FirstOrDefault(i => i?.ToString() == _sipConfig.AudioInputDevice)
                  ?? cmbMicrofone.Items[0];

            cmbAltoFalante.SelectedItem = string.IsNullOrEmpty(_sipConfig.CallOutputDevice)
                ? cmbAltoFalante.Items[0]
                : cmbAltoFalante.Items.Cast<object>().FirstOrDefault(i => i?.ToString() == _sipConfig.CallOutputDevice)
                  ?? cmbAltoFalante.Items[0];

            cmbDispToque.SelectedItem = string.IsNullOrEmpty(_sipConfig.RingOutputDevice)
                ? cmbDispToque.Items[0]
                : cmbDispToque.Items.Cast<object>().FirstOrDefault(i => i is RingDeviceItem r && r.Nome == _sipConfig.RingOutputDevice)
                  ?? cmbDispToque.Items[0];

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

        private void BtnTestarToque_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _testRing?.Parar();
                _testRing = new RingtoneService();

                var dispSelecionado = cmbDispToque?.SelectedItem as RingDeviceItem;
                var deviceId   = dispSelecionado?.Id   ?? string.Empty;
                var deviceNome = dispSelecionado?.Nome ?? string.Empty;
                var volume = sldVolumeToque != null ? (float)sldVolumeToque.Value / 100f : 0.8f;

                var path = string.IsNullOrWhiteSpace(txtToque?.Text)
                    ? "Assets\\toque_padrao.mp3"
                    : txtToque.Text.Trim();
                var fullPath = System.IO.Path.IsPathRooted(path)
                    ? path
                    : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);

                if (!System.IO.File.Exists(fullPath))
                {
                    MessageBox.Show("Arquivo de toque não encontrado:\n" + fullPath, "Waven VoIP");
                    return;
                }

                var svc = _testRing;
                svc.Tocar(fullPath, deviceId, deviceNome, "TestButton", loop: false, volume: volume);
                System.Threading.Tasks.Task.Delay(4000).ContinueWith(_ => Dispatcher.Invoke(() => svc.Parar()));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao testar toque: " + ex.Message, "Waven VoIP");
            }
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

            _sipConfig.AudioInputDevice = selMic == "Automático" ? string.Empty : selMic;
            _sipConfig.CallOutputDevice = selSpeaker == "Automático" ? string.Empty : selSpeaker;
            _sipConfig.RingOutputDevice = (dispToque == null || string.IsNullOrEmpty(dispToque.Nome) || dispToque.Nome == "Automático")
                ? string.Empty : dispToque.Nome;
            _sipConfig.RingVolume = sldVolumeToque != null ? (int)sldVolumeToque.Value : 80;

            // Preenche campos ainda vazios com auto-detecção
            AudioDeviceService.AplicarSelecaoAutomatica(_sipConfig);

            // Mantém compatibilidade com audio-config.json legado
            _config.Microfone        = string.IsNullOrEmpty(_sipConfig.AudioInputDevice) ? "Padrão do sistema" : _sipConfig.AudioInputDevice;
            _config.AltoFalante      = string.IsNullOrEmpty(_sipConfig.CallOutputDevice) ? "Padrão do sistema" : _sipConfig.CallOutputDevice;
            _config.DispositivoToqueNome = _sipConfig.RingOutputDevice;
            _config.DispositivoToqueId   = dispToque?.Id ?? string.Empty;
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

        private async void BtnTestarAmi_Click(object sender, RoutedEventArgs e)
        {
            btnTestarAmi.IsEnabled = false;
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
                    var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exe))
                        key.SetValue("WavenVoIP", "\"" + exe + "\"");
                }
                else
                {
                    key.DeleteValue("WavenVoIP", false);
                }
            }
            catch { }
        }
    }
}
