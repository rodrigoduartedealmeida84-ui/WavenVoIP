using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace WavenVoIP.Views
{
    public partial class TransferControlWindow : Window
    {
        public event Action? OnReturnRequested;
        public event Action? OnCompleteRequested;
        public event Action? OnStarRequested;

        private readonly DispatcherTimer _timerTransferencia = new();
        private DateTime _inicioTransferencia;
        private DispatcherTimer? _pulseTimer;
        private double _pulsePhase;
        private ScaleTransform? _pulseScale;

        private static readonly string[] _avatarColors =
            { "#6366F1", "#8B5CF6", "#EC4899", "#10B981", "#3B82F6", "#F59E0B" };

        public TransferControlWindow(string destino, string? chamadaOriginal = null)
        {
            InitializeComponent();

            var (nome, numero) = ParsearDestino(destino);
            var exibicao = string.IsNullOrEmpty(nome) ? numero : nome;
            txtNomeDestino.Text = string.IsNullOrEmpty(exibicao) ? "Destino" : exibicao;
            txtNumeroDestino.Text = string.IsNullOrEmpty(nome) ? string.Empty : numero;
            txtAvatar.Text = ComputarIniciais(exibicao);
            var cor = _avatarColors[Math.Abs(exibicao.GetHashCode()) % _avatarColors.Length];
            borderAvatar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cor));

            txtDestinoCard.Text = string.IsNullOrEmpty(nome) ? numero : $"{nome} ({numero})";
            txtChamadaOriginal.Text = string.IsNullOrWhiteSpace(chamadaOriginal) ? "Em espera" : chamadaOriginal;

            txtDestino.Text = $"Destino: {destino}";

            _inicioTransferencia = DateTime.Now;
            _timerTransferencia.Interval = TimeSpan.FromSeconds(1);
            _timerTransferencia.Tick += (_, _) =>
            {
                var t = DateTime.Now - _inicioTransferencia;
                txtTempoTransferencia.Text = t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
            };
            _timerTransferencia.Start();

            IniciarPulse(Color.FromRgb(245, 158, 11));
        }

        protected override void OnClosed(EventArgs e)
        {
            _timerTransferencia.Stop();
            _pulseTimer?.Stop();
            base.OnClosed(e);
        }

        public void AtualizarStatus(string status)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AtualizarStatus(status)); return; }
            if (txtStatusTransferencia != null) txtStatusTransferencia.Text = status;
            var s = status.ToLowerInvariant();
            if (s.Contains("atend") || s.Contains("conectad") || s.Contains("em chamada"))
                IniciarPulse(Color.FromRgb(34, 197, 94));
            else if (s.Contains("chamando") || s.Contains("tocando") || s.Contains("ligando"))
                IniciarPulse(Color.FromRgb(245, 158, 11));
            else if (s.Contains("ocup") || s.Contains("falha") || s.Contains("não atend"))
                PararPulse(Color.FromRgb(239, 68, 68));
            else
                PararPulse(Color.FromRgb(107, 114, 128));
        }

        // ── Parsing ───────────────────────────────────────────────

        private static (string nome, string numero) ParsearDestino(string destino)
        {
            if (string.IsNullOrWhiteSpace(destino)) return ("Destino", string.Empty);
            var m = Regex.Match(destino.Trim(), @"^(.+?)\s*\((\d[\d\s\-+]*)\)\s*$");
            if (m.Success) return (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
            if (destino.Trim().All(c => char.IsDigit(c) || c == '+' || c == ' ' || c == '-'))
                return (string.Empty, destino.Trim());
            return (destino.Trim(), string.Empty);
        }

        private static string ComputarIniciais(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return "?";
            var palavras = texto.Trim()
                .Split(new[] { ' ', '-', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.Length > 0 && char.IsLetter(p[0]))
                .ToList();
            if (palavras.Count == 0) return "#";
            if (palavras.Count == 1) return char.ToUpper(palavras[0][0]).ToString();
            return $"{char.ToUpper(palavras[0][0])}{char.ToUpper(palavras[^1][0])}";
        }

        // ── Pulse animation ───────────────────────────────────────

        private void IniciarPulse(Color cor)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => IniciarPulse(cor)); return; }
            try
            {
                dotStatusCore.Fill = new SolidColorBrush(cor);
                dotStatusPulse.Fill = new SolidColorBrush(cor);
                if (_pulseScale == null)
                {
                    _pulseScale = new ScaleTransform();
                    dotStatusPulse.RenderTransform = _pulseScale;
                    dotStatusPulse.RenderTransformOrigin = new Point(0.5, 0.5);
                }
                _pulseTimer?.Stop();
                _pulsePhase = 0;
                _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                _pulseTimer.Tick += (_, _) =>
                {
                    _pulsePhase = (_pulsePhase + 0.055) % (2 * Math.PI);
                    var p = (1 - Math.Cos(_pulsePhase)) / 2;
                    _pulseScale.ScaleX = 1 + 1.8 * p;
                    _pulseScale.ScaleY = 1 + 1.8 * p;
                    dotStatusPulse.Opacity = 0.55 * (1 - p);
                };
                _pulseTimer.Start();
            }
            catch { }
        }

        private void PararPulse(Color cor)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => PararPulse(cor)); return; }
            try
            {
                _pulseTimer?.Stop();
                _pulseTimer = null;
                if (dotStatusPulse != null) dotStatusPulse.Opacity = 0;
                if (dotStatusCore != null) dotStatusCore.Fill = new SolidColorBrush(cor);
            }
            catch { }
        }

        // ── Button handlers ───────────────────────────────────────

        private void BtnVoltar_Click(object sender, RoutedEventArgs e) => OnReturnRequested?.Invoke();

        private void BtnConcluir_Click(object sender, RoutedEventArgs e)
        {
            OnCompleteRequested?.Invoke();
            Close();
        }

        private void BtnEstrela_Click(object sender, RoutedEventArgs e) => OnStarRequested?.Invoke();

        private void BtnFechar_Click(object sender, RoutedEventArgs e) => Close();
    }
}
