using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class HistoryWindow : Window
    {
        private readonly Func<string, Task>? _onCallRequested;
        private readonly List<HistoricoLigacaoItem> _itens;
        private readonly Action<string>? _onWhatsAppRequested;

        public HistoryWindow(Func<string, Task>? onCallRequested = null, Action<string>? onWhatsAppRequested = null)
        {
            InitializeComponent();
            _onCallRequested = onCallRequested;
            _onWhatsAppRequested = onWhatsAppRequested;
            _itens = HistoricoStorageService.Carregar();
            gridHistorico.ItemsSource = _itens;
        }

        private void TxtBusca_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (btnLimparBuscaHistorico != null)
                btnLimparBuscaHistorico.Visibility = !string.IsNullOrEmpty(txtBusca?.Text)
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;

            var busca = txtBusca.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(busca))
            {
                gridHistorico.ItemsSource = _itens;
                return;
            }

            gridHistorico.ItemsSource = _itens.Where(i =>
                (i.NomeExibido ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                (i.Nome ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                (i.Numero ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                (i.OrigemSaida ?? string.Empty).Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                i.TipoTexto.Contains(busca, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void BtnLimparBuscaHistorico_Click(object sender, RoutedEventArgs e)
        {
            if (txtBusca != null) txtBusca.Text = string.Empty;
            txtBusca?.Focus();
        }

        private void BtnLigarHistorico_Click(object sender, RoutedEventArgs e)
        {
            if (_onCallRequested == null) return;
            if (sender is FrameworkElement fe && fe.Tag is string numero && !string.IsNullOrWhiteSpace(numero))
                AgendarLigacaoAposFechar(numero);
        }

        private void BtnWhatsAppHistorico_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            string numero;
            string nome;
            if (fe.Tag is HistoricoLigacaoItem item)
            {
                numero = item.Numero;
                nome   = item.Nome ?? string.Empty;
            }
            else if (fe.Tag is string s)
            {
                numero = s;
                nome   = string.Empty;
            }
            else return;
            if (string.IsNullOrWhiteSpace(numero)) return;

            if (_onWhatsAppRequested != null)
            {
                Close();
                Application.Current.Dispatcher.BeginInvoke(new Action(() => _onWhatsAppRequested(numero)));
            }
            else
                new WhatsAppMessageWindow(numero, string.Empty, "historico", nome) { Owner = this }.ShowDialog();
        }

        private void GridHistorico_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_onCallRequested == null) return;
            if (gridHistorico.SelectedItem is HistoricoLigacaoItem item && !string.IsNullOrWhiteSpace(item.Numero))
                AgendarLigacaoAposFechar(item.Numero);
        }

        private void AgendarLigacaoAposFechar(string numero)
        {
            // Não fecha a janela antes do fluxo da chamada terminar de iniciar.
            // Fechar a janela durante o retorno do seletor de saída pode cancelar o encadeamento
            // de eventos do WPF em alguns computadores. Escondemos, iniciamos a ligação e só então fechamos.
            try { Hide(); } catch { }

            Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (_onCallRequested != null)
                        await _onCallRequested(numero);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Não foi possível iniciar a chamada.\n" + ex.Message, "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    try { Close(); } catch { }
                }
            }), DispatcherPriority.ApplicationIdle);
        }
    }
}
