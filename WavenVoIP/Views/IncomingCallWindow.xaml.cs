using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class IncomingCallWindow : Window
    {
        private readonly RingtoneService _ringtoneService = new RingtoneService();
        private static readonly Regex _rexNomeNum =
            new(@"^(.+)\s+\((\d+)\)$", RegexOptions.Compiled);

        public bool Aceita { get; private set; }
        public bool EncerradaPeloSistema { get; private set; }
        public bool RecusadaPeloUsuario { get; private set; }
        public event Action? AtenderSolicitado;
        public event Action? RecusarSolicitado;

        public IncomingCallWindow(string caller)
        {
            InitializeComponent();
            SetarInfoChamador(caller);
            Loaded += IncomingCallWindow_Loaded;
            Closing += (_, __) => PararToque();
            Closed += (_, __) => System.Threading.Tasks.Task.Run(() => { try { _ringtoneService.Dispose(); } catch { } });
        }

        private void SetarInfoChamador(string caller)
        {
            var m = _rexNomeNum.Match(caller ?? string.Empty);
            if (m.Success)
            {
                var nome = m.Groups[1].Value.Trim();
                var num  = m.Groups[2].Value.Trim();
                txtCallerName.Text              = nome;
                txtCallerNumDisplay.Text        = num;
                txtCallerNumDisplay.Visibility  = Visibility.Visible;
                txtInitials.Text                = GerarIniciais(nome);
            }
            else
            {
                txtCallerName.Text              = caller ?? string.Empty;
                txtCallerNumDisplay.Visibility  = Visibility.Collapsed;
                txtInitials.Text                = GerarIniciais(caller ?? string.Empty);
            }
        }

        private static string GerarIniciais(string texto)
        {
            var nome = new string((texto ?? string.Empty).TakeWhile(c => c != '(').ToArray()).Trim();
            var partes = nome.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length >= 2) return $"{partes[0][0]}{partes[1][0]}".ToUpperInvariant();
            if (partes.Length == 1 && partes[0].Length > 0) return partes[0][0].ToString().ToUpperInvariant();
            return "\uE717";
        }

        // Fica no canto inferior direito; se já houver outra janela do Waven nesse canto (chamada
        // em andamento, aviso de cliente aguardando), sobe para não cobrir nem ser coberta.
        private void Posicionar()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 6;
            var top = area.Bottom - ActualHeight - 6;
            try
            {
                foreach (Window w in Application.Current.Windows)
                {
                    if (ReferenceEquals(w, this) || !w.IsVisible || w.WindowState == WindowState.Minimized) continue;
                    if (w is not (CallWindow or ChamadasAguardandoPopup)) continue;
                    bool sobrepoeH = w.Left < Left + ActualWidth && w.Left + w.ActualWidth > Left;
                    bool sobrepoeV = w.Top < top + ActualHeight && w.Top + w.ActualHeight > top;
                    if (sobrepoeH && sobrepoeV) top = w.Top - ActualHeight - 4;
                }
            }
            catch { }
            Top = Math.Max(area.Top + 6, top);
        }

        private void Cabecalho_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); } catch { }
        }

        private void IncomingCallWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try { Posicionar(); } catch { }
            try { if (txtInitials.Text == "\uE717") { txtInitials.FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"); txtInitials.FontWeight = FontWeights.Normal; } } catch { }
            Activate();

            // v2.5.0 — travamentos: procurar o dispositivo de toque e abrir o áudio levava de
            // décimos a segundos na thread da interface, bem na hora em que a chamada chega.
            System.Threading.Tasks.Task.Run(() =>
            {
            try
            {
                if (_toqueParado) return;
                var sipCfg = SipConfig.CarregarSalva();
                var audioCfg = ConfiguracaoAudioService.Carregar();

                // Arquivo de toque
                var toqueArquivo = audioCfg.Toque;
                if (string.IsNullOrWhiteSpace(toqueArquivo)) toqueArquivo = "Assets\\toque_padrao.mp3";
                var fullPath = Path.IsPathRooted(toqueArquivo)
                    ? toqueArquivo
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, toqueArquivo);

                // Dispositivo de toque — preferir SipConfig.RingOutputDevice
                var ringNome = sipCfg?.RingOutputDevice ?? string.Empty;
                var ringId   = string.IsNullOrEmpty(ringNome)
                    ? (audioCfg.DispositivoToqueId ?? string.Empty)
                    : (Services.AudioDeviceService.BuscarIdDispositivoPorNome(ringNome, saida: true) ?? string.Empty);
                if (string.IsNullOrEmpty(ringNome)) ringNome = audioCfg.DispositivoToqueNome ?? string.Empty;

                // Volume
                var volume = sipCfg != null && sipCfg.RingVolume > 0
                    ? (float)sipCfg.RingVolume / 100f
                    : 0.8f;

                if (_toqueParado) return;
                _ringtoneService.Tocar(fullPath, ringId, ringNome, "IncomingCallWindow", volume: volume);
                if (_toqueParado) _ringtoneService.Parar();
            }
            catch { }
            });
        }

        public void FecharPorSistema()
        {
            EncerradaPeloSistema = true;
            Aceita = false;
            RecusadaPeloUsuario = false;
            try
            {
                if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(FecharPorSistema); return; }
                PararToque();
                Close();
            }
            catch { }
        }

        private volatile bool _toqueParado;
        private void PararToque()
        {
            _toqueParado = true;
            System.Threading.Tasks.Task.Run(() => { try { _ringtoneService.Parar(); } catch { } });
        }

        private void BtnAtender_Click(object sender, RoutedEventArgs e)
        {
            Aceita = true;
            RecusadaPeloUsuario = false;
            PararToque();
            AtenderSolicitado?.Invoke();
            Close();
        }

        private void BtnSilenciar_Click(object sender, RoutedEventArgs e)
        {
            PararToque();
            try { if (btnSilenciar != null) btnSilenciar.IsEnabled = false; if (txtSilenciar != null) txtSilenciar.Text = "Silenciado"; } catch { }
        }

        private void BtnRecusar_Click(object sender, RoutedEventArgs e)
        {
            Aceita = false;
            RecusadaPeloUsuario = true;
            PararToque();
            RecusarSolicitado?.Invoke();
            Close();
        }

        private void BtnFechar_Click(object sender, RoutedEventArgs e)
        {
            // Apenas fecha o popup — o shell detecta via Closed e chama IgnorarChamadaLocal()
            PararToque();
            Close();
        }
    }
}
