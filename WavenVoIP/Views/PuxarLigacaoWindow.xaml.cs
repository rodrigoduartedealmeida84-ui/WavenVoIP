using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    // v2.5.0 — "Puxar ligação": lista, em tempo real, as chamadas de clientes vivas no PABX —
    // em atendimento por outro ramal, tocando, na fila, na URA, ouvindo a mensagem de fora do
    // horário ou na caixa postal — e permite assumir qualquer uma no ramal atual.
    // A leitura vem do AMI (SipService.ListarChamadasAtivasAsync) a cada 2 s; quem executa a
    // troca é o SipService.PuxarChamadaAsync: o ramal de quem puxa toca primeiro e a chamada
    // só muda de mãos quando ele atende.
    public partial class PuxarLigacaoWindow : Window
    {
        public sealed class Linha : INotifyPropertyChanged
        {
            private string _cliente = "", _telefone = "", _canal = "", _status = "", _duracao = "", _atendente = "";
            private Brush _cor = Brushes.Gray, _corFundo = Brushes.Transparent;
            public string Chave { get; init; } = "";
            public ChamadaAtivaInfo Info { get; set; } = new();
            public string Cliente   { get => _cliente;   set { if (_cliente != value)   { _cliente = value;   On(); } } }
            public string Telefone  { get => _telefone;  set { if (_telefone != value)  { _telefone = value;  On(); } } }
            public string Canal     { get => _canal;     set { if (_canal != value)     { _canal = value;     On(); } } }
            public string Status    { get => _status;    set { if (_status != value)    { _status = value;    On(); } } }
            public string Duracao   { get => _duracao;   set { if (_duracao != value)   { _duracao = value;   On(); } } }
            public string Atendente { get => _atendente; set { if (_atendente != value) { _atendente = value; On(); } } }
            public Brush Cor        { get => _cor;       set { if (!Equals(_cor, value))      { _cor = value;      On(); } } }
            public Brush CorFundo   { get => _corFundo;  set { if (!Equals(_corFundo, value)) { _corFundo = value; On(); } } }
            public event PropertyChangedEventHandler? PropertyChanged;
            private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        private static readonly Brush _roxo = Congelar(109, 40, 217), _roxoF = Congelar(245, 243, 255);
        private static readonly Brush _azul = Congelar(37, 99, 235),  _azulF = Congelar(239, 246, 255);
        private static readonly Brush _ambar = Congelar(180, 83, 9),  _ambarF = Congelar(255, 251, 235);
        private static readonly Brush _verde = Congelar(21, 128, 61), _verdeF = Congelar(240, 253, 244);
        private static readonly Brush _cinza = Congelar(71, 85, 105), _cinzaF = Congelar(241, 245, 249);
        private static readonly Brush _verdeVivo = Congelar(74, 222, 128);
        private static Brush Congelar(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

        private readonly SipService _sip;
        private readonly ObservableCollection<Linha> _linhas = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private int _ticks;
        private bool _lendo, _puxando;
        private DateTime _ultimaLeitura = DateTime.MinValue;
        private string _erroLeitura = string.Empty;

        public PuxarLigacaoWindow(SipService sip, string meuRamal)
        {
            InitializeComponent();
            _sip = sip;
            lstChamadas.ItemsSource = _linhas;
            _timer.Tick += async (_, _) => await TickAsync();
            // nunca maior que a tela (resoluções baixas / escala do Windows em 125–150%)
            var area = SystemParameters.WorkArea;
            if (Width > area.Width - 24) Width = Math.Max(MinWidth, area.Width - 24);
            if (Height > area.Height - 24) Height = Math.Max(MinHeight, area.Height - 24);
            Loaded += async (_, _) => { await LerAsync(); _timer.Start(); };
            Closed += (_, _) => { _timer.Stop(); try { _sip.EncerrarMonitorChamadasAtivas(); } catch { } };
        }

        private async Task TickAsync()
        {
            // o relógio de cada linha anda a cada segundo; o PABX é consultado a cada 2 s
            foreach (var l in _linhas)
                l.Duracao = FormatarDuracao(TimeSpan.FromSeconds(l.Info.DuracaoSeg) + (DateTime.Now - l.Info.LidoEm));
            if (++_ticks % 2 == 0) await LerAsync();
            AtualizarRodape();
        }

        private async Task LerAsync()
        {
            if (_lendo) return;
            _lendo = true;
            try
            {
                var chamadas = await _sip.ListarChamadasAtivasAsync();
                _ultimaLeitura = DateTime.Now; _erroLeitura = string.Empty;

                foreach (var velha in _linhas.Where(l => chamadas.All(c => c.CanalCliente != l.Chave)).ToList())
                    _linhas.Remove(velha);

                foreach (var c in chamadas)
                {
                    var linha = _linhas.FirstOrDefault(l => l.Chave == c.CanalCliente);
                    if (linha == null) { linha = new Linha { Chave = c.CanalCliente }; _linhas.Add(linha); }
                    linha.Info = c;

                    var numero = PhoneNumberNormalizer.NormalizeForDisplay(DialPlanService.RemoverPrefixoDeRota(c.Numero));
                    string nome;
                    try { nome = ContatoStorageService.ResolverNomePorNumero(numero); } catch { nome = numero; }
                    if (string.Equals(nome, numero, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Nome) && !c.Nome.All(ch => char.IsDigit(ch) || ch == '+'))
                        nome = c.Nome;
                    linha.Cliente = nome;
                    linha.Telefone = string.Equals(nome, numero, StringComparison.OrdinalIgnoreCase) ? string.Empty : numero;
                    linha.Canal = c.Tronco;
                    linha.Status = c.StatusTexto;
                    linha.Duracao = FormatarDuracao(TimeSpan.FromSeconds(c.DuracaoSeg));
                    linha.Atendente = NomeDoRamal(c.RamalAtendente);
                    (linha.Cor, linha.CorFundo) = c.Status switch
                    {
                        StatusChamadaAtiva.EmAtendimento => (_verde, _verdeF),
                        StatusChamadaAtiva.NaFila or StatusChamadaAtiva.TocandoRamais => (_ambar, _ambarF),
                        StatusChamadaAtiva.NaUra => (_azul, _azulF),
                        StatusChamadaAtiva.ForaDoHorario or StatusChamadaAtiva.CaixaPostal => (_roxo, _roxoF),
                        _ => (_cinza, _cinzaF)
                    };
                }
            }
            catch (Exception ex)
            {
                _erroLeitura = ex.Message;
            }
            finally
            {
                _lendo = false;
                AtualizarRodape();
            }
        }

        private void AtualizarRodape()
        {
            txtVazio.Visibility = _linhas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            txtContagem.Text = _linhas.Count == 1 ? "1 ligação" : $"{_linhas.Count} ligações";
            var semLeitura = !string.IsNullOrEmpty(_erroLeitura);
            bolinhaAoVivo.Fill = semLeitura ? Brushes.OrangeRed : _verdeVivo;
            if (semLeitura)
                txtSubtitulo.Text = "Sem leitura do PABX: " + _erroLeitura;
            else if (_ultimaLeitura == DateTime.MinValue)
                txtSubtitulo.Text = "Consultando o PABX…";
            else
                txtSubtitulo.Text = $"Ao vivo · atualizado às {_ultimaLeitura:HH:mm:ss} · em atendimento, fila, URA, fora do horário e caixa postal";
        }

        private static string NomeDoRamal(string ramal)
        {
            if (string.IsNullOrWhiteSpace(ramal)) return "—";
            try
            {
                var nome = ContatoStorageService.ResolverNomePorNumero(ramal);
                return string.Equals(nome, ramal, StringComparison.OrdinalIgnoreCase) ? $"Ramal {ramal}" : $"{nome} ({ramal})";
            }
            catch { return $"Ramal {ramal}"; }
        }

        private static string FormatarDuracao(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes:D2}:{t.Seconds:D2}";
        }

        private async void BtnPuxar_Click(object sender, RoutedEventArgs e)
        {
            if (_puxando || (sender as Button)?.Tag is not Linha linha) return;

            if (_sip.IsInCall)
            {
                txtStatus.Text = "Encerre a sua ligação atual antes de puxar outra.";
                return;
            }

            var info = linha.Info;
            var situacao = info.Status == StatusChamadaAtiva.EmAtendimento
                ? $"que está com {linha.Atendente}"
                : $"({info.StatusTexto.ToLowerInvariant()})";
            var efeito = info.Status == StatusChamadaAtiva.EmAtendimento
                ? "Ao atender, você entra na ligação em conferência: cliente, atendente atual e você. Ninguém é desconectado."
                : "Ao atender, o cliente sai de onde está e fala com você.";
            var resposta = MessageBox.Show(this,
                (info.Status == StatusChamadaAtiva.EmAtendimento ? $"Entrar na ligação de {linha.Cliente} {situacao}?\n\n" : $"Puxar a ligação de {linha.Cliente} {situacao}?\n\n") +
                "O seu ramal vai tocar. " + efeito + "\n" +
                "Se você não atender, nada muda para o cliente.",
                "Puxar ligação", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (resposta != MessageBoxResult.Yes) return;

            _puxando = true;
            txtStatus.Text = "Conferindo a ligação no PABX…";
            try
            {
                var (ok, mensagem) = await _sip.PuxarChamadaAsync(info);
                txtStatus.Text = mensagem;
                if (ok) _ = FecharDepoisAsync();
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Não foi possível puxar: " + ex.Message;
            }
            finally { _puxando = false; }
        }

        private async Task FecharDepoisAsync()
        {
            await Task.Delay(2500);
            try { Close(); } catch { }
        }

        private void BtnFechar_Click(object sender, RoutedEventArgs e) => Close();
    }
}
