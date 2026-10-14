using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NAudio.Codecs;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;

namespace WavenVoIP.Services
{
    // Proxy de IAudioSource que intercepta samples TX e injeta silêncio quando mutado.
    // WaveIn continua rodando: RTP TX flui normalmente com silence, Asterisk não descarta a sessão.
    // WaveOut (RX/playback) nunca é afetado — o proxy só toca no pipeline de envio.
    //
    // v1.3.1 — silence byte correto por codec:
    //   PCMU (G.711 u-law, FormatID=0): silence = 0xFF  (PCM 0 codificado em u-law)
    //   PCMA (G.711 a-law, FormatID=8): silence = 0xD5  (PCM 0 codificado em a-law)
    //   0x00 (all-zeros) NÃO é silêncio em G.711 — decodifica para ruído muito alto.
    //
    // v2.5.0 — também nivela o microfone (MicAutoGain) quando o codec negociado é G.711:
    // decodifica o bloco, aplica o ganho automático com limitador e recodifica. O endpoint só
    // entrega áudio já codificado, por isso o ajuste é feito aqui. Outros codecs passam direto.
    internal sealed class MuteableAudioSource : IAudioSource
    {
        private readonly WindowsAudioEndPoint _inner;
        private volatile bool _muted;

        private readonly MicAutoGain? _autoGain;
        private readonly object _autoGainLock = new();
        private AudioCodecsEnum _codec = AudioCodecsEnum.PCMU;
        private int _clockRate = 8000;
        // O endpoint pode entregar o MESMO bloco pelos dois eventos (sample e frame): o bloco
        // já nivelado é reaproveitado para não processar (nem avançar o estado) duas vezes.
        private byte[]? _ultimaEntrada;
        private byte[]? _ultimaSaida;

        // Silence byte negociado via SetAudioSourceFormat — padrão PCMU (mais comum no Asterisk)
        private byte _silenceByte = 0xFF;

        public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
        public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
        public event SourceErrorDelegate? OnAudioSourceError;

        // Obsoleto em SIPSorcery 8.x — endpoint só gera encoded samples
        public event RawAudioSampleDelegate? OnAudioSourceRawSample
        {
            add { }
            remove { }
        }

        public MuteableAudioSource(WindowsAudioEndPoint inner, bool nivelarMicrofone = false)
        {
            _inner = inner;
            _autoGain = nivelarMicrofone ? new MicAutoGain() : null;
            _inner.OnAudioSourceEncodedSample     += RelayEncodedSample;
            _inner.OnAudioSourceEncodedFrameReady += RelayEncodedFrame;
            _inner.OnAudioSourceError             += e => OnAudioSourceError?.Invoke(e);
        }

        public void SetMute(bool muted) => _muted = muted;

        // v2.5.0 — nível do áudio RECEBIDO (pacote RTP G.711 que vai para o alto-falante). O
        // nivelamento usa isso para não tratar como fala — nem amplificar — o retorno do
        // alto-falante captado pelo microfone (ver MicAutoGain, supressão de eco).
        private double _farDb = -120.0;
        private long _farTick;

        public void InformarAudioRemoto(int payloadType, byte[]? payload)
        {
            if (_autoGain == null || payload == null || payload.Length == 0) return;
            if (payloadType != 0 && payloadType != 8) return;      // só G.711 (PCMU/PCMA)
            double soma = 0;
            if (payloadType == 8)
                for (int i = 0; i < payload.Length; i++) { double v = ALawDecoder.ALawToLinearSample(payload[i]); soma += v * v; }
            else
                for (int i = 0; i < payload.Length; i++) { double v = MuLawDecoder.MuLawToLinearSample(payload[i]); soma += v * v; }
            var rms = Math.Sqrt(soma / payload.Length) / 32768.0;
            System.Threading.Volatile.Write(ref _farDb, 20.0 * Math.Log10(Math.Max(rms, 1e-7)));
            System.Threading.Volatile.Write(ref _farTick, Environment.TickCount64);
        }

        private double FarDbAtual() =>
            Environment.TickCount64 - System.Threading.Volatile.Read(ref _farTick) <= 80
                ? System.Threading.Volatile.Read(ref _farDb) : -120.0;

        // Resumo do nivelamento do microfone nesta chamada (vazio quando desligado).
        public string EncerrarAutoGain()
        {
            if (_autoGain == null) return string.Empty;
            lock (_autoGainLock) return _autoGain.EncerrarEResumir();
        }

        // (nível médio da fala captada, blocos com fala, blocos limitados) desta chamada.
        public (double falaEntradaDb, long blocosFala, long blocosLimitados) EstatisticaAutoGain()
        {
            if (_autoGain == null) return (double.NaN, 0, 0);
            lock (_autoGainLock) return (_autoGain.FalaEntradaMediaDb, _autoGain.BlocosFala, _autoGain.BlocosLimitados);
        }

        private byte[] Nivelar(byte[] encoded)
        {
            if (_autoGain == null || encoded == null || encoded.Length == 0) return encoded!;
            var codec = _codec;
            if (codec != AudioCodecsEnum.PCMU && codec != AudioCodecsEnum.PCMA) return encoded;

            lock (_autoGainLock)
            {
                if (ReferenceEquals(encoded, _ultimaEntrada) && _ultimaSaida != null) return _ultimaSaida;

                var pcm = new short[encoded.Length];
                if (codec == AudioCodecsEnum.PCMA)
                    for (int i = 0; i < pcm.Length; i++) pcm[i] = ALawDecoder.ALawToLinearSample(encoded[i]);
                else
                    for (int i = 0; i < pcm.Length; i++) pcm[i] = MuLawDecoder.MuLawToLinearSample(encoded[i]);

                _autoGain.Processar(pcm, _clockRate, FarDbAtual());

                var saida = new byte[encoded.Length];
                if (codec == AudioCodecsEnum.PCMA)
                    for (int i = 0; i < pcm.Length; i++) saida[i] = ALawEncoder.LinearToALawSample(pcm[i]);
                else
                    for (int i = 0; i < pcm.Length; i++) saida[i] = MuLawEncoder.LinearToMuLawSample(pcm[i]);

                _ultimaEntrada = encoded;
                _ultimaSaida = saida;
                return saida;
            }
        }

        // Desconecta do inner ao encerrar chamada para evitar memory leak
        public void Detach()
        {
            _inner.OnAudioSourceEncodedSample     -= RelayEncodedSample;
            _inner.OnAudioSourceEncodedFrameReady -= RelayEncodedFrame;
        }

        private void RelayEncodedSample(uint durationRtpUnits, byte[] sample)
        {
            var handler = OnAudioSourceEncodedSample;
            if (handler == null) return;
            if (_muted)
            {
                // Silêncio válido G.711: preenche com _silenceByte (0xFF para PCMU, 0xD5 para PCMA).
                // NÃO usar all-zeros — 0x00 em G.711 decodifica para ruído alto (~-8031 linear PCM).
                var silence = new byte[sample.Length];
                Array.Fill(silence, _silenceByte);
                handler.Invoke(durationRtpUnits, silence);
            }
            else
            {
                handler.Invoke(durationRtpUnits, Nivelar(sample));
            }
        }

        private void RelayEncodedFrame(EncodedAudioFrame frame)
        {
            var handler = OnAudioSourceEncodedFrameReady;
            if (handler == null) return;
            if (_muted)
            {
                var silence = new byte[frame.EncodedAudio.Length];
                Array.Fill(silence, _silenceByte);
                var silenceFrame = new EncodedAudioFrame(
                    frame.MediaStreamIndex,
                    frame.AudioFormat,
                    frame.DurationMilliSeconds,
                    silence);
                handler.Invoke(silenceFrame);
            }
            else if (_autoGain == null)
            {
                handler.Invoke(frame);
            }
            else
            {
                var nivelado = Nivelar(frame.EncodedAudio);
                handler.Invoke(ReferenceEquals(nivelado, frame.EncodedAudio)
                    ? frame
                    : new EncodedAudioFrame(frame.MediaStreamIndex, frame.AudioFormat, frame.DurationMilliSeconds, nivelado));
            }
        }

        // Toda gestão de ciclo de vida delega ao inner endpoint
        public Task PauseAudio()  => _inner.PauseAudio();
        public Task ResumeAudio() => _inner.ResumeAudio();
        public Task StartAudio()  => _inner.StartAudio();
        public Task CloseAudio()  => _inner.CloseAudio();

        public List<AudioFormat> GetAudioSourceFormats()            => _inner.GetAudioSourceFormats();
        public void RestrictFormats(Func<AudioFormat, bool> filter) => _inner.RestrictFormats(filter);
        public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
        public bool IsAudioSourcePaused()        => _inner.IsAudioSourcePaused();
        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
            => _inner.ExternalAudioSourceRawSample(samplingRate, durationMilliseconds, sample);

        // Intercepta a negociação de formato para definir o silence byte correto
        public void SetAudioSourceFormat(AudioFormat audioFormat)
        {
            _inner.SetAudioSourceFormat(audioFormat);
            // PCMA (G.711 a-law, Codec=PCMA ou FormatID=8): silence = 0xD5
            // PCMU (G.711 u-law, Codec=PCMU ou FormatID=0): silence = 0xFF
            // Outros codecs (G.722, Opus, etc.): 0xFF como aproximação segura
            _silenceByte = audioFormat.Codec == AudioCodecsEnum.PCMA ? (byte)0xD5 : (byte)0xFF;
            _codec = audioFormat.Codec;
            _clockRate = audioFormat.ClockRate > 0 ? audioFormat.ClockRate : 8000;
        }
    }
}
