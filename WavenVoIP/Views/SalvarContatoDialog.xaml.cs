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

        public bool AdicionadoAosFavoritos { get; private set; }

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
            var nome   = txtNome.Text?.Trim() ?? string.Empty;
            var numero = new string((txtNumero.Text ?? "").Where(char.IsDigit).ToArray());
            var obs    = txtObservacao.Text?.Trim() ?? string.Empty;

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

            // Normalize before saving — removes +55, adds 9th digit for old mobiles
            var numeroNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(numero);
            if (string.IsNullOrWhiteSpace(numeroNorm)) numeroNorm = numero;

            var contatos = ContatoStorageService.Carregar();

            if (_modoEdicao && _contatoOriginal != null)
            {
                var numOriginal = new string((_contatoOriginal.Numero ?? "").Where(char.IsDigit).ToArray());
                var numOrigNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(numOriginal);
                if (string.IsNullOrWhiteSpace(numOrigNorm)) numOrigNorm = numOriginal;
                var ehGoogle = _contatoOriginal.FonteGoogle;

                if (ehGoogle)
                {
                    // Google → salvar como local; se já existe local com mesmo número normalizado, atualizar
                    var existenteLocal = contatos.FirstOrDefault(c =>
                    {
                        if (c.FonteGoogle) return false;
                        var cn    = SomenteDigitos(c.Numero);
                        var cnNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(cn);
                        return cn == numero || cnNorm == numeroNorm;
                    });

                    if (existenteLocal != null)
                    {
                        existenteLocal.Nome        = nome;
                        existenteLocal.Numero      = numeroNorm;
                        existenteLocal.Observacao  = string.IsNullOrWhiteSpace(obs) ? existenteLocal.Observacao : obs;
                        existenteLocal.AtualizadoEm = DateTime.Now;
                    }
                    else
                    {
                        contatos.Add(new Contato
                        {
                            Nome         = nome,
                            Numero       = numeroNorm,
                            Observacao   = string.IsNullOrWhiteSpace(obs) ? "Cópia local" : obs,
                            EhRamalIssabel = false,
                            FonteGoogle  = false,
                            AtualizadoEm = DateTime.Now
                        });
                    }
                }
                else
                {
                    // Editar contato local in-place
                    var original = contatos.FirstOrDefault(c =>
                    {
                        if (c.FonteGoogle) return false;
                        var cn    = SomenteDigitos(c.Numero);
                        var cnNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(cn);
                        return cn == numOriginal || cnNorm == numOrigNorm;
                    });

                    if (original != null)
                    {
                        // If number changed, check for normalized conflict
                        if (numeroNorm != numOrigNorm)
                        {
                            var conflito = contatos.FirstOrDefault(c =>
                            {
                                if (c == original || c.FonteGoogle) return false;
                                var cn    = SomenteDigitos(c.Numero);
                                var cnNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(cn);
                                return cn == numero || cnNorm == numeroNorm;
                            });
                            if (conflito != null)
                            {
                                MessageBox.Show($"O número {numeroNorm} já está salvo como '{conflito.Nome}'.", "Editar contato", MessageBoxButton.OK, MessageBoxImage.Information);
                                return;
                            }
                        }
                        original.Nome        = nome;
                        original.Numero      = numeroNorm;
                        original.Observacao  = obs;
                        original.AtualizadoEm = DateTime.Now;
                    }
                    else
                    {
                        // Edge case: not found — add as new
                        contatos.Add(new Contato { Nome = nome, Numero = numeroNorm, Observacao = obs, AtualizadoEm = DateTime.Now });
                    }
                }
            }
            else
            {
                // Novo contato — check by normalized number; update existing instead of blocking
                var existenteNorm = contatos.FirstOrDefault(c =>
                {
                    if (c.EhRamalIssabel) return false;
                    var cn    = SomenteDigitos(c.Numero);
                    var cnNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(cn);
                    return cn == numero || cnNorm == numeroNorm;
                });

                if (existenteNorm != null)
                {
                    existenteNorm.Nome        = nome;
                    existenteNorm.Numero      = numeroNorm;
                    if (!string.IsNullOrWhiteSpace(obs)) existenteNorm.Observacao = obs;
                    existenteNorm.AtualizadoEm = DateTime.Now;
                    ContatoStorageService.Salvar(contatos);

                    if (chkFavoritos?.IsChecked == true)
                    {
                        var adicionado = FavoritesStorageService.Adicionar(new Models.FavoriteItem
                            { Nome = nome, Numero = numeroNorm, Favorito = true });
                        AdicionadoAosFavoritos = adicionado;
                    }

                    MessageBox.Show("Contato atualizado com sucesso!", "Salvar contato",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                    return;
                }

                contatos.Add(new Contato
                {
                    Nome         = nome,
                    Numero       = numeroNorm,
                    Observacao   = obs,
                    EhRamalIssabel = false,
                    AtualizadoEm = DateTime.Now
                });
            }

            ContatoStorageService.Salvar(contatos);

            if (chkFavoritos?.IsChecked == true)
            {
                var adicionado = FavoritesStorageService.Adicionar(new Models.FavoriteItem
                {
                    Nome     = nome,
                    Numero   = numeroNorm,
                    Favorito = true
                });
                AdicionadoAosFavoritos = adicionado;
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static string SomenteDigitos(string? valor) =>
            string.IsNullOrWhiteSpace(valor) ? "" : new string(valor.Where(char.IsDigit).ToArray());
    }
}
