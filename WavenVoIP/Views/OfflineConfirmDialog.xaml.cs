using System.Windows;

namespace WavenVoIP.Views
{
    public partial class OfflineConfirmDialog : Window
    {
        public bool Confirmado { get; private set; }

        public OfflineConfirmDialog(bool emChamada = false)
        {
            InitializeComponent();
            MouseLeftButtonDown += (_, e) => { try { DragMove(); } catch { } };
            if (emChamada)
                bannerChamada.Visibility = Visibility.Visible;
        }

        private void BtnFicarOffline_Click(object sender, RoutedEventArgs e)
        {
            Confirmado   = true;
            DialogResult = true;
            Close();
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            Confirmado   = false;
            DialogResult = false;
            Close();
        }
    }
}
