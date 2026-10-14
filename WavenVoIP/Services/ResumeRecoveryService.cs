using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WavenVoIP.Services
{
    // v2.5.0 — volta da suspensão do Windows.
    // Ao acordar, a rede leva alguns segundos para voltar (sem DNS). Antes disso, cada
    // sincronização falhava por conta própria e o ramal só voltava a registrar quando o
    // temporizador normal do registro (até ~2 min) disparasse. Aqui: espera a rede responder
    // (fora da thread da interface) e então manda registrar o ramal e sincronizar tudo de uma vez.
    public static class ResumeRecoveryService
    {
        private static int _emAndamento;
        private static bool _iniciado;
        private static string _hostDeTeste = string.Empty;
        private static Func<Task>? _aoVoltarARede;

        // true entre o "acordou" e a rede responder: quem roda em temporizador pode pular o ciclo.
        public static bool AguardandoRede { get; private set; }

        public static void Iniciar(string hostDeTeste, Func<Task> aoVoltarARede)
        {
            _hostDeTeste = hostDeTeste ?? string.Empty;
            _aoVoltarARede = aoVoltarARede;
            if (_iniciado) return;
            _iniciado = true;
            SystemEvents.PowerModeChanged += (_, e) =>
            {
                try
                {
                    if (e.Mode == PowerModes.Suspend) LogHelper.Info("POWER_SUSPEND | computador entrando em suspensao");
                    else if (e.Mode == PowerModes.Resume) _ = Task.Run(() => RetomarAsync(_hostDeTeste, _aoVoltarARede, ResolverAsync));
                }
                catch { }
            };
        }

        private static async Task<bool> ResolverAsync(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            if (IPAddress.TryParse(host, out _)) return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            var enderecos = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            return enderecos.Length > 0;
        }

        // Separado do evento do Windows para poder ser exercitado em teste.
        internal static async Task<(bool redeVoltou, int tentativas)> RetomarAsync(
            string host, Func<Task>? aoVoltarARede, Func<string, Task<bool>> redeResponde,
            int limiteSegundos = 90, int intervaloMs = 1000)
        {
            if (Interlocked.CompareExchange(ref _emAndamento, 1, 0) != 0) return (false, 0);
            AguardandoRede = true;
            var inicio = DateTime.UtcNow;
            var tentativas = 0; var voltou = false;
            try
            {
                LogHelper.Info("POWER_RESUME | computador voltou da suspensao; aguardando a rede");
                while ((DateTime.UtcNow - inicio).TotalSeconds < limiteSegundos)
                {
                    tentativas++;
                    try { if (await redeResponde(host).ConfigureAwait(false)) { voltou = true; break; } } catch { }
                    await Task.Delay(intervaloMs).ConfigureAwait(false);
                }
                AguardandoRede = false;
                LogHelper.Info($"POWER_RESUME_REDE | voltou={voltou} espera_ms={(int)(DateTime.UtcNow - inicio).TotalMilliseconds} tentativas={tentativas}");
                // mesmo sem confirmar a rede, tenta reconectar: os serviços têm as próprias retentativas
                if (aoVoltarARede != null)
                {
                    try { await aoVoltarARede().ConfigureAwait(false); LogHelper.Info("POWER_RESUME_RECONEXAO_DISPARADA"); }
                    catch (Exception ex) { LogHelper.Info($"POWER_RESUME_RECONEXAO_ERRO | {ex.Message}"); }
                }
                return (voltou, tentativas);
            }
            finally
            {
                AguardandoRede = false;
                Interlocked.Exchange(ref _emAndamento, 0);
            }
        }
    }
}
