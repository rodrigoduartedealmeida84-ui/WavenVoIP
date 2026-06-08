using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class WhatsAppMessageWindow : Window
    {
        private readonly string _tipoEvento;
        private readonly string _numeroOriginal;

        public WhatsAppMessageWindow(string numero, string mensagemPadrao = "", string tipoEvento = "manual", string nome = "")
        {
            InitializeComponent();
            _tipoEvento    = tipoEvento;
            _numeroOriginal = numero;

            var temNome = !string.IsNullOrWhiteSpace(nome);

            if (temNome)
            {
                panelContatoNome.Visibility  = Visibility.Visible;
                panelNumeroManual.Visibility = Visibility.Collapsed;

                txtNomeContato.Text   = nome;
                txtAvatarInicial.Text = ObterInicial(nome);

                var normalizado = WhatsAppService.NormalizarTelefoneParaEnvio(numero);
                txtNumeroFormatado.Text   = FormatarTelefoneDisplay(normalizado);
                txtNumeroNormalizado.Text = "Será enviado para: " + normalizado;
            }
            else
            {
                panelContatoNome.Visibility  = Visibility.Collapsed;
                panelNumeroManual.Visibility = Visibility.Visible;

                txtNumero.Text = WhatsAppService.RemoverPrefixoRota(numero);
                AtualizarNumeroNormalizado();
                txtNumero.TextChanged += (_, _) => AtualizarNumeroNormalizado();
            }
        }

        private void AtualizarNumeroNormalizado()
        {
            try
            {
                var n = WhatsAppService.NormalizarTelefoneParaEnvio(txtNumero.Text);
                txtNumeroNormalizado.Text       = "Será enviado para: " + n;
                txtNumeroNormalizado.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74));
            }
            catch
            {
                txtNumeroNormalizado.Text       = "Informe o número com DDD.";
                txtNumeroNormalizado.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            }
        }

        private async void BtnEnviar_Click(object sender, RoutedEventArgs e)
        {
            var numero = panelNumeroManual.Visibility == Visibility.Visible
                ? txtNumero.Text
                : _numeroOriginal;

            btnEnviar.IsEnabled   = false;
            txtRetorno.Text       = "Enviando template...";
            txtRetorno.Foreground = (Brush)FindResource("MutedBrush");

            try
            {
                var resultado = await WhatsAppService.EnviarTemplateWabaAsync(numero, _tipoEvento);

                if (resultado.Bloqueado)
                {
                    txtRetorno.Text       = resultado.Debug;
                    txtRetorno.Foreground = new SolidColorBrush(Color.FromRgb(202, 138, 4));
                    MessageBox.Show(resultado.Debug, "Waven VoIP — Anti-spam");
                    return;
                }

                if (resultado.Sucesso)
                {
                    txtRetorno.Text       = $"✔ Template WhatsApp enviado. Aguarde o cliente responder para continuar o atendimento.";
                    txtRetorno.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                    MessageBox.Show(
                        "Template WhatsApp enviado.\nAguarde o cliente responder para continuar o atendimento.",
                        "Waven VoIP");
                }
                else
                {
                    var mensagemErro = ObterMensagemErro(resultado.HttpStatusCode, resultado.Debug);
                    txtRetorno.Text       = $"✘ {mensagemErro}";
                    txtRetorno.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                    MessageBox.Show(mensagemErro, "Waven VoIP — Erro no envio");
                }
            }
            catch (Exception ex)
            {
                txtRetorno.Text       = $"✘ {ex.Message}";
                txtRetorno.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            }
            finally
            {
                btnEnviar.IsEnabled = true;
            }
        }

        private static string ObterMensagemErro(int httpCode, string debug)
        {
            if (httpCode == 0 && debug.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                return "API Waven Chat indisponível (timeout). Verifique a conexão.";
            if (httpCode == 0 && !string.IsNullOrWhiteSpace(debug))
                return debug;

            return httpCode switch
            {
                401  => "Token inválido ou expirado. Verifique o Bearer Token em Configurações.",
                404  => "Canal ou endpoint inválido. Verifique a URL da API em Configurações.",
                429  => "Limite de envio atingido. Aguarde antes de tentar novamente.",
                500  => "Erro interno da API Waven Chat. Tente novamente em alguns instantes.",
                >= 500 => $"Erro interno da API (HTTP {httpCode}). Tente novamente.",
                _    => $"Falha no envio (HTTP {httpCode}). {debug}"
            };
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e) => Close();

        private static string ObterInicial(string nome)
        {
            var partes = nome.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length >= 2)
                return $"{char.ToUpper(partes[0][0])}{char.ToUpper(partes[1][0])}";
            if (partes.Length == 1 && partes[0].Length > 0)
                return char.ToUpper(partes[0][0]).ToString();
            return "?";
        }

        private static string FormatarTelefoneDisplay(string numero)
        {
            var d = new string(numero.Where(char.IsDigit).ToArray());
            if (d.StartsWith("55") && d.Length > 10) d = d[2..];
            return d.Length switch
            {
                11 => $"({d[..2]}) {d[2..7]}-{d[7..]}",
                10 => $"({d[..2]}) {d[2..6]}-{d[6..]}",
                _  => numero
            };
        }
    }
}
