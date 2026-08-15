using System;
using System.IO;
using System.Text.Json;
using WavenVoIP.Services;

namespace WavenVoIP
{
    public class SipConfig
    {

        // ── Caminhos ─────────────────────────────────────────────────────────────
        // v2.2.5 — Movido de %APPDATA% (roaming) para %LOCALAPPDATA% (local), igual a
        // todo o resto do app (Logs, contatos.json, historico.json, favoritos, flags de
        // update/instalacao). O roaming profile pode ser redirecionado para um share de
        // rede via GPO em maquinas de dominio; se esse share nao estiver disponivel no
        // instante exato em que o Windows inicia o Waven via autostart, File.Exists()
        // no caminho antigo podia retornar false mesmo com a config real existindo —
        // fazendo o app tratar isso como instalacao nova. %LOCALAPPDATA% nunca e
        // redirecionado por Folder Redirection do Windows, entao esse caminho e sempre
        // local e imediatamente disponivel no logon.
        public static string ConfigDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WavenVoIP");

        public static string ConfigFilePath => Path.Combine(ConfigDir, "sipconfig.json");

        // Caminho antigo (roaming) — mantido apenas para migracao automatica de quem
        // ja tinha configuracao salva la. Nunca escrito novamente.
        private static string LegacyConfigFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WavenVoIP", "sipconfig.json");

        private const int BackupCount = 3;
        private static string BackupPath(int n) => Path.Combine(ConfigDir, $"sipconfig.backup{n}.json");

        private static bool _legacyMigrationChecked;

        // v2.3.7 — cache em memória de CarregarSalva(). Investigação real (v2.3.7, CPU ociosa alta)
        // mediu SipConfig.CarregarSalva() sendo chamado ~4-5x/segundo em regime permanente com o
        // app ocioso (maior parte vindo do loop de correção de ramal dentro de
        // IssabelCdrService.ReprocessarHistoricoCdrLocalAsync, que roda a cada ciclo de sync de CDR
        // — por padrão a cada poucos segundos, para sempre, mesmo sem nada novo). Cada chamada faz
        // File.Exists+File.ReadAllText+JsonSerializer.Deserialize+RepairDefaults — I/O de disco e
        // parse de JSON reais, não uma leitura de campo simples.
        //
        // Design: lock único protege carregar-e-cachear (raro: só na primeira chamada e depois de
        // Salvar()) e o cache é sempre devolvido como CÓPIA rasa (MemberwiseClone) — nunca a mesma
        // instância para dois chamadores. SipConfig só tem campos de valor (string/int/bool/
        // DateTime?), então a cópia rasa é uma cópia completa e segura: nenhum chamador pode mutar o
        // objeto em cache sem chamar Salvar() explicitamente, e Salvar() sempre invalida o cache no
        // final — a próxima leitura sempre reflete o que foi realmente persistido em disco. Isso
        // elimina o polling de arquivo (não existe) e qualquer leitura "stale": o cache só existe
        // entre uma leitura bem-sucedida e o próximo Salvar() (ou até o processo reiniciar).
        private static readonly object _cacheLock = new object();
        private static SipConfig? _cacheInstancia;
        private static bool _cacheCarregado;

        // Chamado no fim de um Salvar() bem-sucedido — garante que a PRÓXIMA CarregarSalva()
        // sempre releia do disco em vez de devolver um valor antigo em memória.
        private static void InvalidarCache()
        {
            lock (_cacheLock)
            {
                _cacheCarregado = false;
                _cacheInstancia = null;
            }
        }

        public bool EstaCompleta =>
            !string.IsNullOrWhiteSpace(Ramal) &&
            !string.IsNullOrWhiteSpace(Login) &&
            !string.IsNullOrWhiteSpace(Senha) &&
            !string.IsNullOrWhiteSpace(ServerIp) &&
            (!string.IsNullOrWhiteSpace(NomeUsuario) || !string.IsNullOrWhiteSpace(DisplayName));

        // ── Gravação atômica com backup rotativo ────────────────────────────────
        public void Salvar()
        {
            AplicarPadroes();
            Directory.CreateDirectory(ConfigDir);
            LogHelper.Info($"CONFIG_SAVE_START | path={ConfigFilePath}");

            try
            {
                CriarBackup();

                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

                // Valida o JSON antes de tocar no arquivo principal — nunca deixa um
                // arquivo corrompido/zerado no lugar por causa de uma escrita interrompida.
                JsonSerializer.Deserialize<SipConfig>(json);

                var tempPath = ConfigFilePath + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(ConfigFilePath))
                    File.Replace(tempPath, ConfigFilePath, null);
                else
                    File.Move(tempPath, ConfigFilePath);

                LogHelper.Info("CONFIG_SAVE_OK");

                // v2.3.7 — sempre invalida DEPOIS de escrever no disco com sucesso: a próxima
                // CarregarSalva() releem do arquivo (que agora é a fonte de verdade) e recacheia.
                InvalidarCache();
            }
            catch (Exception ex)
            {
                LogHelper.Error("CONFIG_SAVE_FAILED", ex);
                throw;
            }
        }

        // Mantem ate 3 gerações de backup (sipconfig.backup1.json = mais recente).
        public static void CriarBackup()
        {
            try
            {
                if (!File.Exists(ConfigFilePath)) return;
                Directory.CreateDirectory(ConfigDir);

                for (int i = BackupCount; i >= 2; i--)
                {
                    var src = BackupPath(i - 1);
                    if (File.Exists(src)) File.Copy(src, BackupPath(i), overwrite: true);
                }
                File.Copy(ConfigFilePath, BackupPath(1), overwrite: true);
            }
            catch { }
        }

        /// <summary>
        /// Fills every field that is empty/zero with the company default.
        /// Safe to call on any config — loaded from file or freshly constructed.
        /// </summary>
        public void RepairDefaults()
        {
            // SIP connection
            if (string.IsNullOrWhiteSpace(ServerIp))  ServerIp  = "191.252.202.208";
            if (Port <= 0)                             Port      = 5061;
            if (string.IsNullOrWhiteSpace(Transport)) Transport = "udp";
            if (string.IsNullOrWhiteSpace(Domain))    Domain    = ServerIp;
            if (string.IsNullOrWhiteSpace(ProxySip))  ProxySip  = $"{ServerIp}:{Port}";
            if (HistoricoRetencaoDias <= 0)            HistoricoRetencaoDias = 7;

            // AMI
            AmiAtivo = true;
            if (string.IsNullOrWhiteSpace(AmiHost))    AmiHost    = ServerIp;
            if (AmiPorta <= 0)                         AmiPorta   = 5038;
            if (string.IsNullOrWhiteSpace(AmiUsuario)) AmiUsuario = "waven";
            if (string.IsNullOrWhiteSpace(AmiSenha))   AmiSenha   = "Waven@2025";
            if (AmiIntervaloMinutos <= 0)               AmiIntervaloMinutos = 10;

            // Channels / routes
            if (string.IsNullOrWhiteSpace(SalaConferenciaIssabel)) SalaConferenciaIssabel = "800";
            if (string.IsNullOrWhiteSpace(CanalOperadora))         CanalOperadora   = "6631998716;IN-BRDID-6631998716";
            if (string.IsNullOrWhiteSpace(Canal0800))              Canal0800         = "08001901900;VONO-0800-ENTRADA";
            if (string.IsNullOrWhiteSpace(CanalWhatsAppTim))       CanalWhatsAppTim  = "556684263277;WAVOIP-556684263277";
            if (string.IsNullOrWhiteSpace(CanalWhatsAppVivo))      CanalWhatsAppVivo = "556696308630;WAVOIP-556696308630";

            // CDR
            if (CdrPorta <= 0)                              CdrPorta   = 3306;
            if (string.IsNullOrWhiteSpace(CdrBanco))        CdrBanco   = "asteriskcdrdb";
            if (string.IsNullOrWhiteSpace(CdrTabela))       CdrTabela  = "cdr";
            if (string.IsNullOrWhiteSpace(CdrUsuario))      CdrUsuario = "waven";
            if (string.IsNullOrWhiteSpace(HistoricoModoExibicao)) HistoricoModoExibicao = "MeuRamal";
            if (HistoricoSyncIntervalSeconds < 0)            HistoricoSyncIntervalSeconds = 3;
            if (AmiSyncIntervalSeconds    < 0)              AmiSyncIntervalSeconds       = 3;
            if (GoogleSyncIntervalSeconds < 0)              GoogleSyncIntervalSeconds    = 3;

            // Recordings
            if (string.IsNullOrWhiteSpace(GravacaoTipoAcesso)) GravacaoTipoAcesso = "URL";
            if (string.IsNullOrWhiteSpace(GravacaoUrlBase))    GravacaoUrlBase    = "http://pabx.almeidagas.com/gravacoes/monitor/";

            // Áudio
            if (RingVolume <= 0) RingVolume = 80;
        }

        // Kept for call-site compatibility — delegates to RepairDefaults.
        public void AplicarPadroes() => RepairDefaults();

        // Clears only operator identity — preserves all server/AMI/CDR/integration settings.
        public void ResetarIdentidadeUsuario()
        {
            NomeUsuario  = string.Empty;
            RamalNome    = string.Empty;
            DisplayName  = string.Empty;
            Ramal        = string.Empty;
            Login        = string.Empty;
            Senha        = string.Empty;
        }

        // ── Carregamento resiliente ──────────────────────────────────────────────
        // Nunca conclui "não existe configuração" por causa de uma falha transitória de
        // leitura. Ordem de tentativa: arquivo principal -> backups (mais recente
        // primeiro) -> caminho legado (roaming). Só retorna null quando nenhuma dessas
        // fontes tem uma configuração válida.
        public static SipConfig? CarregarSalva()
        {
            lock (_cacheLock)
            {
                if (_cacheCarregado)
                    return (SipConfig?)_cacheInstancia?.MemberwiseClone();

                // Ainda dentro do lock: qualquer chamada concorrente que chegue aqui enquanto
                // a primeira leitura ainda está em andamento espera aqui, em vez de bater no
                // disco ao mesmo tempo (evita leituras duplicadas na largada/pós-invalidação).
                _cacheInstancia = CarregarSalvaImpl();
                _cacheCarregado = true;
                return (SipConfig?)_cacheInstancia?.MemberwiseClone();
            }
        }

        private static SipConfig? CarregarSalvaImpl()
        {
            LogHelper.Info($"CONFIG_PATH_RESOLVED | path={ConfigFilePath}");
            LogHelper.Info("CONFIG_LOAD_START");

            MigrarConfigLegada();

            var principal = TentarCarregarArquivo(ConfigFilePath, "principal");
            if (principal != null)
            {
                LogHelper.Info("CONFIG_LOAD_OK | origem=principal");
                principal.RepairDefaults();
                return principal;
            }

            for (int i = 1; i <= BackupCount; i++)
            {
                var origem = $"backup{i}";
                var recuperado = TentarCarregarArquivo(BackupPath(i), origem);
                if (recuperado == null) continue;

                LogHelper.Warn($"CONFIG_LOAD_BACKUP_RECOVERED | origem={origem}");
                recuperado.RepairDefaults();

                // Restaura o principal a partir do backup — próxima leitura já acha o arquivo certo.
                try { recuperado.Salvar(); }
                catch (Exception ex) { LogHelper.Error("CONFIG_BACKUP_RESTORE_SAVE_FAILED", ex); }

                return recuperado;
            }

            LogHelper.Warn("CONFIG_LOAD_NOT_FOUND | nenhuma configuracao valida (principal, backups ou legado)");
            return null;
        }

        // Tenta ler+desserializar um arquivo de config, com pequenas retentativas para
        // sobreviver a um lock momentâneo (ex.: antivírus varrendo o arquivo). Nunca lança —
        // qualquer falha vira log e retorno null, deixando a chamada seguir para o próximo
        // candidato (backup/legado) em vez de decidir "instalação nova" na primeira falha.
        private static SipConfig? TentarCarregarArquivo(string path, string origem)
        {
            for (int tentativa = 1; tentativa <= 3; tentativa++)
            {
                try
                {
                    if (!File.Exists(path)) return null;

                    var texto = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(texto))
                    {
                        LogHelper.Warn($"CONFIG_LOAD_INVALID | origem={origem} motivo=arquivo_vazio");
                        return null;
                    }

                    var cfg = JsonSerializer.Deserialize<SipConfig>(texto);
                    if (cfg == null)
                    {
                        LogHelper.Warn($"CONFIG_LOAD_INVALID | origem={origem} motivo=deserializacao_nula");
                        return null;
                    }

                    return cfg;
                }
                catch (IOException) when (tentativa < 3)
                {
                    System.Threading.Thread.Sleep(150);
                }
                catch (Exception ex)
                {
                    LogHelper.Warn($"CONFIG_LOAD_INVALID | origem={origem} motivo={ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
            return null;
        }

        // Migra config do caminho antigo (%APPDATA% roaming) para o novo (%LOCALAPPDATA%),
        // uma única vez por processo. Não apaga o arquivo antigo — apenas copia. Idempotente:
        // se o arquivo novo já existe, não faz nada (evita sobrescrever dado mais recente).
        private static void MigrarConfigLegada()
        {
            if (_legacyMigrationChecked) return;
            _legacyMigrationChecked = true;

            try
            {
                if (File.Exists(ConfigFilePath)) return;

                var legado = LegacyConfigFilePath;
                if (!File.Exists(legado)) return;

                LogHelper.Info($"CONFIG_LEGACY_FOUND | path={legado}");

                var texto = File.ReadAllText(legado);
                var legacyCfg = JsonSerializer.Deserialize<SipConfig>(texto);
                if (legacyCfg == null)
                {
                    LogHelper.Warn("CONFIG_LEGACY_FOUND_BUT_INVALID | migracao ignorada");
                    return;
                }

                Directory.CreateDirectory(ConfigDir);
                File.Copy(legado, ConfigFilePath, overwrite: false);

                var legadoBackup = Path.ChangeExtension(legado, ".backup.json");
                if (File.Exists(legadoBackup))
                {
                    try { File.Copy(legadoBackup, BackupPath(1), overwrite: true); } catch { }
                }

                LogHelper.Info($"CONFIG_LEGACY_MIGRATED | de={legado} para={ConfigFilePath}");
            }
            catch (Exception ex)
            {
                LogHelper.Error("CONFIG_LEGACY_MIGRATION_FAILED", ex);
            }
        }

        public string ServerIp { get; set; } = "191.252.202.208";
        public int Port { get; set; } = 5061;
        public string Transport { get; set; } = "udp";

        public string ProxySip { get; set; } = "191.252.202.208:5061";
        public string Domain { get; set; } = "191.252.202.208";
        public string Login { get; set; } = string.Empty;
        public string Ramal { get; set; } = string.Empty;
        public string Senha { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string RamalNome    { get; set; } = string.Empty;
        public string NomeUsuario  { get; set; } = string.Empty;
        public int HistoricoRetencaoDias { get; set; } = 7;

        // AMI do Issabel/Asterisk para sincronizar ramais e nomes automaticamente.
        public bool AmiAtivo { get; set; } = true;
        public string AmiHost { get; set; } = string.Empty;
        public int AmiPorta { get; set; } = 5038;
        public string AmiUsuario { get; set; } = "waven";
        public string AmiSenha { get; set; } = "Waven@2025";
        public int AmiIntervaloMinutos { get; set; } = 10;

        // Permite transferência cega/assistida para números externos.
        // Quando desligado, apenas ramais internos podem ser transferidos.
        // Conferência continua podendo chamar números externos.
        public bool PermitirTransferenciaExterna { get; set; } = true;
        public string SalaConferenciaIssabel { get; set; } = "800";

        // Mapeamento dos canais de entrada do Issabel. O app procura estes valores
        // no SIP recebido (DID/DDR, Request-URI, To, Diversion, P-Called-Party-ID ou nome da rota).
        public string CanalOperadora { get; set; } = "6631998716;IN-BRDID-6631998716";
        public string Canal0800 { get; set; } = "08001901900;VONO-0800-ENTRADA";
        public string CanalWhatsAppTim { get; set; } = "556684263277;WAVOIP-556684263277";
        public string CanalWhatsAppVivo { get; set; } = "556696308630;WAVOIP-556696308630";

        // CDR / Histórico real do Issabel (MySQL)
        public bool CdrAtivo { get; set; } = false;
        public string CdrHost { get; set; } = string.Empty;
        public int CdrPorta { get; set; } = 3306;
        public string CdrBanco { get; set; } = "asteriskcdrdb";
        public string CdrTabela { get; set; } = "cdr";
        public string CdrUsuario { get; set; } = "waven";
        public string CdrSenha { get; set; } = string.Empty;
        // "MeuRamal" or "TodosRamais"
        public string HistoricoModoExibicao { get; set; } = "MeuRamal";
        public int HistoricoSyncIntervalMinutes { get; set; } = 0; // kept for migration only
        public int HistoricoSyncIntervalSeconds { get; set; } = 3;
        public int AmiSyncIntervalSeconds    { get; set; } = 3;
        public int GoogleSyncIntervalSeconds { get; set; } = 3;

        // Gravações de chamadas
        public bool GravacaoAtiva { get; set; } = false;
        // "URL" or "Local"
        public string GravacaoTipoAcesso { get; set; } = "URL";
        public string GravacaoUrlBase { get; set; } = "http://pabx.almeidagas.com/gravacoes/monitor/";
        public string GravacaoCaminhoLocal { get; set; } = string.Empty;

        public bool AutoUpdateEnabled { get; set; } = true;

        public bool LogEnabled         { get; set; } = true;
        public bool LogDetailedEnabled { get; set; } = false;

        // ── Áudio v1.2.13 ──────────────────────────────────────────────────────
        // Friendly names dos dispositivos; vazio = padrão do sistema (auto-detectado)
        public string AudioInputDevice  { get; set; } = string.Empty;  // microfone da ligação
        public string CallOutputDevice  { get; set; } = string.Empty;  // áudio da ligação
        public string RingOutputDevice  { get; set; } = string.Empty;  // saída do toque
        public int    RingVolume        { get; set; } = 80;            // 0-100

        // ── Company config auto-sync v1.2.14 ──────────────────────────────────
        public string    CompanyConfigVersion  { get; set; } = string.Empty;
        public DateTime? CompanyConfigLastSync { get; set; }

        // ── Waven API v1 ───────────────────────────────────────────────────────
        public bool   UsarWavenApi   { get; set; } = false;
        public string WavenApiUrl    { get; set; } = "https://api.almeidagas.com";
        public string WavenApiToken  { get; set; } = string.Empty;

        // ── Diagnóstico remoto v2.4.2 (ver DiagnosticTelemetryService) ─────────
        // Reaproveita WavenApiUrl/WavenApiToken acima — mesmo mecanismo de autenticação,
        // nenhuma credencial nova no cliente. Default true: frota é toda Almeida Gás: ligado
        // por padrão, mas pode ser desligado por instalação se necessário (ex.: pedido do
        // cliente, investigação em andamento que não deve gerar tráfego extra).
        public bool DiagnosticoRemotoAtivado { get; set; } = true;

        // ── Migrações automáticas ──────────────────────────────────────────────
        public bool MigracaoAplicada142   { get; set; } = false;
        public bool MigracaoWaba144Aplicada { get; set; } = false;

        // ── Status Online/Offline v2.1.1 ───────────────────────────────────────
        // Controlado localmente pelo WavenVoIP — não depende de discagem no Issabel.
        public bool IsOfflinePersistido { get; set; } = false;
    }
}
