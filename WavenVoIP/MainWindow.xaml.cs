using System;
using System.Windows;
using WavenVoIP.Services;
using WavenVoIP.Views;

namespace WavenVoIP
{
    public partial class MainWindow : Window
    {
        private readonly SipService _sipService = new SipService();

        public MainWindow()
        {
            InitializeComponent();
            Title = VersionService.VersaoCompleta;
            CarregarConfigSalvaNaTela();

            _sipService.StatusChanged += status =>
            {
                Dispatcher.Invoke(() => { txtStatus.Text = status; });
            };
        }


        private void CarregarConfigSalvaNaTela()
        {
            var config = SipConfig.CarregarSalva();
            if (config == null) return;
            txtRamal.Text = config.Ramal;
            txtLogin.Text = config.Login;
            txtSenha.Password = config.Senha;
            txtDomain.Text = config.Domain;
            txtProxySip.Text = config.ProxySip;
            txtServerIp.Text = config.ServerIp;
            txtPort.Text = config.Port.ToString();
            txtDisplayName.Text = string.IsNullOrWhiteSpace(config.DisplayName) ? config.Ramal : config.DisplayName;
            if (txtNomeUsuario != null)
                txtNomeUsuario.Text = config.NomeUsuario ?? string.Empty;
        }

        private void Entrar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ramal = txtRamal.Text?.Trim() ?? string.Empty;
                var login = txtLogin.Text?.Trim() ?? string.Empty;
                var senha = txtSenha.Password ?? string.Empty;

                if (string.IsNullOrWhiteSpace(ramal))
                    throw new InvalidOperationException("Informe o ramal.");

                if (string.IsNullOrWhiteSpace(login))
                    throw new InvalidOperationException("Informe o Login/Auth User.");

                if (string.IsNullOrWhiteSpace(senha))
                    throw new InvalidOperationException("Informe a senha.");

                var nomeUsuario = txtNomeUsuario?.Text?.Trim() ?? string.Empty;

                var config = new SipConfig
                {
                    Ramal       = ramal,
                    Login       = login,
                    Senha       = senha,
                    Domain      = txtDomain.Text?.Trim() ?? string.Empty,
                    ProxySip    = txtProxySip.Text?.Trim() ?? string.Empty,
                    ServerIp    = txtServerIp.Text?.Trim() ?? string.Empty,
                    Port        = int.TryParse(txtPort.Text, out var porta) ? porta : 5061,
                    DisplayName = txtDisplayName.Text?.Trim() ?? ramal,
                    NomeUsuario = nomeUsuario,
                    // Keep RamalNome in sync so existing settings screens work
                    RamalNome   = nomeUsuario
                };

                config.Salvar();

                _sipService.Inicializar(config);
                _sipService.Registrar();

                var shell = new DialerShellWindow(_sipService) { Owner = this };
                shell.Closed += (_, __) =>
                {
                    this.Show();
                    txtStatus.Text = "Não conectado";
                };

                this.Hide();
                shell.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
