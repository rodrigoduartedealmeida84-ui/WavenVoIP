using System;
using System.Collections.Concurrent;
using System.Timers;

namespace WavenVoIP.Services
{
    /// Manages exponential-backoff reconnect timers for integrations.
    /// Backoff: 5s → 15s → 30s → 60s → 180s → 300s (then every 5 min).
    public static class IntegrationAutoReconnectService
    {
        /// Fired on the timer thread when it is time to attempt reconnection.
        /// Subscribers must marshal to the UI thread themselves.
        public static event Action<IntegracaoNome>? ReconectarAgora;

        private static readonly int[] _backoff = { 5, 15, 30, 60, 180, 300 };

        private static readonly ConcurrentDictionary<IntegracaoNome, Timer?> _timers     = new();
        private static readonly ConcurrentDictionary<IntegracaoNome, int>    _tentativas = new();

        /// Schedule the next reconnect attempt (or the first one if no previous attempt).
        public static void AgendarReconexao(IntegracaoNome nome)
        {
            var attempt  = _tentativas.GetOrAdd(nome, 0);
            var delaySec = attempt < _backoff.Length ? _backoff[attempt] : _backoff[^1];
            _tentativas.AddOrUpdate(nome, 1, (_, v) => v + 1);

            LogHelper.Info($"[INTEGRATION_AUTO_RECONNECT_SCHEDULED] {nome} tentativa={attempt + 1} delay={delaySec}s");

            CancelarTimerInterno(nome);

            var t = new Timer(delaySec * 1000d) { AutoReset = false };
            t.Elapsed += (_, _) =>
            {
                LogHelper.Info($"[INTEGRATION_AUTO_RECONNECT_ATTEMPT] {nome}");
                try { ReconectarAgora?.Invoke(nome); } catch { }
            };
            _timers[nome] = t;
            t.Start();
        }

        /// Cancel any pending reconnect timer and reset the attempt counter for this integration.
        public static void CancelarReconexao(IntegracaoNome nome)
        {
            CancelarTimerInterno(nome);
            if (_tentativas.TryRemove(nome, out var n) && n > 0)
                LogHelper.Info($"[INTEGRATION_AUTO_RECONNECT_CANCELLED] {nome} apos={n} tentativas");
        }

        private static void CancelarTimerInterno(IntegracaoNome nome)
        {
            if (_timers.TryRemove(nome, out var old))
                try { old?.Stop(); old?.Dispose(); } catch { }
        }
    }
}
