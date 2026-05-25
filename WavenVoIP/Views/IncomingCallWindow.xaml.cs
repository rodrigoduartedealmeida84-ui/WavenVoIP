using System;
using System.IO;
using System.Linq;
using System.Windows;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class IncomingCallWindow : Window
    {
        private readonly RingtoneService _ringtoneService = new RingtoneService();

        public bool Aceita { get; private set; }
        public bool EncerradaPeloSistema { get; private set; }
        public bool RecusadaPeloUsuario { get; private set; }
        public event Action? AtenderSolicitado;
        public event Action? RecusarSolicitado;

        public IncomingCallWindow(string caller)
        {
            InitializeComponent();
            txtCaller.Text = caller;
            txtInitials.Text = GerarIniciais(caller);
            Loaded += IncomingCallWindow_Loaded;
            Closing += (_, __) => PararToque();
            Closed += (_, __) => _ringtoneService.Dispose();
        }

        private static string GerarIniciais(string texto)
        {
            var nome = new string((texto ?? string.Empty).TakeWhile(c => c != '(').ToArray()).Trim();
            var partes = nome.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length >= 2) return $"{partes[0][0]}{partes[1][0]}".ToUpperInvariant();
            if (partes.Length == 1 && partes[0].Length > 0) return partes[0][0].ToString().ToUpperInvariant();
            return "☎";
        }

        private void IncomingCallWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - Width - 18;
                Top = area.Bottom - Height - 18;

                var config = ConfiguracaoAudioService.Carregar();
                var path = string.IsNullOrWhiteSpace(config.Toque) ? "Assets\\toque_padrao.mp3" : config.Toque;
                var fullPath = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);

                _ringtoneService.Tocar(fullPath, config.DispositivoToqueId, config.DispositivoToqueNome, "IncomingCallWindow");
            }
            catch { }
            Activate();
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

        private void PararToque() => _ringtoneService.Parar();

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
            try { if (btnSilenciar != null) btnSilenciar.IsEnabled = false; } catch { }
        }

        private void BtnRecusar_Click(object sender, RoutedEventArgs e)
        {
            Aceita = false;
            RecusadaPeloUsuario = true;
            PararToque();
            RecusarSolicitado?.Invoke();
            Close();
        }
    }
}
