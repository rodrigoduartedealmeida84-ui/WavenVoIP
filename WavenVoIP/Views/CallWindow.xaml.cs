
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace WavenVoIP.Views
{
    public partial class CallWindow : Window
    {
        private readonly DispatcherTimer _timer;
        private DateTime _inicioChamada;
        private bool _gravando;
        private bool _mudo;
        private bool _segurado;
        private bool _desligamentoSolicitado;
        private bool _fechamentoPorSistema;
        private bool _fechandoJanela;
        private volatile bool _muteOperacaoEmAndamento;
        private readonly MediaPlayer _ringbackPlayer = new MediaPlayer();

        private DispatcherTimer? _pulseTimer;
        private double _pulsePhase;
        private ScaleTransform? _pulseScale;

        private static readonly string[] _avatarColors =
            { "#6366F1", "#8B5CF6", "#EC4899", "#10B981", "#3B82F6", "#F59E0B", "#EF4444" };

        public event Action? OnHangupRequested;
        public event Action? OnBlindTransferRequested;
        public event Action? OnAttendedTransferRequested;
        public event Action? OnAddParticipantRequested;
        public event Action? OnRecordingToggleRequested;
        public event Action? OnDtmfKeyboardRequested;
        public event Func<bool>? OnHoldToggleRequested;
        public event Action<bool>? OnMuteToggleRequested;
        public event Action? OnAudioRouteRequested;
#pragma warning disable CS0067
        public event Action? OnOpenConferenceParticipantsRequested;
#pragma warning restore CS0067
        public event Action<string>? OnWhatsAppRequested;

        public string NomeNumeroAtual => txtNomeNumero?.Text ?? string.Empty;

        public CallWindow(string nomeNumero, string statusInicial = "Ligando...")
        {
            InitializeComponent();

            txtNomeNumero.Text = nomeNumero;
            txtStatus.Text = statusInicial;

            AtualizarAvatar(nomeNumero);
            AtualizarCorStatus(statusInicial);

            Loaded += CallWindow_Loaded;
            Closing += CallWindow_Closing;
            StateChanged += CallWindow_StateChanged;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;
        }

        private void CallWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - Width - 12;
                Top = area.Bottom - Height - 12;
            }
            catch { }

            DefinirMudo(false);
            if (string.Equals(txtStatus.Text, "Ligando...", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(txtStatus.Text, "Chamando...", StringComparison.OrdinalIgnoreCase))
                TocarTomDeChamando();
        }

        private void CallWindow_StateChanged(object? sender, EventArgs e) { }

        public void FecharPorSistema()
        {
            _fechamentoPorSistema = true;
            PararTomDeChamando();
            FecharSeguro();
        }

        private void FecharSeguro()
        {
            if (_fechandoJanela) return;
            _fechandoJanela = true;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { if (IsLoaded) Close(); } catch { }
                }), DispatcherPriority.Background);
            }
            catch { }
        }

        private void CallWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_fechamentoPorSistema && !_desligamentoSolicitado)
            {
                _desligamentoSolicitado = true;
                PararTomDeChamando();
                try { OnHangupRequested?.Invoke(); } catch { }
            }
            // _timer (contador de duracao) nao era parado aqui — ficava rodando a cada 1s
            // mantendo a janela viva na memoria (leak) mesmo apos fechada. CallWindow se
            // acumula a cada chamada atendida ao longo de um dia de uso.
            _timer.Stop();
            _pulseTimer?.Stop();
            try { _ringbackPlayer.Close(); } catch { }
        }

        public string DuracaoAtual
        {
            get
            {
                try
                {
                    if (_inicioChamada == DateTime.MinValue) return "00:00";
                    var tempo = DateTime.Now - _inicioChamada;
                    if (tempo.TotalSeconds < 0) tempo = TimeSpan.Zero;
                    return tempo.ToString(tempo.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
                }
                catch { return "00:00"; }
            }
        }

        public void IniciarContador()
        {
            PararTomDeChamando();
            _inicioChamada = DateTime.Now;
            txtTempo.Text = "00:00";
            _timer.Start();
        }

        public void TocarTomDeChamando()
        {
            try
            {
                var caminho = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "ringback_tuuu.wav");
                if (!System.IO.File.Exists(caminho)) return;
                _ringbackPlayer.Open(new Uri(caminho, UriKind.Absolute));
                _ringbackPlayer.MediaEnded += (_, __) => { try { _ringbackPlayer.Position = TimeSpan.Zero; _ringbackPlayer.Play(); } catch { } };
                _ringbackPlayer.Play();
            }
            catch { }
        }

        public void PararTomDeChamando()
        {
            try { _ringbackPlayer.Stop(); } catch { }
        }

        public void DefinirStatus(string status)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => DefinirStatus(status)); return; }
            txtStatus.Text = status;
            AtualizarCorStatus(status);
            if (status.Contains("chamada", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("falha", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("encerrada", StringComparison.OrdinalIgnoreCase))
                PararTomDeChamando();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            var tempo = DateTime.Now - _inicioChamada;
            txtTempo.Text = tempo.ToString(tempo.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
        }

        // ── Avatar ────────────────────────────────────────────────

        private void AtualizarAvatar(string nomeNumero)
        {
            try
            {
                var iniciais = ComputarIniciais(nomeNumero);
                txtAvatar.Text = iniciais;
                var cor = _avatarColors[Math.Abs(nomeNumero.GetHashCode()) % _avatarColors.Length];
                borderAvatar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cor));
            }
            catch { }
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

        // ── Status dot + pulse ────────────────────────────────────

        private void AtualizarCorStatus(string status)
        {
            var s = status.ToLowerInvariant();
            if (s.Contains("ligando") || s.Contains("chamando") || s.Contains("tocando"))
                IniciarPulse(Color.FromRgb(245, 158, 11));
            else if (s.Contains("em chamada") || s.Contains("atendeu") || s.Contains("conectad"))
                IniciarPulse(Color.FromRgb(34, 197, 94));
            else if (s.Contains("espera") || s.Contains("segurand"))
                PararPulse(Color.FromRgb(107, 114, 128));
            else if (s.Contains("transfer"))
                IniciarPulse(Color.FromRgb(99, 102, 241));
            else if (s.Contains("encerr") || s.Contains("falha") || s.Contains("ocup"))
                PararPulse(Color.FromRgb(239, 68, 68));
            else
                PararPulse(Color.FromRgb(107, 114, 128));
        }

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

        // ── Mute ──────────────────────────────────────────────────

        private void DefinirMudo(bool ativo)
        {
            if (_muteOperacaoEmAndamento) return;
            _muteOperacaoEmAndamento = true;
            bool estadoAnterior = _mudo;
            _mudo = ativo;
            try { OnMuteToggleRequested?.Invoke(_mudo); }
            catch { _mudo = estadoAnterior; }
            _muteOperacaoEmAndamento = false;

            try
            {
                txtMudoIcon.Text = _mudo ? "" : "";
                txtMudoTexto.Text = _mudo ? "Mudo" : "Com áudio";
            }
            catch { }
        }

        // ── Button handlers ───────────────────────────────────────

        private void BtnMudo_Click(object sender, RoutedEventArgs e) => DefinirMudo(!_mudo);

        private void BtnTeclado_Click(object sender, RoutedEventArgs e) => OnDtmfKeyboardRequested?.Invoke();

        private void BtnGravar_Click(object sender, RoutedEventArgs e)
        {
            _gravando = !_gravando;
            OnRecordingToggleRequested?.Invoke();
            try { dotGravando.Visibility = _gravando ? Visibility.Visible : Visibility.Collapsed; } catch { }
            if (btnGravar.Content is System.Windows.Controls.StackPanel sp && sp.Children.Count > 1)
                ((System.Windows.Controls.TextBlock)sp.Children[1]).Text = _gravando ? "Parar" : "Gravar";
        }

        private void BtnTransferenciaCega_Click(object sender, RoutedEventArgs e) => OnBlindTransferRequested?.Invoke();
        private void BtnTransferenciaAssistida_Click(object sender, RoutedEventArgs e) => OnAttendedTransferRequested?.Invoke();

        private void BtnWhatsApp_Click(object sender, RoutedEventArgs e) => OnWhatsAppRequested?.Invoke(txtNomeNumero.Text);
        private void BtnAudio_Click(object sender, RoutedEventArgs e) => OnAudioRouteRequested?.Invoke();

        private void BtnSegurar_Click(object sender, RoutedEventArgs e)
        {
            bool ok = true;
            if (OnHoldToggleRequested != null)
            {
                try { ok = OnHoldToggleRequested.Invoke(); } catch { ok = false; }
            }
            _segurado = !_segurado;
            txtStatus.Text = _segurado ? "Em espera" : "Em chamada";
            AtualizarCorStatus(txtStatus.Text);
            AtualizarBotaoSegurar();
            if (!ok) txtStatus.Text = _segurado ? "Em espera" : "Em chamada";
        }

        private void AtualizarBotaoSegurar()
        {
            if (btnSegurar.Content is System.Windows.Controls.StackPanel sp && sp.Children.Count > 1)
            {
                ((System.Windows.Controls.TextBlock)sp.Children[0]).Text = _segurado ? "▶" : "⏸";
                ((System.Windows.Controls.TextBlock)sp.Children[1]).Text = _segurado ? "Retomar" : "Segurar";
            }
        }

        private void BtnParticipantes_Click(object sender, RoutedEventArgs e) => OnAddParticipantRequested?.Invoke();

        private void BtnDesligar_Click(object sender, RoutedEventArgs e)
        {
            if (_desligamentoSolicitado) return;
            _desligamentoSolicitado = true;
            _fechamentoPorSistema = true;
            PararTomDeChamando();
            try { OnHangupRequested?.Invoke(); } catch { }
            FecharSeguro();
        }
    }
}
