using System.Windows;

namespace WavenVoIP
{
    public partial class RouteSelectorWindow : Window
    {
        public SaidaChamada? SaidaSelecionada { get; private set; }
        public bool Confirmado { get; private set; }

        public RouteSelectorWindow(string numeroDigitado)
        {
            InitializeComponent();
            var numeroTratado = DialPlanService.RemoverDuplicacaoSequencial(numeroDigitado);
            txtNumeroOriginal.Text = $"Número digitado: {numeroTratado}";
        }

        private void Operadora_Click(object sender, RoutedEventArgs e)
        {
            Selecionar(SaidaChamada.Operadora);
        }

        private void WhatsAppTim_Click(object sender, RoutedEventArgs e)
        {
            Selecionar(SaidaChamada.WhatsAppTim);
        }

        private void WhatsAppVivo_Click(object sender, RoutedEventArgs e)
        {
            Selecionar(SaidaChamada.WhatsAppVivo);
        }

        private void Cancelar_Click(object sender, RoutedEventArgs e)
        {
            Confirmado = false;
            FecharJanela();
        }

        private void Selecionar(SaidaChamada saida)
        {
            SaidaSelecionada = saida;
            Confirmado = true;
            FecharJanela();
        }

        private void FecharJanela()
        {
            // Quando a janela foi aberta com ShowDialog(), o correto é devolver DialogResult.
            // Se ela foi aberta com Show(), o WPF pode lançar exceção ao setar DialogResult;
            // nesse caso usamos Close() normal e o chamador lê Confirmado/SaidaSelecionada.
            try
            {
                DialogResult = Confirmado;
            }
            catch
            {
                try { Close(); } catch { }
            }
        }
    }
}
