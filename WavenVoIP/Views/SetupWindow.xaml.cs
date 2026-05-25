using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class SetupWindow : Window
    {
        // null = standalone (first run); set = alterar mode
        private readonly SipService? _sipService;

        // Standalone — primeiro acesso
        public SetupWindow()
        {
            InitializeComponent();
            CarregarConfigNaTela();
            expanderAvancado.IsExpanded = false;
        }

        // Alterar — reabre com SipService existente para reinicializar
        public SetupWindow(SipService sipService)
        {
            _sipService = sipService;
            InitializeComponent();
            txtTitulo.Text    = "Alterar configuração SIP";
            txtSubtitulo.Text = "Edite os dados e salve para reconectar o ramal.";
            btnSalvar.Content = "Salvar e reconectar";
            expanderAvancado.IsExpanded = true;
            CarregarConfigNaTela();
        }

        private void CarregarConfigNaTela()
        {
            // Usa defaults da empresa quando não há config salva
            var cfg = SipConfig.CarregarSalva() ?? new SipConfig();
            cfg.AplicarPadroes();

            txtNomeOperador.Text = cfg.NomeUsuario;
            txtRamal.Text        = cfg.Ramal;
            txtLogin.Text        = cfg.Login;
            txtSenha.Password    = cfg.Senha;
            txtServidor.Text     = string.IsNullOrWhiteSpace(cfg.ServerIp) ? "191.252.202.208" : cfg.ServerIp;
            txtPorta.Text        = cfg.Port > 0 ? cfg.Port.ToString() : "5061";
            txtDominio.Text      = string.IsNullOrWhiteSpace(cfg.Domain)  ? cfg.ServerIp : cfg.Domain;
            txtAmiHost.Text      = string.IsNullOrWhiteSpace(cfg.AmiHost) ? cfg.ServerIp : cfg.AmiHost;
            txtAmiPorta.Text     = cfg.AmiPorta > 0 ? cfg.AmiPorta.ToString() : "5038";
            txtAmiUsuario.Text   = string.IsNullOrWhiteSpace(cfg.AmiUsuario) ? "waven" : cfg.AmiUsuario;
            txtAmiSenha.Password = string.IsNullOrWhiteSpace(cfg.AmiSenha)   ? "Waven@2025" : cfg.AmiSenha;
        }

        private void TxtServidor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (txtAmiHost != null && string.IsNullOrWhiteSpace(txtAmiHost.Text))
                txtAmiHost.Text = txtServidor.Text.Trim();
        }

        private SipConfig ValidarEConstruirConfig()
        {
            var nome  = txtNomeOperador.Text.Trim();
            var ramal = txtRamal.Text.Trim();
            var login = txtLogin.Text.Trim();
            var senha = txtSenha.Password;

            if (string.IsNullOrWhiteSpace(nome))  throw new InvalidOperationException("Informe o nome do operador.");
            if (string.IsNullOrWhiteSpace(ramal)) throw new InvalidOperationException("Informe o ramal.");
            if (string.IsNullOrWhiteSpace(login)) throw new InvalidOperationException("Informe o login SIP.");
            if (string.IsNullOrWhiteSpace(senha)) throw new InvalidOperationException("Informe a senha SIP.");

            // Load existing config so we NEVER wipe fields not shown in this form
            // (CDR, WhatsApp, recordings, audio, Google, preferences, etc.)
            var config = SipConfig.CarregarSalva() ?? new SipConfig();
            config.AplicarPadroes();

            MergeUserFieldsOnly(config, nome, ramal, login, senha);
            MergeAdvancedFields(config);

            return config;
        }

        private static void MergeUserFieldsOnly(SipConfig config, string nome, string ramal, string login, string senha)
        {
            config.NomeUsuario = nome;
            config.RamalNome   = nome;
            config.DisplayName = nome;
            config.Ramal       = ramal;
            config.Login       = login;
            config.Senha       = senha;
        }

        private void MergeAdvancedFields(SipConfig config)
        {
            var server  = txtServidor.Text.Trim();
            var dominio = txtDominio.Text.Trim();
            var amiHost = txtAmiHost.Text.Trim();
            var amiUser = txtAmiUsuario.Text.Trim();
            var amiPwd  = txtAmiSenha.Password;

            if (!int.TryParse(txtPorta.Text.Trim(),    out var porta)    || porta    <= 0) porta    = config.Port > 0    ? config.Port    : 5061;
            if (!int.TryParse(txtAmiPorta.Text.Trim(), out var amiPorta) || amiPorta <= 0) amiPorta = config.AmiPorta > 0 ? config.AmiPorta : 5038;

            if (!string.IsNullOrWhiteSpace(server))  config.ServerIp = server;
            config.Port = porta;
            config.Domain   = !string.IsNullOrWhiteSpace(dominio) ? dominio
                            : !string.IsNullOrWhiteSpace(config.Domain) ? config.Domain
                            : config.ServerIp;
            config.ProxySip = $"{config.ServerIp}:{config.Port}";
            config.AmiAtivo = true;
            if (!string.IsNullOrWhiteSpace(amiHost)) config.AmiHost    = amiHost;
            config.AmiPorta = amiPorta;
            if (!string.IsNullOrWhiteSpace(amiUser)) config.AmiUsuario = amiUser;
            if (!string.IsNullOrWhiteSpace(amiPwd))  config.AmiSenha   = amiPwd;
        }

        private void MostrarErro(string msg)
        {
            // Reset to error style before showing
            errorBox.Background  = new SolidColorBrush(Color.FromRgb(255, 241, 242));
            errorBox.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
            txtErro.Foreground   = new SolidColorBrush(Color.FromRgb(220, 38,   38));
            txtErro.Text         = msg;
            errorBox.Visibility  = Visibility.Visible;
        }

        private void LimparErro()
        {
            txtErro.Text        = string.Empty;
            errorBox.Visibility = Visibility.Collapsed;
        }

        private static readonly string FreshInstallFlagPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP", "fresh_install.flag");

        private void BtnSalvar_Click(object sender, RoutedEventArgs e)
        {
            LimparErro();
            try
            {
                var config = ValidarEConstruirConfig();
                config.Salvar();

                // Delete fresh_install.flag if this is a fresh installation completing setup
                if (File.Exists(FreshInstallFlagPath))
                {
                    try
                    {
                        File.Delete(FreshInstallFlagPath);
                        LogHelper.Info("FRESH_INSTALL_FLAG_CLEARED — instalacao nova concluida com sucesso");
                    }
                    catch { }
                }

                if (_sipService != null)
                {
                    try { _sipService.Inicializar(config); _sipService.Registrar(); } catch { }
                    DialogResult = true;
                    Close();
                }
                else
                {
                    var sip = new SipService();
                    try { sip.Inicializar(config); sip.Registrar(); } catch { }

                    var shell = new DialerShellWindow(sip);
                    Application.Current.MainWindow = shell;
                    shell.Show();
                    Close();
                }
            }
            catch (Exception ex)
            {
                MostrarErro(ex.Message);
            }
        }

        private async void BtnTestar_Click(object sender, RoutedEventArgs e)
        {
            LimparErro();

            // Usar default silenciosamente — nunca abrir o Expander por erro de campo vazio
            var servidor = txtServidor.Text.Trim();
            if (string.IsNullOrWhiteSpace(servidor))
            {
                servidor         = "191.252.202.208";
                txtServidor.Text = servidor;
            }

            if (!int.TryParse(txtPorta.Text.Trim(), out var porta) || porta <= 0)
            {
                porta         = 5061;
                txtPorta.Text = porta.ToString();
            }

            btnTestar.IsEnabled  = false;
            errorBox.Background  = new SolidColorBrush(Color.FromRgb(241, 245, 249));
            errorBox.BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240));
            txtErro.Foreground   = new SolidColorBrush(Color.FromRgb(71, 85, 105));
            txtErro.Text         = "Testando conexão…";
            errorBox.Visibility  = Visibility.Visible;

            try
            {
                using var tcp     = new TcpClient();
                var connectTask   = tcp.ConnectAsync(servidor, porta);
                bool respondeu    = await Task.WhenAny(connectTask, Task.Delay(5000)) == connectTask && tcp.Connected;

                if (respondeu)
                {
                    errorBox.Background  = new SolidColorBrush(Color.FromRgb(240, 253, 244));
                    errorBox.BorderBrush = new SolidColorBrush(Color.FromRgb(187, 247, 208));
                    txtErro.Foreground   = new SolidColorBrush(Color.FromRgb(22,  163,  74));
                    txtErro.Text         = $"Conexão OK — {servidor}:{porta} respondeu.";
                }
                else
                {
                    errorBox.Background  = new SolidColorBrush(Color.FromRgb(255, 241, 242));
                    errorBox.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                    txtErro.Foreground   = new SolidColorBrush(Color.FromRgb(220,  38,  38));
                    txtErro.Text         = $"Sem resposta de {servidor}:{porta} em 5 s. Verifique o servidor e a porta.";
                }
            }
            catch (Exception ex)
            {
                errorBox.Background  = new SolidColorBrush(Color.FromRgb(255, 241, 242));
                errorBox.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                txtErro.Foreground   = new SolidColorBrush(Color.FromRgb(220,  38,  38));
                txtErro.Text         = $"Falha: {ex.Message}";
            }
            finally
            {
                btnTestar.IsEnabled = true;
            }
        }
    }
}
