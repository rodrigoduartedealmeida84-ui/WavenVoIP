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
                // Modo contato: mostra avatar + nome, esconde campo editável
                panelContatoNome.Visibility  = Visibility.Visible;
                panelNumeroManual.Visibility = Visibility.Collapsed;

                txtNomeContato.Text   = nome;
                txtAvatarInicial.Text = ObterInicial(nome);

                var normalizado = WhatsAppService.NormalizarTelefoneParaEnvio(numero);
                txtNumeroFormatado.Text     = FormatarTelefoneDisplay(normalizado);
                txtNumeroNormalizado.Text   = "Será enviado para: " + normalizado;
            }
            else
            {
                // Modo manual: esconde avatar, mostra campo editável
                panelContatoNome.Visibility  = Visibility.Collapsed;
                panelNumeroManual.Visibility = Visibility.Visible;

                txtNumero.Text = WhatsAppService.RemoverPrefixoRota(numero);
                AtualizarNumeroNormalizado();
                txtNumero.TextChanged += (_, _) => AtualizarNumeroNormalizado();
            }

            txtMensagem.Text = string.IsNullOrWhiteSpace(mensagemPadrao)
                ? "Olá! Estamos falando com você pelo Grupo Almeida Gás."
                : mensagemPadrao;

            AtualizarContador();
            txtMensagem.TextChanged += (_, _) => AtualizarContador();
        }

        private void AtualizarContador()
        {
            var len = txtMensagem.Text?.Length ?? 0;
            txtContadorChars.Text = $"{len} / 1000";
            txtContadorChars.Foreground = len > 900
                ? new SolidColorBrush(Color.FromRgb(185, 28, 28))
                : (Brush)FindResource("MutedBrush");
        }

        private void AtualizarNumeroNormalizado()
        {
            try
            {
                var n = WhatsAppService.NormalizarTelefoneParaEnvio(txtNumero.Text);
                txtNumeroNormalizado.Text = "Será enviado para: " + n;
                txtNumeroNormalizado.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74));
            }
            catch
            {
                txtNumeroNormalizado.Text = "Informe o número com DDD.";
                txtNumeroNormalizado.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            }
        }

        private async void BtnEnviar_Click(object sender, RoutedEventArgs e)
        {
            var numero = panelNumeroManual.Visibility == Visibility.Visible
                ? txtNumero.Text
                : _numeroOriginal;

            btnEnviar.IsEnabled = false;
            txtRetorno.Text       = "Enviando...";
            txtRetorno.Foreground = (Brush)FindResource("MutedBrush");
            try
            {
                var resultado = await WhatsAppService.EnviarMensagemAsync(numero, txtMensagem.Text, _tipoEvento);
                if (resultado.Sucesso)
                {
                    txtRetorno.Text       = $"✔ Mensagem enviada. HTTP {resultado.HttpStatusCode} · {resultado.NumeroNormalizado}";
                    txtRetorno.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                    MessageBox.Show("Mensagem enviada com sucesso.", "Waven VoIP");
                }
                else
                {
                    txtRetorno.Text       = $"✘ Falha no envio. HTTP {resultado.HttpStatusCode}\n{resultado.Debug}\n{resultado.RespostaBruta}";
                    txtRetorno.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
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
