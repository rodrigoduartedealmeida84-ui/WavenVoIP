using System;

namespace WavenVoIP.Services
{
    public static class VersionService
    {
        public const string Versao  = "1.4.3";
        public const string NomeApp = "WavenVoIP";

        public static readonly DateTime DataBuild = new DateTime(2026, 6, 2);

        public static string VersaoCompleta => $"{NomeApp} v{Versao}";
        public static string VersaoComData  => $"{NomeApp} v{Versao}  •  build {DataBuild:dd/MM/yyyy}";

        public static string Changelog =>
            "v1.4.3 — Anti-ressurreicao de contatos + Edicao Google + Tombstone (02/06/2026)\n" +
            "• [FIX] Edicao de contato compartilhado voltava nome antigo apos sync — bug timezone UTC/Local no AtualizadoEm\n" +
            "• [FIX] Exclusao de contato ressurregia apos sync — tombstone local previne recriacao pela API\n" +
            "• [FIX] Contato Google editado voltava nome antigo — conversao correta Google→SharedCompany via Waven API\n" +
            "• [FIX] Contato Google excluido reaparecia na sync — tombstone por GoogleContactId e NumeroNormalizado\n" +
            "• [FIX] Google sem GoogleContactId (contatos antigos) nao recebia tombstone — corrigido\n" +
            "• [FIX] Botoes Editar/Excluir de contatos Google sem GoogleContactId mostravam dialog errado\n" +
            "• [FIX] Modal Contato Google: botao cortava texto — largura 420→560px, botoes com tamanhos fixos\n" +
            "• [NEW] ContactTombstoneService: suppression list em contact-tombstones.json\n" +
            "• [NEW] Tombstone por WavenApiId, GoogleContactId e NumeroNormalizado\n" +
            "• [NEW] AplicarContatos: verifica fila offline antes de recriar contato excluido (anti-ressurreicao)\n" +
            "• [NEW] AplicarContatos: nao sobrescreve edicao local se UPDATE pendente na fila offline\n" +
            "• [NEW] Edicao de contato Google com API ativa: cria na Waven API + vincula WavenApiId\n" +
            "• [NEW] SincronizarContatosGoogle: verifica tombstone antes de reimportar\n" +
            "• [UI] Texto do botao: 'Salvar na empresa' (era 'copia local') — deixa claro que e compartilhado\n" +
            "• [UI] Banner verde no modal indicando que sera compartilhado via Waven API\n" +
            "• [LOG] CONTACT_UPDATE_API_START | CONTACT_UPDATE_API_OK | CONTACT_UPDATE_API_FAIL\n" +
            "• [LOG] CONTACT_DELETE_API_START | CONTACT_DELETE_API_OK | CONTACT_DELETE_API_FAIL\n" +
            "• [LOG] GOOGLE_SYNC_SUPPRESSED_BY_TOMBSTONE | GOOGLE_CONTACT_CONVERTED_TO_SHARED\n" +
            "• [LOG] GOOGLE_CONTACT_NOT_OVERWRITING_SHARED | CONTACT_TOMBSTONE_CREATED\n" +
            "• [LOG] API_SYNC_SUPPRESSED_BY_TOMBSTONE | API_CONTACT_UPDATE_SKIPPED_PENDING\n" +
            "\n" +
            "v1.4.2 — Configuracao automatica para todos os ramais (02/06/2026)\n" +
            "• [NEW] Migracao automatica v1.4.2: configura Waven API e WhatsApp API silenciosamente no primeiro start\n" +
            "• [NEW] Waven API ativada automaticamente em todos os ramais apos atualizacao\n" +
            "• [NEW] CDR e AMI habilitados automaticamente para funcionar via API\n" +
            "• [NEW] WhatsApp API atualizado para novo endpoint wavenchat.com.br automaticamente\n" +
            "• [SAFE] Migracao nao sobrescreve usuario SIP, ramal, senha, Google token, audio, favoritos\n" +
            "• [SAFE] Tokens mascarados nos logs — nunca expostos\n" +
            "• [SAFE] Migracao roda apenas uma vez por maquina (flag MigracaoAplicada142)\n" +
            "• [LOG] AUTO_MIGRATION_142_APPLIED | AUTO_MIGRATION_142_ALREADY_APPLIED\n" +
            "\n" +
            "v1.4.1 — CDR e AMI via Waven API (02/06/2026)\n" +
            "• [NEW] Fase 2: CDR via API — historico e gravacoes sem conexao direta ao MySQL\n" +
            "• [NEW] Fase 3: AMI via API — ramais do Issabel sem conexao direta na porta 5038\n" +
            "• [NEW] Quando UsarWavenApi=true: CDR passa por https://api.almeidagas.com/api/cdr/calls\n" +
            "• [NEW] Quando UsarWavenApi=true: AMI passa por https://api.almeidagas.com/api/ami/extensions\n" +
            "• [NEW] API: GET /api/cdr/calls — consulta MySQL local no servidor\n" +
            "• [NEW] API: GET /api/cdr/test — testa conexao MySQL\n" +
            "• [NEW] API: GET /api/cdr/recordings — serve arquivo de gravacao do servidor\n" +
            "• [NEW] API: GET /api/ami/extensions — consulta AMI local no servidor\n" +
            "• [NEW] API: GET /api/ami/status — verifica acessibilidade do AMI\n" +
            "• [NEW] API: POST /api/ami/test — testa login AMI\n" +
            "• [FIX] Sem mais Connect Timeout 3306/5038 no cliente quando UsarWavenApi=true\n" +
            "• [FIX] Credenciais MySQL/AMI ficam APENAS no servidor (appsettings.Production.json)\n" +
            "• [LOG] CLIENT_CDR_USING_API | API_CDR_QUERY_START | API_CDR_QUERY_OK | API_CDR_QUERY_ERROR\n" +
            "• [LOG] CLIENT_AMI_USING_API | API_AMI_CONNECT_START | API_AMI_CONNECT_OK | API_AMI_CONNECT_ERROR\n" +
            "\n" +
            "v1.4.0 — Waven API + Autostart + Bandeja (01/06/2026)\n" +
            "• [NEW] Waven API v1: sincronizacao de contatos compartilhados entre ramais\n" +
            "• [NEW] Favoritos por usuario (ramal) e favoritos globais da empresa\n" +
            "• [NEW] Sincronizacao incremental (since=lastSyncUtc) — apenas mudancas desde ultimo sync\n" +
            "• [NEW] Soft delete com tombstone — exclusao propagada para todos os ramais\n" +
            "• [NEW] Reativacao automatica de contato excluido ao criar com mesmo numero\n" +
            "• [NEW] Offline queue: operacoes CREATE/UPDATE/DELETE/FAVORITE_ADD/FAVORITE_REMOVE enfileiradas offline\n" +
            "• [NEW] Migracao automatica dos 7 favoritos atuais como favoritos globais na primeira ativacao\n" +
            "• [FIX] Offline queue: ops CREATE e UPDATE agora sao executadas ao reconectar (estavam sendo descartadas)\n" +
            "• [FIX] Bandeja: icone carregado com fallback robusto — System.Drawing.SystemIcons.Application se exe nao tiver icone\n" +
            "• [FIX] Bandeja: recuperacao automatica do icone quando Explorer.exe e reiniciado (WM_TASKBARCREATED)\n" +
            "• [FIX] Autostart: caminho registrado no registro usa Environment.ProcessPath (mais confiavel que MainModule.FileName)\n" +
            "• [FIX] Autostart: flag /autostart adicionada ao comando de inicializacao automatica\n" +
            "• [FIX] Autostart: registro SIP atrasado 8s quando iniciado via autostart — aguarda rede estar disponivel\n" +
            "• [FIX] WavenApiSyncService: CancellationToken previne callback em andamento apos Dispose\n" +
            "• [LOG] STARTUP_MODE=AUTOSTART/MANUAL | AUTOSTART_REGISTERED | AUTOSTART_UNREGISTERED\n" +
            "• [LOG] TRAY_ICON_CREATED | TRAY_ICON_RECOVERED | TRAY_ICON_RECREATED | TRAY_ICON_INIT_FAILED\n" +
            "• [LOG] AUTOSTART_SIP_DELAY | AUTOSTART_REGISTER_DELAYED\n" +
            "• [LOG] API_CONTACT_SYNC_START/DONE | API_CONTACT_CREATED/UPDATED/DELETED\n" +
            "• [LOG] API_CONTACT_FAVORITE_MIGRATED | API_CONTACT_OFFLINE_QUEUE_SAVED/SENT\n" +
            "\n" +
            "v1.3.2 — Hold + CDR canal correto (29/05/2026)\n" +
            "• [FIX] CDR: chamadas WhatsApp TIM/Vivo/Operadora nao mais classificadas como Operadora\n" +
            "• [FIX] DedurzirTronco() identifica canal via DID no campo channel do Asterisk (SIP/PJSIP)\n" +
            "• [FIX] Ex: PJSIP/WAVOIP-556684263277-... agora detectado como WhatsApp TIM\n" +
            "• [LOG] CDR_CHANNEL_IDENTIFIED | channel => canal identificado\n" +
            "\n" +
            "v1.3.2 — Hold completo: silencia mic + limpa buffer ao pausar/retomar (ANTERIOR)\n" +
            "• [FIX] Ao pausar (Segurar): mic silenciado via proxy — audio nao acumula no WaveIn\n" +
            "• [FIX] Ao pausar (Segurar): WaveOut pausado — operador nao ouve cliente\n" +
            "• [FIX] Ao pausar e retomar: BufferedWaveProvider.ClearBuffer() chamado via reflexao\n" +
            "• [FIX] Audio residual/eco acumulado no buffer NAudio descartado ao retomar Hold\n" +
            "• [FIX] Ao retomar: estado do mute restaurado ao que era antes da pausa\n" +
            "• [LOG] CALL_HOLD_MIC_MUTED / CALL_HOLD_MIC_RESTORED\n" +
            "• [LOG] CALL_HOLD_WAVEOUT_PAUSED / CALL_HOLD_WAVEOUT_RESUMED\n" +
            "• [LOG] CALL_HOLD_BUFFER_CLEARED\n" +
            "\n" +
            "v1.3.1 — Mute: silence G.711 correto (29/05/2026)\n" +
            "• [FIX] Silence injection usa byte correto por codec (0xFF PCMU, 0xD5 PCMA)\n" +
            "• [FIX] 0x00 (all-zeros) em G.711 decodifica como ruido alto — removido\n" +
            "• [FIX] Asterisk nao mais interpreta pacotes de mute como sinal invalido\n" +
            "• [FIX] SetAudioSourceFormat interceptado para detectar codec negociado\n" +
            "• [FIX] Cliente ouve silencio real durante mute, sem musica ou ruido\n" +
            "\n" +
            "v1.3.0 — Mute TX-Only correto via silence injection (29/05/2026)\n" +
            "• [FIX] Mute aplica SOMENTE no pipeline TX (microfone/envio) — cliente nao me escuta\n" +
            "• [FIX] Audio recebido do cliente (RX/WaveOut) continua 100% ativo durante mute\n" +
            "• [FIX] RTP TX continua fluindo com silence — Asterisk nao descarta sessao apos timeout\n" +
            "• [FIX] Mute de qualquer duracao (inclusive >3 min) nao derruba a chamada\n" +
            "• [FIX] Descligar chamada mutada reseta mute — proxima chamada comeca desmutada\n" +
            "• [NEW] MuteableAudioSource: proxy IAudioSource que injeta silence encodado no TX\n" +
            "• [NEW] AudioSource=proxy (TX), AudioSink=endpoint direto (RX) — separacao completa\n" +
            "• [LOG] CALL_MUTE_ENABLED_TX_ONLY — mute ativado somente no TX\n" +
            "• [LOG] CALL_MUTE_DISABLED_TX_RESTORED — microfone restaurado\n" +
            "• [LOG] CALL_RX_AUDIO_CONTINUES_DURING_MUTE — confirmacao que WaveOut nao foi afetado\n" +
            "• [LOG] CALL_MUTE_DID_NOT_AFFECT_PLAYBACK — pipeline RX intacto\n" +
            "• [LOG] CALL_MUTE_STATE_RESET_ON_END — mute resetado ao encerrar chamada\n" +
            "• [LOG] AUDIO_MUTE_PROXY_CREATED — proxy criado na sessao de midia\n" +
            "\n" +
            "v1.2.14 — Historico CDR + Gravacoes + Contatos da Empresa (28/05/2026)\n" +
            "• [FIX] CDR: chamada sainte corretamente associada ao numero real discado\n" +
            "  — CDR do tronco (src=DID) agora extrai destino real do nome do arquivo de gravacao\n" +
            "  — Numero e tipo (Realizada) corrigidos automaticamente pelo prefixo de rota no filename\n" +
            "• [FIX] CDR: canal/tronco exibido corretamente nas saintes (Operadora/WhatsApp TIM/Vivo)\n" +
            "  — Prefixo 1/2/3 do filename de gravacao determina a origem da saida\n" +
            "• [FIX] CDR: duplicatas de chamadas recebidas removidas automaticamente\n" +
            "  — MesclarCdr normaliza 55+numero antes de comparar entrada local com CDR\n" +
            "  — ReprocessarHistorico limpa duplicatas legadas no historico.json existente\n" +
            "• [FIX] CDR: entrada local SIP removida corretamente ao sincronizar CDR (normalize +55)\n" +
            "• [UI] Botao X para limpar pesquisa nos paineis Contatos e Historico\n" +
            "  — Visivel apenas quando ha texto; foco retorna ao campo apos limpar\n" +
            "• [NEW] Sincronizacao automatica de contatos da empresa entre todos os ramais (GitHub raw, 5 min)\n" +
            "• [NEW] Favoritos sempre no topo da lista de contatos, restante em ordem alfabetica\n" +
            "• [NEW] Estrela colorida: cinza=nao favorito, amarelo=favorito (com tooltip)\n" +
            "• [NEW] Deduplicacao automatica de contatos por numero normalizado (local > Google)\n" +
            "• [NEW] Contato duplicado no cadastro: atualiza existente em vez de bloquear\n" +
            "• [NEW] company-contacts.json: 7 contatos/favoritos distribuidos para todos os ramais\n" +
            "• [NEW] company-config.json: config WhatsApp + CDR centralizada e auto-sincronizada\n" +
            "• [FIX] Mute sem queda de chamada: MutarMicrofone() nao para RTP TX\n" +
            "• [FIX] Watchdog RTP: keepalive a cada 20s durante mute (previne timeout Asterisk)\n" +
            "• [FIX] Transferencia: flag _transferenciaEmAndamento impede destruicao prematura de sessao\n" +
            "• [FIX] PhoneNumberNormalizer: remove 55 de numeros 12-13 digitos + casos rota+55\n" +
            "• [LOG] CDR_RECORDING_CORRECTS_NUMBER / REPROCESS_DUPLICATE_REMOVED\n" +
            "• [LOG] CONTACTS_SYNC_START / CONTACTS_SYNC_DONE / CONTACT_CREATED / CONTACT_UPDATED\n" +
            "• [LOG] CONTACT_DELETED / FAVORITE_SYNCED / CONTACTS_DEDUP_UI\n" +
            "• [LOG] COMPANY_CONTACTS_IMPORTED_ON_UPDATE / COMPANY_FAVORITES_IMPORTED_ON_UPDATE\n" +
            "• [LOG] COMPANY_CONTACTS_EXPORT_START / COMPANY_CONTACTS_EXPORT_DONE\n" +
            "• [LOG] CALL_START_REQUEST / CALL_CONNECTED / CALL_MUTE_ENABLED / CALL_MUTE_DISABLED\n" +
            "• [LOG] AUDIO_DEVICE_DETECTED / AUDIO_DEVICE_SWITCH / AUDIO_DEVICE_RECOVERY\n" +
            "\n" +
            "v1.2.13 — Audio inteligente (26/05/2026)\n" +
            "• [NEW] Separacao entre audio da conversa e toque de chamada\n" +
            "• [NEW] Toque pode sair no alto-falante enquanto chamada usa headset\n" +
            "• [NEW] Volume do toque configuravel (slider 0-100)\n" +
            "• [NEW] Auto-deteccao de headset Intelbras USB na abertura de Configuracoes\n" +
            "• [NEW] Campos: Microfone da ligacao, Audio da ligacao, Saida do toque\n" +
            "• [NEW] Botao 'Testar toque' em Configuracoes com volume real\n" +
            "• [FIX] Compatibilidade com headset Intelbras USB\n" +
            "• [FIX] Toque NUNCA sai no headset se houver alto-falante disponivel\n" +
            "• [LOG] AUDIO_DEVICE_DETECTED / AUDIO_HEADSET_INTELBRAS_DETECTED\n" +
            "• [LOG] AUDIO_AUTO_INPUT_SELECTED / AUDIO_AUTO_CALL_OUTPUT_SELECTED\n" +
            "• [LOG] AUDIO_AUTO_RING_OUTPUT_SELECTED / AUDIO_RING_VOLUME_SET\n" +
            "• [LOG] AUDIO_LEGACY_CONFIG_MIGRATED / AUDIO_RING_TEST_PLAYED\n" +
            "\n" +
            "v1.2.12 — Hotfix Configuracoes (25/05/2026)\n" +
            "• [FIX] Janela Configuracoes nao abria apos atualizacao v1.2.11\n" +
            "• [FIX] StaticResource SectionCardStyle/SectionTitleStyle inexistentes removidos\n" +
            "• [FIX] Secao Logs e Diagnostico reescrita com estilos inline corretos\n" +
            "\n" +
            "v1.2.11 — Logs e Diagnostico (25/05/2026)\n" +
            "• [NEW] Seção 'Logs e Diagnóstico' em Configurações\n" +
            "• [NEW] Checkbox: Ativar logs do sistema (persiste em SipConfig)\n" +
            "• [NEW] Checkbox: Ativar logs detalhados — SIP, AMI, CDR\n" +
            "• [NEW] Indicador visual de status dos logs (verde=ativo, cinza=desativado)\n" +
            "• [NEW] Botão 'Abrir pasta de logs' — abre %LOCALAPPDATA%\\WavenVoIP\\Logs\n" +
            "• [NEW] Botão 'Copiar diagnóstico' — copia resumo + últimas 30 linhas de cada canal\n" +
            "• [NEW] Botão 'Limpar logs' — remove todos os arquivos .log (com confirmação)\n" +
            "• [NEW] Botão 'Exportar diagnóstico ZIP' — gera ZIP com logs + resumo de ambiente\n" +
            "• [NEW] LogHelper.ConfigurarDeSettings(SipConfig): aplica flags ao iniciar\n" +
            "• [NEW] LogHelper.IsEnabled / IsDetailedEnabled — ERRORs sempre gravados\n" +
            "• [NEW] LogHelper.Google() e LogHelper.WhatsApp() — canais dedicados\n" +
            "• [FIX] Flags de log lidas no startup via App.xaml.cs\n" +
            "\n" +
            "v1.2.10 — Migração GitHub Releases (25/05/2026)\n" +
            "• [INFRA] Distribuição migrada de Hostinger para GitHub Releases\n" +
            "• [FIX] SHA256 do pacote agora idêntico no download (sem recompressão do servidor)\n" +
            "• [FIX] Updater não precisa mais de headers anti-WAF/ModSecurity\n" +
            "• [INFRA] version.json servido via GitHub raw (sempre sincronia com o repo)\n" +
            "\n" +
            "v1.2.9 — Reconexão Automática Silenciosa (25/05/2026)\n" +
            "• [NEW] IntegrationFailureClassifier: distingue falha temporária de falha de autenticação\n" +
            "• [NEW] IntegrationAutoReconnectService: backoff 5s→15s→30s→1m→3m→5m por integração\n" +
            "• [NEW] Status Reconectando (🟡 amarelo) — sem modal, sem ação necessária\n" +
            "• [FIX] Queda de internet: status Reconectando, tentativa automática, ZERO modal\n" +
            "• [FIX] Credencial inválida: status Ação necessária + modal + botão Reconectar\n" +
            "• [FIX] AMI socket/timeout: reconexão automática sem perturbar usuário\n" +
            "• [FIX] CDR MySQL offline: reconexão automática; access denied → modal\n" +
            "• [FIX] Google sem internet: reconexão automática; token revogado → modal\n" +
            "• [LOG] INTEGRATION_TEMPORARY_FAILURE / INTEGRATION_AUTO_RECONNECT_SCHEDULED\n" +
            "• [LOG] INTEGRATION_AUTO_RECONNECT_ATTEMPT / INTEGRATION_AUTO_RECONNECT_SUCCESS\n" +
            "• [LOG] INTEGRATION_ACTION_REQUIRED / INTEGRATION_MODAL_SUPPRESSED_TEMPORARY\n" +
            "• [LOG] INTEGRATION_MODAL_SHOWN_AUTH_FAILURE\n" +
            "\n" +
            "v1.2.8 — Painel de Logs em Tempo Real (25/05/2026)\n" +
            "• [NEW] Log panel em tempo real via LogHelper.LogWritten event\n" +
            "• [NEW] Filtros rápidos: Todos / Erros / Updater / Google / AMI / WhatsApp / SIP\n" +
            "• [NEW] Cores: ERROR=vermelho, WARN=amarelo, SUCCESS=verde, INFO=cinza\n" +
            "• [NEW] Checkbox Rolagem automática\n" +
            "• [NEW] Botão Limpar logs (visual only — arquivo físico intacto)\n" +
            "• [NEW] Botão Copiar logs (copia conteúdo visível para clipboard)\n" +
            "• [NEW] Botão Abrir pasta de logs (%LOCALAPPDATA%\\WavenVoIP\\Logs)\n" +
            "• [NEW] Carga inicial dos últimos 400 linhas por canal na abertura\n" +
            "• [FIX] Painel lê logs de %LOCALAPPDATA%\\WavenVoIP\\Logs\\ (caminho correto)\n" +
            "• [SAFE] Append assíncrono via Dispatcher.BeginInvoke — UI nunca trava\n" +
            "• [SAFE] Limite de 2000 entradas na UI; arquivo físico completo\n" +
            "\n" +
            "v1.2.7 — Sistema Global de Reconexão Manual (24/05/2026)\n" +
            "• [NEW] IntegrationStatusService: status Conectado/Desconectado/Erro para todas as integrações\n" +
            "• [NEW] IntegrationDisconnectedOverlay: modal uma vez por sessão por integração\n" +
            "• [NEW] Anti-loop global: bloqueia reconexão por 10 minutos após tentativa\n" +
            "• [FIX] Google OAuth: NUNCA abre navegador automaticamente — só via botão Conectar Google\n" +
            "• [FIX] AMI: retry silencioso, sem popup infinito; modal apenas na primeira falha por sessão\n" +
            "• [FIX] CDR: marca 'CDR indisponível', app continua funcionando; modal uma vez por sessão\n" +
            "• [UI] Configurações > Status das Integrações: dots coloridos + botões Conectar/Desconectar/Reconectar\n" +
            "• [LOG] INTEGRATION_MODAL_SHOWN / INTEGRATION_MODAL_DISMISSED / INTEGRATION_LOOP_BLOCKED\n" +
            "• [LOG] INTEGRATION_RECONNECT_ATTEMPT / GOOGLE_DISCONNECTED / GOOGLE_TOKEN_MISSING\n" +
            "\n" +
            "v1.2.6 — Download 403 Fix V2 (24/05/2026)\n" +
            "• [FIX] DOWNLOAD_CODE_VERSION=2026-05-24-FIX403-V2 marker confirma binario novo\n" +
            "• [FIX] Headers completos: User-Agent Chrome, Accept, Accept-Language, Referer, Cache-Control\n" +
            "• [FIX] HttpClientHandler: AutomaticDecompression gzip/deflate/br + AllowAutoRedirect\n" +
            "• [NEW] DOWNLOAD_PRECHECK_START: HEAD pre-check loga Server, CF-Ray, X-ModSecurity, Content-Length\n" +
            "• [NEW] DOWNLOAD_FALLBACK_START: fallback WebClient com mesmos headers quando HttpClient recebe 403\n" +
            "• [LOG] DOWNLOAD_ERROR_BODY: primeiros 500 chars do HTML de bloqueio do WAF\n" +
            "• [LOG] WEBCLIENT_ERROR: exception completa do fallback WebClient\n" +
            "\n" +
            "v1.2.5 — Correcao Download Hostinger 403 (24/05/2026)\n" +
            "• [FIX] Download do pacote bloqueado com HTTP 403 pela Hostinger (ModSecurity/WAF)\n" +
            "• [FIX] HttpClient agora envia User-Agent de navegador e cabecalhos Accept identicos ao Chrome\n" +
            "• [FIX] TLS 1.2/1.3 forcado explicitamente via ServicePointManager\n" +
            "• [FIX] Descompressao automatica ativada (gzip/deflate/br)\n" +
            "• [NEW] Fallback para WebClient quando HttpClient recebe 403\n" +
            "• [LOG] HEAD pre-check antes do download: status, Content-Length, Server, CF-Ray, X-ModSecurity\n" +
            "• [LOG] Corpo da resposta 403 salvo no log (ate 500 chars) para diagnostico\n" +
            "\n" +
            "v1.2.4 — Correcao WavenUpdater (24/05/2026)\n" +
            "• [FIX] WavenUpdater nao fecha mais imediatamente apos ser iniciado\n" +
            "• [FIX] Todos os 4 arquivos do .NET host copiados para pasta temp (exe+dll+deps+runtimeconfig)\n" +
            "• [FIX] UseShellExecute=true restaurado — necessario para inicializacao WPF/window station\n" +
            "• [LOG] APP_STARTUP / APP_RUNTIME / APP_WORKDIR / APP_BASE_DIR no update.log\n" +
            "• [LOG] LAUNCH_CMD / LAUNCH_WORKDIR com caminho e argumentos exatos do processo\n" +
            "• [SAFE] AppDomain.UnhandledException + DispatcherUnhandledException no WavenUpdater\n" +
            "\n" +
            "v1.2.3 — Protecao Instalacao Nova (24/05/2026)\n" +
            "• [FIX] Instalacao limpa nao abre mais com usuario anterior (ex: Pabliny)\n" +
            "• [FIX] fresh_install.flag criado pelo instalador reseta identidade do usuario\n" +
            "• [FIX] Configuracoes avancadas (AMI/CDR/WhatsApp) sao preservadas na reinstalacao\n" +
            "• [UI] Instalador: checkbox 'Preservar configuracoes de usuario existentes'\n" +
            "• [LOG] INSTALL_MODE=FRESH/UPGRADE / FRESH_INSTALL_FLAG_FOUND / USER_IDENTITY_RESET\n" +
            "\n" +
            "v1.2.2 — Protecao Anti-Loop de Update (24/05/2026)\n" +
            "• [FIX] Update nao fecha mais o WavenVoIP se o WavenUpdater falhar ao iniciar\n" +
            "• [FIX] Flag update_failed.flag criada automaticamente em caso de falha — pausa updates automaticos\n" +
            "• [FIX] Anti-loop: 2 tentativas em menos de 10 min bloqueiam update por 24h\n" +
            "• [UI] Configuracoes > Atualizacoes: banner de aviso + botao 'Limpar falha'\n" +
            "• [UI] WavenUpdater: botoes 'Abrir WavenVoIP' e 'Copiar log' ao falhar (nao fecha sozinho)\n" +
            "• [LOG] UPDATE_LOOP_GUARD / UPDATER_START_OK / UPDATER_START_FAILED / APP_CLOSE_CANCELLED\n" +
            "\n" +
            "v1.2.1 — Correncao URL Pacote .zip (24/05/2026)\n" +
            "• [FIX] Pacote renomeado de WavenVoIP.pkg para WavenVoIP.zip\n" +
            "• [LOG] CHECK_URL / CHECK_JSON / PACOTE / DOWNLOAD_URL / DOWNLOAD_HTTP adicionados\n" +
            "• [UI] WavenUpdater: botao 'Copiar log' no rodape\n" +
            "\n" +
            "v1.2.0 — Auto-Update + Icone Oficial (24/05/2026)\n" +
            "• [NEW] Sistema de atualizacao automatica silenciosa sem UAC\n" +
            "• [NEW] Icone oficial: arte completa W+headset em PNG transparente\n" +
            "• [NEW] Painel de Atualizacoes em Configuracoes com progresso e controle manual\n" +
            "\n" +
            "v1.1.2 — WhatsApp Incoming Call Delay Fix (19/05/2026)\n" +
            "• [FIX] OnIncomingCall: DetectarOrigemEntrada (reflexão + I/O disco) movido para Task.Run após evento\n" +
            "• [FIX] Removido EnviarRespostaTentandoETocando duplicado em OnIncomingCall (já enviado no transport)\n" +
            "• [FIX] CanalIdentificacaoService: SipConfig cacheada 30 s (evita leitura de arquivo por INVITE)\n" +
            "• [FIX] Dispatcher.Invoke → BeginInvoke no popup de chamada recebida (SIP thread não bloqueia)\n" +
            "• [LOG] WHATSAPP_INCOMING_DETECTED / WHATSAPP_CALL_EVENT_RECEIVED / EXTENSION_RING_STARTED\n" +
            "• [LOG] WHATSAPP_FORWARD_TOTAL_DELAY_MS / WHATSAPP_DUPLICATE_EVENT_IGNORED / SIP_INVITE_TO_ISSABEL_SENT\n" +
            "\n" +
            "v1.1.1 — Contact Migration Safe Rules (19/05/2026)\n" +
            "• [FIX] MigrarContatosAntigos: números 14+ dígitos marcados como CONTACT_NUMBER_SUSPICIOUS, não alterados\n" +
            "• [FIX] Guard de execução única por inicialização (_migracaoJaExecutada)\n" +
            "• [FIX] Timeout de 10 segundos — migração nunca trava a UI\n" +
            "• [LOG] Relatório: migrados / deduplicados / suspeitos / total\n" +
            "\n" +
            "v1.1.0 — UI Modernization (20/05/2026)\n" +
            "• [UI] CallWindow: avatar com iniciais, dot de status com animação pulse, indicador de gravação, tooltips\n" +
            "• [UI] TransferControlWindow: layout profissional, avatar, timer, cards dual-call, botões coloridos\n" +
            "• [UI] BlindTransferWindow: nova tela de confirmação com card de contato e aviso antes de transferir\n" +
            "• [UI] ConferenceControlWindow: participantes com avatar, dot de status colorido por estado\n" +
            "• [FIX] Transferência cega agora exibe confirmação com nome/número antes de executar\n" +
            "• [FIX] TransferControlWindow recebe info da chamada original para exibir no card dual-call\n" +
            "\n" +
            "v1.0.2 — Estabilização final de normalização (19/05/2026)\n" +
            "• [FIX] Prefixo 55 removido de todas as telas: histórico, popup chamada, tela em chamada, notificações, transferência, conferência, WhatsApp, contatos\n" +
            "• [FIX] Nome do contato exibido no popup de chamada entrante: 'Nome (número)' quando encontrado\n" +
            "• [FIX] Mesmo formato 'Nome (número)' em MissedCallToast, MissedCallPopup, CallWindow e ConferenceControlWindow\n" +
            "• [FIX] Labels SIP técnicos (SIP, PJSIP, DAHDI) substituídos por 'Chamada do sistema'\n" +
            "• [FIX] HistoricoLigacaoItem.NumeroLimpoVisual agora remove prefixo 55 além do prefixo de rota\n" +
            "• [FIX] ContatoStorageService: busca por variantes (com/sem 9º dígito, com/sem 55) — mais matches\n" +
            "• [FIX] CDR timer padrão 5 s com campo HistoricoSyncIntervalSeconds e migração automática\n" +
            "• [LOG] CONTACT_MATCH_SUCCESS / CONTACT_MATCH_FAIL / PHONE_NORMALIZED em ui_flow_debug.log\n" +
            "• [FIX] Modal SalvarContatoDialog: altura aumentada (460→560) para não cortar botões com banner Google\n" +
            "• [FIX] ContactsWindow: deduplicação por número normalizado (local > AMI > Google)\n" +
            "\n" +
            "v1.0.1 — Hotfix crítico CDR (19/05/2026)\n" +
            "• [FIX] Crash 'An item with the same key has already been added' na sincronização CDR\n" +
            "  — linkedid/uniqueid duplicado do Asterisk (filas/URA/transferências) não aborta mais a sync\n" +
            "  — TryAdd substitui ToDictionary; duplicatas logadas como CDR_DUPLICATE_KEY_DETECTED\n" +
            "\n" +
            "v1.0.0 — Versão base definitiva (18/05/2026)\n" +
            "• CDR com deduplicação de fila e ramal real\n" +
            "• Validação rigorosa de ramais (2-5 dígitos)\n" +
            "• Detecção de caixa postal e URA/IVR\n" +
            "• Gravações com fallback via diretório HTTP + HEAD validation\n" +
            "• Sync de CDR em 5 s / 15 s / 30 s pós-chamada\n" +
            "• Status mostrando nome do operador (ramal)";
    }
}
