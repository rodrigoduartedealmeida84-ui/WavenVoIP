using System;
using System.Windows;
using System.Windows.Threading;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    // Toast para chamadas REALIZADAS (nós ligamos) classificadas como Recusada — nunca reutiliza
    // o MissedCallPopup/MissedCallToast, que são exclusivos de chamadas RECEBIDAS não atendidas.
    public partial class OutboundDeclinedToast : Window
    {
        private readonly DispatcherTimer _timer = new DispatcherTimer();

        public OutboundDeclinedToast(string nome, string numero)
        {
            InitializeComponent();

            if (string.IsNullOrWhiteSpace(nome))
            {
                txtNome.Text = string.IsNullOrWhiteSpace(numero) ? "Número desconhecido" : numero;
                txtNumero.Visibility = Visibility.Collapsed;
            }
            else
            {
                txtNome.Text = nome;
                txtNumero.Text = numero ?? string.Empty;
                txtNumero.Visibility = string.IsNullOrWhiteSpace(numero) ? Visibility.Collapsed : Visibility.Visible;
            }

            Loaded += (_, __) => Posicionar();
            _timer.Interval = TimeSpan.FromSeconds(6);
            _timer.Tick += Timer_Tick;
            _timer.Start();
            // Garante que o timer seja parado ao fechar por qualquer meio (X, auto-close, sistema)
            Closed += (_, __) => PararTimer();

            LogHelper.Sip("OUTBOUND_DECLINED_TOAST_SHOW");
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            PararTimer();
            LogHelper.Sip("OUTBOUND_DECLINED_TOAST_AUTO_CLOSE");
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
            LogHelper.Sip("OUTBOUND_DECLINED_TOAST_MANUAL_CLOSE");
            try { Close(); } catch { }
        }
    }
}
