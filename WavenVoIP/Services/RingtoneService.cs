using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WavenVoIP.Services
{
    public sealed class RingtoneService : IDisposable
    {
        private IWavePlayer? _player;
        private AudioFileReader? _reader;
        private MMDeviceEnumerator? _enumerator;
        private bool _disposed;
        private bool _looping = true;

        private static readonly string[] _speakerKeywords =
            { "speaker", "speakers", "alto-falante", "alto falante", "realtek", "intel",
              "high definition audio", "áudio interno", "internal" };

        private static readonly string[] _headsetKeywords =
            { "headset", "headphone", "fone", "bluetooth", "hands-free", "hands free",
              "wh-", "airpods", "jbl", "redmi", "galaxy buds" };

        public static List<(string Id, string Nome)> ListarDispositivosSaida()
        {
            var result = new List<(string, string)>();
            try
            {
                using var e = new MMDeviceEnumerator();
                foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    result.Add((d.ID, d.FriendlyName));
            }
            catch { }
            return result;
        }

        public void Tocar(string arquivoToque, string dispositivoId, string dispositivoNome, string fonte, bool loop = true)
        {
            Parar();
            if (_disposed) return;
            _looping = loop;

            try
            {
                Log($"RING_START | RING_SOURCE={fonte}");
                Log($"RING_CONFIG_DEVICE_ID={dispositivoId}");
                Log($"RING_CONFIG_DEVICE_NAME={dispositivoNome}");

                if (!File.Exists(arquivoToque))
                {
                    Log($"RING_ERROR=arquivo não encontrado: {arquivoToque}");
                    return;
                }

                // Enumerator stays alive for the duration of playback
                _enumerator = new MMDeviceEnumerator();
                MMDevice? device = null;

                if (!string.IsNullOrEmpty(dispositivoId))
                {
                    device = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                                        .FirstOrDefault(d => d.ID == dispositivoId);

                    if (device != null)
                        Log($"RING_DEVICE_FOUND=True | RING_DEVICE_USED_NAME={device.FriendlyName}");
                    else
                        Log($"RING_DEVICE_FOUND=False | RING_FALLBACK_REASON=ID '{dispositivoId}' não encontrado");
                }

                if (device == null)
                {
                    device = SelecionarSpeakerFisico(_enumerator);
                    if (device != null)
                        Log($"RING_DEVICE_FOUND=True (auto) | RING_DEVICE_USED_NAME={device.FriendlyName}");
                    else
                    {
                        device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        Log($"RING_FALLBACK_REASON=SelecionarSpeakerFisico retornou null | RING_DEVICE_USED_NAME={device?.FriendlyName ?? "?"}");
                    }
                }

                Log($"RING_DEVICE_USED_ID={device?.ID ?? "?"}");
                Log("RING_PLAYER=WasapiOut");

                _reader = new AudioFileReader(arquivoToque);
                _player = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
                _player.Init(_reader);
                _player.PlaybackStopped += OnPlaybackStopped;
                _player.Play();
            }
            catch (Exception ex)
            {
                Log($"RING_ERROR={ex.GetType().Name}: {ex.Message}");
                Parar();
            }
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (_disposed || !_looping || _player == null || _reader == null) return;
            try
            {
                _reader.Position = 0;
                _player.Play();
            }
            catch { }
        }

        public void Parar()
        {
            _looping = false;
            try { _player?.Stop(); } catch { }
            try { _player?.Dispose(); _player = null; } catch { }
            try { _reader?.Dispose(); _reader = null; } catch { }
            // dispose enumerator AFTER player and reader
            try { _enumerator?.Dispose(); _enumerator = null; } catch { }
        }

        public void Dispose()
        {
            _disposed = true;
            Parar();
        }

        private static MMDevice? SelecionarSpeakerFisico(MMDeviceEnumerator enumerator)
        {
            var todos = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            foreach (var d in todos)
            {
                var n = d.FriendlyName?.ToLowerInvariant() ?? string.Empty;
                Log($"RING_DEVICE_SCAN: {d.FriendlyName} | ignorado={_headsetKeywords.Any(k => n.Contains(k))}");
            }

            var candidatos = todos.Where(d =>
            {
                var n = d.FriendlyName?.ToLowerInvariant() ?? string.Empty;
                return !_headsetKeywords.Any(k => n.Contains(k));
            }).ToList();

            return candidatos.FirstOrDefault(d =>
            {
                var n = d.FriendlyName?.ToLowerInvariant() ?? string.Empty;
                return _speakerKeywords.Any(k => n.Contains(k));
            }) ?? candidatos.FirstOrDefault();
        }

        internal static void Log(string mensagem)
        {
            try
            {
                var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WavenVoIP");
                Directory.CreateDirectory(pasta);
                File.AppendAllText(Path.Combine(pasta, "ui_flow_debug.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {mensagem}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
