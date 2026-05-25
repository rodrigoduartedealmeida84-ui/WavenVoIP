using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class ContactsTab : UserControl
    {
        private List<Contato> _contatos = new();

        public ContactsTab()
        {
            InitializeComponent();
            Carregar();
        }

        private void Carregar()
        {
            _contatos = ContatoStorageService.Carregar();
            gridContatos.ItemsSource = null;
            gridContatos.ItemsSource = _contatos;
        }

        private void BtnSalvar_Click(object sender, RoutedEventArgs e)
        {
            var nome = txtNome.Text?.Trim() ?? string.Empty;
            var numero = txtNumero.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(numero))
            {
                MessageBox.Show("Preencha nome e número.", "Waven VoIP");
                return;
            }

            _contatos.Add(new Contato { Nome = nome, Numero = numero });
            ContatoStorageService.Salvar(_contatos);
            Carregar();

            txtNome.Clear();
            txtNumero.Clear();
        }
    }
}
