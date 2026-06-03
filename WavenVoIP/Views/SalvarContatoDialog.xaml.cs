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
                txtDialogSubtitulo.Text = "Este contato será salvo como contato compartilhado da empresa pela Waven API e ficará visível para todos os ramais.";
                bannerGoogle.Visibility = Visibility.Visible;
                btnSalvar.Content = "Salvar na empresa";
            }
            else
            {
                txtDialogTitulo.Text = "Editar contato";
                txtDialogSubtitulo.Text = "Atualize os dados do contato.";
            }

            Loaded += (_, __) => txtNome.Focus();
            MouseLeftButtonDown += (_, e) => { try { DragMove(); } catch { } };
        }

        private async void BtnSalvar_Click(object sender, RoutedEventArgs e)
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
                    // Google → converter para contato da empresa; se já existe local com mesmo número, atualizar
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
                        existenteLocal.AtualizadoEm = DateTime.UtcNow;
                        if (string.IsNullOrWhiteSpace(existenteLocal.GoogleContactId))
                            existenteLocal.GoogleContactId = _contatoOriginal.GoogleContactId;
                    }
                    else
                    {
                        contatos.Add(new Contato
                        {
                            Nome            = nome,
                            Numero          = numeroNorm,
                            Observacao      = string.IsNullOrWhiteSpace(obs) ? "Contato empresa" : obs,
                            EhRamalIssabel  = false,
                            FonteGoogle     = false,
                            AtualizadoEm    = DateTime.UtcNow,
                            GoogleContactId = _contatoOriginal.GoogleContactId
                        });
                    }

                    ContatoStorageService.Salvar(contatos);

                    // Se API ativa, criar na Waven API para distribuir para todos os ramais
                    var cfgGoogle = SipConfig.CarregarSalva();
                    if (cfgGoogle?.UsarWavenApi == true && !string.IsNullOrWhiteSpace(cfgGoogle.WavenApiToken))
                    {
                        LogHelper.Info($"CONTACT_UPDATE_API_START | googleId={_contatoOriginal.GoogleContactId} nome={nome}");
                        var (apiOk, apiContact, _) = await WavenApiService.CriarContatoAsync(
                            nome, numeroNorm, empresa: null, observacao: obs, ramal: cfgGoogle.Ramal)
                            .ConfigureAwait(true);

                        if (apiOk && apiContact != null)
                        {
                            // Vincula WavenApiId ao contato local recém-criado
                            var todosAtual = ContatoStorageService.Carregar();
                            var localCopy = todosAtual.FirstOrDefault(c =>
                                !c.FonteGoogle && c.GoogleContactId == _contatoOriginal.GoogleContactId);
                            if (localCopy != null)
                            {
                                localCopy.WavenApiId = apiContact.Id;
                                ContatoStorageService.Salvar(todosAtual);
                            }
                            LogHelper.Info($"GOOGLE_CONTACT_CONVERTED_TO_SHARED | googleId={_contatoOriginal.GoogleContactId} wavenId={apiContact.Id} nome={nome}");
                            LogHelper.Info($"CONTACT_UPDATE_API_OK | id={apiContact.Id} nome={nome}");
                        }
                        else if (apiOk)
                        {
                            // 409 duplicado — WavenApiId será vinculado na próxima sync
                            LogHelper.Info($"GOOGLE_CONTACT_CONVERTED_TO_SHARED | googleId={_contatoOriginal.GoogleContactId} wavenId=pending_sync nome={nome}");
                            LogHelper.Info($"CONTACT_UPDATE_API_OK | nome={nome} motivo=duplicate_already_exists");
                        }
                        else
                        {
                            WavenApiSyncService.EnfileirarOffline(new Models.WavenApiOfflineOp
                            {
                                Op      = "CREATE",
                                Payload = System.Text.Json.JsonSerializer.Serialize(
                                    new { nome, numero = numeroNorm, observacao = obs, criadoPor = cfgGoogle.Ramal }),
                                Ramal   = cfgGoogle.Ramal
                            });
                            LogHelper.Info($"CONTACT_UPDATE_API_FAIL | googleId={_contatoOriginal.GoogleContactId} nome={nome} motivo=offline_queued");
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(_contatoOriginal.GoogleContactId))
                    {
                        LogHelper.Info($"GOOGLE_CONTACT_CONVERTED_TO_SHARED | googleId={_contatoOriginal.GoogleContactId} wavenId=none_api_disabled nome={nome}");
                    }

                    if (chkFavoritos?.IsChecked == true)
                    {
                        var adicionado = FavoritesStorageService.Adicionar(new Models.FavoriteItem
                            { Nome = nome, Numero = numeroNorm, Favorito = true });
                        AdicionadoAosFavoritos = adicionado;
                    }

                    DialogResult = true;
                    Close();
                    return;
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
                        // Se número mudou, verificar conflito
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
                        original.AtualizadoEm = DateTime.UtcNow;
                    }
                    else
                    {
                        contatos.Add(new Contato { Nome = nome, Numero = numeroNorm, Observacao = obs, AtualizadoEm = DateTime.UtcNow });
                    }

                    ContatoStorageService.Salvar(contatos);

                    // Push para API se for contato compartilhado
                    if (!string.IsNullOrWhiteSpace(_contatoOriginal.WavenApiId))
                    {
                        var cfg = SipConfig.CarregarSalva();
                        if (cfg?.UsarWavenApi == true && !string.IsNullOrWhiteSpace(cfg.WavenApiToken))
                        {
                            LogHelper.Info($"CONTACT_UPDATE_API_START | id={_contatoOriginal.WavenApiId} nome={nome}");
                            var ok = await WavenApiService.AtualizarContatoAsync(
                                _contatoOriginal.WavenApiId, nome,
                                empresa: null, observacao: obs,
                                ramal: cfg.Ramal, atualizadoEm: DateTime.UtcNow)
                                .ConfigureAwait(true);

                            if (ok)
                            {
                                LogHelper.Info($"CONTACT_UPDATE_API_OK | id={_contatoOriginal.WavenApiId} nome={nome}");
                            }
                            else
                            {
                                WavenApiSyncService.EnfileirarOffline(new Models.WavenApiOfflineOp
                                {
                                    Op        = "UPDATE",
                                    ContactId = _contatoOriginal.WavenApiId,
                                    Payload   = System.Text.Json.JsonSerializer.Serialize(new
                                    {
                                        nome, observacao = obs,
                                        atualizadoEm  = DateTime.UtcNow,
                                        atualizadoPor = cfg.Ramal
                                    }),
                                    Ramal = cfg.Ramal
                                });
                                LogHelper.Info($"CONTACT_UPDATE_API_FAIL | id={_contatoOriginal.WavenApiId} nome={nome} motivo=offline_queued");
                            }
                        }
                    }

                    if (chkFavoritos?.IsChecked == true)
                    {
                        var adicionado = FavoritesStorageService.Adicionar(new Models.FavoriteItem
                            { Nome = nome, Numero = numeroNorm, Favorito = true });
                        AdicionadoAosFavoritos = adicionado;
                    }

                    DialogResult = true;
                    Close();
                    return;
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
                    existenteNorm.AtualizadoEm = DateTime.UtcNow;
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
                    AtualizadoEm = DateTime.UtcNow
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
