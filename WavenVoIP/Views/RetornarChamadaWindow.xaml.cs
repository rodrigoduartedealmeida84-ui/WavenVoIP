using System.Windows;
using System.Windows.Media;
using WavenVoIP.Models;

namespace WavenVoIP.Views
{
    // v2.3.6 — resultado da confirmação exibida pelo botão "Ligar" do Histórico, para chamadas com
    // canal identificado com segurança (Recebida/Perdida via CanalEntrada; Realizada/Recusada/
    // NaoAtendida via OrigemSaida — ver DialerShellWindow.ResolverSaidaHistoricoAsync).
    public enum RetornarChamadaResultado
    {
        Cancelado,
        UsarMesmoCanal,
        EscolherOutro
    }

    public partial class RetornarChamadaWindow : Window
    {
        public RetornarChamadaResultado Resultado { get; private set; } = RetornarChamadaResultado.Cancelado;

        // Paleta local desta janela — acompanha a identidade visual já usada no restante do Waven
        // (roxo = Operadora/marca, verde = WhatsApp, azul = 0800) sem depender/alterar
        // CanalIdentificacaoService.BrushCanal (que serve o Histórico e outras telas).
        private static readonly Color CorPadrao = Color.FromRgb(0x64, 0x74, 0x8B);

        private static Color CorDoCanal(string canal)
        {
            if (string.IsNullOrWhiteSpace(canal)) return CorPadrao;
            if (canal.Equals("Operadora", System.StringComparison.OrdinalIgnoreCase))
                return Color.FromRgb(0x7C, 0x3A, 0xED);
            if (canal.Equals("WhatsApp Vivo", System.StringComparison.OrdinalIgnoreCase))
                return Color.FromRgb(0x0D, 0x94, 0x88);
            if (canal.IndexOf("WhatsApp", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return Color.FromRgb(0x16, 0xA3, 0x4A);
            if (canal.Equals("0800", System.StringComparison.OrdinalIgnoreCase))
                return Color.FromRgb(0x02, 0x84, 0xC7);
            return CorPadrao;
        }

        // Concordância de gênero em português para "Ligar pel{a|o} <canal>" — Operadora é feminina
        // ("pela Operadora"); WhatsApp é tratado como masculino ("pelo WhatsApp TIM"/"pelo WhatsApp
        // Vivo"), uso corrente em português do Brasil.
        private static string PreposicaoDoCanal(string canal)
            => canal.Equals("Operadora", System.StringComparison.OrdinalIgnoreCase) ? "pela" : "pelo";

        // v2.3.6 — construtor único, cobre todos os cenários:
        //
        //   canalExibido = o canal REALMENTE identificado (o que aparece no indicador colorido —
        //                  ex.: "0800"). Nunca é inventado; sempre vem de CanalEntrada (chamadas
        //                  Recebida/Perdida) ou OrigemSaida (Realizada/Recusada/NaoAtendida).
        //   canalAcao    = o canal que SERÁ discado se o operador clicar no botão principal — igual
        //                  a canalExibido em quase todos os casos, EXCETO 0800 (canal só de
        //                  ENTRADA, sem rota de saída própria): nesse caso canalAcao é sempre
        //                  "Operadora" e canalExibido continua "0800" (nunca inventa outra coisa
        //                  para o indicador, só define qual rota real será usada ao confirmar).
        //   tipo         = decide a frase ("este cliente ligou" vs "esta chamada/tentativa foi
        //                  realizada") — nunca trata uma chamada de SAÍDA como se o cliente
        //                  tivesse ligado, e vice-versa.
        //
        // Não recebe SaidaChamada — o chamador (DialerShellWindow) já sabe qual rota vai discar se
        // o resultado for UsarMesmoCanal; esta janela só precisa comunicar/decidir visualmente.
        public RetornarChamadaWindow(string nomeCliente, string numero, TipoHistoricoLigacao tipo,
            string canalExibido, string canalAcao)
        {
            InitializeComponent();

            txtNomeCliente.Text = string.IsNullOrWhiteSpace(nomeCliente) ? numero : nomeCliente;
            txtNumeroCliente.Text = numero;
            txtNumeroCliente.Visibility = string.Equals(txtNomeCliente.Text, numero, System.StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed
                : Visibility.Visible;

            txtCanal.Text = canalExibido;
            var corExibida = CorDoCanal(canalExibido);
            txtCanal.Foreground = new SolidColorBrush(corExibida);
            dotCanal.Fill = new SolidColorBrush(corExibida);

            var eh0800 = canalExibido.Equals("0800", System.StringComparison.OrdinalIgnoreCase);

            txtLinha1.Text = tipo switch
            {
                TipoHistoricoLigacao.Recebida => "Este cliente ligou pelo canal:",
                TipoHistoricoLigacao.Perdida  => "Este cliente ligou pelo canal:",
                TipoHistoricoLigacao.Realizada => "Esta chamada foi realizada pelo canal:",
                _ /* Recusada, NaoAtendida — tentativas de saída */ => "Esta tentativa foi realizada pelo canal:"
            };

            if (eh0800)
            {
                txtPergunta.Visibility = Visibility.Collapsed;
                txtAviso.Visibility = Visibility.Visible;
                txtAviso.Text = "O 0800 é somente de entrada.\nPara retornar esta chamada, será utilizada a Operadora.";
            }
            else
            {
                txtAviso.Visibility = Visibility.Collapsed;
                txtPergunta.Visibility = Visibility.Visible;
                txtPergunta.Text = tipo switch
                {
                    TipoHistoricoLigacao.Recebida  => "Deseja retornar pelo mesmo canal?",
                    TipoHistoricoLigacao.Perdida   => "Deseja retornar pelo mesmo canal?",
                    TipoHistoricoLigacao.Realizada => "Deseja ligar novamente pelo mesmo canal?",
                    _ => "Deseja tentar novamente pelo mesmo canal?"
                };
            }

            btnUsarMesmoCanal.Content = $"Ligar {PreposicaoDoCanal(canalAcao)} {canalAcao}";
            btnUsarMesmoCanal.Background = new SolidColorBrush(CorDoCanal(canalAcao));
        }

        private void BtnUsarMesmoCanal_Click(object sender, RoutedEventArgs e) => Fechar(RetornarChamadaResultado.UsarMesmoCanal);
        private void BtnEscolherOutro_Click(object sender, RoutedEventArgs e) => Fechar(RetornarChamadaResultado.EscolherOutro);
        private void BtnFechar_Click(object sender, RoutedEventArgs e) => Fechar(RetornarChamadaResultado.Cancelado);

        private void Fechar(RetornarChamadaResultado resultado)
        {
            Resultado = resultado;
            try { DialogResult = resultado != RetornarChamadaResultado.Cancelado; }
            catch { try { Close(); } catch { } }
        }
    }
}
