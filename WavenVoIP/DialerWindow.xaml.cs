using System;
using System.Windows;
using System.Windows.Controls;

namespace WavenVoIP
{
    public partial class DialerWindow : Window
    {
        private readonly SipService _sipService;

        public DialerWindow(SipService sipService)
        {
            InitializeComponent();
            _sipService = sipService ?? throw new ArgumentNullException(nameof(sipService));

            _sipService.StatusChanged += status =>
            {
                Dispatcher.Invoke(() =>
                {
                    txtCallStatus.Text = status;
                    this.Title = $"Waven VoIP - {status}";
                });
            };
        }

        private void Tecla_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button botao && botao.Content != null)
                txtNumero.Text += botao.Content.ToString();
        }

        private async void Ligar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var numeroDigitado = txtNumero.Text?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(numeroDigitado))
                    throw new InvalidOperationException("Digite um número.");

                if (!_sipService.IsRegistered)
                    throw new InvalidOperationException("O ramal ainda não está registrado no Issabel.");

                string numeroFinal;

                if (DialPlanService.EhRamalInterno(numeroDigitado))
                {
                    numeroFinal = DialPlanService.AplicarRegraDeDiscagem(numeroDigitado, SaidaChamada.Operadora);
                    txtCallStatus.Text = $"Chamada interna para ramal {numeroFinal}...";
                }
                else
                {
                    var seletor = new RouteSelectorWindow(numeroDigitado) { Owner = this };
                    var resultado = seletor.ShowDialog();
                    if (resultado != true || seletor.SaidaSelecionada == null)
                        return;

                    numeroFinal = DialPlanService.AplicarRegraDeDiscagem(numeroDigitado, seletor.SaidaSelecionada.Value);
                    txtCallStatus.Text = $"Chamando {numeroFinal}...";
                }

                bool ok = await _sipService.Ligar(numeroFinal);

                if (!ok)
                {
                    MessageBox.Show(
                        $"Não foi possível iniciar a chamada para {numeroFinal}.",
                        "Waven VoIP",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    txtCallStatus.Text = "Falha ao iniciar chamada";
                }
            }
            catch (Exception ex)
            {
                txtCallStatus.Text = "Erro ao iniciar chamada";
                MessageBox.Show(ex.Message, "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Apagar_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(txtNumero.Text))
                txtNumero.Text = txtNumero.Text.Substring(0, txtNumero.Text.Length - 1);
        }

        private void Desligar_Click(object sender, RoutedEventArgs e)
        {
            _sipService.Desligar();
            txtCallStatus.Text = "Chamada desligada";
        }
    }
}
