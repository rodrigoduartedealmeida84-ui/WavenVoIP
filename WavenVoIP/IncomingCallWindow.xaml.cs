using System.Windows;

namespace WavenVoIP
{
    public partial class IncomingCallWindow : Window
    {
        public bool Aceitar { get; private set; }
        public bool Aceita => Aceitar;

        public IncomingCallWindow(string caller)
        {
            InitializeComponent();
            txtCaller.Text = $"Origem: {caller}";
        }

        private void Atender_Click(object sender, RoutedEventArgs e)
        {
            Aceitar = true;
            DialogResult = true;
        }

        private void Recusar_Click(object sender, RoutedEventArgs e)
        {
            Aceitar = false;
            DialogResult = false;
        }
    }
}
