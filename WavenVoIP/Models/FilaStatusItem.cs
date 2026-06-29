using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WavenVoIP.Models
{
    public class FilaStatusItem : INotifyPropertyChanged
    {
        private int _aguardando;
        private int _agentesOnline;
        private int _agentesEmAtendimento;
        private int _agentesPausados;
        private int _agentesOffline;

        public string Fila  { get; set; } = string.Empty;
        public string Nome  { get; set; } = string.Empty;
        public int    Atendidas  { get; set; }
        public int    Abandonadas { get; set; }
        public int    TempoMedioEspera { get; set; }
        public int    AgentesTotal { get; set; }

        public ObservableCollection<ApiAmiQueueMember> Agentes  { get; } = new();
        public ObservableCollection<ApiAmiQueueEntry>  Clientes { get; } = new();

        public int Aguardando
        {
            get => _aguardando;
            set { _aguardando = value; Notify(); Notify(nameof(CorFundoCard)); Notify(nameof(CorBordaCard)); Notify(nameof(CorAguardando)); }
        }

        public int AgentesOnline
        {
            get => _agentesOnline;
            set { _agentesOnline = value; Notify(); }
        }

        public int AgentesEmAtendimento
        {
            get => _agentesEmAtendimento;
            set { _agentesEmAtendimento = value; Notify(); }
        }

        public int AgentesPausados
        {
            get => _agentesPausados;
            set { _agentesPausados = value; Notify(); }
        }

        public int AgentesOffline
        {
            get => _agentesOffline;
            set { _agentesOffline = value; Notify(); }
        }

        // Cor do card baseada em chamadas aguardando
        public Brush CorFundoCard => Aguardando == 0
            ? new SolidColorBrush(Color.FromRgb(240, 253, 244))   // verde claro
            : Aguardando <= 3
                ? new SolidColorBrush(Color.FromRgb(255, 251, 235)) // amarelo claro
                : new SolidColorBrush(Color.FromRgb(254, 242, 242)); // vermelho claro

        public Brush CorBordaCard => Aguardando == 0
            ? new SolidColorBrush(Color.FromRgb(134, 239, 172))
            : Aguardando <= 3
                ? new SolidColorBrush(Color.FromRgb(252, 211, 77))
                : new SolidColorBrush(Color.FromRgb(252, 165, 165));

        public Brush CorAguardando => Aguardando == 0
            ? new SolidColorBrush(Color.FromRgb(21, 128, 61))
            : Aguardando <= 3
                ? new SolidColorBrush(Color.FromRgb(180, 83, 9))
                : new SolidColorBrush(Color.FromRgb(185, 28, 28));

        public string TextoAguardando => Aguardando == 0 ? "Sem fila" : $"{Aguardando} aguardando";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? p = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
