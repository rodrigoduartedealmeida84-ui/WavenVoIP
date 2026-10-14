using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WavenVoIP.Models;
using WavenVoIP.Services;
using WavenVoIP;

namespace WavenVoIP.Views
{
    // v2.5.0 — tela de conferência compacta: os ramais online aparecem direto (nome, número,
    // livre/ocupado, em tempo real); um clique seleciona e "Adicionar" confirma (ou dois cliques
    // no ramal). Contatos externos ficam atrás do botão "Buscar Contato". Os participantes
    // mostram, em tempo real, quem já está na sala.
    public partial class ConferenceControlWindow : Window
    {
        public sealed class ParticipantItem : INotifyPropertyChanged
        {
            private static readonly Brush _verde = Congelar(34, 197, 94), _vermelho = Congelar(239, 68, 68), _ambar = Congelar(245, 158, 11), _cinza = Congelar(148, 163, 184);
            private static Brush Congelar(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

            private string _status = string.Empty, _exibicao = string.Empty;
            public string Numero { get; set; } = string.Empty;       // identificador usado nos comandos (mutar/remover)
            public bool NaSala { get; set; }
            public bool JaEsteveNaSala { get; set; }
            public string Exibicao { get => string.IsNullOrEmpty(_exibicao) ? Numero : _exibicao; set { _exibicao = value; On(); } }
            public string Status { get => _status; set { if (_status != value) { _status = value; On(); On(nameof(BrushStatus)); } } }

            public Brush BrushStatus
            {
                get
                {
                    var s = (Status ?? string.Empty).ToLowerInvariant();
                    if (s.Contains("na conferência") || s.Contains("atendeu") || s.Contains("conectad") || s.Contains("em chamada")) return _verde;
                    if (s.Contains("saiu")) return _cinza;
                    if (s.Contains("ocup") || s.Contains("falha") || s.Contains("erro") || s.Contains("removend") || s.Contains("cancelad")) return _vermelho;
                    return _ambar;
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        public Func<string, string, Task<bool>>? OnAddParticipantAsync;
        public Action? OnRefreshRequested;
        public Action<string>? OnMuteRequested;
        public Action<string>? OnRemoveRequested;
        public Func<string, Task<bool>>? OnJoinCurrentCallAsync;
        public Action<bool>? OnMuteHostRequested;
        // Quem está na sala agora (ramais e números chamados) — consultado a cada 3 s com a janela aberta.
        public Func<Task<IReadOnlyList<string>>>? OnListarPresencaAsync;

        private static readonly Brush _verde = Congelar(21, 128, 61), _verdeF = Congelar(220, 252, 231);
        private static readonly Brush _ambar = Congelar(180, 83, 9), _ambarF = Congelar(254, 243, 199);
        private static readonly Brush _roxo = Congelar(124, 58, 237), _cinza = Congelar(107, 114, 128);
        private static Brush Congelar(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

        private readonly ObservableCollection<ParticipantItem> _participantes = new();
        private readonly ObservableCollection<EscolherDestinoWindow.RamalCard> _ramais = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly string _meuRamal;
        private List<Contato>? _contatos;
        private bool _hostMudo, _lendoPresenca, _ignorarTexto;
        private int _ticks;

        public ConferenceControlWindow(string salaPadrao)
        {
            InitializeComponent();
            txtSala.Text = string.IsNullOrWhiteSpace(salaPadrao) ? "800" : salaPadrao;
            txtSalaVisual.Text = txtSala.Text;
            lstParticipantes.ItemsSource = _participantes;
            listaRamais.ItemsSource = _ramais;
            _participantes.CollectionChanged += (_, _) => AtualizarContagem();

            string meu = string.Empty;
            try { var cfg = SipConfig.CarregarSalva(); meu = (string.IsNullOrWhiteSpace(cfg?.Ramal) ? cfg?.Login : cfg?.Ramal) ?? string.Empty; } catch { }
            _meuRamal = meu.Trim();

            var area = SystemParameters.WorkArea;
            if (Width > area.Width - 24) Width = Math.Max(MinWidth, area.Width - 24);
            if (Height > area.Height - 24) Height = Math.Max(MinHeight, area.Height - 24);

            _timer.Tick += async (_, _) =>
            {
                AtualizarRamais();
                if (_ticks % 3 == 0) await AtualizarPresencaAsync();
            };
            Loaded += (_, _) => { AtualizarRamais(); _timer.Start(); };
            Closed += (_, _) => _timer.Stop();
            AtualizarContagem();
        }

        public void AtualizarStatus(string mensagem)
        {
            if (txtStatus != null) txtStatus.Text = mensagem;
        }

        private void AtualizarContagem()
        {
            try
            {
                txtQtdParticipantes.Text = _participantes.Count.ToString();
                txtSemParticipantes.Visibility = _participantes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        // ── Ramais online (mesma fonte do Dashboard: monitor de ramais via Waven API/AMI) ──
        private void AtualizarRamais()
        {
            try
            {
                var svc = AmiMonitorService.Current;
                if (svc == null)
                {
                    txtSemRamais.Text = "Monitor de ramais indisponível. Digite o ramal abaixo.";
                    txtSemRamais.Visibility = Visibility.Visible;
                    return;
                }
                if (_ticks++ % 3 == 0) { try { svc.ForcePoll(); } catch { } }

                var online = svc.Ramais
                    .Where(r => EscolherDestinoWindow.EhOnline(r.Status) && !string.Equals(r.Ramal, _meuRamal, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r => r.Status == StatusRamal.Online ? 0 : 1).ThenBy(r => r.Ramal)
                    .ToList();

                foreach (var velho in _ramais.Where(c => online.All(o => o.Ramal != c.Ramal)).ToList())
                    _ramais.Remove(velho);

                for (int i = 0; i < online.Count; i++)
                {
                    var r = online[i];
                    var card = _ramais.FirstOrDefault(c => c.Ramal == r.Ramal);
                    if (card == null) { card = new EscolherDestinoWindow.RamalCard { Ramal = r.Ramal }; _ramais.Add(card); }
                    var (texto, livre) = EscolherDestinoWindow.Situacao(r.Status);
                    card.Nome = string.IsNullOrWhiteSpace(r.Nome) ? $"Ramal {r.Ramal}" : r.Nome;
                    card.StatusTexto = texto; card.Livre = livre;
                    card.Cor = livre ? _verde : _ambar; card.CorFundo = livre ? _verdeF : _ambarF;
                    var atual = _ramais.IndexOf(card);
                    if (atual != i && i < _ramais.Count) _ramais.Move(atual, i);
                }

                txtSemRamais.Text = "Nenhum outro ramal online no momento.";
                txtSemRamais.Visibility = _ramais.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                var livres = _ramais.Count(c => c.Livre);
                txtAoVivo.Text = svc.Conectado ? $"ao vivo · {livres} livre(s) de {_ramais.Count}" : "sem conexão com o monitor";
            }
            catch { }
        }

        private void Ramal_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not EscolherDestinoWindow.RamalCard card) return;
            _ignorarTexto = true; txtDestino.Text = card.Ramal; _ignorarTexto = false;
            foreach (var c in _ramais) c.Selecionado = c.Ramal == card.Ramal;
            txtStatus.Text = $"{card.Nome} ({card.Ramal}) selecionado — clique em Adicionar.";
            if (e.ClickCount >= 2) BtnAdicionar_Click(sender, e);
        }

        private void TxtDestino_TextChanged(object sender, TextChangedEventArgs e)
        {
            txtDicaDestino.Visibility = txtDestino.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_ignorarTexto) return;
            foreach (var c in _ramais) c.Selecionado = false;
        }

        private void TxtDestino_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; BtnAdicionar_Click(sender, e); }
        }

        // ── Presença em tempo real ───────────────────────────────────────────────
        private static string Digitos(string s) => new string((s ?? string.Empty).Where(char.IsDigit).ToArray());

        internal static bool MesmoParticipante(string numeroItem, string identificadorNaSala)
        {
            var a = Digitos(numeroItem); var b = Digitos(identificadorNaSala);
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b) return true;
            // número externo: a sala guarda o número com o prefixo de rota e o item pode estar sem ele
            return a.Length >= 8 && b.Length >= 8 && (a.EndsWith(b) || b.EndsWith(a));
        }

        private async Task AtualizarPresencaAsync()
        {
            if (_lendoPresenca || OnListarPresencaAsync == null || _participantes.Count == 0) return;
            _lendoPresenca = true;
            try
            {
                var naSala = await OnListarPresencaAsync();
                if (naSala == null) return;
                foreach (var p in _participantes)
                {
                    bool presente = naSala.Any(id => MesmoParticipante(p.Numero, id));
                    if (presente && !p.NaSala) { p.NaSala = true; p.JaEsteveNaSala = true; p.Status = "Na conferência"; }
                    else if (!presente && p.NaSala) { p.NaSala = false; p.Status = "Saiu da conferência"; }
                }
            }
            catch { }
            finally { _lendoPresenca = false; }
        }

        public void AdicionarOuAtualizarParticipante(string numero, string status)
        {
            var existing = _participantes.FirstOrDefault(p => MesmoParticipante(p.Numero, numero ?? string.Empty) || p.Numero == numero);
            if (existing != null) existing.Status = status;
            else _participantes.Add(new ParticipantItem { Numero = numero ?? string.Empty, Exibicao = NomeDe(numero ?? string.Empty), Status = status });
        }

        private string NomeDe(string numero)
        {
            try
            {
                var ramal = _ramais.FirstOrDefault(c => c.Ramal == numero);
                if (ramal != null) return $"{ramal.Nome} ({ramal.Ramal})";
                var numDisplay = PhoneNumberNormalizer.NormalizeForDisplay(DialPlanService.RemoverPrefixoDeRota(numero));
                if (string.IsNullOrWhiteSpace(numDisplay)) numDisplay = numero;
                var nome = ContatoStorageService.ResolverNomePorNumero(numero);
                return string.Equals(nome, numero, StringComparison.OrdinalIgnoreCase) ? numDisplay : $"{nome} ({numDisplay})";
            }
            catch { return numero; }
        }

        // ── Buscar contato externo ───────────────────────────────────────────────
        private async void BtnBuscarContato_Click(object sender, RoutedEventArgs e)
        {
            var abrir = painelContatos.Visibility != Visibility.Visible;
            painelContatos.Visibility = abrir ? Visibility.Visible : Visibility.Collapsed;
            if (!abrir) return;
            if (_contatos == null)
            {
                try { _contatos = await Task.Run(() => ContatoStorageService.Carregar()); } catch { _contatos = new List<Contato>(); }
            }
            FiltrarContatos();
            txtBuscaContato.Focus();
        }

        private void TxtBuscaContato_TextChanged(object sender, TextChangedEventArgs e)
        {
            txtDicaBusca.Visibility = txtBuscaContato.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            FiltrarContatos();
        }

        private void FiltrarContatos()
        {
            if (_contatos == null) return;
            var busca = txtBuscaContato.Text.Trim();
            IEnumerable<Contato> q = _contatos.Where(c => !string.IsNullOrWhiteSpace(c.Numero));
            if (busca.Length > 0)
                q = q.Where(c => (c.Nome ?? "").Contains(busca, StringComparison.OrdinalIgnoreCase) || (c.Numero ?? "").Contains(busca));
            lstContatos.ItemsSource = q.OrderBy(c => c.Nome).Take(60).ToList();
        }

        private void LstContatos_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstContatos.SelectedItem is not Contato c || string.IsNullOrWhiteSpace(c.Numero)) return;
            var numNorm = PhoneNumberNormalizer.NormalizeForDial(c.Numero);
            if (!string.Equals(numNorm, c.Numero, StringComparison.Ordinal))
                LogHelper.Write($"CONFERENCE_NUMBER_NORMALIZED origem=selecionar entrada={c.Numero} saida={numNorm}");
            txtDestino.Text = numNorm;
            txtStatus.Text = $"{c.Nome} selecionado — clique em Adicionar.";
        }

        private void LstContatos_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstContatos.SelectedItem is Contato) BtnAdicionar_Click(sender, e);
        }

        // ── Adicionar ────────────────────────────────────────────────────────────
        private async void BtnAdicionar_Click(object sender, RoutedEventArgs e)
        {
            if (btnAdicionar != null && !btnAdicionar.IsEnabled) return;
            var destino = txtDestino.Text?.Trim() ?? string.Empty;
            var sala = txtSala.Text?.Trim() ?? "800";
            if (string.IsNullOrWhiteSpace(destino))
            {
                txtStatus.Text = "Escolha um ramal ou digite um número para adicionar.";
                return;
            }

            if (btnAdicionar != null) btnAdicionar.IsEnabled = false;

            var numeroLimpo = PhoneNumberNormalizer.NormalizeBrazilPhone(destino);
            if (!string.Equals(numeroLimpo, destino, StringComparison.Ordinal))
                LogHelper.Write($"CONFERENCE_NUMBER_NORMALIZED origem=adicionar entrada={destino} saida={numeroLimpo}");
            var ehInterno = DialPlanService.EhRamalInterno(numeroLimpo);

            var item = new ParticipantItem
            {
                Numero = destino,
                Exibicao = NomeDe(destino),
                Status = ehInterno ? "Chamando ramal…" : "Escolhendo a rota…"
            };
            _participantes.Add(item);

            txtStatus.Text = ehInterno ? $"Chamando o ramal {destino}…" : $"Escolha a rota para {destino}…";

            txtDestino.Clear();
            foreach (var c in _ramais) c.Selecionado = false;
            painelContatos.Visibility = Visibility.Collapsed;

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
                if (!item.NaSala) item.Status = "Chamando…";
                txtStatus.Text = $"{item.Exibicao} foi chamado para a sala {sala}.";
            }
            else
            {
                item.Status = string.IsNullOrWhiteSpace(erro) ? "Cancelado ou não enviado" : "Erro: " + erro;
                txtStatus.Text = string.IsNullOrWhiteSpace(erro)
                    ? $"{item.Exibicao} não foi adicionado (seleção cancelada ou sala indisponível)."
                    : item.Status;
            }

            if (btnAdicionar != null) btnAdicionar.IsEnabled = true;
        }

        private async void BtnAtualizar_Click(object sender, RoutedEventArgs e)
        {
            try { OnRefreshRequested?.Invoke(); } catch { }
            try { AmiMonitorService.Current?.ForcePoll(); } catch { }
            await AtualizarPresencaAsync();
            txtStatus.Text = "Ramais e participantes atualizados.";
        }

        private void BtnMuteHost_Click(object sender, RoutedEventArgs e)
        {
            _hostMudo = !_hostMudo;
            btnMuteHost.Content = _hostMudo ? "" : "";
            btnMuteHost.Background = _hostMudo ? _cinza : _roxo;
            txtHostStatus.Text = _hostMudo ? "Silenciado" : "Ativo";
            try { OnMuteHostRequested?.Invoke(_hostMudo); } catch { }
        }

        private void BtnMutarSelecionado_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string numero) return;
            var item = _participantes.FirstOrDefault(p => p.Numero == numero);
            try { OnMuteRequested?.Invoke(numero); } catch { }
            if (item != null) txtStatus.Text = $"Pedido para silenciar {item.Exibicao} enviado.";
        }

        private void BtnRemoverSelecionado_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string numero) return;
            var item = _participantes.FirstOrDefault(p => p.Numero == numero);
            // quem nunca chegou a ser chamado/entrar sai da lista na hora
            if (item != null && !item.NaSala && (item.Status.StartsWith("Cancelado") || item.Status.StartsWith("Erro") || item.Status.StartsWith("Saiu")))
            {
                _participantes.Remove(item);
                return;
            }
            try { OnRemoveRequested?.Invoke(numero); } catch { }
            if (item != null) item.Status = "Removendo…";
        }

        public void MarcarParticipanteRemovido(string numero)
        {
            var item = _participantes.FirstOrDefault(p => p.Numero == numero);
            if (item != null) _participantes.Remove(item);
        }

        private void BtnFechar_Click(object sender, RoutedEventArgs e) => Close();
    }
}
