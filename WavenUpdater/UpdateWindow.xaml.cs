using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace WavenUpdater;

public partial class UpdateWindow : Window
{
    private readonly UpdateOptions _opts;
    private readonly string        _logPath;
    private readonly string        _backupDir;
    private bool                   _updateDone;
    private bool                   _allowClose;
    private readonly CancellationTokenSource _cts = new();

    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public UpdateWindow(UpdateOptions opts)
    {
        InitializeComponent();
        _opts      = opts;
        _logPath   = Path.Combine(LocalAppData, "WavenVoIP", "Logs", "update.log");
        _backupDir = Path.Combine(LocalAppData, "WavenVoIP", "Backup");

        if (!string.IsNullOrWhiteSpace(opts.OldVersion) && !string.IsNullOrWhiteSpace(opts.NewVersion))
            txtVersion.Text = $"v{opts.OldVersion} → v{opts.NewVersion}";

        Loaded += (_, _) =>
        {
            CleanOldTempUpdaters();
            _ = RunUpdateAsync();
        };
    }

    // ── UI helpers ───────────────────────────────────────────────────────────

    private void UI(Action a) => Dispatcher.Invoke(a);

    private void SetStatus(string msg, double? pct = null, string? pctLabel = null)
        => UI(() =>
        {
            txtStatus.Text = msg;
            if (pct.HasValue)
            {
                progressBar.IsIndeterminate = false;
                progressBar.Value = pct.Value;
            }
            if (pctLabel != null) txtPercent.Text = pctLabel;
        });

    private void SetIndeterminate(string msg)
        => UI(() =>
        {
            txtStatus.Text = msg;
            progressBar.IsIndeterminate = true;
            txtPercent.Text = "";
        });

    private void ShowHint(string hint)
        => UI(() =>
        {
            txtHint.Text = hint;
            txtHint.Visibility = Visibility.Visible;
        });

    // ── Main pipeline ────────────────────────────────────────────────────────

    private async Task RunUpdateAsync()
    {
        try
        {
            Log("START",       $"Iniciando atualização → v{_opts.NewVersion} | app-dir={_opts.AppDir}");
            Log("VERSAO_LOCAL", _opts.OldVersion ?? "(desconhecida)");
            Log("VERSAO_NOVA",  _opts.NewVersion ?? "(desconhecida)");
            Log("PACOTE_URL",   _opts.ZipUrl     ?? "(vazia)");
            Log("SHA256",       _opts.Sha256      ?? "(vazio)");

            await WaitForMainProcessAsync();

            if (string.IsNullOrWhiteSpace(_opts.ZipUrl))
                throw new InvalidOperationException("URL do pacote não foi informada pelo launcher (--zip vazio).");

            var zipBytes = await DownloadWithRetryAsync(_opts.ZipUrl, maxRetries: 3);

            SetIndeterminate("Verificando integridade...");
            VerifyIntegrity(zipBytes);

            SetIndeterminate("Criando backup da versão atual...");
            BackupCurrentApp(_opts.AppDir);

            await ExtractAsync(zipBytes, _opts.AppDir);

            SetStatus("Atualização concluída!", 100, "100%");
            Log("SUCCESS", $"v{_opts.NewVersion} instalada com sucesso");

            await Task.Delay(1200, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("CANCELLED", "Atualização cancelada pelo usuário");
            _allowClose = true;
            UI(() => Application.Current.Shutdown(2));
            return;
        }
        catch (Exception ex)
        {
            Log("ERROR", ex.ToString());
            SetStatus("Falha na atualização — use os botões abaixo");
            ShowHint($"Erro: {ex.Message}");
            await TryRollbackAsync();
            await Task.Delay(500);
            _allowClose = true;
            UI(() => panelErrorButtons.Visibility = Visibility.Visible);
            // DO NOT auto-close or auto-relaunch — let the user decide
            return;
        }

        // Success path only
        _updateDone = true;
        LaunchApp();
        UI(() => Application.Current.Shutdown(0));
    }

    // ── Step 1: wait for main process ────────────────────────────────────────

    private async Task WaitForMainProcessAsync()
    {
        if (_opts.MainPid <= 0) return;
        SetIndeterminate("Aguardando WavenVoIP fechar...");

        await Task.Run(() =>
        {
            try
            {
                using var proc = Process.GetProcessById(_opts.MainPid);
                proc.WaitForExit(20_000);
            }
            catch (ArgumentException) { } // already exited
        }, _cts.Token);

        // Extra margin so OS releases file handles
        await Task.Delay(700, _cts.Token);
    }

    // ── Step 2: download with retry ──────────────────────────────────────────

    private async Task<byte[]> DownloadWithRetryAsync(string url, int maxRetries)
    {
        Exception? lastEx = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await DownloadAsync(url, attempt, maxRetries);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastEx = ex;
                Log("DOWNLOAD_RETRY", $"Tentativa {attempt}/{maxRetries} falhou: {ex.Message}");

                if (attempt < maxRetries)
                {
                    var wait = 2000 * attempt;
                    SetIndeterminate($"Download falhou (tentativa {attempt}/{maxRetries}). Aguardando {wait / 1000}s...");
                    ShowHint($"Erro: {ex.Message}");
                    await Task.Delay(wait, _cts.Token);
                    UI(() => txtHint.Visibility = Visibility.Collapsed);
                }
            }
        }

        throw new IOException($"Download falhou após {maxRetries} tentativas.", lastEx);
    }

    private async Task<byte[]> DownloadAsync(string url, int attempt, int maxRetries)
    {
        // Version marker — confirms this binary has the 403 fix
        Log("DOWNLOAD_CODE_VERSION", "2026-05-24-FIX403-V2");

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect      = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
        http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

        Log("DOWNLOAD_TENTATIVA", $"{attempt}/{maxRetries}");
        Log("DOWNLOAD_URL",       url);

        // HEAD pre-check on first attempt — exposes WAF/CDN/ModSecurity response headers
        if (attempt == 1)
        {
            Log("DOWNLOAD_PRECHECK_START", url);
            try
            {
                using var headResp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), _cts.Token);
                Log("HEAD_STATUS",  $"{headResp.StatusCode} ({(int)headResp.StatusCode})");
                Log("HEAD_LENGTH",  headResp.Content.Headers.ContentLength?.ToString() ?? "(unknown)");
                Log("HEAD_SERVER",  headResp.Headers.TryGetValues("Server",               out var srv) ? string.Join(", ", srv) : "(none)");
                Log("HEAD_CF_RAY",  headResp.Headers.TryGetValues("CF-Ray",               out var cf)  ? string.Join(", ", cf)  : "(none)");
                Log("HEAD_MODSEC",  headResp.Headers.TryGetValues("X-ModSecurity-Status", out var ms2) ? string.Join(", ", ms2) : "(none)");

                if (!headResp.IsSuccessStatusCode)
                {
                    try
                    {
                        var headBody    = await headResp.Content.ReadAsStringAsync(_cts.Token);
                        var headSnippet = headBody.Length > 500 ? headBody[..500] : headBody;
                        Log("HEAD_BODY", headSnippet.Replace('\n', ' ').Replace('\r', ' '));
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log("HEAD_ERROR", ex.ToString());
            }
        }

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
        Log("DOWNLOAD_HTTP", $"Status={resp.StatusCode} ({(int)resp.StatusCode}) ContentType={resp.Content.Headers.ContentType}");

        if (!resp.IsSuccessStatusCode)
        {
            // Log WAF block-page to expose the exact rule triggered
            try
            {
                var body    = await resp.Content.ReadAsStringAsync(_cts.Token);
                var snippet = body.Length > 500 ? body[..500] : body;
                Log("DOWNLOAD_ERROR_BODY", snippet.Replace('\n', ' ').Replace('\r', ' '));
            }
            catch { }

            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                Log("DOWNLOAD_FALLBACK_START", $"HttpClient bloqueado (403) tentativa {attempt}/{maxRetries} — ativando WebClient");
                return await DownloadWithWebClientAsync(url, attempt, maxRetries);
            }
        }

        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        using var src = await resp.Content.ReadAsStreamAsync(_cts.Token);
        using var ms  = new MemoryStream(total > 0 ? (int)total : 8 * 1024 * 1024);
        var buf = new byte[65536];
        long received = 0;
        int  n;

        while ((n = await src.ReadAsync(buf, _cts.Token)) > 0)
        {
            ms.Write(buf, 0, n);
            received += n;

            if (total > 0)
            {
                var pct   = (double)received / total * 75.0; // 0–75 % for download phase
                var label = $"{received / 1024.0:N0} KB / {total / 1024.0:N0} KB";
                SetStatus($"Baixando... (tentativa {attempt}/{maxRetries})", pct, label);
            }
            else
            {
                SetIndeterminate($"Baixando... {received / 1024.0:N0} KB");
            }
        }

        Log("DOWNLOAD_OK", $"{received:N0} bytes");
        return ms.ToArray();
    }

    private async Task<byte[]> DownloadWithWebClientAsync(string url, int attempt, int maxRetries)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

#pragma warning disable SYSLIB0014
        using var wc = new WebClient();
#pragma warning restore SYSLIB0014
        wc.Headers.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36");
        wc.Headers.Add("Accept",          "*/*");
        wc.Headers.Add("Accept-Language", "pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
        wc.Headers.Add("Cache-Control",   "no-cache");

        SetIndeterminate($"Baixando via fallback... (tentativa {attempt}/{maxRetries})");
        Log("WEBCLIENT_START", url);

        using var reg = _cts.Token.Register(wc.CancelAsync);
        try
        {
            var data = await wc.DownloadDataTaskAsync(url);
            Log("WEBCLIENT_OK", $"{data.Length:N0} bytes");
            return data;
        }
        catch (Exception ex)
        {
            _cts.Token.ThrowIfCancellationRequested();
            Log("WEBCLIENT_ERROR", ex.ToString());
            throw new IOException($"WebClient fallback falhou (tentativa {attempt}/{maxRetries}): {ex.Message}", ex);
        }
    }

    // ── Step 3: integrity ────────────────────────────────────────────────────

    private void VerifyIntegrity(byte[] data)
    {
        if (string.IsNullOrWhiteSpace(_opts.Sha256))
        {
            Log("INTEGRITY_SKIP", "SHA-256 não informado, pulando verificação");
            return;
        }

        var actual   = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var expected = _opts.Sha256.ToLowerInvariant().Replace("-", "").Replace(" ", "");

        if (actual != expected)
            throw new InvalidDataException(
                $"Verificação de integridade falhou.\nEsperado: {expected}\nRecebido:  {actual}");

        Log("INTEGRITY_OK", "SHA-256 verificado com sucesso");
    }

    // ── Step 4: backup ───────────────────────────────────────────────────────

    private void BackupCurrentApp(string srcDir)
    {
        try
        {
            if (!Directory.Exists(srcDir)) return;

            var ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = Path.Combine(_backupDir, $"backup_{ts}");
            Directory.CreateDirectory(dest);

            foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var rel      = Path.GetRelativePath(srcDir, file);
                var destFile = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(file, destFile, overwrite: true);
            }

            Log("BACKUP_OK", dest);
            PruneOldBackups(_backupDir, keep: 3);
        }
        catch (Exception ex)
        {
            Log("BACKUP_WARN", $"Backup falhou (não crítico): {ex.Message}");
        }
    }

    private static void PruneOldBackups(string dir, int keep)
    {
        try
        {
            foreach (var old in Directory.EnumerateDirectories(dir, "backup_*")
                         .OrderByDescending(x => x).Skip(keep))
                Directory.Delete(old, recursive: true);
        }
        catch { }
    }

    // Files that must NEVER be written into the app directory — they belong in APPDATA/LOCALAPPDATA
    private static readonly HashSet<string> _sensitiveFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sipconfig.json",
        "sipconfig.backup.json",
        "sipconfig.backup1.json",
        "sipconfig.backup2.json",
        "sipconfig.backup3.json",
        "contatos.json",
        "historico.json",
        "audio-config.json",
        "whatsapp_config.json",
        "whatsapp_envios.json",
        "google_contacts_cache.json",
        "user.config",
        "settings.json",
        "crash.log",
    };

    // ── Step 5: extract ──────────────────────────────────────────────────────

    private async Task ExtractAsync(byte[] zipBytes, string targetDir)
    {
        SetStatus("Instalando arquivos...", 77);
        Directory.CreateDirectory(targetDir);

        // Remove any sensitive files that may have been placed in the app directory
        // by a previous (buggy) version. User data must live in APPDATA/LOCALAPPDATA only.
        RemoveSensitiveFilesFromAppDir(targetDir);

        await Task.Run(() =>
        {
            using var ms  = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            int total = zip.Entries.Count;
            int done  = 0;
            var root  = Path.GetFullPath(targetDir);

            foreach (var entry in zip.Entries)
            {
                _cts.Token.ThrowIfCancellationRequested();

                var destFull = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));

                // Guard against ZIP path traversal
                if (!destFull.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    Log("EXTRACT_BLOCKED", $"Path traversal bloqueado: {entry.FullName}");
                    continue;
                }

                // Never extract user-data files into the app directory
                if (_sensitiveFileNames.Contains(entry.Name))
                {
                    Log("EXTRACT_SKIPPED_SENSITIVE", $"Arquivo sensível ignorado: {entry.FullName}");
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Name)) // directory entry
                {
                    Directory.CreateDirectory(destFull);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                    WriteEntryWithRetry(entry, destFull);
                }

                done++;
                var pct = 77.0 + (double)done / total * 20.0; // 77–97 %
                SetStatus("Instalando arquivos...", pct, $"{done}/{total}");
            }
        }, _cts.Token);
    }

    private void RemoveSensitiveFilesFromAppDir(string appDir)
    {
        try
        {
            foreach (var name in _sensitiveFileNames)
            {
                var path = Path.Combine(appDir, name);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Log("CLEANUP_SENSITIVE", $"Removido arquivo sensível do diretório do app: {path}");
                }
            }
        }
        catch (Exception ex)
        {
            Log("CLEANUP_WARN", $"Limpeza de arquivos sensíveis falhou (não crítico): {ex.Message}");
        }
    }

    private static void WriteEntryWithRetry(ZipArchiveEntry entry, string destPath)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var src = entry.Open();
                src.CopyTo(dst);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(250);
            }
        }
    }

    // ── Rollback ─────────────────────────────────────────────────────────────

    private async Task TryRollbackAsync()
    {
        try
        {
            var latest = Directory.EnumerateDirectories(_backupDir, "backup_*")
                .OrderByDescending(x => x)
                .FirstOrDefault();

            if (latest == null)
            {
                Log("ROLLBACK_SKIP", "Nenhum backup disponível");
                return;
            }

            SetIndeterminate("Restaurando versão anterior...");
            Log("ROLLBACK_START", latest);

            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(latest, "*", SearchOption.AllDirectories))
                {
                    var rel  = Path.GetRelativePath(latest, file);
                    var dest = Path.Combine(_opts.AppDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: true);
                }
            });

            Log("ROLLBACK_OK", "Versão anterior restaurada com sucesso");
        }
        catch (Exception ex)
        {
            Log("ROLLBACK_ERROR", ex.Message);
        }
    }

    // ── Restart ──────────────────────────────────────────────────────────────

    private void LaunchApp()
    {
        var exeName = string.IsNullOrWhiteSpace(_opts.ExeName) ? "WavenVoIP.exe" : _opts.ExeName;
        var exePath = Path.Combine(_opts.AppDir, exeName);
        Log("LAUNCH", exePath);

        try
        {
            if (File.Exists(exePath))
                Process.Start(new ProcessStartInfo
                {
                    FileName         = exePath,
                    UseShellExecute  = true,
                    WorkingDirectory = _opts.AppDir
                });
            else
                Log("LAUNCH_MISSING", $"Executável não encontrado: {exePath}");
        }
        catch (Exception ex)
        {
            Log("LAUNCH_ERROR", ex.Message);
        }
    }

    // ── Logging ──────────────────────────────────────────────────────────────

    private void Log(string tag, string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(
                _logPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [UPDATER_{tag}] {msg}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { }
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    private static void CleanOldTempUpdaters()
    {
        try
        {
            var tmp = Path.GetTempPath();
            // Clean temp directories (current approach)
            foreach (var dir in Directory.EnumerateDirectories(tmp, "WavenUpdater_*"))
            {
                try
                {
                    if ((DateTime.Now - Directory.GetLastWriteTime(dir)).TotalMinutes > 10)
                        Directory.Delete(dir, recursive: true);
                }
                catch { }
            }
            // Legacy: clean old single-file copies from earlier versions
            foreach (var f in Directory.EnumerateFiles(tmp, "WavenUpdater_*.exe"))
            {
                try
                {
                    if ((DateTime.Now - File.GetLastWriteTime(f)).TotalMinutes > 10)
                        File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    // ── Button: open app (on error) ──────────────────────────────────────────

    private void BtnAbrirApp_Click(object sender, RoutedEventArgs e)
    {
        Log("MANUAL_LAUNCH", "Usuário solicitou abertura manual do WavenVoIP após falha");
        LaunchApp();
        _allowClose = true;
        _updateDone = true;
        UI(() => Application.Current.Shutdown(1));
    }

    // ── Button: copy log ─────────────────────────────────────────────────────

    private void BtnCopiarLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = File.Exists(_logPath)
                ? File.ReadAllText(_logPath, Encoding.UTF8)
                : "(log vazio ou arquivo não encontrado)";
            Clipboard.SetText(content);
            btnCopiarLog.Content = "Copiado!";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao copiar log:\n{ex.Message}", "WavenUpdater");
        }
    }

    // ── Window events ────────────────────────────────────────────────────────

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_allowClose || _updateDone) return;

        var r = MessageBox.Show(
            "Fechar agora pode deixar o WavenVoIP em estado inconsistente.\n\nDeseja realmente cancelar a atualização?",
            "Atualização em andamento",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (r == MessageBoxResult.No)
        {
            e.Cancel = true;
            return;
        }

        _cts.Cancel();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
