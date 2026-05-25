using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace WavenUpdater;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WavenVoIP", "Logs", "update.log");

    internal static void AppLog(string tag, string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [APP_{tag}] {msg}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Global handlers — updater must NEVER close silently
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            AppLog("CRASH", $"IsTerminating={args.IsTerminating} | {ex}");
            try
            {
                MessageBox.Show(
                    $"Erro crítico no WavenUpdater:\n\n{ex?.Message}\n\nLog salvo em:\n{LogPath}",
                    "WavenUpdater — Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        };

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog("DISPATCHER_CRASH", args.Exception.ToString());
            try
            {
                MessageBox.Show(
                    $"Erro no WavenUpdater:\n\n{args.Exception.Message}\n\nLog salvo em:\n{LogPath}",
                    "WavenUpdater — Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            args.Handled = true;
        };

        base.OnStartup(e);

        var args = e.Args;
        AppLog("STARTUP",  $"Args=[{string.Join("] [", args)}]");
        AppLog("RUNTIME",  RuntimeInformation.FrameworkDescription);
        AppLog("WORKDIR",  Environment.CurrentDirectory);
        AppLog("BASE_DIR", AppDomain.CurrentDomain.BaseDirectory);

        string Get(string key)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == key) return args[i + 1];
            return string.Empty;
        }

        var opts = new UpdateOptions
        {
            MainPid    = int.TryParse(Get("--pid"), out int p) ? p : -1,
            ZipUrl     = Get("--zip"),
            Sha256     = Get("--sha256"),
            NewVersion = Get("--ver"),
            OldVersion = Get("--oldver"),
            AppDir     = Get("--app-dir"),
            ExeName    = Get("--exe")
        };

        AppLog("OPTS", $"pid={opts.MainPid} zip={opts.ZipUrl} ver={opts.NewVersion} oldver={opts.OldVersion} app-dir={opts.AppDir} exe={opts.ExeName}");

        if (string.IsNullOrWhiteSpace(opts.ZipUrl) || string.IsNullOrWhiteSpace(opts.AppDir))
        {
            AppLog("INVALID_ARGS", $"zip='{opts.ZipUrl}' app-dir='{opts.AppDir}'");
            MessageBox.Show(
                $"Parâmetros inválidos.\n\nO WavenUpdater deve ser iniciado automaticamente pelo WavenVoIP.\n\nLog: {LogPath}",
                "WavenUpdater", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var win = new UpdateWindow(opts);
        MainWindow = win;
        win.Show();
    }
}
