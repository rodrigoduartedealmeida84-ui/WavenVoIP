using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    internal sealed class MuteableAudioSource : IAudioSource
    {
        private readonly WindowsAudioEndPoint _inner;
        private volatile bool _muted;

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

        public MuteableAudioSource(WindowsAudioEndPoint inner)
        {
            _inner = inner;
            _inner.OnAudioSourceEncodedSample     += RelayEncodedSample;
            _inner.OnAudioSourceEncodedFrameReady += RelayEncodedFrame;
            _inner.OnAudioSourceError             += e => OnAudioSourceError?.Invoke(e);
        }

        public void SetMute(bool muted) => _muted = muted;

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
                handler.Invoke(durationRtpUnits, sample);
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
            else
            {
                handler.Invoke(frame);
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
        }
    }
}
