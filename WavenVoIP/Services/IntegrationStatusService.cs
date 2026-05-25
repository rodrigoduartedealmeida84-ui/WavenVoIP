using System;
using System.Collections.Concurrent;

namespace WavenVoIP.Services
{
    public enum IntegracaoNome { Google, WhatsApp, Ami, Cdr }

    public enum IntegracaoStatus { Conectado, Desconectado, Reconectando, Erro }

    public static class IntegrationStatusService
    {
        public static event Action<IntegracaoNome, IntegracaoStatus>? StatusChanged;

        private static readonly ConcurrentDictionary<IntegracaoNome, IntegracaoStatus> _status       = new();
        private static readonly ConcurrentDictionary<IntegracaoNome, bool>             _modalMostrado = new();
        private static readonly ConcurrentDictionary<IntegracaoNome, DateTime>         _ultimaTentativa = new();

        private static readonly TimeSpan LoopGuard = TimeSpan.FromMinutes(10);

        // ── Status ───────────────────────────────────────────────────────────────

        public static IntegracaoStatus ObterStatus(IntegracaoNome nome)
            => _status.TryGetValue(nome, out var s) ? s : IntegracaoStatus.Desconectado;

        public static void Atualizar(IntegracaoNome nome, IntegracaoStatus status)
        {
            _status[nome] = status;
            if (status == IntegracaoStatus.Conectado)
            {
                _modalMostrado.TryRemove(nome, out _);
                _ultimaTentativa.TryRemove(nome, out _);
                IntegrationAutoReconnectService.CancelarReconexao(nome);
            }
            else if (status == IntegracaoStatus.Reconectando)
            {
                _modalMostrado.TryRemove(nome, out _); // allow modal if next failure is auth
            }
            LogHelper.Info($"[INTEGRATION_STATUS] {nome}={status}");
            StatusChanged?.Invoke(nome, status);
        }

        // ── Modal guard (once per session per integration) ────────────────────

        public static bool ModalJaMostrado(IntegracaoNome nome)
            => _modalMostrado.TryGetValue(nome, out var v) && v;

        public static void MarcarModalMostrado(IntegracaoNome nome)
        {
            _modalMostrado[nome] = true;
            LogHelper.Info($"[INTEGRATION_MODAL_SHOWN] {nome}");
        }

        public static void MarcarModalDismissed(IntegracaoNome nome)
            => LogHelper.Info($"[INTEGRATION_MODAL_DISMISSED] {nome}");

        // ── Anti-loop guard (10 min between reconnect attempts) ──────────────

        public static bool PermitirReconexao(IntegracaoNome nome)
        {
            if (_ultimaTentativa.TryGetValue(nome, out var ultimo) && DateTime.Now - ultimo < LoopGuard)
            {
                LogHelper.Info($"[INTEGRATION_LOOP_BLOCKED] {nome} — ultima tentativa {ultimo:HH:mm:ss}");
                return false;
            }
            return true;
        }

        public static void RegistrarTentativa(IntegracaoNome nome)
        {
            _ultimaTentativa[nome] = DateTime.Now;
            LogHelper.Info($"[INTEGRATION_RECONNECT_ATTEMPT] {nome}");
        }
    }
}
