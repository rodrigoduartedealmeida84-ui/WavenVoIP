using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace WavenVoIP.Services
{
    /// v2.6.0 — interface responsiva. Adapta uma janela à área útil do monitor em que ela
    /// está (em DIP, ou seja, já descontada a escala do Windows 100/125/150%) e ao espaço da
    /// própria janela:
    ///  1. ao criar a janela, nenhum tamanho/mínimo passa da área útil (nada fica cortado
    ///     num notebook 1366×768 a 125/150%) e, em monitores grandes, ela já abre maior;
    ///  2. a cada redimensionamento, o conteúdo recebe um LayoutTransform: amplia de forma
    ///     amortecida quando sobra espaço (monitor 22–27") e reduz até um piso legível quando
    ///     falta (notebook). Abaixo do piso, quem rola é o próprio conteúdo (só na vertical).
    /// Só mexe em apresentação — nenhuma regra de telefonia, API, AMI ou histórico passa aqui.
    internal static class LayoutResponsivo
    {
        internal sealed class Opcoes
        {
            /// true: escala proporcional à janela (shell, configurações).
            /// false: só reduz quando o conteúdo não cabe (janela de chamada, que tem altura variável).
            public bool Proporcional { get; init; } = true;
            /// fração do espaço extra convertida em ampliação (1 = totalmente proporcional).
            public double Amortecimento { get; init; } = 0.6;
            public double EscalaMaxima { get; init; } = 1.45;
            /// menor escala aceita quando o Windows já está em 125%+ (o texto continua maior que a 100%).
            public double PisoReducao { get; init; } = 0.85;
            /// abre a janela proporcionalmente maior em monitores grandes.
            public bool AumentarTamanhoInicial { get; init; } = true;
            /// chamado após cada ajuste: escala aplicada e área lógica disponível ao layout.
            public Action<double, Size>? AoAplicar { get; init; }
        }

        // borda + barra de título padrão do Windows 11 em DIP (cliente = janela − isto)
        private const double CromoLargura = 16;
        private const double CromoAltura = 39;

        internal static void Ativar(Window janela, Opcoes op)
        {
            if (janela.Content is not FrameworkElement conteudo) return;

            var larguraProjeto = janela.Width;
            var alturaProjeto = janela.Height;
            var baseLargura = double.IsNaN(larguraProjeto) ? 0 : larguraProjeto - CromoLargura;
            var baseAltura = double.IsNaN(alturaProjeto) ? 0 : alturaProjeto - CromoAltura;

            // envolve o conteúdo: o invólucro mede a área cliente real; o conteúdo recebe a escala
            janela.Content = null;
            var area = new Grid { ClipToBounds = true };
            area.Children.Add(conteudo);
            janela.Content = area;

            var escalaAtual = 1.0;
            void Recalcular()
            {
                try
                {
                    var w = area.ActualWidth;
                    var h = area.ActualHeight;
                    if (w < 1 || h < 1) return;

                    var dpi = DpiDe(janela);
                    var piso = Math.Max(op.PisoReducao, Math.Min(1.0, 1.0 / dpi));
                    double s;
                    if (op.Proporcional && baseLargura > 0 && baseAltura > 0)
                    {
                        var bruto = Math.Min(w / baseLargura, h / baseAltura);
                        s = bruto >= 1 ? 1 + (bruto - 1) * op.Amortecimento : bruto;
                        s = Math.Clamp(s, piso, op.EscalaMaxima);
                    }
                    else
                    {
                        // altura lógica que o conteúdo pede nesta largura; duas passadas bastam
                        s = escalaAtual;
                        for (var i = 0; i < 2; i++)
                        {
                            conteudo.Measure(new Size(w / s, double.PositiveInfinity));
                            var pedida = conteudo.DesiredSize.Height;
                            if (pedida < 1) break;
                            s = Math.Clamp(h / pedida, Math.Min(piso, 0.7), 1.0);
                        }
                    }

                    s = Math.Round(s, 2);
                    if (Math.Abs(s - escalaAtual) >= 0.005)
                    {
                        escalaAtual = s;
                        conteudo.LayoutTransform = s == 1.0 ? Transform.Identity : new ScaleTransform(s, s);
                    }
                    op.AoAplicar?.Invoke(escalaAtual, new Size(w / escalaAtual, h / escalaAtual));
                }
                catch (Exception ex)
                {
                    LogHelper.Warn($"LAYOUT_RESPONSIVO_ERRO | {janela.GetType().Name} | {ex.Message}");
                }
            }

            area.SizeChanged += (_, _) => Recalcular();
            janela.DpiChanged += (_, _) => { EncaixarMinimos(janela); Recalcular(); };

            janela.SourceInitialized += (_, _) =>
            {
                try
                {
                    var util = AreaUtilDip(janela, usarDono: true);
                    if (util.IsEmpty) return;
                    var sugerida = op.AumentarTamanhoInicial ? EscalaSugerida(util, op) : 1.0;
                    if (!double.IsNaN(larguraProjeto)) janela.Width = Math.Min(larguraProjeto * sugerida, util.Width);
                    if (!double.IsNaN(alturaProjeto)) janela.Height = Math.Min(alturaProjeto * sugerida, util.Height);
                    EncaixarMinimos(janela);
                    LogHelper.Info($"LAYOUT_RESPONSIVO | {janela.GetType().Name} | area={util.Width:0}x{util.Height:0} dpi={DpiDe(janela):0.##} " +
                                   $"inicial={janela.Width:0}x{janela.Height:0} sugerida={sugerida:0.##}");
                }
                catch (Exception ex)
                {
                    LogHelper.Warn($"LAYOUT_RESPONSIVO_INIT_ERRO | {janela.GetType().Name} | {ex.Message}");
                }
            };

            // depois dos Loaded da própria janela (que podem posicioná-la): nada fora da tela
            janela.Loaded += (_, _) => GarantirDentroDaTela(janela);
        }

        /// Escala sugerida para o monitor: 1 num notebook 1366×768 a 100%, ~1,24 num 24" Full HD,
        /// teto em 2560×1440 / 4K a 150%.
        private static double EscalaSugerida(Rect util, Opcoes op)
        {
            var bruto = Math.Min(util.Width / 1366.0, util.Height / 728.0);
            if (bruto <= 1) return 1.0;
            return Math.Min(1 + (bruto - 1) * 0.6, op.EscalaMaxima);
        }

        private static void EncaixarMinimos(Window janela)
        {
            var util = AreaUtilDip(janela);
            if (util.IsEmpty) return;
            if (janela.MinWidth > util.Width) janela.MinWidth = util.Width;
            if (janela.MinHeight > util.Height) janela.MinHeight = util.Height;
        }

        private static void GarantirDentroDaTela(Window janela)
        {
            try
            {
                if (janela.WindowState != WindowState.Normal) return;
                var util = AreaUtilDip(janela);
                if (util.IsEmpty) return;
                if (janela.Width > util.Width) janela.Width = util.Width;
                if (janela.Height > util.Height) janela.Height = util.Height;
                if (janela.Left + janela.Width > util.Right) janela.Left = util.Right - janela.Width;
                if (janela.Top + janela.Height > util.Bottom) janela.Top = util.Bottom - janela.Height;
                if (janela.Left < util.Left) janela.Left = util.Left;
                if (janela.Top < util.Top) janela.Top = util.Top;
            }
            catch { }
        }

        private static double DpiDe(Window janela)
        {
            try { return VisualTreeHelper.GetDpi(janela).DpiScaleX; }
            catch { return 1.0; }
        }

        /// Área útil (sem barra de tarefas) do monitor da janela, em DIP. Na criação a janela
        /// ainda não tem posição: usa o monitor do dono, se houver.
        internal static Rect AreaUtilDip(Window janela, bool usarDono = false)
        {
            try
            {
                var hwnd = new WindowInteropHelper(usarDono && janela.Owner != null ? janela.Owner : janela).Handle;
                if (hwnd == IntPtr.Zero) hwnd = new WindowInteropHelper(janela).Handle;
                if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea;

                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;

                var dpi = DpiDe(janela);
                var r = info.rcWork;
                return new Rect(r.Left / dpi, r.Top / dpi, (r.Right - r.Left) / dpi, (r.Bottom - r.Top) / dpi);
            }
            catch
            {
                return SystemParameters.WorkArea;
            }
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    }
}
