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
    internal sealed class MuteableAudioSource : IAudioSource
    {
        private readonly WindowsAudioEndPoint _inner;
        private volatile bool _muted;

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
            _inner.OnAudioSourceEncodedSample      += RelayEncodedSample;
            _inner.OnAudioSourceEncodedFrameReady  += RelayEncodedFrame;
            _inner.OnAudioSourceError              += e => OnAudioSourceError?.Invoke(e);
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
            // Mutado: buffer zerado (silence) de mesmo tamanho mantém RTP TX ativo
            handler.Invoke(durationRtpUnits, _muted ? new byte[sample.Length] : sample);
        }

        private void RelayEncodedFrame(EncodedAudioFrame frame)
        {
            var handler = OnAudioSourceEncodedFrameReady;
            if (handler == null) return;
            if (_muted)
            {
                var silenceFrame = new EncodedAudioFrame(
                    frame.MediaStreamIndex,
                    frame.AudioFormat,
                    frame.DurationMilliSeconds,
                    new byte[frame.EncodedAudio.Length]);
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
        public void SetAudioSourceFormat(AudioFormat audioFormat)   => _inner.SetAudioSourceFormat(audioFormat);
        public void RestrictFormats(Func<AudioFormat, bool> filter) => _inner.RestrictFormats(filter);
        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
            => _inner.ExternalAudioSourceRawSample(samplingRate, durationMilliseconds, sample);
        public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
        public bool IsAudioSourcePaused()        => _inner.IsAudioSourcePaused();
    }
}
