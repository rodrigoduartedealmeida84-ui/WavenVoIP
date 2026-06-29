using System;
using System.Windows;
using System.Windows.Threading;

namespace WavenVoIP.Views
{
    public partial class MissedCallToast : Window
    {
        private readonly DispatcherTimer _timer = new DispatcherTimer();

        public MissedCallToast(string caller)
        {
            InitializeComponent();
            // caller já é o displayCaller resolvido (pode ser "11987654321" ou "Nome (11987654321)")
            txtCaller.Text = string.IsNullOrWhiteSpace(caller) ? "Chamada do sistema" : caller;
            Loaded += (_, __) => Posicionar();
            _timer.Interval = TimeSpan.FromSeconds(8);
            _timer.Tick += Timer_Tick;
            _timer.Start();
            // Garante que o timer seja parado ao fechar por qualquer meio
            Closed += (_, __) => PararTimer();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            PararTimer();
            try { Close(); } catch { }
        }

        private void PararTimer()
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
        }

        private void Posicionar()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 18;
            Top  = area.Bottom - Height - 18;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            PararTimer();
            try { Close(); } catch { }
        }
    }
}
