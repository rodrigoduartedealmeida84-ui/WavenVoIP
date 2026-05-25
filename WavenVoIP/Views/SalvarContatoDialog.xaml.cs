using System;
using System.Linq;
using System.Windows;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class SalvarContatoDialog : Window
    {
        private readonly Contato? _contatoOriginal;
        private readonly bool _modoEdicao;

        public SalvarContatoDialog(string numeroPreencher = "")
        {
            InitializeComponent();
            _modoEdicao = false;
            txtNumero.Text = numeroPreencher;
            Loaded += (_, __) => txtNome.Focus();
            MouseLeftButtonDown += (_, e) => { try { DragMove(); } catch { } };
        }

        public SalvarContatoDialog(Contato contato)
        {
            InitializeComponent();
            _contatoOriginal = contato;
            _modoEdicao = true;

            txtNome.Text = contato.Nome;
            txtNumero.Text = contato.Numero;
            txtObservacao.Text = contato.Observacao;

            if (contato.FonteGoogle)
            {
                txtDialogTitulo.Text = "Contato Google";
                txtDialogSubtitulo.Text = "Contatos Google são somente leitura. Você pode salvar uma cópia local.";
                bannerGoogle.Visibility = Visibility.Visible;
                btnSalvar.Content = "Salvar cópia local";
            }
            else
            {
                txtDialogTitulo.Text = "Editar contato";
                txtDialogSubtitulo.Text = "Atualize os dados do contato.";
            }

            Loaded += (_, __) => txtNome.Focus();
            MouseLeftButtonDown += (_, e) => { try { DragMove(); } catch { } };
        }

        private void BtnSalvar_Click(object sender, RoutedEventArgs e)
        {
            var nome = txtNome.Text?.Trim() ?? string.Empty;
            var numero = new string((txtNumero.Text ?? "").Where(char.IsDigit).ToArray());
            var obs = txtObservacao.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(nome))
            {
                MessageBox.Show("Preencha o nome do contato.", "Salvar contato", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtNome.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(numero))
            {
                MessageBox.Show("Preencha o número do contato.", "Salvar contato", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtNumero.Focus();
                return;
            }

            var contatos = ContatoStorageService.Carregar();

            if (_modoEdicao && _contatoOriginal != null)
            {
                var numOriginal = new string((_contatoOriginal.Numero ?? "").Where(char.IsDigit).ToArray());
                var ehGoogle = _contatoOriginal.FonteGoogle;

                if (ehGoogle)
                {
                    // Google → salvar como local; se já existe local com mesmo número, atualizar
                    var existenteLocal = contatos.FirstOrDefault(c =>
                        !c.FonteGoogle && new string((c.Numero ?? "").Where(char.IsDigit).ToArray()) == numero);

                    if (existenteLocal != null)
                    {
                        existenteLocal.Nome = nome;
                        existenteLocal.Observacao = string.IsNullOrWhiteSpace(obs) ? existenteLocal.Observacao : obs;
                        existenteLocal.AtualizadoEm = DateTime.Now;
                    }
                    else
                    {
                        contatos.Add(new Contato
                        {
                            Nome = nome,
                            Numero = numero,
                            Observacao = string.IsNullOrWhiteSpace(obs) ? "Cópia local" : obs,
                            EhRamalIssabel = false,
                            FonteGoogle = false,
                            AtualizadoEm = DateTime.Now
                        });
                    }
                }
                else
                {
                    // Editar contato local in-place
                    var original = contatos.FirstOrDefault(c =>
                        !c.FonteGoogle && new string((c.Numero ?? "").Where(char.IsDigit).ToArray()) == numOriginal);

                    if (original != null)
                    {
                        // Se o número mudou e já existe outro local com o novo número, avisar
                        if (numero != numOriginal)
                        {
                            var conflito = contatos.FirstOrDefault(c =>
                                c != original && !c.FonteGoogle &&
                                new string((c.Numero ?? "").Where(char.IsDigit).ToArray()) == numero);
                            if (conflito != null)
                            {
                                MessageBox.Show($"O número {numero} já está salvo como '{conflito.Nome}'.", "Editar contato", MessageBoxButton.OK, MessageBoxImage.Information);
                                return;
                            }
                        }
                        original.Nome = nome;
                        original.Numero = numero;
                        original.Observacao = obs;
                        original.AtualizadoEm = DateTime.Now;
                    }
                    else
                    {
                        // Contato não encontrado (edge case) — adicionar como novo
                        contatos.Add(new Contato { Nome = nome, Numero = numero, Observacao = obs, AtualizadoEm = DateTime.Now });
                    }
                }
            }
            else
            {
                // Novo contato — verificar duplicado
                if (ContatoStorageService.ExisteNumero(numero))
                {
                    var nomeExistente = ContatoStorageService.ResolverNomePorNumero(numero);
                    MessageBox.Show($"O número {numero} já está salvo como '{nomeExistente}'.", "Salvar contato", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                contatos.Add(new Contato
                {
                    Nome = nome,
                    Numero = numero,
                    Observacao = obs,
                    EhRamalIssabel = false,
                    AtualizadoEm = DateTime.Now
                });
            }

            ContatoStorageService.Salvar(contatos);
            DialogResult = true;
            Close();
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
