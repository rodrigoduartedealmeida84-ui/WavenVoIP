using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class SettingsTab : UserControl
    {
        private ConfiguracaoAudio _config;
        private WhatsAppConfig _whatsAppConfig;

        public SettingsTab()
        {
            InitializeComponent();
            _config = ConfiguracaoAudioService.Carregar();
            _whatsAppConfig = WhatsAppConfigService.Carregar();

            txtToque.Text = _config.Toque;
            chkInvadirTela.IsChecked = _config.TocarEmTelaCheia;

            CarregarWhatsAppNaTela();
        }

        private void CarregarWhatsAppNaTela()
        {
            txtWhatsAppApiUrl.Text = _whatsAppConfig.ApiUrl;
            txtWhatsAppBearerToken.Password = _whatsAppConfig.BearerToken;
            txtWhatsAppNumeroTeste.Text = _whatsAppConfig.NumeroTeste;
            txtWhatsAppMensagemTeste.Text = string.IsNullOrWhiteSpace(_whatsAppConfig.MensagemTeste)
                ? "Teste de envio pelo Waven VoIP."
                : _whatsAppConfig.MensagemTeste;
        }

        private void SalvarWhatsAppDaTela()
        {
            _whatsAppConfig.ApiUrl = txtWhatsAppApiUrl.Text?.Trim() ?? string.Empty;
            _whatsAppConfig.BearerToken = txtWhatsAppBearerToken.Password?.Trim() ?? string.Empty;
            _whatsAppConfig.NumeroTeste = txtWhatsAppNumeroTeste.Text?.Trim() ?? string.Empty;
            _whatsAppConfig.MensagemTeste = txtWhatsAppMensagemTeste.Text ?? string.Empty;
            WhatsAppConfigService.Salvar(_whatsAppConfig);
        }

        private void BtnTrocarToque_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Arquivos WAV (*.wav)|*.wav|Todos os arquivos (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
                txtToque.Text = dlg.FileName;
        }

        private void BtnSalvar_Click(object sender, RoutedEventArgs e)
        {
            _config.Toque = txtToque.Text?.Trim() ?? "toque-padrao.wav";
            _config.TocarEmTelaCheia = chkInvadirTela.IsChecked == true;
            ConfiguracaoAudioService.Salvar(_config);

            SalvarWhatsAppDaTela();

            MessageBox.Show("Configurações salvas.", "Waven VoIP");
        }

        private async void BtnWhatsAppTeste_Click(object sender, RoutedEventArgs e)
        {
            SalvarWhatsAppDaTela();

            if (string.IsNullOrWhiteSpace(_whatsAppConfig.ApiUrl) || string.IsNullOrWhiteSpace(_whatsAppConfig.BearerToken))
            {
                MessageBox.Show("Informe a URL da API e o Bearer Token antes de enviar o teste.", "Waven VoIP");
                return;
            }

            if (string.IsNullOrWhiteSpace(_whatsAppConfig.NumeroTeste))
            {
                MessageBox.Show("Informe o número de teste.", "Waven VoIP");
                return;
            }

            btnWhatsAppTeste.IsEnabled = false;
            txtWhatsAppStatus.Text = "Enviando teste...";

            try
            {
                var resultado = await WhatsAppService.EnviarMensagemAsync(
                    _whatsAppConfig.NumeroTeste,
                    _whatsAppConfig.MensagemTeste,
                    "teste_configuracao");

                txtWhatsAppStatus.Text = resultado.Sucesso
                    ? $"Teste enviado. HTTP {resultado.HttpStatusCode}"
                    : $"Falha no teste. HTTP {resultado.HttpStatusCode}";

                MessageBox.Show(
                    $"HTTP: {resultado.HttpStatusCode}\nNúmero: {resultado.NumeroNormalizado}\n\nResposta:\n{resultado.RespostaBruta}\n\nDebug:\n{resultado.Debug}",
                    resultado.Sucesso ? "WhatsApp enviado" : "Falha no WhatsApp");
            }
            finally
            {
                btnWhatsAppTeste.IsEnabled = true;
            }
        }
    }
}
