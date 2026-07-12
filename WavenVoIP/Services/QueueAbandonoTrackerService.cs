using System;
using System.Collections.Generic;
using System.Linq;

namespace WavenVoIP.Services
{
    public enum QueuePendingStatus
    {
        AguardandoNaFila,
        AguardandoCdrFinal,
        Recebida,
        Abandonada
    }

    public sealed class QueuePendingCall
    {
        public string NumeroNormalizado { get; set; } = string.Empty;
        public string Fila { get; set; } = string.Empty;
        public DateTime HorarioEntrada { get; set; }
        public DateTime UltimaVezVista { get; set; }
        public DateTime? HorarioSaida { get; set; }
        public QueuePendingStatus Status { get; set; } = QueuePendingStatus.AguardandoNaFila;
        public int TentativasConfirmacao { get; set; }
        public bool JaImportada { get; set; }
        public bool JaNotificada { get; set; }
    }

    /// <summary>
    /// Rastreia, a partir dos snapshots de "Filas em Tempo Real" (AmiMonitorService.Filas), o
    /// momento exato em que um cliente sai de uma fila — atendido ou abandonado — em vez de
    /// depender só de um novo ciclo de sincronização de CDR "adivinhar" isso por polling.
    /// Isso permite disparar uma recheck acelerada do CDR assim que a saída é detectada
    /// (ver DialerShellWindow.OnClienteSaiuDaFila), em vez de esperar o próximo tick do timer.
    /// </summary>
    public static class QueueAbandonoTrackerService
    {
        private static readonly Dictionary<string, QueuePendingCall> _pendentes =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        /// Disparado quando um número antes visto esperando numa fila deixa de aparecer no snapshot.
        public static event Action<QueuePendingCall>? SaiuDaFila;

        private static void Log(string msg)
        {
            try { LogHelper.Cdr(msg); } catch { }
        }

        private static string Mask(string numero)
        {
            if (string.IsNullOrEmpty(numero)) return numero;
            if (numero.Length <= 4) return new string('*', numero.Length);
            return numero.Substring(0, 2) + new string('*', numero.Length - 4) + numero.Substring(numero.Length - 2);
        }

        /// Chamado a cada snapshot de Filas em Tempo Real (FilasChanged). Atualiza o estado
        /// pendente de cada número visto e detecta quem saiu desde a última leva.
        public static void ProcessarSnapshot(IEnumerable<(string Numero, string Fila)> clientesAtuais)
        {
            var agora = DateTime.Now;
            var vistosAgora = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var saidas = new List<QueuePendingCall>();

            lock (_lock)
            {
                foreach (var (numeroRaw, fila) in clientesAtuais)
                {
                    var numero = PhoneNumberNormalizer.NormalizeBrazilPhone(
                        new string((numeroRaw ?? string.Empty).Where(char.IsDigit).ToArray()));
                    if (numero.Length < 7) continue;
                    vistosAgora.Add(numero);

                    if (!_pendentes.TryGetValue(numero, out var call))
                    {
                        call = new QueuePendingCall
                        {
                            NumeroNormalizado = numero,
                            Fila = fila,
                            HorarioEntrada = agora,
                            UltimaVezVista = agora,
                            Status = QueuePendingStatus.AguardandoNaFila
                        };
                        _pendentes[numero] = call;
                        Log($"QUEUE_PENDING_CREATED numero={Mask(numero)} fila={fila}");
                    }
                    else
                    {
                        call.UltimaVezVista = agora;
                        call.Fila = fila;
                        if (call.Status != QueuePendingStatus.AguardandoNaFila)
                        {
                            // Reapareceu na fila (nova tentativa/nova chamada com o mesmo número) —
                            // reinicia como uma nova espera.
                            call.Status = QueuePendingStatus.AguardandoNaFila;
                            call.HorarioSaida = null;
                            call.TentativasConfirmacao = 0;
                            call.JaImportada = false;
                            call.JaNotificada = false;
                        }
                    }
                }

                foreach (var call in _pendentes.Values
                             .Where(c => c.Status == QueuePendingStatus.AguardandoNaFila &&
                                         !vistosAgora.Contains(c.NumeroNormalizado))
                             .ToList())
                {
                    call.Status = QueuePendingStatus.AguardandoCdrFinal;
                    call.HorarioSaida = agora;
                    Log($"QUEUE_LEFT_WAITING numero={Mask(call.NumeroNormalizado)} fila={call.Fila} " +
                        $"esperouSeg={(agora - call.HorarioEntrada).TotalSeconds:F0}");
                    saidas.Add(call);
                }

                // Limpeza: remove pendentes já finalizadas ou abandonadas na estrutura há muito
                // tempo sem uma leva nova (evita crescimento ilimitado do dicionário).
                foreach (var k in _pendentes
                             .Where(kv => kv.Value.Status is QueuePendingStatus.Recebida or QueuePendingStatus.Abandonada
                                       || (agora - kv.Value.UltimaVezVista) > TimeSpan.FromMinutes(15))
                             .Select(kv => kv.Key)
                             .ToList())
                {
                    _pendentes.Remove(k);
                }
            }

            foreach (var call in saidas)
                SaiuDaFila?.Invoke(call);
        }

        /// Marca o resultado final confirmado pelo CDR para um número que saiu da fila —
        /// encerra o estado pendente (a linha do Histórico já foi importada pelo pipeline normal
        /// de sincronização de CDR; isto só fecha o rastreamento local e evita reprocessar).
        public static void MarcarFinalizada(string numeroNormalizado, bool abandonada)
        {
            lock (_lock)
            {
                if (_pendentes.TryGetValue(numeroNormalizado, out var call) &&
                    call.Status == QueuePendingStatus.AguardandoCdrFinal)
                {
                    call.Status = abandonada ? QueuePendingStatus.Abandonada : QueuePendingStatus.Recebida;
                    call.JaImportada = true;
                }
            }
        }

        public static bool EstaResolvida(string numeroNormalizado)
        {
            lock (_lock)
            {
                if (!_pendentes.TryGetValue(numeroNormalizado, out var call)) return true;
                return call.Status is QueuePendingStatus.Recebida or QueuePendingStatus.Abandonada;
            }
        }

        public static QueuePendingCall? ObterPendente(string numeroNormalizado)
        {
            lock (_lock)
            {
                return _pendentes.TryGetValue(numeroNormalizado, out var call) ? call : null;
            }
        }
    }
}
