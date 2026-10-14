using System;
using System.Windows;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    // v2.5.0 — aviso fixo de chamada não atendida. Fica na tela até o usuário fechar, retornar
    // ou a ligação ser resolvida (o mesmo número foi atendido depois). O botão WhatsApp só abre
    // a conversa: não fecha o aviso nem baixa a pendência.
    public partial class MissedCallPopup : Window
    {
        public string CallerNumber { get; }
        public HistoricoLigacaoItem? Item { get; private set; }
        public string ChaveNumero { get; }
        public int Quantidade { get; private set; } = 1;

        // Aviso de demonstração (WavenVoIP.exe /demo-naoatendida): mesmo visual e mesmos textos,
        // mas os botões só dizem o que fariam. Nenhum evento sai daqui: não liga, não abre
        // conversa e não mexe no Histórico.
        public bool Demonstracao
        {
            get => _demonstracao;
            set { _demonstracao = value; bdDemo.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
        }
        private bool _demonstracao;

        public event Action<string>? RetornarSolicitado;
        public event Action<string>? WhatsAppSolicitado;
        public event Action? VerHistoricoSolicitado;

        public MissedCallPopup(HistoricoLigacaoItem item)
            : this(item.NumeroLimpoVisual, item.NomeExibido, item.DataHora, item.CanalEntrada,
                   NaoAtendidasPolicy.Motivo(item))
        {
            Item = item;
        }

        public MissedCallPopup(string callerNumber, string callerName, DateTime horario, string canalEntrada,
                               NaoAtendidaMotivo motivo = NaoAtendidaMotivo.Perdida)
        {
            InitializeComponent();
            CallerNumber = callerNumber;
            ChaveNumero = MissedCallPolicy.NumeroChave(callerNumber);
            var numDisplay = PhoneNumberNormalizer.NormalizeForDisplay(callerNumber);
            if (string.IsNullOrWhiteSpace(numDisplay)) numDisplay = callerNumber;
            var semNome = string.IsNullOrWhiteSpace(callerName) ||
                          string.Equals(callerName, callerNumber, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(callerName, numDisplay, StringComparison.OrdinalIgnoreCase);
            txtCallerName.Text = semNome ? "Cliente sem cadastro" : callerName;
            txtCallerNumber.Text = NaoAtendidasPolicy.FormatarTelefone(numDisplay);
            Preencher(horario, canalEntrada, motivo);
            Loaded += (_, _) => PosicionarBottomRight(0);
        }

        // Outra chamada não atendida do mesmo número com o aviso ainda aberto: atualiza o aviso
        // existente (horário da última e quantidade) em vez de abrir uma segunda janela.
        public void RegistrarNovaChamada(HistoricoLigacaoItem item)
        {
            Item = item;
            Quantidade++;
            Preencher(item.DataHora, item.CanalEntrada, NaoAtendidasPolicy.Motivo(item));
        }

        private void Preencher(DateTime horario, string canalEntrada, NaoAtendidaMotivo motivo)
        {
            var rotulo = motivo switch
            {
                NaoAtendidaMotivo.ClienteDesistiu => "Cliente desistiu",
                NaoAtendidaMotivo.AbandonoFila => "Abandono na fila",
                _ => "Perdida"
            };
            txtMotivo.Text = Quantidade > 1 ? $"{rotulo} · {Quantidade} chamadas deste número" : rotulo;
            txtHorario.Text = FormatarHorario(horario);

            var canal = (canalEntrada ?? string.Empty).Trim();
            txtCanal.Text = string.IsNullOrWhiteSpace(canal) ? "Não identificado" : canal;

            var saida = NaoAtendidasPolicy.SaidaDeRetorno(canal);
            txtCanalRetorno.Text = saida.HasValue
                ? $"Retorno pelo canal: {NaoAtendidasPolicy.NomeDoCanal(saida.Value)}"
                : "Canal não identificado: o retorno pede a escolha";
            btnRetornar.ToolTip = saida.HasValue
                ? $"Ligar de volta pelo canal {NaoAtendidasPolicy.NomeDoCanal(saida.Value)}"
                : "Ligar de volta (será preciso escolher o canal)";
        }

        internal static string FormatarHorario(DateTime horario)
        {
            if (horario == default) return "—";
            var hoje = DateTime.Today;
            if (horario.Date == hoje) return $"Hoje às {horario:HH:mm}";
            if (horario.Date == hoje.AddDays(-1)) return $"Ontem às {horario:HH:mm}";
            return $"{horario:dd/MM} às {horario:HH:mm}";
        }

        // deslocamento = altura já ocupada por outros avisos empilhados acima da borda inferior.
        // coluna = quando a pilha não cabe na altura da tela, os avisos seguintes vão para a esquerda.
        public void PosicionarBottomRight(double deslocamento, int coluna = 0)
        {
            var area = SystemParameters.WorkArea;
            var altura = ActualHeight > 0 ? ActualHeight : 250;
            Left = Math.Max(area.Left, area.Right - Width - 10 - coluna * (Width - 12));
            Top  = Math.Max(area.Top, area.Bottom - altura - 10 - deslocamento);
        }

        private void AvisarDemonstracao(string texto)
        {
            txtCanalRetorno.Text = texto;
            txtCanalRetorno.ToolTip = texto;
            txtCanalRetorno.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7C, 0x3A, 0xED));
            txtCanalRetorno.FontWeight = FontWeights.SemiBold;
        }

        private void BtnRetornar_Click(object sender, RoutedEventArgs e)
        {
            if (Demonstracao)
            {
                var saida = NaoAtendidasPolicy.SaidaDeRetorno(Item?.CanalEntrada);
                AvisarDemonstracao(saida.HasValue
                    ? $"Simulação: ligaria por {NaoAtendidasPolicy.NomeDoCanal(saida.Value)}"
                    : "Simulação: pediria a escolha do canal");
                return;
            }
            RetornarSolicitado?.Invoke(CallerNumber);
            Close();
        }

        // Só abre a conversa — o aviso continua na tela e a pendência continua contando.
        private void BtnWhatsApp_Click(object sender, RoutedEventArgs e)
        {
            if (Demonstracao)
            {
                var destino = NaoAtendidasPolicy.DestinoWhatsApp(CallerNumber);
                AvisarDemonstracao(destino != null ? $"Simulação: conversa com {destino}" : "Simulação: número sem WhatsApp");
                return;
            }
            WhatsAppSolicitado?.Invoke(CallerNumber);
        }

        private void BtnVerHistorico_Click(object sender, RoutedEventArgs e)
        {
            if (Demonstracao) { AvisarDemonstracao("Simulação: abriria o Histórico"); return; }
            VerHistoricoSolicitado?.Invoke();
            Close();
        }

        private void BtnFechar_Click(object sender, RoutedEventArgs e) => Close();
    }
}
