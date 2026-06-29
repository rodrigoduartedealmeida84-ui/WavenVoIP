using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class RamaisRealtimeControl : UserControl
    {
        private string _filtroAtivo = "Todos";
        private string _busca = string.Empty;
        private ListCollectionView? _ramaisView;
        private readonly DispatcherTimer _durationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _contadorTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

        // Brushes cached para evitar alocações a cada tick dos timers
        private static readonly SolidColorBrush _brConectado   = Freeze(Color.FromRgb(22, 163, 74));
        private static readonly SolidColorBrush _brModoBasico  = Freeze(Color.FromRgb(202, 138, 4));
        private static readonly SolidColorBrush _brDesconectado = Freeze(Color.FromRgb(220, 38, 38));
        private static readonly SolidColorBrush _brConectando  = Freeze(Color.FromRgb(75, 85, 99));

        private static SolidColorBrush Freeze(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public RamaisRealtimeControl()
        {
            InitializeComponent();
            _durationTimer.Tick += (_, _) => AtualizarDuracoes();
            _contadorTimer.Tick += (_, _) => AtualizarContadores();
            // Timers iniciam apenas após o controle ser carregado (Loaded),
            // não no construtor, evitando ticks antes de estar visível.
            Loaded   += (_, _) => { ConectarAoServico(); _durationTimer.Start(); _contadorTimer.Start(); };
            Unloaded += (_, _) =>
            {
                _durationTimer.Stop();
                _contadorTimer.Stop();
                var svc = AmiMonitorService.Current;
                if (svc != null) svc.EstadoChanged -= OnEstadoChanged;
            };
        }

        private void ConectarAoServico()
        {
            var svc = AmiMonitorService.Current;
            if (svc == null)
            {
                try
                {
                    var cfg = SipConfig.CarregarSalva();
                    if (cfg != null && cfg.AmiAtivo)
                        svc = AmiMonitorService.Iniciar(cfg);
                }
                catch { }
            }

            if (svc == null)
            {
                txtVazio.Text = "AMI não configurado. Verifique as configurações.";
                txtVazio.Visibility = Visibility.Visible;
                return;
            }

            // Dedup — never double-subscribe
            svc.EstadoChanged -= OnEstadoChanged;
            svc.EstadoChanged += OnEstadoChanged;

            // ListCollectionView is an independent live view of the ObservableCollection.
            // svc.Ramais.Add() auto-notifies the view — no snapshot needed.
            _ramaisView = new ListCollectionView(svc.Ramais);
            _ramaisView.Filter = FiltrarRamalObj;
            _ramaisView.SortDescriptions.Clear();
            _ramaisView.SortDescriptions.Add(new SortDescription(nameof(RamalStatusItem.SortKey), ListSortDirection.Ascending));
            _ramaisView.SortDescriptions.Add(new SortDescription(nameof(RamalStatusItem.Ramal),   ListSortDirection.Ascending));
            icRamais.ItemsSource = _ramaisView;

            AtualizarContadores();
            OnEstadoChanged();
        }

        private bool FiltrarRamalObj(object obj)
        {
            if (obj is not RamalStatusItem r) return false;
            if (!string.IsNullOrWhiteSpace(_busca) &&
                !r.Ramal.Contains(_busca, StringComparison.OrdinalIgnoreCase) &&
                !r.Nome.Contains(_busca, StringComparison.OrdinalIgnoreCase))
                return false;
            return _filtroAtivo switch
            {
                "Online"        => r.Status == StatusRamal.Online,
                "Offline"       => r.Status == StatusRamal.Offline,
                "Indisponivel"  => r.Status == StatusRamal.Indisponivel || r.Status == StatusRamal.Desconhecido,
                "EmLigacao"     => r.Status == StatusRamal.EmLigacao,
                "Tocando"       => r.Status == StatusRamal.Tocando || r.Status == StatusRamal.Chamando,
                _               => true
            };
        }

        private void OnEstadoChanged()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnEstadoChanged); return; }
            var svc = AmiMonitorService.Current;
            if (svc == null) return;

            AtualizarBadgeConexao(svc);
            AtualizarContadores();
            _ramaisView?.Refresh();
            txtAtualizado.Text = $"Atualizado às {DateTime.Now:HH:mm:ss}";
        }

        private void AtualizarBadgeConexao(AmiMonitorService svc)
        {
            bool temErro = !string.IsNullOrWhiteSpace(svc.ErroConexao);

            if (svc.Conectado && temErro)
            {
                bdConexao.Background  = _brModoBasico;
                txtStatusConexao.Text = "● Modo básico";
                bdAviso.Visibility    = Visibility.Visible;
                txtAviso.Text         = svc.ErroConexao;
            }
            else if (svc.Conectado)
            {
                bdConexao.Background  = _brConectado;
                txtStatusConexao.Text = "● Conectado";
                bdAviso.Visibility    = Visibility.Collapsed;
            }
            else if (temErro)
            {
                bdConexao.Background  = _brDesconectado;
                txtStatusConexao.Text = "● Desconectado";
                bdAviso.Visibility    = Visibility.Visible;
                txtAviso.Text         = svc.ErroConexao;
            }
            else
            {
                bdConexao.Background  = _brConectando;
                txtStatusConexao.Text = "● Conectando...";
                bdAviso.Visibility    = Visibility.Collapsed;
            }
        }

        private void AtualizarDuracoes() => AmiMonitorService.Current?.AtualizarDuracoes();

        private void AtualizarContadores()
        {
            var svc    = AmiMonitorService.Current;
            var ramais = svc?.Ramais;
            if (ramais == null) return;

            var online    = ramais.Count(r => r.Status == StatusRamal.Online);
            var emLigacao = ramais.Count(r => r.Status == StatusRamal.EmLigacao);
            var tocando   = ramais.Count(r => r.Status == StatusRamal.Tocando || r.Status == StatusRamal.Chamando);
            // Offline: apenas ramais confirmados sem registro pelo AMI
            var offline   = ramais.Count(r => r.Status == StatusRamal.Offline);
            // Indisponível: API não confirmou status (modo básico ou ramal sem contato AMI)
            var indisp    = ramais.Count(r => r.Status == StatusRamal.Indisponivel || r.Status == StatusRamal.Desconhecido);

            txtOnlineCount.Text    = online.ToString();
            txtEmLigacaoCount.Text = emLigacao.ToString();
            txtTocandoCount.Text   = tocando.ToString();
            txtOfflineCount.Text   = offline.ToString();
            txtIndispCount.Text    = indisp.ToString();

            if (svc != null) AtualizarBadgeConexao(svc);

            bool conectado = svc?.Conectado == true;
            var total = ramais.Count;

            var visiveis = _ramaisView?.Count ?? 0;
            txtVazio.Visibility = (visiveis == 0) ? Visibility.Visible : Visibility.Collapsed;
            if (visiveis == 0)
            {
                if (!conectado && !string.IsNullOrWhiteSpace(svc?.ErroConexao))
                    txtVazio.Text = svc!.ErroConexao;
                else if (!conectado)
                    txtVazio.Text = "Aguardando Waven API...";
                else if (total > 0)
                    txtVazio.Text = "Nenhum ramal corresponde ao filtro.";
                else
                    txtVazio.Text = "Nenhum ramal retornado pela API.";
            }
        }

        private void BtnAtualizar_Click(object sender, RoutedEventArgs e)
        {
            // Força nova poll imediata na API (não apenas refresh da view)
            AmiMonitorService.Current?.ForcePoll();
            _ramaisView?.Refresh();
            AtualizarContadores();
            txtAtualizado.Text = $"Atualizado às {DateTime.Now:HH:mm:ss}";
        }

        private void TxtBusca_TextChanged(object sender, TextChangedEventArgs e)
        {
            _busca = txtBusca.Text.Trim();
            _ramaisView?.Refresh();
        }

        private void BtnFiltroDropdown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.ContextMenu == null) return;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }

        private void FiltroMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item) return;
            _filtroAtivo = item.Tag?.ToString() ?? "Todos";
            var label = (item.Header?.ToString() ?? "Todos").TrimStart('●').Trim();
            btnFiltroDropdown.Content = _filtroAtivo == "Todos" ? "Filtros ▾" : $"{label} ▾";
            _ramaisView?.Refresh();
            AtualizarContadores();
        }
    }
}
