using System.Windows.Controls;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class HistoryTab : UserControl
    {
        public HistoryTab()
        {
            InitializeComponent();
            gridHistorico.ItemsSource = HistoricoStorageService.Carregar();
        }
    }
}
