using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WavenVoIP.Services
{
    /// v2.6.0 — interface responsiva. Adapta uma janela ao espaço da própria janela (em DIP,
    /// ou seja, já descontada a escala do Windows 100/125/150%) e ao tamanho FÍSICO da tela
    /// em que ela está (lido do EDID do monitor):
    ///  1. a janela abre no tamanho de projeto (o mesmo da 2.5.4) ou no último tamanho que o
    ///     usuário deixou — nunca maior só porque o monitor tem mais pixels. Nada passa da
    ///     área útil, nem ao trocar de monitor;
    ///  2. a cada redimensionamento o conteúdo recebe um LayoutTransform: reduz até um piso
    ///     legível quando falta espaço e amplia, amortecido, quando o usuário aumenta ou
    ///     maximiza a janela — pouco no notebook, mais no monitor de 24".
    ///     Abaixo do piso, quem rola é o próprio conteúdo (só na vertical).
    /// Só mexe em apresentação — nenhuma regra de telefonia, API, AMI ou histórico passa aqui.
    internal static class LayoutResponsivo
    {
        internal sealed class Opcoes
        {
            /// true: escala proporcional à janela (shell, configurações).
            /// false: só reduz quando o conteúdo não cabe (janela de chamada, que tem altura variável).
            public bool Proporcional { get; init; } = true;
            /// fração do espaço extra convertida em ampliação (1 = totalmente proporcional).
            public double Amortecimento { get; init; } = 0.55;
            /// menor escala aceita quando o Windows já está em 125%+ (o texto continua maior que a 100%).
            public double PisoReducao { get; init; } = 0.85;
            /// nome sob o qual tamanho, posição e "maximizada" são lembrados entre aberturas (null = não lembra).
            public string? LembrarComo { get; init; }
            /// chamado após cada ajuste: escala aplicada e área lógica disponível ao layout.
            public Action<double, Size>? AoAplicar { get; init; }
        }

        // borda + barra de título padrão do Windows 11 em DIP (cliente = janela − isto)
        private const double CromoLargura = 16;
        private const double CromoAltura = 39;

        /// Teste de tela (WavenVoIP.UiTests): simula a diagonal do monitor, que lá não existe.
        internal static double? DiagonalSimulada;

        /// Teto da ampliação pelo tamanho físico da tela: no notebook a janela maximizada fica
        /// praticamente como na 2.5.4; no monitor de 24" o conteúdo cresce até 30%.
        internal static double EscalaMaximaPara(double? diagonalPolegadas) => diagonalPolegadas switch
        {
            null or <= 0 => 1.15,   // tela sem EDID (projetor, remoto): meio-termo
            < 17.5 => 1.06,         // notebook 13–17"
            < 21.5 => 1.18,         // 19–21"
            _ => 1.30,              // 22" ou maior
        };

        internal static void Ativar(Window janela, Opcoes op)
        {
            if (janela.Content is not FrameworkElement conteudo) return;

            var baseLargura = double.IsNaN(janela.Width) ? 0 : janela.Width - CromoLargura;
            var baseAltura = double.IsNaN(janela.Height) ? 0 : janela.Height - CromoAltura;

            // envolve o conteúdo: o invólucro mede a área cliente real; o conteúdo recebe a escala
            janela.Content = null;
            var area = new Grid { ClipToBounds = true };
            area.Children.Add(conteudo);
            janela.Content = area;

            var salvo = op.LembrarComo != null ? LerSalvo(op.LembrarComo) : null;
            if (salvo != null) janela.WindowStartupLocation = WindowStartupLocation.Manual;

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
                        var teto = EscalaMaximaPara(DiagonalSimulada ?? DiagonalPolegadas(janela));
                        var bruto = Math.Min(w / baseLargura, h / baseAltura);
                        s = bruto >= 1 ? 1 + (bruto - 1) * op.Amortecimento : bruto;
                        s = Math.Clamp(s, piso, teto);
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
            // trocou de monitor/escala: o tamanho em DIP se mantém, então a janela pode passar da
            // área útil da tela nova (ex.: do monitor a 100% para o notebook a 125%) — encaixa
            janela.DpiChanged += (_, _) =>
            {
                EncaixarMinimos(janela);
                Recalcular();
                janela.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => GarantirDentroDaTela(janela)));
            };

            janela.SourceInitialized += (_, _) =>
            {
                try
                {
                    var restaurado = salvo != null && Restaurar(janela, salvo);
                    EncaixarMinimos(janela);
                    var util = AreaUtilDip(janela, usarDono: true);
                    if (!restaurado && !util.IsEmpty)
                    {
                        if (janela.Width > util.Width) janela.Width = util.Width;
                        if (janela.Height > util.Height) janela.Height = util.Height;
                        if (salvo != null)
                        {
                            // o lugar lembrado sumiu (monitor desligado): centraliza, como sem memória
                            janela.Left = util.Left + (util.Width - janela.Width) / 2;
                            janela.Top = util.Top + (util.Height - janela.Height) / 2;
                        }
                    }
                    var diag = DiagonalPolegadas(janela);
                    LogHelper.Info($"LAYOUT_RESPONSIVO | {janela.GetType().Name} | area={util.Width:0}x{util.Height:0} dpi={DpiDe(janela):0.##} " +
                                   $"tela={(diag is double d ? d.ToString("0.#") + "pol" : "?")} teto={EscalaMaximaPara(diag):0.##} " +
                                   $"inicial={janela.Width:0}x{janela.Height:0} lembrado={(restaurado ? (salvo!.Maximizada ? "maximizada" : "sim") : "nao")}");
                }
                catch (Exception ex)
                {
                    LogHelper.Warn($"LAYOUT_RESPONSIVO_INIT_ERRO | {janela.GetType().Name} | {ex.Message}");
                }
            };

            // depois dos Loaded da própria janela (que podem posicioná-la): nada fora da tela
            janela.Loaded += (_, _) => GarantirDentroDaTela(janela);

            if (op.LembrarComo is string chave)
            {
                // grava ao fechar (inclusive quando o shell só vai para a bandeja) e, para não
                // perder num desligamento/atualização, 2 s depois de mover ou redimensionar
                var adiado = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                adiado.Tick += (_, _) => { adiado.Stop(); Salvar(janela, chave); };
                void Agendar() { if (janela.IsLoaded) { adiado.Stop(); adiado.Start(); } }
                janela.SizeChanged += (_, _) => Agendar();
                janela.LocationChanged += (_, _) => Agendar();
                janela.StateChanged += (_, _) => Agendar();
                janela.Closing += (_, _) => { adiado.Stop(); Salvar(janela, chave); };
            }
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

        // ── Tamanho/posição lembrados ────────────────────────────────────────────────
        // Em pixels de tela (GetWindowPlacement): vale entre monitores com escalas diferentes.

        internal sealed class JanelaSalva
        {
            public int Esquerda { get; set; }
            public int Topo { get; set; }
            public int Direita { get; set; }
            public int Base { get; set; }
            public bool Maximizada { get; set; }
        }

        private static string ArquivoJanelas => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WavenVoIP", "janelas.json");

        private static Dictionary<string, JanelaSalva> LerTodas()
        {
            try
            {
                if (File.Exists(ArquivoJanelas))
                    return JsonSerializer.Deserialize<Dictionary<string, JanelaSalva>>(File.ReadAllText(ArquivoJanelas)) ?? new();
            }
            catch (Exception ex) { LogHelper.Warn($"LAYOUT_JANELA_LER_ERRO | {ex.Message}"); }
            return new();
        }

        private static JanelaSalva? LerSalvo(string chave) => LerTodas().TryGetValue(chave, out var j) ? j : null;

        private static void Salvar(Window janela, string chave)
        {
            try
            {
                var hwnd = new WindowInteropHelper(janela).Handle;
                if (hwnd == IntPtr.Zero) return;
                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (!GetWindowPlacement(hwnd, ref wp)) return;
                var r = wp.rcNormalPosition;
                if (r.Right - r.Left < 200 || r.Bottom - r.Top < 150) return;
                var todas = LerTodas();
                var nova = new JanelaSalva
                {
                    Esquerda = r.Left, Topo = r.Top, Direita = r.Right, Base = r.Bottom,
                    // minimizada/na bandeja: guarda o estado de antes (maximizada ou não)
                    Maximizada = janela.WindowState == WindowState.Maximized ||
                                 (janela.WindowState == WindowState.Minimized && (wp.flags & WPF_RESTORETOMAXIMIZED) != 0),
                };
                if (todas.TryGetValue(chave, out var atual) && atual.Esquerda == nova.Esquerda && atual.Topo == nova.Topo &&
                    atual.Direita == nova.Direita && atual.Base == nova.Base && atual.Maximizada == nova.Maximizada) return;
                todas[chave] = nova;
                Directory.CreateDirectory(Path.GetDirectoryName(ArquivoJanelas)!);
                var tmp = ArquivoJanelas + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(todas));
                File.Move(tmp, ArquivoJanelas, overwrite: true);
            }
            catch (Exception ex) { LogHelper.Warn($"LAYOUT_JANELA_SALVAR_ERRO | {ex.Message}"); }
        }

        /// Recoloca a janela onde o usuário deixou — só se o retângulo ainda cair num monitor
        /// ligado (monitor externo desconectado: abre no padrão, centralizada).
        private static bool Restaurar(Window janela, JanelaSalva s)
        {
            var hwnd = new WindowInteropHelper(janela).Handle;
            if (hwnd == IntPtr.Zero) return false;
            var r = new RECT { Left = s.Esquerda, Top = s.Topo, Right = s.Direita, Bottom = s.Base };
            var monitor = MonitorFromRect(ref r, MONITOR_DEFAULTTONULL);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;

            // cabe na área útil daquele monitor (resolução pode ter mudado desde então)
            var u = info.rcWork;
            var larg = Math.Min(r.Right - r.Left, u.Right - u.Left);
            var alt = Math.Min(r.Bottom - r.Top, u.Bottom - u.Top);
            var x = Math.Clamp(r.Left, u.Left, u.Right - larg);
            var y = Math.Clamp(r.Top, u.Top, u.Bottom - alt);
            var wp = new WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<WINDOWPLACEMENT>(),
                showCmd = SW_HIDE,   // só posiciona: quem mostra (ou deixa na bandeja) é o app
                rcNormalPosition = new RECT { Left = x, Top = y, Right = x + larg, Bottom = y + alt },
            };
            if (!SetWindowPlacement(hwnd, ref wp)) return false;
            if (s.Maximizada) janela.WindowState = WindowState.Maximized;
            return true;
        }

        // ── Monitor ──────────────────────────────────────────────────────────────────

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

        private static readonly Dictionary<string, double?> _diagonais = new(StringComparer.OrdinalIgnoreCase);

        /// Diagonal física (polegadas) do monitor em que a janela está, pelo EDID que o Windows
        /// guarda no registro (bytes 21/22 = largura/altura em cm). null se não der para saber.
        internal static double? DiagonalPolegadas(Window janela)
        {
            try
            {
                var hwnd = new WindowInteropHelper(janela).Handle;
                if (hwnd == IntPtr.Zero) return null;
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (monitor == IntPtr.Zero || !GetMonitorInfoEx(monitor, ref info)) return null;
                lock (_diagonais)
                {
                    if (_diagonais.TryGetValue(info.szDevice, out var c)) return c;
                    var d = LerDiagonalEdid(info.szDevice);
                    _diagonais[info.szDevice] = d;
                    return d;
                }
            }
            catch { return null; }
        }

        private static double? LerDiagonalEdid(string dispositivo)
        {
            // \\?\DISPLAY#CMN15E7#4&512c522&0&UID265988#{e6f07b5f-...} → Enum\DISPLAY\CMN15E7\4&512c522&0&UID265988
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(dispositivo, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)) return null;
            var partes = dd.DeviceID.Split('#');
            if (partes.Length < 3) return null;
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{partes[1]}\{partes[2]}\Device Parameters");
            if (k?.GetValue("EDID") is not byte[] edid || edid.Length < 23) return null;
            double cmL = edid[21], cmA = edid[22];
            if (cmL <= 0 || cmA <= 0) return null;   // projetor/TV sem tamanho
            return Math.Sqrt(cmL * cmL + cmA * cmA) / 2.54;
        }

        private const uint MONITOR_DEFAULTTONULL = 0;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;
        private const int SW_HIDE = 0;
        private const int WPF_RESTORETOMAXIMIZED = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    }
}
