using System;
using System.Windows;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class IntegrationDisconnectedOverlay : Window
    {
        private readonly IntegracaoNome _integracao;
        private readonly Action? _onReconectar;

        public IntegrationDisconnectedOverlay(
            IntegracaoNome integracao,
            string nomeVisual,
            string mensagem,
            Window owner,
            Action? onReconectar = null)
        {
            InitializeComponent();
            Owner        = owner;
            _integracao  = integracao;
            _onReconectar = onReconectar;

            txtNomeIntegracao.Text = nomeVisual;
            txtMensagem.Text       = mensagem;

            // Show anti-loop warning if reconnect is blocked
            if (!IntegrationStatusService.PermitirReconexao(integracao))
            {
                borderLoopWarning.Visibility = Visibility.Visible;
                btnReconectar.IsEnabled      = false;
            }
        }

        private void BtnReconectar_Click(object sender, RoutedEventArgs e)
        {
            if (!IntegrationStatusService.PermitirReconexao(_integracao))
            {
                borderLoopWarning.Visibility = Visibility.Visible;
                btnReconectar.IsEnabled      = false;
                return;
            }
            IntegrationStatusService.RegistrarTentativa(_integracao);
            _onReconectar?.Invoke();
            Close();
        }

        private void BtnAgoraNao_Click(object sender, RoutedEventArgs e)
        {
            IntegrationStatusService.MarcarModalDismissed(_integracao);
            Close();
        }
    }
}
