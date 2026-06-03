using System.Windows;
using System.Windows.Media;

namespace WavenVoIP.Views
{
    public enum SharedContactWarningModo { Editar, Excluir, EditarGoogle, ExcluirGoogle }
    public enum GoogleDeleteOpcao { Cancelado, SomenteDaEmpresa, ExcluirDoGoogle }

    public partial class SharedContactWarningDialog : Window
    {
        private readonly SharedContactWarningModo _modo;

        public bool Confirmado { get; private set; }
        public GoogleDeleteOpcao OpcaoGoogleDelete { get; private set; } = GoogleDeleteOpcao.Cancelado;

        public SharedContactWarningDialog(SharedContactWarningModo modo, bool origemGoogle = false)
        {
            InitializeComponent();
            _modo = modo;
            MouseLeftButtonDown += (_, e) => { try { DragMove(); } catch { } };

            switch (modo)
            {
                case SharedContactWarningModo.Excluir:
                    txtIcone.Text  = "\U0001F5D1";
                    txtTitulo.Text = "Excluir contato compartilhado";
                    txtMensagem.Text =
                        "Você está prestes a excluir um contato compartilhado da empresa.\n\n" +
                        "Esta ação removerá o contato para TODOS os usuários e TODOS os ramais " +
                        "conectados ao WavenVoIP.";
                    btnAcao.Content    = "Excluir contato";
                    btnAcao.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

                    if (origemGoogle)
                    {
                        bannerGoogle.Visibility = Visibility.Visible;
                        txtMensagemGoogle.Text =
                            "Este contato teve origem no Google Contacts. A exclusão remove apenas " +
                            "o contato da empresa — o contato continuará existindo no Google e " +
                            "poderá ser importado novamente no futuro.";
                    }
                    break;

                case SharedContactWarningModo.Editar:
                    txtIcone.Text  = "✏️";
                    txtTitulo.Text = "Editar contato compartilhado";
                    txtMensagem.Text =
                        "Este contato é compartilhado entre todos os usuários da empresa.\n\n" +
                        "Qualquer alteração realizada será sincronizada automaticamente para " +
                        "todos os ramais.\n\n" +
                        "Deseja continuar?";
                    btnAcao.Content    = "Continuar edição";
                    btnAcao.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
                    break;

                case SharedContactWarningModo.EditarGoogle:
                    txtIcone.Text  = "✏️";
                    txtTitulo.Text = "Editar contato Google";
                    txtMensagem.Text =
                        "Este contato tem origem no Google Contacts.\n\n" +
                        "Ao editar, este contato será salvo como contato compartilhado da empresa " +
                        "pela Waven API e ficará visível para todos os ramais.\n\n" +
                        "O contato original no Google Contacts não será alterado.";
                    btnAcao.Content    = "Editar contato";
                    btnAcao.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
                    break;

                case SharedContactWarningModo.ExcluirGoogle:
                    txtIcone.Text  = "\U0001F5D1";
                    txtTitulo.Text = "Excluir contato Google";
                    txtMensagem.Text =
                        "Este contato tem origem no Google Contacts.\n\n" +
                        "Escolha como deseja excluí-lo:";

                    btnAcaoSecundario.Content    = "Somente da empresa";
                    btnAcaoSecundario.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    btnAcaoSecundario.Visibility = Visibility.Visible;

                    btnAcao.Content    = "Do Google e da empresa";
                    btnAcao.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

                    bannerGoogle.Visibility = Visibility.Visible;
                    txtMensagemGoogle.Text =
                        "Somente da empresa: remove da Waven API. O contato Google original permanece e não voltará graças ao tombstone.\n" +
                        "Do Google e da empresa: remove permanentemente do Google Contacts e da Waven API.";
                    break;
            }
        }

        private void BtnAcao_Click(object sender, RoutedEventArgs e)
        {
            if (_modo == SharedContactWarningModo.ExcluirGoogle)
                OpcaoGoogleDelete = GoogleDeleteOpcao.ExcluirDoGoogle;
            Confirmado   = true;
            DialogResult = true;
            Close();
        }

        private void BtnAcaoSecundario_Click(object sender, RoutedEventArgs e)
        {
            OpcaoGoogleDelete = GoogleDeleteOpcao.SomenteDaEmpresa;
            Confirmado   = true;
            DialogResult = true;
            Close();
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            OpcaoGoogleDelete = GoogleDeleteOpcao.Cancelado;
            Confirmado   = false;
            DialogResult = false;
            Close();
        }
    }
}
