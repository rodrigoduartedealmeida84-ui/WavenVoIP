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

        // Dispositivos que NUNCA devem tocar o ringtone — headsets, USB audio, Intelbras
        private static readonly string[] _headsetKeywords =
            { "headset", "headphone", "fone", "bluetooth", "hands-free", "hands free",
              "wh-", "airpods", "jbl", "redmi", "galaxy buds",
              "intelbras", "usb audio", "usb audio device", "usb headset" };

        // Prioridade para seleção de alto-falante físico (ordem decrescente)
        private static readonly string[][] _speakerPriority =
        {
            new[] { "realtek" },
            new[] { "alto-falante", "alto falante" },
            new[] { "speaker", "speakers" },
            new[] { "intel", "high definition audio" },
            new[] { "áudio interno", "internal audio", "internal" },
            new[] { "output", "saída" }
        };

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

        public void Tocar(string arquivoToque, string dispositivoId, string dispositivoNome, string fonte, bool loop = true, float volume = 1.0f)
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

                var volClamp = Math.Clamp(volume, 0f, 1f);
                Log($"AUDIO_RING_VOLUME_SET | volume={volClamp:F2}");

                // Enumerator stays alive for the duration of playback
                _enumerator = new MMDeviceEnumerator();
                MMDevice? device = null;

                // 1) Tenta usar o ID explícito salvo pelo usuário
                if (!string.IsNullOrEmpty(dispositivoId))
                {
                    device = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                                        .FirstOrDefault(d => d.ID == dispositivoId);

                    if (device != null)
                        Log($"RING_DEVICE_FOUND=True | RING_DEVICE_USED_NAME={device.FriendlyName}");
                    else
                        Log($"RING_DEVICE_FOUND=False | RING_FALLBACK_REASON=ID '{dispositivoId}' não encontrado — tentando auto-seleção");
                }

                // 2) Se não encontrou por ID, auto-selecionar por nome (nunca headset)
                if (device == null && !string.IsNullOrEmpty(dispositivoNome))
                {
                    var todos = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                    device = todos.FirstOrDefault(d =>
                        d.FriendlyName.Contains(dispositivoNome, StringComparison.OrdinalIgnoreCase) ||
                        dispositivoNome.Contains(d.FriendlyName, StringComparison.OrdinalIgnoreCase));

                    if (device != null)
                        Log($"RING_DEVICE_FOUND=True (por nome) | RING_DEVICE_USED_NAME={device.FriendlyName}");
                    else
                        Log($"RING_DEVICE_FOUND=False (por nome) | nome='{dispositivoNome}' não encontrado — usando auto-seleção");
                }

                // 3) Fallback inteligente: speaker físico, rejeita headset/Intelbras/USB
                if (device == null)
                {
                    device = SelecionarSpeakerFisico(_enumerator);
                    if (device == null)
                    {
                        device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        Log($"RING_DEVICE_FALLBACK_DEFAULT | sem speaker fisico disponivel — usando padrao Windows: {device?.FriendlyName ?? "?"}");
                    }
                }

                Log($"RING_DEVICE_USED_ID={device?.ID ?? "?"}");
                Log("RING_PLAYER=WasapiOut");

                if (fonte == "TestButton")
                    Log($"RING_TEST_DEVICE_USED | dispositivo={device?.FriendlyName ?? "padrão do sistema"}");

                if (fonte == "IncomingCallWindow")
                    Log($"AUDIO_RING_TEST_PLAYED | dispositivo={device?.FriendlyName ?? "padrão do sistema"}");

                _reader = new AudioFileReader(arquivoToque);
                _reader.Volume = volClamp;
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
            Log("RING_DEVICE_AUTO_SELECT_START | enumerando dispositivos de saida");

            List<MMDevice> todos;
            try { todos = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList(); }
            catch { todos = new List<MMDevice>(); }

            // Classificar cada dispositivo
            var candidatos = new List<MMDevice>();
            foreach (var d in todos)
            {
                var n = d.FriendlyName?.ToLowerInvariant() ?? string.Empty;
                bool isHeadset = _headsetKeywords.Any(k => n.Contains(k));
                if (isHeadset)
                    Log($"RING_DEVICE_REJECTED_HEADSET | nome={d.FriendlyName}");
                else
                {
                    Log($"RING_DEVICE_CANDIDATE | nome={d.FriendlyName}");
                    candidatos.Add(d);
                }
            }

            if (candidatos.Count == 0)
            {
                Log("RING_DEVICE_FALLBACK_DEFAULT | nenhum candidato nao-headset encontrado");
                return null;
            }

            // Seleciona por ordem de prioridade
            foreach (var grupo in _speakerPriority)
            {
                var found = candidatos.FirstOrDefault(d =>
                {
                    var n = d.FriendlyName?.ToLowerInvariant() ?? string.Empty;
                    return grupo.Any(k => n.Contains(k));
                });
                if (found != null)
                {
                    Log($"RING_DEVICE_SELECTED | nome={found.FriendlyName} (prioridade: {string.Join("/", grupo)})");
                    return found;
                }
            }

            // Primeiro candidato não-headset
            var fallback = candidatos[0];
            Log($"RING_DEVICE_SELECTED | nome={fallback.FriendlyName} (primeiro nao-headset disponivel)");
            return fallback;
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
