using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class DashboardControl : UserControl
    {
        private DateTime _dataInicio = DateTime.Today;
        private DateTime _dataFim    = DateTime.Today.AddDays(1).AddSeconds(-1);
        private readonly DispatcherTimer _autoRefresh = new() { Interval = TimeSpan.FromMinutes(2) };
        private List<RamalMetrica> _todasMetricas = new();

        public DashboardControl()
        {
            InitializeComponent();
            _autoRefresh.Tick += (_, _) => AtualizarDados();
            _autoRefresh.Start();
            Loaded += (_, _) =>
            {
                if (dpInicio != null) dpInicio.SelectedDate = DateTime.Today;
                if (dpFim    != null) dpFim.SelectedDate    = DateTime.Today;
                MarcarFiltro(btnFiltroHoje);
                AtualizarDados();
            };
        }

        public void AtualizarDados()
        {
            try
            {
                var todos = HistoricoStorageService.Carregar();
                var metricas = DashboardService.Calcular(todos, _dataInicio, _dataFim);

                txtRecebidas.Text  = metricas.Recebidas.ToString();
                txtRealizadas.Text = metricas.Realizadas.ToString();
                txtPerdidas.Text   = metricas.Perdidas.ToString();
                txtTotal.Text      = metricas.TotalAtendimentos.ToString();
                txtTempoMedio.Text = metricas.TempoMedio;
                txtGravacoes.Text  = metricas.Gravacoes.ToString();

                icChamadasPorHora.ItemsSource = metricas.ChamadasPorHora;

                _todasMetricas = metricas.PorRamal;
                AplicarFiltroBusca();

                var fmt = "dd/MM";
                txtPeriodo.Text = _dataInicio.Date == _dataFim.Date
                    ? _dataInicio.ToString("dd/MM/yyyy")
                    : $"{_dataInicio.ToString(fmt)} – {_dataFim.ToString(fmt)}";

                if (txtAtualizadoAs != null)
                    txtAtualizadoAs.Text = $"Atualizado às {DateTime.Now:HH:mm}";
            }
            catch { }
        }

        private void BtnAplicarPeriodo_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (dpInicio?.SelectedDate == null || dpFim?.SelectedDate == null)
            {
                System.Windows.MessageBox.Show("Selecione data inicial e data final.", "Dashboard");
                return;
            }
            _dataInicio = dpInicio.SelectedDate.Value.Date;
            _dataFim    = dpFim.SelectedDate.Value.Date.AddDays(1).AddSeconds(-1);
            foreach (var btn in new[] { btnFiltroHoje, btnFiltroOntem, btnFiltro7d, btnFiltro30d })
                MarcarFiltro_Desativar(btn);
            AtualizarDados();
        }

        private void MarcarFiltro_Desativar(System.Windows.Controls.Button? btn)
        {
            if (btn == null) return;
            btn.Background  = new SolidColorBrush(Color.FromRgb(243, 244, 246));
            btn.Foreground  = new SolidColorBrush(Color.FromRgb(71, 85, 105));
            btn.BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240));
        }

        private void AplicarFiltroBusca()
        {
            var busca = txtBuscaRamal?.Text?.Trim() ?? string.Empty;
            var lista = string.IsNullOrWhiteSpace(busca)
                ? _todasMetricas
                : _todasMetricas.Where(r =>
                    r.Ramal.Contains(busca, StringComparison.OrdinalIgnoreCase) ||
                    r.Nome.Contains(busca, StringComparison.OrdinalIgnoreCase)).ToList();

            gridRamais.ItemsSource = null;
            gridRamais.ItemsSource = lista;
        }

        private void TxtBuscaRamal_TextChanged(object sender, TextChangedEventArgs e)
            => AplicarFiltroBusca();

        private void BtnFiltroHoje_Click(object sender, RoutedEventArgs e)
        {
            _dataInicio = DateTime.Today;
            _dataFim    = DateTime.Today.AddDays(1).AddSeconds(-1);
            MarcarFiltro(btnFiltroHoje);
            AtualizarDados();
        }

        private void BtnFiltroOntem_Click(object sender, RoutedEventArgs e)
        {
            _dataInicio = DateTime.Today.AddDays(-1);
            _dataFim    = DateTime.Today.AddSeconds(-1);
            MarcarFiltro(btnFiltroOntem);
            AtualizarDados();
        }

        private void BtnFiltro7d_Click(object sender, RoutedEventArgs e)
        {
            _dataInicio = DateTime.Today.AddDays(-6);
            _dataFim    = DateTime.Today.AddDays(1).AddSeconds(-1);
            MarcarFiltro(btnFiltro7d);
            AtualizarDados();
        }

        private void BtnFiltro30d_Click(object sender, RoutedEventArgs e)
        {
            _dataInicio = DateTime.Today.AddDays(-29);
            _dataFim    = DateTime.Today.AddDays(1).AddSeconds(-1);
            MarcarFiltro(btnFiltro30d);
            AtualizarDados();
        }

        private void BtnAtualizar_Click(object sender, RoutedEventArgs e) => AtualizarDados();

        private void MarcarFiltro(Button ativo)
        {
            var todos = new[] { btnFiltroHoje, btnFiltroOntem, btnFiltro7d, btnFiltro30d };
            foreach (var btn in todos)
            {
                if (btn == null) continue;
                bool sel = ReferenceEquals(btn, ativo);
                btn.Background  = new SolidColorBrush(sel ? Color.FromRgb(124, 58, 237) : Color.FromRgb(243, 244, 246));
                btn.Foreground  = new SolidColorBrush(sel ? Colors.White : Color.FromRgb(71, 85, 105));
                btn.BorderBrush = new SolidColorBrush(sel ? Color.FromRgb(124, 58, 237) : Color.FromRgb(226, 232, 240));
            }
        }
    }
}
