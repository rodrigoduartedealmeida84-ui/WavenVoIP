using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WavenVoIP.Models;
using WavenVoIP.Services;
using WavenVoIP;

namespace WavenVoIP.Views
{
    public partial class ConferenceControlWindow : Window
    {
        public sealed class ParticipantItem
        {
            public string Numero { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;

            public string Iniciais
            {
                get
                {
                    var n = Numero.Trim();
                    if (n.Length == 0) return "?";
                    var c = n[0];
                    return char.IsLetter(c) ? char.ToUpper(c).ToString() : "#";
                }
            }

            public System.Windows.Media.Brush BrushAvatar =>
                new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(99, 102, 241));

            public System.Windows.Media.Brush BrushStatus
            {
                get
                {
                    var s = (Status ?? string.Empty).ToLowerInvariant();
                    if (s.Contains("atend") || s.Contains("conectad") || s.Contains("em chamada"))
                        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
                    if (s.Contains("ocup") || s.Contains("falha") || s.Contains("erro") || s.Contains("removend"))
                        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                    return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
                }
            }
        }

        public Func<string, string, Task<bool>>? OnAddParticipantAsync;
        public Action? OnRefreshRequested;
        public Action<string>? OnMuteRequested;
        public Action<string>? OnRemoveRequested;
        public Func<string, Task<bool>>? OnJoinCurrentCallAsync;
        public Action<bool>? OnMuteHostRequested;

        private readonly ObservableCollection<ParticipantItem> _participantes = new();
        private System.Collections.Generic.List<Contato> _contatos = new();
        private bool _hostMudo;

        // Mute mic icon paths
        private const string MicOnPath = "M12,2A3,3 0 0,1 15,5V11A3,3 0 0,1 12,14A3,3 0 0,1 9,11V5A3,3 0 0,1 12,2M19,11C19,14.53 16.39,17.44 13,17.93V21H11V17.93C7.61,17.44 5,14.53 5,11H7A5,5 0 0,0 12,16A5,5 0 0,0 17,11H19Z";
        private const string MicOffPath = "M19,11H17.3C17.3,14 14.76,16.1 12,16.1C9.24,16.1 6.7,14 6.7,11H5C5,14.41 7.72,17.23 11,17.72V21H13V17.72C16.28,17.23 19,14.41 19,11M4.27,3L3,4.27L9,10.27V11A3,3 0 0,0 12,14A3,3 0 0,0 12.28,13.97L14.5,16.2C13.74,16.61 12.9,16.9 12,16.9C9.24,16.9 6.7,14.8 6.7,12H5C5,15.41 7.72,18.23 11,18.72V22H13V18.72C14.22,18.54 15.35,18.06 16.3,17.37L19.73,20.8L21,19.53L4.27,3M15,11A3,3 0 0,0 12,8A3,3 0 0,0 11.37,8.06L15,11.7V11Z";

        public ConferenceControlWindow(string salaPadrao)
        {
            InitializeComponent();
            txtSala.Text = string.IsNullOrWhiteSpace(salaPadrao) ? "800" : salaPadrao;
            txtSalaVisual.Text = txtSala.Text;
            lstParticipantes.ItemsSource = _participantes;
            CarregarContatos();
            txtDestino.Focus();
        }

        public void AtualizarStatus(string mensagem)
        {
            if (txtStatus != null) txtStatus.Text = mensagem;
        }

        public void AdicionarOuAtualizarParticipante(string numero, string status)
        {
            var numDisplay = PhoneNumberNormalizer.NormalizeForDisplay(
                DialPlanService.RemoverPrefixoDeRota(numero ?? string.Empty));
            if (string.IsNullOrWhiteSpace(numDisplay)) numDisplay = numero ?? string.Empty;

            var nomeContato = ContatoStorageService.ResolverNomePorNumero(numero ?? string.Empty);
            var exibicao = string.Equals(nomeContato, numero, StringComparison.OrdinalIgnoreCase)
                ? numDisplay
                : $"{nomeContato} ({numDisplay})";

            var existing = _participantes.FirstOrDefault(p =>
                p.Numero == exibicao || p.Numero == numero || p.Numero == numDisplay);
            if (existing != null)
                existing.Status = status;
            else
                _participantes.Add(new ParticipantItem { Numero = exibicao, Status = status });
            lstParticipantes.Items.Refresh();
        }

        private void CarregarContatos()
        {
            try { _contatos = ContatoStorageService.Carregar(); } catch { _contatos = new(); }
            AtualizarListaContatos();
        }

        private void AtualizarListaContatos()
        {
            var busca = txtBuscaContato?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            var lista = string.IsNullOrWhiteSpace(busca)
                ? _contatos
                : _contatos.Where(c => (c.Nome ?? "").ToLowerInvariant().Contains(busca) || (c.Numero ?? "").Contains(busca)).ToList();
            lstContatos.ItemsSource = lista;
        }

        private void TxtBuscaContato_TextChanged(object sender, TextChangedEventArgs e) => AtualizarListaContatos();

        private void SelecionarContato_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string numero)
            {
                var numNorm = PhoneNumberNormalizer.NormalizeForDial(numero);
                if (!string.Equals(numNorm, numero, StringComparison.Ordinal))
                    LogHelper.Write($"CONFERENCE_NUMBER_NORMALIZED origem=selecionar entrada={numero} saida={numNorm}");
                txtDestino.Text = numNorm;
                txtDestino.Focus();
                txtDestino.CaretIndex = txtDestino.Text.Length;
            }
        }

        private void LstContatos_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstContatos.SelectedItem is Contato c && !string.IsNullOrWhiteSpace(c.Numero))
            {
                var numNorm = PhoneNumberNormalizer.NormalizeForDial(c.Numero);
                if (!string.Equals(numNorm, c.Numero, StringComparison.Ordinal))
                    LogHelper.Write($"CONFERENCE_NUMBER_NORMALIZED origem=double_click entrada={c.Numero} saida={numNorm}");
                txtDestino.Text = numNorm;
                txtDestino.Focus();
                txtDestino.CaretIndex = txtDestino.Text.Length;
            }
        }

        private async void BtnAdicionar_Click(object sender, RoutedEventArgs e)
        {
            var destino = txtDestino.Text?.Trim() ?? string.Empty;
            var sala = txtSala.Text?.Trim() ?? "800";
            if (string.IsNullOrWhiteSpace(destino))
            {
                txtStatus.Text = "Digite ou selecione um ramal/número para adicionar.";
                return;
            }

            if (btnAdicionar != null) btnAdicionar.IsEnabled = false;

            var numeroLimpo = PhoneNumberNormalizer.NormalizeBrazilPhone(destino);
            if (!string.Equals(numeroLimpo, destino, StringComparison.Ordinal))
                LogHelper.Write($"CONFERENCE_NUMBER_NORMALIZED origem=adicionar entrada={destino} saida={numeroLimpo}");
            var ehInterno = DialPlanService.EhRamalInterno(numeroLimpo);

            var item = new ParticipantItem
            {
                Numero = destino!,
                Status = ehInterno ? "Discando ramal interno..." : "Abrindo seletor de rota..."
            };
            _participantes.Add(item);
            lstParticipantes.Items.Refresh();

            txtStatus.Text = ehInterno
                ? $"Discando ramal {destino}..."
                : $"Abrindo seletor de rota para {destino}...";

            txtDestino.Clear();

            bool ok = false;
            string erro = string.Empty;

            try
            {
                if (OnAddParticipantAsync != null)
                    ok = await OnAddParticipantAsync(numeroLimpo, sala);
            }
            catch (Exception ex)
            {
                erro = ex.Message;
                ok = false;
            }

            if (ok)
            {
                item.Status = "Chamada enviada — aguardando participante atender.";
                txtStatus.Text = $"Participante {destino} → sala {sala}. Aguardando atender.";
            }
            else
            {
                item.Status = string.IsNullOrWhiteSpace(erro)
                    ? "Cancelado ou não enviado."
                    : "Erro: " + erro;
                txtStatus.Text = string.IsNullOrWhiteSpace(erro)
                    ? $"Participante {destino} não foi enviado (seleção cancelada ou falha AMI)."
                    : item.Status;
            }

            lstParticipantes.Items.Refresh();
            if (btnAdicionar != null) btnAdicionar.IsEnabled = true;
        }

        private void BtnAtualizar_Click(object sender, RoutedEventArgs e)
        {
            try { OnRefreshRequested?.Invoke(); } catch { }
            txtStatus.Text = "Atualização solicitada.";
        }

        private void BtnMuteHost_Click(object sender, RoutedEventArgs e)
        {
            _hostMudo = !_hostMudo;
            try
            {
                var icon = btnMuteHost?.Template?.FindName("pathMuteHostIcon", btnMuteHost) as System.Windows.Shapes.Path;
                if (icon != null)
                    icon.Data = System.Windows.Media.Geometry.Parse(_hostMudo ? MicOffPath : MicOnPath);
            }
            catch { }
            if (btnMuteHost != null)
                btnMuteHost.Background = _hostMudo
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 114, 128))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(99, 102, 241));
            if (txtHostStatus != null)
                txtHostStatus.Text = _hostMudo ? "Microfone MUDO" : "Microfone ativo";
            try { OnMuteHostRequested?.Invoke(_hostMudo); } catch { }
        }

        private void BtnSairConferencia_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnMutarSelecionado_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string numero) return;
            var item = _participantes.FirstOrDefault(p => p.Numero == numero);
            try { OnMuteRequested?.Invoke(numero); } catch { }
            if (item != null)
            {
                item.Status = "Comando mute enviado.";
                lstParticipantes.Items.Refresh();
            }
        }

        private void BtnRemoverSelecionado_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string numero) return;
            var item = _participantes.FirstOrDefault(p => p.Numero == numero);
            try { OnRemoveRequested?.Invoke(numero); } catch { }
            if (item != null)
            {
                item.Status = "Removendo...";
                lstParticipantes.Items.Refresh();
            }
        }

        public void MarcarParticipanteRemovido(string numero)
        {
            var item = _participantes.FirstOrDefault(p => p.Numero == numero);
            if (item != null)
            {
                _participantes.Remove(item);
                lstParticipantes.Items.Refresh();
            }
        }

        private void BtnFechar_Click(object sender, RoutedEventArgs e) => Close();
    }
}
