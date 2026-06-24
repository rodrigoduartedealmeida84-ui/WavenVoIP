using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WavenVoIP.Services;

public static class UpdateService
{
    private const string VersionUrl = "https://raw.githubusercontent.com/rodrigoduartedealmeida84-ui/WavenVoIP/main/servidor_hostinger/version.json";

    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly string FlagPath =
        Path.Combine(LocalAppData, "WavenVoIP", "update_failed.flag");

    private static readonly string AttemptsPath =
        Path.Combine(LocalAppData, "WavenVoIP", "update_attempts.txt");

    public static Func<bool>? EmChamadaAtiva { get; set; }

    public static event Action<UpdateProgress>? ProgressoChanged;
    public static UpdateProgress EstadoAtual { get; private set; } = new UpdateProgress();

    private static int _checking;
    private static string? _pendingZipUrl;
    private static string? _pendingSha256;
    private static string? _pendingVersao;

    public static bool TemAtualizacaoPendente => _pendingVersao != null;
    public static string? VersaoPendente      => _pendingVersao;

    // ── Flag file API ─────────────────────────────────────────────────────────

    public static bool TemFalhaFlag() => File.Exists(FlagPath);

    public static void LimparFlagFalha()
    {
        try
        {
            if (File.Exists(FlagPath)) File.Delete(FlagPath);
            if (File.Exists(AttemptsPath)) File.Delete(AttemptsPath);
            LogHelper.Update("[UPDATE_FAILED_FLAG_CLEARED] Flag de falha removida pelo usuario");
        }
        catch (Exception ex)
        {
            LogHelper.Update($"[CLEAR_FLAG_ERROR] {ex.Message}", LogLevel.WARN);
        }
    }

    private static void CriarFlagFalha(string motivo)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FlagPath)!);
            File.WriteAllText(FlagPath, $"{DateTime.Now:O}\n{motivo}");
            LogHelper.Update($"[UPDATE_FAILED_FLAG_CREATED] {motivo}");
        }
        catch { }
    }

    // Records this attempt and returns false (blocking) if too many recent attempts detected.
    private static bool RegistrarTentativaEVerificarLoop()
    {
        try
        {
            var now = DateTime.Now;
            Directory.CreateDirectory(Path.GetDirectoryName(AttemptsPath)!);

            var lines = File.Exists(AttemptsPath)
                ? new List<string>(File.ReadAllLines(AttemptsPath))
                : new List<string>();

            // Prune entries older than 24 h
            lines = lines
                .Where(l => DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) && (now - dt).TotalHours < 24)
                .ToList();

            var recentCount = lines.Count(l =>
                DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) &&
                (now - dt).TotalMinutes < 10);

            LogHelper.Update($"[UPDATE_LOOP_GUARD] Tentativas ultimos 10 min: {recentCount} | ultimas 24h: {lines.Count}");

            if (recentCount >= 2)
            {
                CriarFlagFalha($"Loop detectado: {recentCount + 1} tentativas em menos de 10 minutos");
                return false;
            }

            lines.Add(now.ToString("O"));
            File.WriteAllLines(AttemptsPath, lines);
            return true;
        }
        catch
        {
            return true; // allow if we can't check
        }
    }

    // ── Public entry points ───────────────────────────────────────────────────

    public static async Task VerificarAtualizacaoAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8));

        if (TemFalhaFlag())
        {
            LogHelper.Update("[AUTO_UPDATE_PAUSED] Flag de falha encontrada — atualizacao automatica pausada");
            Reportar(new UpdateProgress
            {
                Stage        = UpdateStage.Error,
                Message      = "Atualizacoes automaticas pausadas (falha anterior detectada)",
                ErrorMessage = "Abra Configuracoes > Atualizacoes para limpar a falha ou verificar manualmente."
            });
            return;
        }

        var cfg = WavenVoIP.SipConfig.CarregarSalva();
        if (cfg != null && !cfg.AutoUpdateEnabled)
        {
            LogHelper.Update("[CHECK_AUTO_SKIP] Atualizacoes automaticas desativadas nas configuracoes");
            return;
        }

        if (!RegistrarTentativaEVerificarLoop())
        {
            LogHelper.Update("[UPDATE_LOOP_GUARD] Atualizacao bloqueada por anti-loop");
            Reportar(new UpdateProgress
            {
                Stage        = UpdateStage.Error,
                Message      = "Atualizacao automatica bloqueada (muitas tentativas recentes)",
                ErrorMessage = "Abra Configuracoes > Atualizacoes para limpar a falha."
            });
            return;
        }

        await ExecutarVerificacaoAsync(manual: false);
    }

    public static Task<UpdateCheckResult> VerificarManualAsync()
        => ExecutarVerificacaoAsync(manual: true);

    public static Task LancarPendenteAsync()
    {
        if (_pendingZipUrl == null || _pendingVersao == null) return Task.CompletedTask;
        return LancarUpdaterAsync(_pendingZipUrl, _pendingSha256 ?? string.Empty, _pendingVersao);
    }

    // ── Core verification logic ───────────────────────────────────────────────

    private static async Task<UpdateCheckResult> ExecutarVerificacaoAsync(bool manual)
    {
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
        {
            LogHelper.Update("[CHECK_SKIP] Verificacao ja em andamento");
            return UpdateCheckResult.JaVerificando;
        }

        try
        {
            Reportar(new UpdateProgress { Stage = UpdateStage.Checking, Message = "Verificando atualizacoes..." });
            LogHelper.Update($"[CHECK] Local={VersionService.Versao}");

            // Cache-busting: raw.githubusercontent.com fica atras de CDN e pode servir
            // uma copia antiga por alguns minutos apos um push. A query string unica
            // forca tratamento como recurso novo e evita falso "ja esta atualizado".
            var urlComCacheBusting = $"{VersionUrl}?ticks={DateTime.UtcNow.Ticks}";
            LogHelper.Update($"[CHECK_URL] {urlComCacheBusting}");

            string json;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.CacheControl =
                    new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                http.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
                json = await http.GetStringAsync(urlComCacheBusting);
                LogHelper.Update($"[CHECK_JSON] Recebido {json.Length} bytes: {json.Replace('\n', ' ').Replace('\r', ' ')}");
            }
            catch (Exception ex)
            {
                var errMsg = $"Erro ao consultar servidor: {ex.Message}";
                Reportar(new UpdateProgress { Stage = UpdateStage.Error, Message = errMsg, ErrorMessage = ex.Message });
                LogHelper.Update($"[CHECK_ERROR] {ex.GetType().Name}: {ex.Message}", LogLevel.WARN);
                LogHelper.Update("[DECISAO] ERRO_FALHA_DOWNLOAD_VERSION_JSON", LogLevel.WARN);
                return UpdateCheckResult.Erro;
            }

            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("versao", out var propVer) ||
                !root.TryGetProperty("zip",    out var propZip))
            {
                var errMsg = "version.json invalido: campos 'versao'/'zip' ausentes";
                Reportar(new UpdateProgress { Stage = UpdateStage.Error, Message = errMsg, ErrorMessage = errMsg });
                LogHelper.Update($"[CHECK_SKIP] {errMsg}");
                LogHelper.Update("[DECISAO] ERRO_CAMPO_VERSAO_AUSENTE", LogLevel.WARN);
                return UpdateCheckResult.Erro;
            }

            var versaoRemota = propVer.GetString() ?? string.Empty;
            var zipUrl       = propZip.GetString() ?? string.Empty;
            var sha256       = root.TryGetProperty("sha256", out var h) ? (h.GetString() ?? string.Empty) : string.Empty;

            LogHelper.Update($"[CHECK] Remota={versaoRemota} | Local={VersionService.Versao}");
            LogHelper.Update($"[PACOTE] {zipUrl}");
            LogHelper.Update($"[SHA256] {sha256}");

            if (string.IsNullOrWhiteSpace(versaoRemota) || string.IsNullOrWhiteSpace(zipUrl))
            {
                Reportar(new UpdateProgress { Stage = UpdateStage.Error, Message = "version.json invalido", ErrorMessage = "versao ou zip vazios" });
                LogHelper.Update("[DECISAO] ERRO_VERSAO_OU_ZIP_VAZIOS", LogLevel.WARN);
                return UpdateCheckResult.Erro;
            }

            var agora      = DateTime.Now;
            var comparacao = CompararVersoes(versaoRemota, VersionService.Versao);
            LogHelper.Update($"[CHECK] Comparacao: remota={versaoRemota} vs local={VersionService.Versao} => {comparacao}");

            if (!IsRemotaMaior(versaoRemota, VersionService.Versao))
            {
                LogHelper.Update($"[CHECK_OK] Versao atual e a mais recente ({VersionService.Versao})");
                LogHelper.Update("[DECISAO] NAO_ATUALIZAR");
                Reportar(new UpdateProgress
                {
                    Stage         = UpdateStage.UpToDate,
                    Message       = $"Versao {VersionService.Versao} e a mais recente",
                    RemoteVersion = versaoRemota,
                    CheckedAt     = agora
                });
                return UpdateCheckResult.EmDia;
            }

            LogHelper.Update($"[DISPONIVEL] Atualizacao disponivel: {VersionService.Versao} -> {versaoRemota}");
            LogHelper.Update("[DECISAO] ATUALIZAR");
            _pendingZipUrl = zipUrl;
            _pendingSha256 = sha256;
            _pendingVersao = versaoRemota;

            Reportar(new UpdateProgress
            {
                Stage         = UpdateStage.UpdateAvailable,
                Message       = $"Nova versao disponivel: {versaoRemota}",
                RemoteVersion = versaoRemota,
                CheckedAt     = agora
            });

            if (!manual)
            {
                for (int i = 0; i < 60 && EmChamadaAtiva?.Invoke() == true; i++)
                {
                    LogHelper.Update("[AGUARD_CHAMADA] Chamada ativa, aguardando 30 s...");
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }

                if (EmChamadaAtiva?.Invoke() == true)
                {
                    LogHelper.Update("[SKIP] Em chamada por muito tempo — update adiado para proxima sessao");
                    return UpdateCheckResult.EmDia;
                }

                await LancarUpdaterAsync(zipUrl, sha256, versaoRemota);
            }

            return UpdateCheckResult.AtualizacaoIniciada;
        }
        catch (Exception ex)
        {
            Reportar(new UpdateProgress
            {
                Stage        = UpdateStage.Error,
                Message      = $"Erro inesperado: {ex.Message}",
                ErrorMessage = ex.ToString()
            });
            LogHelper.Update($"[CHECK_ERROR] {ex.GetType().Name}: {ex.Message}", LogLevel.WARN);
            LogHelper.Update("[DECISAO] ERRO_INESPERADO", LogLevel.WARN);
            return UpdateCheckResult.Erro;
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private static void Reportar(UpdateProgress progress)
    {
        EstadoAtual = progress;
        ProgressoChanged?.Invoke(progress);
    }

    // ── Launch WavenUpdater ───────────────────────────────────────────────────

    private static async Task LancarUpdaterAsync(string zipUrl, string sha256, string novaVersao)
    {
        Reportar(new UpdateProgress { Stage = UpdateStage.Launching, Message = "Iniciando atualizacao..." });

        var appDir     = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        var updaterSrc = Path.Combine(appDir, "WavenUpdater.exe");

        LogHelper.Update($"[LAUNCH_PREP] appDir={appDir}");
        LogHelper.Update($"[LAUNCH_PREP] updaterSrc={updaterSrc}");

        if (!File.Exists(updaterSrc))
        {
            var msg = $"WavenUpdater.exe nao encontrado em: {updaterSrc}";
            CriarFlagFalha(msg);
            Reportar(new UpdateProgress { Stage = UpdateStage.Error, Message = msg, ErrorMessage = msg });
            LogHelper.Update($"[NO_UPDATER] {msg}", LogLevel.WARN);
            return;
        }

        // Copy all 4 required .NET host files to an isolated temp directory
        var tempDir = Path.Combine(Path.GetTempPath(), $"WavenUpdater_{DateTime.Now:yyyyMMddHHmmss}");
        var tempExe = Path.Combine(tempDir, "WavenUpdater.exe");

        try
        {
            Directory.CreateDirectory(tempDir);

            var updaterFiles = new[] { "WavenUpdater.exe", "WavenUpdater.dll", "WavenUpdater.deps.json", "WavenUpdater.runtimeconfig.json" };
            foreach (var fileName in updaterFiles)
            {
                var src = Path.Combine(appDir, fileName);
                var dst = Path.Combine(tempDir, fileName);
                if (File.Exists(src))
                {
                    File.Copy(src, dst, overwrite: true);
                    LogHelper.Update($"[LAUNCH_COPY] {fileName} -> {dst}");
                }
                else if (fileName == "WavenUpdater.exe")
                {
                    throw new FileNotFoundException($"Arquivo obrigatorio ausente: {src}");
                }
                else
                {
                    LogHelper.Update($"[LAUNCH_COPY_WARN] {fileName} nao encontrado em {src} — continuando", LogLevel.WARN);
                }
            }
        }
        catch (Exception ex)
        {
            var msg = $"Nao foi possivel preparar updater em temp: {ex.Message}";
            CriarFlagFalha(msg);
            Reportar(new UpdateProgress { Stage = UpdateStage.Error, Message = msg, ErrorMessage = ex.Message });
            LogHelper.Update($"[COPY_ERROR] {msg}", LogLevel.ERROR);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            return;
        }

        var pid  = Environment.ProcessId;
        var args = $"--pid {pid}" +
                   $" --zip \"{zipUrl}\"" +
                   $" --sha256 \"{sha256}\"" +
                   $" --ver \"{novaVersao}\"" +
                   $" --oldver \"{VersionService.Versao}\"" +
                   $" --app-dir \"{appDir}\"" +
                   $" --exe \"WavenVoIP.exe\"";

        LogHelper.Update($"[UPDATER_START_ATTEMPT] {tempExe}");
        LogHelper.Update($"[LAUNCH_ARGS] {args}");
        LogHelper.Update($"[LAUNCH_CMD] \"{tempExe}\" {args}");
        LogHelper.Update($"[LAUNCH_WORKDIR] {tempDir}");

        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName         = tempExe,
                Arguments        = args,
                UseShellExecute  = true,  // required for WPF window station initialization
                WorkingDirectory = tempDir
            });
        }
        catch (Exception ex)
        {
            LogHelper.Update($"[UPDATER_START_FAILED] {ex.Message}", LogLevel.ERROR);
            CriarFlagFalha($"Falha ao iniciar WavenUpdater: {ex.Message}");
            Reportar(new UpdateProgress
            {
                Stage        = UpdateStage.Error,
                Message      = $"Erro ao iniciar atualizador: {ex.Message}",
                ErrorMessage = ex.Message
            });
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            LogHelper.Update("[APP_CLOSE_CANCELLED] WavenVoIP nao sera fechado — updater nao iniciou");
            return;
        }

        // Wait briefly and verify the updater is still running
        await Task.Delay(900);

        bool updaterRodando;
        try
        {
            updaterRodando = proc != null && !proc.HasExited;
        }
        catch
        {
            updaterRodando = false;
        }

        if (!updaterRodando)
        {
            int exitCode = -1;
            try { exitCode = proc?.ExitCode ?? -1; } catch { }

            var msg = $"WavenUpdater fechou imediatamente (exitCode={exitCode})";
            LogHelper.Update($"[UPDATER_START_FAILED] {msg}", LogLevel.ERROR);
            CriarFlagFalha(msg);
            Reportar(new UpdateProgress
            {
                Stage        = UpdateStage.Error,
                Message      = "Falha ao iniciar atualizador (fechou imediatamente)",
                ErrorMessage = msg
            });
            LogHelper.Update("[APP_CLOSE_CANCELLED] WavenVoIP nao sera fechado — updater nao esta rodando");
            return;
        }

        LogHelper.Update($"[UPDATER_START_OK] PID={proc!.Id}");

        // Updater confirmed running — safe to close WavenVoIP
        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CompararVersoes(string remota, string local)
    {
        if (Version.TryParse(StripSuffix(remota), out var vR) &&
            Version.TryParse(StripSuffix(local),  out var vL))
        {
            if (vR > vL) return "REMOTA_MAIOR";
            if (vR < vL) return "LOCAL_MAIOR";
            return "IGUAIS";
        }
        return "COMPARACAO_FALHOU";
    }

    private static bool IsRemotaMaior(string remota, string local)
    {
        if (Version.TryParse(StripSuffix(remota), out var vR) &&
            Version.TryParse(StripSuffix(local),  out var vL))
            return vR > vL;
        return false;
    }

    private static string StripSuffix(string ver)
    {
        var idx = ver.IndexOfAny(new[] { '-', '+' });
        return idx >= 0 ? ver[..idx] : ver;
    }
}
