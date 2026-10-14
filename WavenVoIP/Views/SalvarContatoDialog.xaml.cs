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
                    FavoritesStorageService.AtualizarNomesPorContatos(contatos);

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
                            // já existia na API com outro nome: o nome editado agora passa a valer lá também
                            if (!ContatoSyncCoordinator.NomesIguais(apiContact.Nome, nome))
                            {
                                var ren = await WavenApiService.AtualizarContatoAsync(apiContact.Id, nome, empresa: apiContact.Empresa,
                                    observacao: obs, ramal: cfgGoogle.Ramal ?? "", atualizadoEm: DateTime.UtcNow).ConfigureAwait(true);
                                LogHelper.Info($"CONTACT_UPDATE_API_RENAMED | id={apiContact.Id} de={apiContact.Nome} para={nome} ok={ren}");
                            }
                            // Vincula WavenApiId ao contato local recém-criado
                            var todosAtual = ContatoStorageService.Carregar();
                            var localCopy = todosAtual.FirstOrDefault(c =>
                                !c.FonteGoogle && c.GoogleContactId == _contatoOriginal.GoogleContactId);
                            if (localCopy != null)
                            {
                                localCopy.WavenApiId = apiContact.Id;
                                ContatoStorageService.Salvar(todosAtual);
                                FavoritesStorageService.AtualizarNomesPorContatos(todosAtual);
                            }
                            LogHelper.Info($"GOOGLE_CONTACT_CONVERTED_TO_SHARED | googleId={_contatoOriginal.GoogleContactId} wavenId={apiContact.Id} nome={nome}");
                            LogHelper.Info($"CONTACT_UPDATE_API_OK | id={apiContact.Id} nome={nome}");
                            LogHelper.Info($"FAVORITE_CONTACT_SYNC_BROADCAST | id={apiContact.Id} nome={nome}");
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

                    await SincronizarEdicaoComGoogleAsync(numeroNorm, nome);
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
                    FavoritesStorageService.AtualizarNomesPorContatos(contatos);

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
                                LogHelper.Info($"FAVORITE_CONTACT_SYNC_BROADCAST | id={_contatoOriginal.WavenApiId} nome={nome}");
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

                    await SincronizarEdicaoComGoogleAsync(numeroNorm, nome);
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
                    FavoritesStorageService.AtualizarNomesPorContatos(contatos);

                    if (chkFavoritos?.IsChecked == true)
                    {
                        var adicionado = FavoritesStorageService.Adicionar(new Models.FavoriteItem
                            { Nome = nome, Numero = numeroNorm, Favorito = true, ContactId = existenteNorm.WavenApiId });
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
            LogHelper.Info($"CONTACT_SAVE_OK | nome={nome} numero={numeroNorm}");

            // Sincroniza novo contato com Waven API para que todos os ramais o vejam
            string? novoContactId = null;
            var cfgNovo = SipConfig.CarregarSalva();
            if (cfgNovo?.UsarWavenApi == true && !string.IsNullOrWhiteSpace(cfgNovo.WavenApiToken))
            {
                LogHelper.Info($"CONTACT_SAVE_API_START | nome={nome} numero={numeroNorm}");
                var (apiOk, apiContact, _) = await WavenApiService.CriarContatoAsync(
                    nome, numeroNorm, empresa: null, observacao: obs, ramal: cfgNovo.Ramal)
                    .ConfigureAwait(true);

                if (apiOk && apiContact != null)
                {
                    novoContactId = apiContact.Id;

                    // Vincula WavenApiId para que edições futuras sejam propagadas
                    var todosAtual = ContatoStorageService.Carregar();
                    var localCopy = todosAtual.FirstOrDefault(c =>
                        !c.EhRamalIssabel && !c.FonteGoogle &&
                        new string(c.Numero.Where(char.IsDigit).ToArray()) == numeroNorm);
                    if (localCopy != null && string.IsNullOrWhiteSpace(localCopy.WavenApiId))
                    {
                        localCopy.WavenApiId = apiContact.Id;
                        ContatoStorageService.Salvar(todosAtual);
                    }
                    // já existia na API com outro nome/observação: o que o usuário acabou de salvar
                    // passa a valer para todos os computadores
                    if (!string.Equals(apiContact.Nome, nome, StringComparison.Ordinal))
                    {
                        var renomeou = await WavenApiService.AtualizarContatoAsync(
                            apiContact.Id, nome, empresa: apiContact.Empresa, observacao: obs,
                            ramal: cfgNovo.Ramal ?? "", atualizadoEm: DateTime.UtcNow).ConfigureAwait(true);
                        LogHelper.Info($"CONTACT_SAVE_API_RENAMED | id={apiContact.Id} de={apiContact.Nome} para={nome} ok={renomeou}");
                    }
                    LogHelper.Info($"CONTACT_SAVE_API_OK | id={apiContact.Id} nome={nome}");
                }
                else if (!apiOk)
                {
                    WavenApiSyncService.EnfileirarOffline(new Models.WavenApiOfflineOp
                    {
                        Op      = "CREATE",
                        Payload = System.Text.Json.JsonSerializer.Serialize(
                            new { nome, numero = numeroNorm, observacao = obs, criadoPor = cfgNovo.Ramal }),
                        Ramal   = cfgNovo.Ramal
                    });
                    LogHelper.Info($"CONTACT_SAVE_API_FAIL | nome={nome} motivo=offline_queued");
                }
            }

            if (chkFavoritos?.IsChecked == true)
            {
                LogHelper.Info($"FAVORITE_SAVE_START | nome={nome} numero={numeroNorm}");
                var adicionado = FavoritesStorageService.Adicionar(new Models.FavoriteItem
                {
                    Nome      = nome,
                    Numero    = numeroNorm,
                    Favorito  = true,
                    ContactId = novoContactId
                });
                AdicionadoAosFavoritos = adicionado;
                if (adicionado) LogHelper.Info($"FAVORITE_SAVE_OK | nome={nome} numero={numeroNorm}");
            }

            DialogResult = true;
            Close();
        }

        // v2.5.0 — depois de editar um contato que também existe no Google: marca que o Google
        // está com o nome antigo (para ele não sobrescrever a edição), tenta enviar o nome novo e,
        // se o WavenVoIP só tem leitura no Google, avisa e oferece a autorização.
        private async System.Threading.Tasks.Task SincronizarEdicaoComGoogleAsync(string numeroNorm, string nome)
        {
            try
            {
                var marcou = ContatoStorageService.ComArquivoTravado(() =>
                {
                    var todos = ContatoStorageService.Carregar();
                    var c = todos.FirstOrDefault(x => !x.FonteGoogle && !x.EhRamalIssabel && !string.IsNullOrWhiteSpace(x.GoogleContactId) &&
                        PhoneNumberNormalizer.NormalizeBrazilPhone(SomenteDigitos(x.Numero)) == numeroNorm);
                    if (c == null) return false;
                    var nomeNoGoogle = _contatoOriginal != null && _contatoOriginal.FonteGoogle ? _contatoOriginal.Nome : null;
                    if (nomeNoGoogle != null && ContatoSyncCoordinator.NomesIguais(nomeNoGoogle, nome)) return false;
                    c.SincronizacaoPendente = ContatoSyncCoordinator.PendenteGoogle;
                    ContatoStorageService.Salvar(todos);
                    return true;
                });
                if (!marcou) return;

                var (_, semPermissao) = await ContatoSyncCoordinator.EnviarPendentesGoogleAsync().ConfigureAwait(true);
                if (semPermissao == 0) return;

                var resp = MessageBox.Show(this,
                    "O nome foi salvo no WavenVoIP e não será mais substituído pelo nome antigo do Google.\n\n" +
                    "No Google Contacts ele continua com o nome antigo, porque o WavenVoIP só tem permissão de leitura na sua conta Google.\n\n" +
                    "Deseja autorizar agora a edição dos contatos no Google? (abre o navegador)",
                    "Contato não enviado ao Google", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
                if (resp != MessageBoxResult.Yes) return;

                var autorizou = await GoogleContactsService.AutorizarEscritaAsync().ConfigureAwait(true);
                if (!autorizou)
                {
                    MessageBox.Show(this, "A permissão de edição não foi concedida. O contato continua salvo no WavenVoIP e marcado como não enviado ao Google.",
                        "Google Contacts", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var (enviados, aindaSem) = await ContatoSyncCoordinator.EnviarPendentesGoogleAsync().ConfigureAwait(true);
                MessageBox.Show(this, aindaSem == 0 ? $"Pronto: {enviados} contato(s) atualizado(s) no Google Contacts." : "Não foi possível atualizar o Google Contacts agora. O contato continua marcado como não enviado.",
                    "Google Contacts", MessageBoxButton.OK, aindaSem == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex) { LogHelper.Info($"CONTACT_GOOGLE_SYNC_AFTER_EDIT_ERROR | {ex.Message}"); }
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
