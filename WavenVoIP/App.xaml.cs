using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Threading;
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
