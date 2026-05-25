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
    public partial class ContactsWindow : Window
    {
        private List<Contato> _contatos = new();
        private readonly Func<string, Task>? _onCallRequested;
        private readonly Func<string, Task>? _onAddToCallRequested;
        private readonly bool _modoChamada;
        private readonly Action<string>? _onWhatsAppRequested;

        public ContactsWindow(Func<string, Task>? onCallRequested = null, Func<string, Task>? onAddToCallRequested = null, bool modoChamada = false, Action<string>? onWhatsAppRequested = null)
        {
            InitializeComponent();
            _onCallRequested = onCallRequested;
            _onAddToCallRequested = onAddToCallRequested;
            _modoChamada = modoChamada;
            _onWhatsAppRequested = onWhatsAppRequested;
            if (_modoChamada)
            {
                txtTitulo.Text = "Contatos da chamada";
                txtSubtitulo.Text = "Escolha um contato para ligar ou adicionar na chamada/conferência.";
            }
            Carregar();
        }

        private void Carregar()
        {
            var todos = ContatoStorageService.Carregar();

            // Deduplicate by normalized phone: local > AMI > Google
            var vistos = new Dictionary<string, Contato>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in todos)
            {
                var key = PhoneNumberNormalizer.NormalizeForSearch(c.Numero);
                if (string.IsNullOrWhiteSpace(key))
                    key = $"_nome_{(c.Nome ?? string.Empty).ToLowerInvariant()}";

                if (!vistos.TryGetValue(key, out var existente) || PrioridadeContato(c) > PrioridadeContato(existente))
                    vistos[key] = c;
            }

            _contatos = vistos.Values.OrderBy(c => c.Nome).ToList();
            gridContatos.ItemsSource = null;
            gridContatos.ItemsSource = _contatos;
        }

        private static int PrioridadeContato(Contato c)
        {
            if (!c.FonteGoogle && !c.EhRamalIssabel) return 2; // local
            if (c.EhRamalIssabel) return 1;                    // AMI ramal
            return 0;                                           // Google
        }

        private void BtnSalvar_Click(object sender, RoutedEventArgs e)
        {
            var nome = txtNome.Text?.Trim() ?? string.Empty;
            var numero = txtNumero.Text?.Trim() ?? string.Empty;
            var obs = txtObs.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(numero))
            {
                MessageBox.Show("Preencha nome e número.", "Waven VoIP");
                return;
            }

            _contatos.Add(new Contato { Nome = nome, Numero = numero, Observacao = obs });
            ContatoStorageService.Salvar(_contatos);
            Carregar();
            txtNome.Clear(); txtNumero.Clear(); txtObs.Clear();
        }

        private void BtnLigarContato_Click(object sender, RoutedEventArgs e)
        {
            if (_onCallRequested == null) return;
            if (sender is FrameworkElement fe && fe.Tag is string numero && !string.IsNullOrWhiteSpace(numero))
                AgendarLigacaoAposFechar(numero);
        }

        private async void BtnAdicionarContato_Click(object sender, RoutedEventArgs e)
        {
            if (_onAddToCallRequested == null)
            {
                MessageBox.Show("Abra os contatos pela janela da chamada para adicionar na conferência.", "Waven VoIP");
                return;
            }
            if (sender is FrameworkElement fe && fe.Tag is string numero && !string.IsNullOrWhiteSpace(numero))
            {
                await _onAddToCallRequested(numero);
                Close();
            }
        }

        private void BtnWhatsAppContato_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            if (fe.Tag is not string numero || string.IsNullOrWhiteSpace(numero)) return;
            var nome = _contatos.FirstOrDefault(c => c.Numero == numero)?.Nome ?? string.Empty;

            if (_onWhatsAppRequested != null)
            {
                Close();
                Application.Current.Dispatcher.BeginInvoke(new Action(() => _onWhatsAppRequested(numero)));
            }
            else
                new WhatsAppMessageWindow(numero, string.Empty, "contato", nome) { Owner = this }.ShowDialog();
        }

        private void BtnExcluirContato_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is Contato contato)
            {
                if (MessageBox.Show($"Excluir {contato.Nome}?", "Waven VoIP", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    _contatos.Remove(contato);
                    ContatoStorageService.Salvar(_contatos);
                    Carregar();
                }
            }
        }

        private void GridContatos_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_onCallRequested == null) return;
            if (gridContatos.SelectedItem is Contato c && !string.IsNullOrWhiteSpace(c.Numero))
                AgendarLigacaoAposFechar(c.Numero);
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
