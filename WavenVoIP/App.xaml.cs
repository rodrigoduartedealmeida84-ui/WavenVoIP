using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using WavenVoIP.Services;
using WavenVoIP.Views;

namespace WavenVoIP;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateEvent;
    private static WavenApiSyncService? _wavenSyncService;

    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WavenVoIP", "crash.log");

    private static readonly string FreshInstallFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WavenVoIP", "fresh_install.flag");

    public static void LogCrash(string tag, string context, Exception? ex)
    {
        try
        {
            var pasta = Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrWhiteSpace(pasta)) Directory.CreateDirectory(pasta);
            var linha = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tag} | {context}\n{ex}\n---\n";
            File.AppendAllText(CrashLogPath, linha);
        }
        catch { }
        try { LogHelper.Error($"{tag} | {context}", ex); } catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // v2.1.1 — Quando iniciado pelo Run key do Windows (autostart), o processo herda
        // o WorkingDirectory do explorer.exe (ex: System32), não a pasta do executável.
        // Isso quebra qualquer caminho relativo (ícone, assets). Corrige sempre na raiz.
        try { Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory; } catch { }

        try { LogHelper.Info($"APP_WORKING_DIRECTORY | {Environment.CurrentDirectory}"); } catch { }
        try { LogHelper.Info($"APP_EXECUTABLE_PATH | {Environment.ProcessPath ?? AppDomain.CurrentDomain.BaseDirectory}"); } catch { }
        try { VerificarAutoStart(); } catch (Exception ex) { LogCrash("AUTOSTART_PATH_CHECK_ERROR", "Falha ao verificar entrada de autostart", ex); }

        const string mutexName = "WavenVoIP_SingleInstance_v79";
        const string eventName = "WavenVoIP_ActivateEvent_v79";

        _instanceMutex = new Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            // Sinaliza instância existente para trazer ao foco
            try
            {
                using var evt = EventWaitHandle.OpenExisting(eventName);
                evt.Set();
            }
            catch { }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Current.Shutdown();
            return;
        }

        // Cria evento nomeado e monitora sinais de segunda instância
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        var activateThread = new Thread(() =>
        {
            try
            {
                while (_activateEvent?.WaitOne() == true)
                    Dispatcher.Invoke(RestaurarJanelaPrincipal);
            }
            catch { }
        })
        { IsBackground = true, Name = "ActivateListener" };
        activateThread.Start();

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, ex) => { ex.SetObserved(); };

        base.OnStartup(e);

        // ── Migrações automáticas (rodam uma vez por máquina, antes de tudo) ────
        Services.MigracaoService.AplicarMigracao142();
        Services.MigracaoService.AplicarMigracao144();

        // ── Fresh install detection ───────────────────────────────────────────
        if (File.Exists(FreshInstallFlagPath))
        {
            LogHelper.Info("FRESH_INSTALL_FLAG_FOUND — instalacao nova detectada pelo flag");
            LogHelper.Info("INSTALL_MODE=FRESH");

            SipConfig.CriarBackup();

            try
            {
                var oldCfg = SipConfig.CarregarSalva();
                if (oldCfg != null)
                {
                    LogHelper.Info($"OLD_USER_CONFIG_IGNORED — config anterior: Ramal={oldCfg.Ramal} Login={oldCfg.Login} Nome={oldCfg.NomeUsuario}");
                    oldCfg.ResetarIdentidadeUsuario();
                    oldCfg.Salvar();
                    LogHelper.Info("USER_IDENTITY_RESET — NomeUsuario/RamalNome/DisplayName/Ramal/Login/Senha limpos | AMI/CDR/servidor preservados");
                }
                else
                {
                    LogHelper.Info("OLD_USER_CONFIG_IGNORED — nenhuma config anterior encontrada");
                }
            }
            catch (Exception ex)
            {
                LogCrash("FRESH_INSTALL_RESET_ERROR", "Falha ao resetar identidade do usuario", ex);
            }

            LogHelper.Info("SETUPWINDOW_FORCED — abrindo tela de configuracao por fresh install");
            var freshSetup = new SetupWindow();
            MainWindow = freshSetup;
            freshSetup.Show();
            return;
        }

        var cmdArgs = Environment.GetCommandLineArgs();
        var autostart = cmdArgs.Any(a => a.Equals("/autostart", StringComparison.OrdinalIgnoreCase));
        LogHelper.Info(autostart ? "STARTUP_MODE=AUTOSTART | iniciado via inicializacao automatica do Windows" : "STARTUP_MODE=MANUAL");

        LogHelper.Info("INSTALL_MODE=UPGRADE — config existente detectada, iniciando normalmente");

        _ = UpdateService.VerificarAtualizacaoAsync();

        // Backup preventivo antes de carregar (protege contra corrupção)
        SipConfig.CriarBackup();

        SipConfig? config = null;
        try
        {
            config = SipConfig.CarregarSalva();
        }
        catch (Exception ex)
        {
            LogCrash("CONFIG_LOAD_ERROR", "SipConfig.CarregarSalva falhou no startup", ex);
            MessageBox.Show(
                "Não foi possível carregar a configuração salva.\nVocê será redirecionado para a tela de configuração.",
                "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        bool userDataPresente = config != null &&
            !string.IsNullOrWhiteSpace(config.NomeUsuario) &&
            !string.IsNullOrWhiteSpace(config.Ramal) &&
            !string.IsNullOrWhiteSpace(config.Login) &&
            !string.IsNullOrWhiteSpace(config.Senha);

        if (config?.EstaCompleta == true && userDataPresente)
        {
            // Repair any empty/zero fields and persist — fixes configs saved before RepairDefaults existed
            config.RepairDefaults();
            LogHelper.ConfigurarDeSettings(config);
            try { config.Salvar(); } catch { }

            var sip = new SipService();

            // Allow update service to skip update while a call is in progress
            UpdateService.EmChamadaAtiva = () => sip.IsInCall || sip.IsDialing;

            try { sip.Inicializar(config); } catch { }

            // v2.1.1 — Offline é controlado localmente: se o último estado salvo era
            // Offline, o app inicia bloqueando chamadas, sem tentar voltar Online sozinho.
            if (config.IsOfflinePersistido)
            {
                sip.EntrarOffline();
                LogHelper.Info("APP_INICIADO_OFFLINE_POR_CONFIG_SALVA");
            }

            if (autostart)
            {
                // Quando iniciado com Windows, a rede pode ainda nao estar pronta.
                // Aguardamos 8s antes de tentar registrar; o reconnect timer cobre retentativas.
                LogHelper.Info("AUTOSTART_REGISTER_DELAYED | aguardando 8s para rede estar disponivel");
                _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ =>
                {
                    try { Dispatcher.Invoke(() => sip.Registrar()); } catch { }
                }, System.Threading.Tasks.TaskContinuationOptions.None);
            }
            else
            {
                try { sip.Registrar(); } catch { }
            }

            var shell = new DialerShellWindow(sip);
            MainWindow = shell;
            shell.Show();
            _ = System.Threading.Tasks.Task.Run(WavenVoIP.Services.ContatoStorageService.MigrarContatosAntigos);

            // Inicia sync com Waven API se estiver habilitado
            if (config.UsarWavenApi && !string.IsNullOrWhiteSpace(config.WavenApiToken))
            {
                _wavenSyncService = new WavenApiSyncService(config.Ramal);
                LogHelper.Info($"WAVEN_API_SYNC_STARTED | ramal={config.Ramal} url={config.WavenApiUrl}");
            }

            return;
        }

        // Primeira instalação ou config incompleta → SetupWindow
        var setup = new SetupWindow();
        MainWindow = setup;
        setup.Show();
    }

    // Confere a entrada "WavenVoIP" no Run key do Windows: se apontar para um executável
    // que não é mais o atual (versão antiga, pasta antiga) ou que não existe mais no disco,
    // corrige silenciosamente para o caminho em execução agora. Também remove nomes de
    // valor legados de versões anteriores, se existirem, evitando duas entradas de
    // autostart concorrentes.
    private static void VerificarAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) return;

        var registrado = key.GetValue("WavenVoIP") as string;
        LogHelper.Info($"AUTOSTART_PATH_CHECK | registrado={registrado ?? "(nenhum)"}");

        if (string.IsNullOrWhiteSpace(registrado)) return; // autostart desligado — nada a corrigir

        var exeAtual = Environment.ProcessPath
                       ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exeAtual)) return;

        var caminhoRegistrado = registrado.Trim('"').Split(new[] { " /autostart" }, StringSplitOptions.None)[0];

        var apontaParaAtual = string.Equals(caminhoRegistrado, exeAtual, StringComparison.OrdinalIgnoreCase);
        var arquivoRegistradoExiste = File.Exists(caminhoRegistrado);

        if (!apontaParaAtual || !arquivoRegistradoExiste)
        {
            LogHelper.Warn($"AUTOSTART_OLD_VERSION_FOUND | registrado={caminhoRegistrado} existe={arquivoRegistradoExiste} atual={exeAtual}");
            key.SetValue("WavenVoIP", $"\"{exeAtual}\" /autostart");
            LogHelper.Info($"AUTOSTART_PATH_CORRECTED | novo={exeAtual}");
        }

        // Nomes de valor usados por builds bem antigas — remove se sobrarem, para não
        // iniciar duas cópias do app junto com o Windows.
        foreach (var nomeAntigo in new[] { "Waven VoIP", "WavenVoip", "Waven" })
        {
            if (key.GetValue(nomeAntigo) == null) continue;
            LogHelper.Warn($"AUTOSTART_DUPLICATE_FOUND | valor_antigo={nomeAntigo}");
            try { key.DeleteValue(nomeAntigo, false); } catch { }
        }
    }

    private static void RestaurarJanelaPrincipal()
    {
        var w = Current.MainWindow;
        if (w == null) return;
        if (w.WindowState == WindowState.Minimized)
            w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wavenSyncService?.Dispose();
        try { _activateEvent?.Set(); } catch { }
        _activateEvent?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        try { LogHelper.Flush(TimeSpan.FromMilliseconds(500)); } catch { }
        base.OnExit(e);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        LogCrash("RECORDING_PLAYER_CRASH_PREVENTED", "AppDomain.UnhandledException — thread background", ex);

        try
        {
            Current.Dispatcher.Invoke(() =>
            {
                if (!e.IsTerminating)
                    MessageBox.Show("Ocorreu um erro interno e foi registrado.\nO aplicativo continuará em execução.",
                        "Waven VoIP", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }
        catch { }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            LogCrash("RECORDING_PLAYER_CRASH_PREVENTED", "DispatcherUnhandledException", e.Exception);

            if (e.Exception is Win32Exception)
            {
                e.Handled = true;
                return;
            }

            if (e.Exception is InvalidOperationException inv &&
                (inv.Message.Contains("DialogResult", StringComparison.OrdinalIgnoreCase) ||
                 inv.Message.Contains("janela", StringComparison.OrdinalIgnoreCase) ||
                 inv.Message.Contains("Window", StringComparison.OrdinalIgnoreCase)))
            {
                e.Handled = true;
                return;
            }

            if (e.Exception is ObjectDisposedException ||
                e.Exception is InvalidOperationException ||
                e.Exception is System.Threading.Tasks.TaskCanceledException)
            {
                e.Handled = true;
                return;
            }

            MessageBox.Show("Ocorreu um erro inesperado.\nO aplicativo tentará continuar em execução.\n\nLog salvo em:\n" + CrashLogPath,
                "Waven VoIP — Erro", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
        catch { }
    }
}
