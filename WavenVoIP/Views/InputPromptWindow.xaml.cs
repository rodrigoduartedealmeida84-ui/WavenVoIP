using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class InputPromptWindow : Window
    {
        public string ValorDigitado { get; private set; } = string.Empty;
        public bool Confirmado { get; private set; }
        private List<Contato> _contatos = new();

        public InputPromptWindow(string titulo, string valorInicial = "")
        {
            InitializeComponent();
            txtTitulo.Text = titulo;
            txtValor.Text = valorInicial;
            CarregarContatos();
            txtValor.SelectAll();
            txtValor.Focus();
            PreviewKeyDown += InputPromptWindow_PreviewKeyDown;
        }

        private void CarregarContatos()
        {
            _contatos = ContatoStorageService.Carregar();
            AtualizarListaContatos();
        }

        private void AtualizarListaContatos()
        {
            var busca = txtBuscaContato?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            var lista = string.IsNullOrWhiteSpace(busca)
                ? _contatos
                : _contatos.Where(c => (c.Nome ?? "").ToLowerInvariant().Contains(busca) || (c.Numero ?? "").Contains(busca)).ToList();

            lstContatos.ItemsSource = null;
            lstContatos.ItemsSource = lista;
        }

        private void TxtBuscaContato_TextChanged(object sender, TextChangedEventArgs e) => AtualizarListaContatos();

        private void UsarContato_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string numero)
            {
                txtValor.Text = PhoneNumberNormalizer.NormalizeForDial(numero);
                txtValor.CaretIndex = txtValor.Text.Length;
                txtValor.Focus();
            }
        }

        private void LstContatos_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstContatos.SelectedItem is Contato c && !string.IsNullOrWhiteSpace(c.Numero))
            {
                txtValor.Text = PhoneNumberNormalizer.NormalizeForDial(c.Numero);
                txtValor.CaretIndex = txtValor.Text.Length;
                txtValor.Focus();
            }
        }

        private void Tecla_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button botao && botao.Content != null)
            {
                txtValor.Text += botao.Content.ToString();
                txtValor.CaretIndex = txtValor.Text.Length;
                txtValor.Focus();
            }
        }

        private void BtnApagar_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(txtValor.Text))
            {
                txtValor.Text = txtValor.Text.Substring(0, txtValor.Text.Length - 1);
                txtValor.CaretIndex = txtValor.Text.Length;
            }
            txtValor.Focus();
        }

        private void InputPromptWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Confirmar();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Confirmado = false;
                FecharSeguro(false);
                e.Handled = true;
            }
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e) { Confirmado = false; FecharSeguro(false); }
        private void BtnConfirmar_Click(object sender, RoutedEventArgs e) => Confirmar();

        private void Confirmar()
        {
            ValorDigitado = txtValor.Text?.Trim() ?? string.Empty;
            Confirmado = true;
            FecharSeguro(true);
        }

        private void FecharSeguro(bool resultado)
        {
            // Não define DialogResult para evitar InvalidOperationException quando a janela
            // for aberta fora de ShowDialog. O resultado fica salvo em Confirmado.
            try { Close(); } catch { }
        }

    }
}
