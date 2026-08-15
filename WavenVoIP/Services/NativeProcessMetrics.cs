using System;
using System.Runtime.InteropServices;

namespace WavenVoIP.Services
{
    /// <summary>
    /// P/Invoke mínimo para métricas de processo que o .NET não expõe via API gerenciada
    /// (GDI/USER Objects, contadores de I/O). Usado só pela telemetria de diagnóstico —
    /// chamadas baratas (mesmas que o Gerenciador de Tarefas usa), sem alocação relevante.
    /// </summary>
    internal static class NativeProcessMetrics
    {
        private const int GR_GDIOBJECTS  = 0;
        private const int GR_USEROBJECTS = 1;

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, int uiFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

        internal static (int gdi, int user) ObterGdiUserObjects()
        {
            try
            {
                var h = System.Diagnostics.Process.GetCurrentProcess().Handle;
                return ((int)GetGuiResources(h, GR_GDIOBJECTS), (int)GetGuiResources(h, GR_USEROBJECTS));
            }
            catch { return (0, 0); }
        }

        internal static (ulong readOps, ulong writeOps, ulong readBytes, ulong writeBytes) ObterIoCounters()
        {
            try
            {
                var h = System.Diagnostics.Process.GetCurrentProcess().Handle;
                if (GetProcessIoCounters(h, out var c))
                    return (c.ReadOperationCount, c.WriteOperationCount, c.ReadTransferCount, c.WriteTransferCount);
            }
            catch { }
            return (0, 0, 0, 0);
        }
    }
}
