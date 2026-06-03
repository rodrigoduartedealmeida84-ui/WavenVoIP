using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WavenVoIP.Models;
using WavenVoIP.Services;

namespace WavenVoIP.Views
{
    public partial class ContactsWindow : Window
    {
        private List<Contato> _cacheContatos = new();
        private List<Contato> _contatos = new();
        private readonly Func<string, Task>? _onCallRequested;
        private readonly Func<string, Task>? _onAddToCallRequested;
        private readonly bool _modoChamada;
        private readonly Action<string>? _onWhatsAppRequested;

        private DispatcherTimer? _debounceTimer;

        private Contato? _pendingDelete;
        private bool     _pendingDeleteUsandoApi;
        private string?  _pendingDeleteWavenApiId;
        private string?  _pendingDeleteRamal;
        private bool     _pendingDeleteGoogleDelete;
        private string?  _pendingDeleteGoogleId;
        private DispatcherTimer? _undoTimer;

        public ContactsWindow(
            Func<string, Task>? onCallRequested = null,
            Func<string, Task>? onAddToCallRequested = null,
            bool modoChamada = false,
            Action<string>? onWhatsAppRequested = null)
        {
            InitializeComponent();
            _onCallRequested      = onCallRequested;
            _onAddToCallRequested = onAddToCallRequested;
            _modoChamada          = modoChamada;
            _onWhatsAppRequested  = onWhatsAppRequested;

            if (_modoChamada)
            {
                txtTitulo.Text    = "Contatos da chamada";
                txtSubtitulo.Text = "Escolha um contato para ligar ou adicionar na chamada/conferência.";
            }

            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _debounceTimer.Tick += (_, _) =>
            {
                _debounceTimer.Stop();
                AtualizarListaFiltrada(txtBusca.Text?.Trim() ?? string.Empty);
            };

            _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _undoTimer.Tick += (_, _) =>
            {
                _undoTimer.Stop();
                _ = ConfirmarExclusaoDefinitiva();
            };

            CarregarCache();
        }

        // ── Cache & Busca ─────────────────────────────────────────────────────────

        private void CarregarCache()
        {
            var todos = ContatoStorageService.Carregar();

            // Deduplica por número normalizado: local > AMI > Google
            var vistos = new Dictionary<string, Contato>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in todos)
            {
                var key = PhoneNumberNormalizer.NormalizeForSearch(c.Numero);
                if (string.IsNullOrWhiteSpace(key))
                    key = $"_nome_{(c.Nome ?? string.Empty).ToLowerInvariant()}";

                if (!vistos.TryGetValue(key, out var existente) || PrioridadeContato(c) > PrioridadeContato(existente))
                    vistos[key] = c;
            }

            _cacheContatos = vistos.Values.OrderBy(c => c.Nome).ToList();
            AtualizarListaFiltrada(txtBusca?.Text?.Trim() ?? string.Empty);
        }

        private void AtualizarListaFiltrada(string busca)
        {
            if (busca.Length >= 3)
            {
                LogHelper.Info($"SEARCH_START | query={busca}");
                var sw = Stopwatch.StartNew();

                var buscaNorm    = NormalizarTexto(busca);
                var buscaDigitos = PhoneNumberNormalizer.NormalizeForSearch(busca);

                _contatos = _cacheContatos.Where(c =>
                {
                    if (c.PendingDelete) return false;
                    if (NormalizarTexto(c.Nome).Contains(buscaNorm, StringComparison.Ordinal)) return true;
                    if (!string.IsNullOrWhiteSpace(buscaDigitos) &&
                        PhoneNumberNormalizer.NormalizeForSearch(c.Numero)
                            .Contains(buscaDigitos, StringComparison.Ordinal)) return true;
                    if (!string.IsNullOrWhiteSpace(c.Observacao) &&
                        NormalizarTexto(c.Observacao).Contains(buscaNorm, StringComparison.Ordinal)) return true;
                    return false;
                }).ToList();

                sw.Stop();
                LogHelper.Info($"SEARCH_END | query={busca} resultado={_contatos.Count} tempo={sw.ElapsedMilliseconds}ms");
            }
            else
            {
                _contatos = _cacheContatos.Where(c => !c.PendingDelete).ToList();
            }

            gridContatos.ItemsSource = null;
            gridContatos.ItemsSource = _contatos;
            txtContagemContatos.Text = _contatos.Count == 1 ? "1 contato" : $"{_contatos.Count} contatos";
        }

        // Remove acentos, espaços e caracteres especiais — deixa só letras/dígitos minúsculos
        private static string NormalizarTexto(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return string.Empty;
            var decomposed = texto.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToLowerInvariant(c));
            }
            return new string(sb.ToString().Where(c => char.IsLetterOrDigit(c)).ToArray());
        }

        private static int PrioridadeContato(Contato c)
        {
            if (!c.FonteGoogle && !c.EhRamalIssabel) return 2; // local
            if (c.EhRamalIssabel) return 1;                    // AMI ramal
            return 0;                                           // Google
        }

        private void TxtBusca_TextChanged(object sender, TextChangedEventArgs e)
        {
            _debounceTimer?.Stop();
            plcBusca.Visibility = string.IsNullOrEmpty(txtBusca.Text) ? Visibility.Visible : Visibility.Collapsed;

            var busca = txtBusca.Text?.Trim() ?? string.Empty;
            if (busca.Length < 3)
            {
                // Menos de 3 chars → mostra todos imediatamente (sem debounce)
                AtualizarListaFiltrada(busca);
                return;
            }
            // 3+ chars → aguarda 300ms de pausa para buscar
            _debounceTimer?.Start();
        }

        // ── Salvar novo contato ───────────────────────────────────────────────────

        private async void BtnSalvar_Click(object sender, RoutedEventArgs e)
        {
            var nome   = txtNome.Text?.Trim()  ?? string.Empty;
            var numero = txtNumero.Text?.Trim() ?? string.Empty;
            var obs    = txtObs.Text?.Trim()    ?? string.Empty;

            if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(numero))
            {
                MessageBox.Show("Preencha nome e número.", "Waven VoIP");
                return;
            }

            var cfg = SipConfig.CarregarSalva();
            if (cfg?.UsarWavenApi == true && !string.IsNullOrWhiteSpace(cfg.WavenApiToken))
            {
                var confirm = MessageBox.Show(
                    "Esta alteração será aplicada para todos os usuários da empresa. Deseja continuar?",
                    "Alteração global de contatos",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

                var (ok, apiContact, _) = await WavenApiService.CriarContatoAsync(
                    nome, numero, empresa: null, observacao: obs, ramal: cfg.Ramal).ConfigureAwait(true);

                if (ok && apiContact != null)
                    LogHelper.Info($"API_CONTACT_CREATED | id={apiContact.Id} nome={nome}");
                else if (!ok)
                {
                    LogHelper.Info($"API_CONTACT_OFFLINE_QUEUE_SAVED | op=CREATE nome={nome}");
                    WavenApiSyncService.EnfileirarOffline(new WavenApiOfflineOp
                    {
                        Op      = "CREATE",
                        Payload = System.Text.Json.JsonSerializer.Serialize(
                            new { nome, numero, observacao = obs, criadoPor = cfg.Ramal }),
                        Ramal = cfg.Ramal
                    });
                }
            }

            var numNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(
                new string(numero.Where(char.IsDigit).ToArray()));
            var todos = ContatoStorageService.Carregar();
            todos.Add(new Contato { Nome = nome, Numero = numNorm, Observacao = obs, AtualizadoEm = DateTime.Now });
            ContatoStorageService.Salvar(todos);
            CarregarCache();
            txtNome.Clear(); txtNumero.Clear(); txtObs.Clear();
        }

        // ── Ligar / WhatsApp ──────────────────────────────────────────────────────

        private void BtnLigarContato_Click(object sender, RoutedEventArgs e)
        {
            if (_onCallRequested == null) return;
            if (sender is FrameworkElement fe && fe.Tag is string numero && !string.IsNullOrWhiteSpace(numero))
                AgendarLigacaoAposFechar(numero);
        }

        private async void BtnAdicionarContato_Click(object sender, RoutedEventArgs e)
        {
            if (_onAddToCallRequested == null)
            {
                MessageBox.Show("Abra os contatos pela janela da chamada para adicionar na conferência.", "Waven VoIP");
                return;
            }
            if (sender is FrameworkElement fe && fe.Tag is string numero && !string.IsNullOrWhiteSpace(numero))
            {
                await _onAddToCallRequested(numero);
                Close();
            }
        }

        private void BtnWhatsAppContato_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            if (fe.Tag is not string numero || string.IsNullOrWhiteSpace(numero)) return;
            var nome = _contatos.FirstOrDefault(c => c.Numero == numero)?.Nome ?? string.Empty;

            if (_onWhatsAppRequested != null)
            {
                Close();
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => _onWhatsAppRequested(numero)));
            }
            else
                new WhatsAppMessageWindow(numero, string.Empty, "contato", nome) { Owner = this }.ShowDialog();
        }

        // ── Editar contato ────────────────────────────────────────────────────────

        private void BtnEditarContato_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not Contato contato) return;

            var cfg       = SipConfig.CarregarSalva();
            var usandoApi = cfg?.UsarWavenApi == true && !string.IsNullOrWhiteSpace(cfg.WavenApiToken);

            if (contato.FonteGoogle)
            {
                var warn = new SharedContactWarningDialog(SharedContactWarningModo.EditarGoogle) { Owner = this };
                warn.ShowDialog();
                if (!warn.Confirmado) return;
            }
            else if (usandoApi && contato.EhCompartilhado)
            {
                var warn = new SharedContactWarningDialog(SharedContactWarningModo.Editar) { Owner = this };
                warn.ShowDialog();
                if (!warn.Confirmado) return;
            }

            var dlg = new SalvarContatoDialog(contato) { Owner = this };
            if (dlg.ShowDialog() == true)
                CarregarCache();
        }

        // ── Excluir com Undo ──────────────────────────────────────────────────────

        private async void BtnExcluirContato_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not Contato contato) return;

            var cfg       = SipConfig.CarregarSalva();
            var usandoApi = cfg?.UsarWavenApi == true && !string.IsNullOrWhiteSpace(cfg.WavenApiToken);

            bool excluirDoGoogle = false;

            if (contato.FonteGoogle)
            {
                // Todos os contatos Google (com ou sem GoogleContactId) usam o dialog específico
                var dlg = new SharedContactWarningDialog(SharedContactWarningModo.ExcluirGoogle) { Owner = this };
                dlg.ShowDialog();
                if (dlg.OpcaoGoogleDelete == GoogleDeleteOpcao.Cancelado) return;
                excluirDoGoogle = dlg.OpcaoGoogleDelete == GoogleDeleteOpcao.ExcluirDoGoogle;
            }
            else if (usandoApi && contato.EhCompartilhado)
            {
                // Contatos compartilhados da empresa (WavenApiId definido)
                var warn = new SharedContactWarningDialog(SharedContactWarningModo.Excluir) { Owner = this };
                warn.ShowDialog();
                if (!warn.Confirmado)
                {
                    LogHelper.Info($"CONTACT_SHARED_DELETE_WARNING_CANCELLED | nome={contato.Nome}");
                    return;
                }
                LogHelper.Info($"CONTACT_SHARED_DELETE_WARNING_ACCEPTED | nome={contato.Nome}");
            }
            else
            {
                if (MessageBox.Show($"Excluir {contato.Nome}?", "Waven VoIP",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
            }

            // Se há exclusão pendente, confirma antes de iniciar nova
            if (_pendingDelete != null)
                await ConfirmarExclusaoDefinitiva();

            contato.PendingDelete = true;
            LogHelper.Info($"CONTACT_DELETE_PENDING | nome={contato.Nome}");

            _pendingDelete             = contato;
            // Só chama API para contatos compartilhados (não Google)
            _pendingDeleteUsandoApi    = usandoApi && !contato.FonteGoogle && contato.EhCompartilhado;
            _pendingDeleteWavenApiId   = _pendingDeleteUsandoApi ? contato.WavenApiId : null;
            _pendingDeleteRamal        = cfg?.Ramal;
            _pendingDeleteGoogleDelete = excluirDoGoogle;
            _pendingDeleteGoogleId     = excluirDoGoogle ? contato.GoogleContactId : null;

            AtualizarListaFiltrada(txtBusca?.Text?.Trim() ?? string.Empty);
            ExibirToast();

            _undoTimer?.Stop();
            _undoTimer?.Start();
        }

        private void BtnDesfazer_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingDelete == null) return;

            _undoTimer?.Stop();
            _pendingDelete.PendingDelete = false;
            LogHelper.Info($"CONTACT_DELETE_UNDO | nome={_pendingDelete.Nome}");

            _pendingDelete            = null;
            _pendingDeleteUsandoApi   = false;
            _pendingDeleteWavenApiId  = null;
            _pendingDeleteRamal       = null;
            _pendingDeleteGoogleDelete = false;
            _pendingDeleteGoogleId    = null;

            OcultarToast();
            AtualizarListaFiltrada(txtBusca?.Text?.Trim() ?? string.Empty);
        }

        private async Task ConfirmarExclusaoDefinitiva()
        {
            _undoTimer?.Stop();
            if (_pendingDelete == null) return;

            var contato      = _pendingDelete;
            var usandoApi    = _pendingDeleteUsandoApi;
            var wavenApiId   = _pendingDeleteWavenApiId;
            var ramal        = _pendingDeleteRamal;
            var googleDelete = _pendingDeleteGoogleDelete;
            var googleId     = _pendingDeleteGoogleId;

            _pendingDelete            = null;
            _pendingDeleteUsandoApi   = false;
            _pendingDeleteWavenApiId  = null;
            _pendingDeleteRamal       = null;
            _pendingDeleteGoogleDelete = false;
            _pendingDeleteGoogleId    = null;

            OcultarToast();
            LogHelper.Info($"CONTACT_DELETE_CONFIRMED | nome={contato.Nome}");

            var numNormTombstone = PhoneNumberNormalizer.NormalizeForSearch(contato.Numero);

            // Excluir do Google se solicitado
            if (googleDelete && !string.IsNullOrWhiteSpace(googleId))
            {
                LogHelper.Info($"CONTACT_DELETE_API_START | googleId={googleId} nome={contato.Nome} tipo=google");
                var ok = await GoogleContactsService.ExcluirContatoGoogleAsync(googleId);
                if (!ok)
                {
                    LogHelper.Info($"CONTACT_DELETE_API_FAIL | googleId={googleId} nome={contato.Nome} motivo=sem_permissao");
                    MessageBox.Show(
                        "Não foi possível excluir do Google Contacts. Verifique as permissões de acesso ao Google nas Configurações.\n\nO contato foi removido da empresa.",
                        "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    LogHelper.Info($"CONTACT_DELETE_API_OK | googleId={googleId} nome={contato.Nome} tipo=google");
                }
            }

            // Tombstone para contato Google (somente da empresa) — previne ressurreição na próxima sync Google
            // Cobre tanto contatos com GoogleContactId quanto contatos Google antigos sem este campo
            if (contato.FonteGoogle && !googleDelete)
            {
                ContactTombstoneService.Adicionar(new ContactTombstone
                {
                    GoogleContactId       = contato.GoogleContactId,  // pode ser null para contatos antigos
                    NumeroNormalizado     = numNormTombstone,
                    DeletedAt             = DateTime.UtcNow,
                    DeletedBy             = ramal ?? string.Empty,
                    SourceType            = "Google"
                });
                LogHelper.Info($"CONTACT_TOMBSTONE_CREATED | googleId={contato.GoogleContactId ?? "null"} numero={numNormTombstone} tipo=Google");
            }

            // Excluir da Waven API se contato compartilhado
            if (usandoApi && !string.IsNullOrWhiteSpace(wavenApiId))
            {
                LogHelper.Info($"CONTACT_DELETE_API_START | id={wavenApiId} nome={contato.Nome}");
                var ok = await WavenApiService.ExcluirContatoAsync(wavenApiId, ramal ?? string.Empty);
                if (!ok)
                {
                    WavenApiSyncService.EnfileirarOffline(new WavenApiOfflineOp
                    {
                        Op        = "DELETE",
                        ContactId = wavenApiId,
                        Ramal     = ramal ?? string.Empty
                    });
                    LogHelper.Info($"CONTACT_DELETE_API_FAIL | id={wavenApiId} nome={contato.Nome} motivo=offline_queued");
                }
                else
                {
                    LogHelper.Info($"CONTACT_DELETE_API_OK | id={wavenApiId} nome={contato.Nome}");
                }

                // Tombstone de segurança: previne ressurreição caso DELETE fique na fila offline
                ContactTombstoneService.Adicionar(new ContactTombstone
                {
                    DeletedContactSourceId = wavenApiId,
                    NumeroNormalizado      = numNormTombstone,
                    DeletedAt              = DateTime.UtcNow,
                    DeletedBy              = ramal ?? string.Empty,
                    SourceType             = "SharedCompany"
                });
                LogHelper.Info($"CONTACT_TOMBSTONE_CREATED | wavenApiId={wavenApiId} numero={numNormTombstone} tipo=SharedCompany");
            }

            // Remover do armazenamento local
            var todos = ContatoStorageService.Carregar();
            var chaveNum = PhoneNumberNormalizer.NormalizeForSearch(contato.Numero);
            todos.RemoveAll(c =>
            {
                if (!string.IsNullOrWhiteSpace(contato.WavenApiId) && c.WavenApiId == contato.WavenApiId)
                    return true;
                if (!string.IsNullOrWhiteSpace(contato.GoogleContactId) && c.GoogleContactId == contato.GoogleContactId)
                    return true;
                return PhoneNumberNormalizer.NormalizeForSearch(c.Numero) == chaveNum &&
                       string.Equals(c.Nome, contato.Nome, StringComparison.OrdinalIgnoreCase);
            });
            ContatoStorageService.Salvar(todos);
            _cacheContatos.Remove(contato);
        }

        private void ExibirToast()
        {
            try { toastContainer.Visibility = Visibility.Visible; } catch { }
        }

        private void OcultarToast()
        {
            try { toastContainer.Visibility = Visibility.Collapsed; } catch { }
        }

        // ── Navegação ─────────────────────────────────────────────────────────────

        private void GridContatos_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_onCallRequested == null) return;
            if (gridContatos.SelectedItem is Contato c && !string.IsNullOrWhiteSpace(c.Numero))
                AgendarLigacaoAposFechar(c.Numero);
        }

        private void AgendarLigacaoAposFechar(string numero)
        {
            // Esconde primeiro para não bloquear o seletor de saída no retorno
            try { Hide(); } catch { }

            Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (_onCallRequested != null)
                        await _onCallRequested(numero);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Não foi possível iniciar a chamada.\n" + ex.Message,
                        "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    try { Close(); } catch { }
                }
            }), DispatcherPriority.ApplicationIdle);
        }

        protected override void OnClosed(EventArgs e)
        {
            _debounceTimer?.Stop();
            _undoTimer?.Stop();
            if (_pendingDelete != null)
                _ = ConfirmarExclusaoDefinitiva();
            base.OnClosed(e);
        }
    }
}
