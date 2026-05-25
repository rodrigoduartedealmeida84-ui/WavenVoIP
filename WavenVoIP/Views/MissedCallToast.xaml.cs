using System;
using System.Windows;
using System.Windows.Threading;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class MissedCallToast : Window
    {
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        public MissedCallToast(string caller)
        {
            InitializeComponent();
            var numDisplay = PhoneNumberNormalizer.NormalizeForDisplay(caller);
            if (string.IsNullOrWhiteSpace(numDisplay)) numDisplay = "Chamada do sistema";
            var nomeContato = ContatoStorageService.ResolverNomePorNumero(caller);
            txtCaller.Text = string.Equals(nomeContato, caller, StringComparison.OrdinalIgnoreCase)
                ? numDisplay
                : $"{nomeContato} ({numDisplay})";
            Loaded += (_, __) => Posicionar();
            _timer.Interval = TimeSpan.FromSeconds(8);
            _timer.Tick += (_, __) => { _timer.Stop(); Close(); };
            _timer.Start();
        }
        private void Posicionar()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 18;
            Top = area.Bottom - Height - 18;
        }
        private void Close_Click(object sender, RoutedEventArgs e) { _timer.Stop(); Close(); }
    }
}
