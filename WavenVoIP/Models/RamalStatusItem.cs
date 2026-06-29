using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WavenVoIP.Models
{
    public enum StatusRamal
    {
        Desconhecido = 0,
        Offline = 1,
        Online = 2,
        Tocando = 3,
        Chamando = 4,
        EmLigacao = 5,
        Indisponivel = 6
    }

    public class RamalStatusItem : INotifyPropertyChanged
    {
        private StatusRamal _status = StatusRamal.Desconhecido;
        private string _numeroRemoto = string.Empty;
        private string _nomeRemoto = string.Empty;
        private bool _direcaoEntrada;
        private DateTime? _inicioLigacao;
        private string _canal = string.Empty;
        private string _linhaUsada = string.Empty;
        private string _nomeCanal = string.Empty;
        private string _tipoChamada = string.Empty;

        public string Ramal { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;

        public StatusRamal Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnChanged();
                OnChanged(nameof(StatusTexto));
                OnChanged(nameof(CorStatus));
                OnChanged(nameof(CorStatusFundo));
                OnChanged(nameof(CorStatusBorda));
                OnChanged(nameof(EstaEmLigacao));
                OnChanged(nameof(InfoLigacao));
                OnChanged(nameof(SortKey));
                OnChanged(nameof(Opacidade));
            }
        }

        public int SortKey => _status switch
        {
            StatusRamal.EmLigacao    => 0,
            StatusRamal.Chamando     => 1,
            StatusRamal.Tocando      => 2,
            StatusRamal.Online       => 3,
            StatusRamal.Indisponivel => 4,
            StatusRamal.Offline      => 5,
            _                        => 6
        };

        public string NumeroRemoto
        {
            get => _numeroRemoto;
            set { if (_numeroRemoto == value) return; _numeroRemoto = value; OnChanged(); OnChanged(nameof(InfoLigacao)); }
        }

        public string NomeRemoto
        {
            get => _nomeRemoto;
            set { if (_nomeRemoto == value) return; _nomeRemoto = value; OnChanged(); OnChanged(nameof(InfoLigacao)); }
        }

        public bool DirecaoEntrada
        {
            get => _direcaoEntrada;
            set
            {
                if (_direcaoEntrada == value) return;
                _direcaoEntrada = value;
                OnChanged();
                OnChanged(nameof(DirecaoTexto));
                OnChanged(nameof(InfoLigacao));
                OnChanged(nameof(CorStatus));
                OnChanged(nameof(CorStatusFundo));
                OnChanged(nameof(CorStatusBorda));
                OnChanged(nameof(CorDirecao));
                OnChanged(nameof(LabelNumero));
                OnChanged(nameof(LabelLinha));
            }
        }

        public DateTime? InicioLigacao
        {
            get => _inicioLigacao;
            set { _inicioLigacao = value; OnChanged(); OnChanged(nameof(InfoLigacao)); }
        }

        public string Canal
        {
            get => _canal;
            set { if (_canal == value) return; _canal = value; OnChanged(); }
        }

        public string LinhaUsada
        {
            get => _linhaUsada;
            set
            {
                if (_linhaUsada == value) return;
                _linhaUsada = value;
                OnChanged();
                OnChanged(nameof(InfoLinha));
                OnChanged(nameof(TemInfoLinha));
            }
        }

        public string NomeCanal
        {
            get => _nomeCanal;
            set
            {
                if (_nomeCanal == value) return;
                _nomeCanal = value;
                OnChanged();
                OnChanged(nameof(InfoLinha));
                OnChanged(nameof(TemInfoLinha));
            }
        }

        public string TipoChamada
        {
            get => _tipoChamada;
            set
            {
                if (_tipoChamada == value) return;
                _tipoChamada = value;
                OnChanged();
                OnChanged(nameof(DirecaoTexto));
            }
        }

        // ── Computed ──────────────────────────────────────────────────────────

        // Cards offline e indisponível ficam levemente transparentes
        public double Opacidade => _status is StatusRamal.Offline or StatusRamal.Indisponivel ? 0.62 : 1.0;

        public bool EstaEmLigacao =>
            _status == StatusRamal.EmLigacao ||
            _status == StatusRamal.Chamando ||
            _status == StatusRamal.Tocando;

        public string StatusTexto => _status switch
        {
            StatusRamal.Online       => "Livre",
            StatusRamal.Offline      => "Offline",
            StatusRamal.Tocando      => "Tocando",
            StatusRamal.Chamando     => "Chamando",
            StatusRamal.EmLigacao    => "Em ligação",
            StatusRamal.Indisponivel => "Indisponível",
            _                        => "Desconhecido"
        };

        public string DirecaoTexto => _tipoChamada == "interna"
            ? (_direcaoEntrada ? "↙ Entrada interna" : "↗ Saída interna")
            : (_direcaoEntrada ? "↙ Entrada" : "↗ Saída");

        // Rótulos conforme direção da chamada
        public string LabelNumero => _direcaoEntrada ? "Origem:" : "Destino:";
        public string LabelLinha  => _direcaoEntrada ? "Entrou por:" : "Linha usada:";

        // Nome amigável da linha/canal: combina NomeCanal e LinhaUsada quando ambos disponíveis
        public string InfoLinha =>
            !string.IsNullOrWhiteSpace(_nomeCanal) && !string.IsNullOrWhiteSpace(_linhaUsada)
                ? $"{_nomeCanal} · {_linhaUsada}"
                : !string.IsNullOrWhiteSpace(_nomeCanal)   ? _nomeCanal
                : !string.IsNullOrWhiteSpace(_linhaUsada)  ? _linhaUsada
                : string.Empty;

        public bool TemInfoLinha => !string.IsNullOrWhiteSpace(InfoLinha);

        public string Duracao
        {
            get
            {
                if (_inicioLigacao == null || !EstaEmLigacao) return string.Empty;
                var ts = DateTime.Now - _inicioLigacao.Value;
                return ts.TotalHours >= 1
                    ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
            }
        }

        public string InfoLigacao
        {
            get
            {
                if (!EstaEmLigacao) return string.Empty;
                var quem = !string.IsNullOrWhiteSpace(_nomeRemoto) &&
                           !string.Equals(_nomeRemoto, _numeroRemoto, StringComparison.OrdinalIgnoreCase)
                    ? $"{_nomeRemoto} ({_numeroRemoto})"
                    : _numeroRemoto;

                var dur = Duracao;
                if (!string.IsNullOrWhiteSpace(quem) && !string.IsNullOrWhiteSpace(dur))
                    return $"{quem}  •  {dur}";
                if (!string.IsNullOrWhiteSpace(quem)) return quem;
                if (!string.IsNullOrWhiteSpace(dur))  return dur;
                return string.Empty;
            }
        }

        // Cor do badge de direção: azul (entrada) ou laranja (saída)
        public Brush CorDirecao => _direcaoEntrada
            ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
            : new SolidColorBrush(Color.FromRgb(234, 88, 12));

        // Status dot — EmLigacao diferencia entrada (azul) vs saída (laranja)
        public Brush CorStatus => _status switch
        {
            StatusRamal.Online       => new SolidColorBrush(Color.FromRgb(22, 163, 74)),
            StatusRamal.Offline      => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            StatusRamal.Tocando      => new SolidColorBrush(Color.FromRgb(217, 119, 6)),
            StatusRamal.Chamando     => new SolidColorBrush(Color.FromRgb(234, 88, 12)),
            StatusRamal.EmLigacao    => _direcaoEntrada
                ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
                : new SolidColorBrush(Color.FromRgb(234, 88, 12)),
            StatusRamal.Indisponivel => new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            _                        => new SolidColorBrush(Color.FromRgb(148, 163, 184))
        };

        public Brush CorStatusFundo => _status switch
        {
            StatusRamal.Online       => new SolidColorBrush(Color.FromRgb(240, 253, 244)),
            StatusRamal.Offline      => new SolidColorBrush(Color.FromRgb(254, 242, 242)),
            StatusRamal.Tocando      => new SolidColorBrush(Color.FromRgb(255, 247, 237)),
            StatusRamal.Chamando     => new SolidColorBrush(Color.FromRgb(255, 247, 237)),
            StatusRamal.EmLigacao    => _direcaoEntrada
                ? new SolidColorBrush(Color.FromRgb(239, 246, 255))
                : new SolidColorBrush(Color.FromRgb(255, 247, 237)),
            StatusRamal.Indisponivel => new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            _                        => new SolidColorBrush(Color.FromRgb(248, 250, 252))
        };

        public Brush CorStatusBorda => _status switch
        {
            StatusRamal.Online       => new SolidColorBrush(Color.FromRgb(134, 239, 172)),
            StatusRamal.Offline      => new SolidColorBrush(Color.FromRgb(252, 165, 165)),
            StatusRamal.Tocando      => new SolidColorBrush(Color.FromRgb(253, 186, 116)),
            StatusRamal.Chamando     => new SolidColorBrush(Color.FromRgb(253, 186, 116)),
            StatusRamal.EmLigacao    => _direcaoEntrada
                ? new SolidColorBrush(Color.FromRgb(147, 197, 253))
                : new SolidColorBrush(Color.FromRgb(253, 186, 116)),
            StatusRamal.Indisponivel => new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            _                        => new SolidColorBrush(Color.FromRgb(203, 213, 225))
        };

        public void NotificarDuracao()
        {
            if (EstaEmLigacao)
            {
                OnChanged(nameof(Duracao));
                OnChanged(nameof(InfoLigacao));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
