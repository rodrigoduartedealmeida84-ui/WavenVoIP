using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    // v2.5.0 — aviso em tempo real de cliente aguardando na fila de atendimento, com
    // "Puxar Ligação" direto no aviso.
    // Quem decide o que mostrar é a janela principal (monitor de chamadas ativas); este popup
    // só exibe, atualiza o tempo, e devolve os cliques. Uma linha por chamada (canal do cliente).
    public partial class ChamadasAguardandoPopup : Window
    {
        private static readonly Brush _roxo = Congelar(109, 40, 217), _roxoF = Congelar(245, 243, 255);
        private static readonly Brush _ambar = Congelar(180, 83, 9), _ambarF = Congelar(255, 251, 235);
        private static readonly Brush _azul = Congelar(37, 99, 235), _azulF = Congelar(239, 246, 255);
        private static Brush Congelar(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

        private readonly ObservableCollection<PuxarLigacaoWindow.Linha> _linhas = new();


        public event Action<ChamadaAtivaInfo>? PuxarSolicitado;
        public event Action<IReadOnlyList<string>>? Dispensado;   // canais dispensados
        public event Action? VerTudoSolicitado;                    // abrir a janela completa

        public ChamadasAguardandoPopup()
        {
            InitializeComponent();
            lista.ItemsSource = _linhas;
            SizeChanged += (_, _) => Posicionar();
            Loaded += (_, _) => Posicionar();
        }

        // Avisa SOMENTE cliente parado na fila de atendimento. URA, mensagens automáticas, fora
        // do horário, grupo tocando, caixa postal e ligação em atendimento nunca avisam (seguem
        // visíveis na janela "Puxar Ligação"). A regra completa está em AvisoFilaPolicy.
        public static bool DeveAvisar(ChamadaAtivaInfo c) => AvisoFilaPolicy.EstaAguardandoNaFila(c);

        public int Quantidade => _linhas.Count;

        // v2.5.1 — linhas que deixaram de valer fora da leitura normal (ex.: tocando no meu ramal)
        public void RemoverCanais(IEnumerable<string> canais)
        {
            foreach (var c in canais)
                foreach (var l in _linhas.Where(l => string.Equals(l.Chave, c, StringComparison.OrdinalIgnoreCase)).ToList())
                    _linhas.Remove(l);
            txtTitulo.Text = _linhas.Count == 1 ? "1 cliente aguardando na fila" : $"{_linhas.Count} clientes aguardando na fila";
        }

        public void Atualizar(IReadOnlyList<ChamadaAtivaInfo> chamadas)
        {
            foreach (var velha in _linhas.Where(l => chamadas.All(c => c.CanalCliente != l.Chave)).ToList())
                _linhas.Remove(velha);

            foreach (var c in chamadas)
            {
                var linha = _linhas.FirstOrDefault(l => l.Chave == c.CanalCliente);
                if (linha == null) { linha = new PuxarLigacaoWindow.Linha { Chave = c.CanalCliente }; _linhas.Add(linha); }
                linha.Info = c;
                var numero = PhoneNumberNormalizer.NormalizeForDisplay(DialPlanService.RemoverPrefixoDeRota(c.Numero));
                string nome;
                try { nome = ContatoStorageService.ResolverNomePorNumero(numero); } catch { nome = numero; }
                if (string.Equals(nome, numero, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Nome) && !c.Nome.All(ch => char.IsDigit(ch) || ch == '+'))
                    nome = c.Nome;
                linha.Cliente = nome;
                linha.Telefone = string.Equals(nome, numero, StringComparison.OrdinalIgnoreCase) ? "Número não cadastrado" : numero;
                linha.Canal = c.Tronco;
                linha.Status = c.StatusTexto;
                (linha.Cor, linha.CorFundo) = c.Status switch
                {
                    StatusChamadaAtiva.ForaDoHorario => (_roxo, _roxoF),
                    StatusChamadaAtiva.NaUra => (_azul, _azulF),
                    _ => (_ambar, _ambarF)
                };
            }
            AtualizarTempos();
            txtTitulo.Text = _linhas.Count == 1 ? "1 cliente aguardando na fila" : $"{_linhas.Count} clientes aguardando na fila";
        }

        public void AtualizarTempos()
        {
            foreach (var l in _linhas)
            {
                var t = TimeSpan.FromSeconds(l.Info.DuracaoSeg) + (DateTime.Now - l.Info.LidoEm);
                if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                l.Duracao = $"há {(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
            }
            try { Posicionar(); } catch { }
        }

        public void MostrarMensagem(string texto)
        {
            txtStatus.Text = texto;
            txtStatus.Visibility = string.IsNullOrWhiteSpace(texto) ? Visibility.Collapsed : Visibility.Visible;
        }

        // v2.5.1 — o aviso é só visual (nunca toca som) e nunca cobre a janela de uma chamada
        // tocando ou em andamento: fica no canto inferior direito, sobe acima delas e, sem espaço,
        // vai para o lado esquerdo. Reavaliado a cada segundo (AtualizarTempos).
        private void Posicionar()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            var area = SystemParameters.WorkArea;
            var chamadas = JanelasDeChamada();
            // depois que o operador arrasta, fica onde ele deixou — desde que não cubra uma chamada
            if (_arrastado && !chamadas.Any(c => c.IntersectsWith(new Rect(Left, Top, ActualWidth, ActualHeight)))) return;

            double left = area.Right - ActualWidth - 8;
            double top = area.Bottom - ActualHeight - 8;
            for (int i = 0; i < 4; i++)
            {
                var r = new Rect(left, top, ActualWidth, ActualHeight);
                var cruzou = chamadas.Where(c => c.IntersectsWith(r)).ToList();
                if (cruzou.Count == 0) break;
                top = cruzou.Min(c => c.Top) - ActualHeight - 6;
            }
            if (top < area.Top + 8 && chamadas.Count > 0)
            {
                top = area.Bottom - ActualHeight - 8;
                left = Math.Max(area.Left + 8, chamadas.Min(c => c.Left) - ActualWidth - 8);
            }
            top = Math.Max(area.Top + 8, top);
            if (Math.Abs(Left - left) > 0.5) Left = left;
            if (Math.Abs(Top - top) > 0.5) Top = top;
        }

        private List<Rect> JanelasDeChamada()
        {
            var lista = new List<Rect>();
            try
            {
                foreach (Window w in Application.Current.Windows)
                {
                    if (ReferenceEquals(w, this) || !w.IsVisible || w.WindowState == WindowState.Minimized) continue;
                    if (w is not (CallWindow or IncomingCallWindow)) continue;
                    if (w.ActualWidth <= 0 || w.ActualHeight <= 0) continue;
                    lista.Add(new Rect(w.Left - 4, w.Top - 4, w.ActualWidth + 8, w.ActualHeight + 8));
                }
            }
            catch { }
            return lista;
        }

        private bool _arrastado;
        private void Cabecalho_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { _arrastado = true; DragMove(); } catch { }
        }

        private void BtnPuxar_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is PuxarLigacaoWindow.Linha l) PuxarSolicitado?.Invoke(l.Info);
        }

        private void BtnDispensar_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is PuxarLigacaoWindow.Linha l)
            {
                _linhas.Remove(l);
                Dispensado?.Invoke(new[] { l.Chave });
            }
        }

        private void BtnVerTudo_Click(object sender, RoutedEventArgs e) => VerTudoSolicitado?.Invoke();

        private void BtnDispensarTodos_Click(object sender, RoutedEventArgs e)
        {
            var chaves = _linhas.Select(l => l.Chave).ToList();
            _linhas.Clear();
            Dispensado?.Invoke(chaves);
        }
    }
}
