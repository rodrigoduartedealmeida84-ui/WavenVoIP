using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WavenVoIP.Services
{
    /// Carrega o ícone da janela a partir de caminho absoluto em disco (imune a
    /// WorkingDirectory incorreto no autostart e a falhas de resolução de pack URI),
    /// com fallback para o ícone embutido no executável.
    internal static class IconHelper
    {
        internal static ImageSource? CarregarIconeJanela()
        {
            try
            {
                var icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "wavenvoip.ico");
                if (File.Exists(icoPath))
                {
                    var frame = BitmapFrame.Create(new Uri(icoPath, UriKind.Absolute),
                        BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    frame.Freeze();
                    LogHelper.Info("WINDOW_ICON_LOADED_FROM_DISK");
                    return frame;
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error("WINDOW_ICON_DISK_LOAD_FAILED", ex);
            }

            try
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (icon != null)
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle, System.Windows.Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        LogHelper.Info("WINDOW_ICON_LOADED_FROM_EXE_FALLBACK");
                        return src;
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error("WINDOW_ICON_EXE_FALLBACK_FAILED", ex);
            }

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var icoResource = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
                if (icoResource != null)
                {
                    using var stream = asm.GetManifestResourceStream(icoResource);
                    if (stream != null)
                    {
                        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        frame.Freeze();
                        LogHelper.Info("WINDOW_ICON_LOADED_FROM_EMBEDDED_RESOURCE");
                        return frame;
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error("WINDOW_ICON_EMBEDDED_FALLBACK_FAILED", ex);
            }

            LogHelper.Error("WINDOW_ICON_LOAD_FAILED_ALL_FALLBACKS");
            return null;
        }
    }
}
