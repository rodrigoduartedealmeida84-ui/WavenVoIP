using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class FilasRealtimeControl : UserControl
    {
        private static readonly SolidColorBrush _brConectado  = Freeze(Color.FromRgb(22, 163, 74));
        private static readonly SolidColorBrush _brAguardando = Freeze(Color.FromRgb(75, 85, 99));
        private static SolidColorBrush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public FilasRealtimeControl()
        {
            InitializeComponent();
            Loaded   += (_, _) => ConectarAoServico();
            Unloaded += (_, _) => DesconectarDoServico();
        }

        private void ConectarAoServico()
        {
            var svc = AmiMonitorService.Current;
            if (svc == null)
            {
                AtualizarStatusBadge(false);
                txtVazioFilas.Text = "AMI não configurado. Verifique as configurações.";
                txtVazioFilas.Visibility = Visibility.Visible;
                return;
            }

            svc.FilasChanged -= OnFilasChanged;
            svc.FilasChanged += OnFilasChanged;

            icFilas.ItemsSource = svc.Filas;
            AtualizarVazio(svc);
            AtualizarStatusBadge(svc.Conectado);
        }

        private void DesconectarDoServico()
        {
            var svc = AmiMonitorService.Current;
            if (svc != null) svc.FilasChanged -= OnFilasChanged;
        }

        private void OnFilasChanged()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnFilasChanged); return; }
            var svc = AmiMonitorService.Current;
            AtualizarStatusBadge(svc?.Conectado == true);
            AtualizarVazio(svc);
            txtAtualizadoFilas.Text = $"Atualizado às {DateTime.Now:HH:mm:ss}";
        }

        private void AtualizarVazio(AmiMonitorService? svc)
        {
            var count = svc?.Filas.Count ?? 0;
            txtVazioFilas.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (count == 0)
                txtVazioFilas.Text = svc?.Conectado == true
                    ? "Nenhuma fila encontrada no servidor AMI."
                    : "Aguardando Waven API...";
        }

        private void AtualizarStatusBadge(bool conectado)
        {
            bdConexaoFilas.Background = conectado ? _brConectado : _brAguardando;
            txtStatusFilas.Text = conectado ? "● Conectado" : "● Aguardando";
        }

        private void BtnAtualizarFilas_Click(object sender, RoutedEventArgs e)
        {
            AmiMonitorService.Current?.ForcePoll();
            txtAtualizadoFilas.Text = $"Atualizado às {DateTime.Now:HH:mm:ss}";
        }
    }
}
