using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WavenVoIP.Views
{
    public partial class DtmfPadWindow : Window
    {
        private readonly Func<string, bool> _enviarDtmf;

        public DtmfPadWindow(Func<string, bool> enviarDtmf)
        {
            InitializeComponent();
            _enviarDtmf = enviarDtmf;
            PreviewTextInput += DtmfPadWindow_PreviewTextInput;
            PreviewKeyDown += DtmfPadWindow_PreviewKeyDown;
            Focus();
        }

        private void DtmfPadWindow_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Text) && "0123456789*#".Contains(e.Text))
            {
                Enviar(e.Text);
                e.Handled = true;
            }
        }

        private void DtmfPadWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }

        private void Tecla_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag != null)
                Enviar(b.Tag.ToString() ?? string.Empty);
        }

        private void Enviar(string digito)
        {
            if (string.IsNullOrWhiteSpace(digito)) return;

            // Exibição é apenas visual: não depende do retorno do envio,
            // que continua sendo disparado da mesma forma de antes.
            txtEnviado.Text += digito;

            _enviarDtmf(digito);
        }

        private void Limpar_Click(object sender, RoutedEventArgs e)
        {
            txtEnviado.Text = string.Empty;
        }

        private void Fechar_Click(object sender, RoutedEventArgs e) => Close();
    }
}
