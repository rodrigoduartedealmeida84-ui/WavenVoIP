using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    // v2.5.0 — escolha de destino para Transferência e Conferência em até 2 cliques:
    // os ramais internos online aparecem sozinhos (número, nome, livre/ocupado, atualizados em
    // tempo real pelo monitor de ramais — Waven API/AMI); clicar no ramal e em "Confirmar"
    // (ou dois cliques no ramal) conclui. "Buscar Contato" abre a busca nos demais contatos e
    // o campo aceita qualquer ramal ou número digitado.
    public partial class EscolherDestinoWindow : Window
    {
        public sealed class RamalCard : INotifyPropertyChanged
        {
            private string _nome = "", _statusTexto = "";
            private Brush _cor = Brushes.Gray, _corFundo = Brushes.Transparent;
            private bool _selecionado;
            public string Ramal { get; init; } = "";
            public bool Livre { get; set; }
            public string Nome        { get => _nome;        set { if (_nome != value)        { _nome = value;        On(); } } }
            public string StatusTexto { get => _statusTexto; set { if (_statusTexto != value) { _statusTexto = value; On(); } } }
            public Brush Cor          { get => _cor;         set { if (!Equals(_cor, value))      { _cor = value;      On(); } } }
            public Brush CorFundo     { get => _corFundo;    set { if (!Equals(_corFundo, value)) { _corFundo = value; On(); } } }
            public bool Selecionado   { get => _selecionado; set { if (_selecionado != value) { _selecionado = value; On(); } } }
            public event PropertyChangedEventHandler? PropertyChanged;
            private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        private static readonly Brush _verde = Congelar(21, 128, 61), _verdeF = Congelar(220, 252, 231);
        private static readonly Brush _ambar = Congelar(180, 83, 9), _ambarF = Congelar(254, 243, 199);
        private static Brush Congelar(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

        private readonly ObservableCollection<RamalCard> _ramais = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly string _meuRamal;
        private List<Contato>? _contatos;
        private int _ticks;

        public string ValorDigitado { get; private set; } = string.Empty;
        public bool Confirmado { get; private set; }
        // true quando o destino confirmado é um ramal escolhido na lista (seleção + confirmação já feitas aqui)
        public bool EscolheuRamalDaLista { get; private set; }

        public EscolherDestinoWindow(string titulo, string subtitulo, string meuRamal, string textoConfirmar = "Confirmar", string glifo = "")
        {
            InitializeComponent();
            txtTitulo.Text = titulo; Title = titulo;
            txtSubtitulo.Text = subtitulo;
            txtIcone.Text = glifo;
            btnConfirmar.Content = textoConfirmar;
            _meuRamal = (meuRamal ?? string.Empty).Trim();
            listaRamais.ItemsSource = _ramais;

            var area = SystemParameters.WorkArea;
            if (Width > area.Width - 24) Width = Math.Max(MinWidth, area.Width - 24);
            if (Height > area.Height - 24) Height = Math.Max(MinHeight, area.Height - 24);

            _timer.Tick += (_, _) => AtualizarRamais();
            Loaded += (_, _) => { AtualizarRamais(); _timer.Start(); };
            Closed += (_, _) => _timer.Stop();
        }

        // Abre sem bloquear o restante do sistema; conclui quando a janela fecha.
        public System.Threading.Tasks.Task<bool> MostrarAsync()
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            Closed += (_, _) => tcs.TrySetResult(Confirmado && !string.IsNullOrWhiteSpace(ValorDigitado));
            Show();
            Activate();
            return tcs.Task;
        }

        // Ramal interno "online" = registrado e utilizável: livre (Online) ou ocupado (em ligação,
        // chamando, tocando). Offline/indisponível/desconhecido e o próprio ramal ficam de fora.
        internal static bool EhOnline(StatusRamal s) =>
            s == StatusRamal.Online || s == StatusRamal.EmLigacao || s == StatusRamal.Chamando || s == StatusRamal.Tocando;

        internal static (string texto, bool livre) Situacao(StatusRamal s) => s switch
        {
            StatusRamal.Online => ("Livre", true),
            StatusRamal.Tocando => ("Tocando", false),
            StatusRamal.Chamando => ("Chamando", false),
            _ => ("Ocupado", false)
        };

        private void AtualizarRamais()
        {
            try
            {
                var svc = AmiMonitorService.Current;
                if (svc == null) { txtSemRamais.Text = "Monitor de ramais indisponível. Digite o ramal abaixo."; txtSemRamais.Visibility = Visibility.Visible; return; }
                if (_ticks++ % 3 == 0) { try { svc.ForcePoll(); } catch { } }

                var online = svc.Ramais
                    .Where(r => EhOnline(r.Status) && !string.Equals(r.Ramal, _meuRamal, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r => r.Status == StatusRamal.Online ? 0 : 1).ThenBy(r => r.Ramal)
                    .ToList();

                foreach (var velho in _ramais.Where(c => online.All(o => o.Ramal != c.Ramal)).ToList())
                    _ramais.Remove(velho);

                for (int i = 0; i < online.Count; i++)
                {
                    var r = online[i];
                    var card = _ramais.FirstOrDefault(c => c.Ramal == r.Ramal);
                    if (card == null) { card = new RamalCard { Ramal = r.Ramal }; _ramais.Add(card); }
                    var (texto, livre) = Situacao(r.Status);
                    card.Nome = string.IsNullOrWhiteSpace(r.Nome) ? $"Ramal {r.Ramal}" : r.Nome;
                    card.StatusTexto = texto; card.Livre = livre;
                    card.Cor = livre ? _verde : _ambar; card.CorFundo = livre ? _verdeF : _ambarF;
                    var atual = _ramais.IndexOf(card);
                    if (atual != i && i < _ramais.Count) _ramais.Move(atual, i);   // livres primeiro
                }

                txtSemRamais.Text = "Nenhum outro ramal online no momento.";
                txtSemRamais.Visibility = _ramais.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                var livres = _ramais.Count(c => c.Livre);
                txtAoVivo.Text = svc.Conectado ? $"ao vivo · {livres} livre(s) de {_ramais.Count}" : "sem conexão com o monitor";
            }
            catch { }
        }

        private void Selecionar(string valor, string rotulo, bool daLista)
        {
            ValorDigitado = valor ?? string.Empty;
            EscolheuRamalDaLista = daLista;
            foreach (var c in _ramais) c.Selecionado = daLista && c.Ramal == valor;
            var tem = !string.IsNullOrWhiteSpace(ValorDigitado);
            btnConfirmar.IsEnabled = tem;
            txtSelecionado.Text = tem ? "Destino: " + rotulo : "Nenhum destino escolhido";
        }

        private void Ramal_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not RamalCard card) return;
            if (txtNumero.Text.Length > 0) { _ignorarTexto = true; txtNumero.Text = string.Empty; _ignorarTexto = false; txtDicaNumero.Visibility = Visibility.Visible; }
            Selecionar(card.Ramal, $"{card.Nome} ({card.Ramal}) · {card.StatusTexto}", daLista: true);
            if (e.ClickCount >= 2) Confirmar();
        }

        private bool _ignorarTexto;
        private void TxtNumero_TextChanged(object sender, TextChangedEventArgs e)
        {
            txtDicaNumero.Visibility = txtNumero.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_ignorarTexto) return;
            var digitado = new string(txtNumero.Text.Where(ch => char.IsDigit(ch) || ch == '*' || ch == '#').ToArray());
            Selecionar(digitado, digitado, daLista: false);
        }

        private void BtnBuscarContato_Click(object sender, RoutedEventArgs e)
        {
            var abrir = painelContatos.Visibility != Visibility.Visible;
            painelContatos.Visibility = abrir ? Visibility.Visible : Visibility.Collapsed;
            if (!abrir) return;
            try { _contatos ??= ContatoStorageService.Carregar(); } catch { _contatos = new List<Contato>(); }
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
            var numero = PhoneNumberNormalizer.NormalizeForDial(c.Numero);
            _ignorarTexto = true; txtNumero.Text = numero; _ignorarTexto = false;
            txtDicaNumero.Visibility = Visibility.Collapsed;
            Selecionar(numero, $"{c.Nome} ({numero})", daLista: false);
        }

        private void LstContatos_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstContatos.SelectedItem is Contato) Confirmar();
        }

        private void Confirmar()
        {
            if (string.IsNullOrWhiteSpace(ValorDigitado)) return;
            Confirmado = true;
            try { DialogResult = true; } catch { Close(); }
        }

        private void BtnConfirmar_Click(object sender, RoutedEventArgs e) => Confirmar();
        private void BtnCancelar_Click(object sender, RoutedEventArgs e) { Confirmado = false; try { DialogResult = false; } catch { Close(); } }
    }
}
