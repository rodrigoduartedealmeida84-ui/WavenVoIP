using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace WavenVoIP.Services
{
    // v2.5.1 — tom de chamando (ringback) local das ligações realizadas.
    // Padrão brasileiro (Anatel): 425 Hz, 1 s ligado / 4 s desligado. Toca no MESMO dispositivo de
    // saída da chamada (índice waveOut usado pelo WindowsAudioEndPoint), para o atendente ouvir no
    // headset. Iniciar/Parar são idempotentes e podem vir de qualquer thread; a abertura do
    // dispositivo roda fora da thread de quem chama e nunca segura o lock (Parar nunca espera).
    public sealed class RingbackTone : IDisposable
    {
        private readonly object _gate = new();
        private WaveOutEvent? _saida;
        private bool _ativo;
        private int _geracao;
        private bool _disposed;

        public bool Ativo { get { lock (_gate) return _ativo; } }

        public void Iniciar(int dispositivoSaida, string motivo)
        {
            int geracao;
            lock (_gate)
            {
                if (_disposed || _ativo) return;
                _ativo = true;
                geracao = ++_geracao;
            }
            LogHelper.Sip($"RINGBACK_LOCAL_START | motivo={motivo} dispositivo={dispositivoSaida}");

            Task.Run(() =>
            {
                WaveOutEvent? saida = null;
                try
                {
                    saida = Abrir(dispositivoSaida);
                }
                catch (Exception ex)
                {
                    LogHelper.Sip($"RINGBACK_LOCAL_DEVICE_FALHOU | dispositivo={dispositivoSaida} erro={ex.Message} — usando padrao", LogLevel.WARN);
                    try { saida = dispositivoSaida >= 0 ? Abrir(-1) : null; }
                    catch (Exception ex2) { LogHelper.Sip($"RINGBACK_LOCAL_ERRO | {ex2.Message}", LogLevel.WARN); }
                }
                if (saida == null) return;

                bool manter;
                lock (_gate)
                {
                    manter = _ativo && geracao == _geracao && !_disposed;
                    if (manter) _saida = saida;
                }
                if (manter) { try { saida.Play(); } catch { } }
                else Fechar(saida);   // Parar chegou enquanto o dispositivo abria
            });
        }

        public void Parar(string motivo)
        {
            WaveOutEvent? saida;
            lock (_gate)
            {
                if (!_ativo && _saida == null) return;
                _ativo = false;
                _geracao++;
                saida = _saida;
                _saida = null;
            }
            LogHelper.Sip($"RINGBACK_LOCAL_STOP | motivo={motivo}");
            Fechar(saida);
        }

        public void Dispose()
        {
            lock (_gate) _disposed = true;
            Parar("dispose");
        }

        private static WaveOutEvent Abrir(int dispositivo)
        {
            var saida = new WaveOutEvent { DeviceNumber = dispositivo, DesiredLatency = 150 };
            try { saida.Init(new TomBrasil()); }
            catch { saida.Dispose(); throw; }
            return saida;
        }

        private static void Fechar(WaveOutEvent? saida)
        {
            if (saida == null) return;
            try { saida.Stop(); } catch { }
            try { saida.Dispose(); } catch { }
        }

        // 425 Hz, 1 s ligado / 4 s desligado, nível moderado (~ -15 dBFS), com rampa de 10 ms
        // nas bordas para não estalar. Para sozinho após 3 min (rede de segurança).
        private sealed class TomBrasil : ISampleProvider
        {
            private const int Taxa = 8000;
            private const int Ligado = Taxa;           // 1 s
            private const int Ciclo = Taxa * 5;        // 1 s + 4 s
            private const int Rampa = Taxa / 100;      // 10 ms
            private const long Limite = Taxa * 180L;   // 3 min
            private const float Amplitude = 0.18f;
            private long _pos;

            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Taxa, 1);

            public int Read(float[] buffer, int offset, int count)
            {
                if (_pos >= Limite) return 0;
                for (int i = 0; i < count; i++, _pos++)
                {
                    var noCiclo = (int)(_pos % Ciclo);
                    float v = 0f;
                    if (noCiclo < Ligado)
                    {
                        var env = Math.Min(1f, Math.Min(noCiclo, Ligado - noCiclo) / (float)Rampa);
                        v = Amplitude * env * (float)Math.Sin(2 * Math.PI * 425 * _pos / Taxa);
                    }
                    buffer[offset + i] = v;
                }
                return count;
            }
        }
    }
}
