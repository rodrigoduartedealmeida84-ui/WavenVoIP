using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class AudioDeviceService
    {
        private static readonly string[] _headsetKeywords =
            { "headset", "headphone", "fone", "bluetooth", "hands-free", "hands free",
              "wh-", "airpods", "jbl", "redmi", "galaxy buds",
              "intelbras", "usb audio", "usb audio device", "usb headset" };

        private static readonly string[] _speakerKeywords =
            { "speaker", "speakers", "alto-falante", "alto falante", "realtek", "intel",
              "high definition audio", "áudio interno", "internal", "output", "saída" };

        private static readonly string[] _intelbraskeywords =
            { "intelbras" };

        public record AudioInputInfo(string Id, string Nome, int WaveInIndex);
        public record AudioOutputInfo(string Id, string Nome, int WaveOutIndex);

        public static List<AudioInputInfo> ListarDispositivosEntrada()
        {
            var result = new List<AudioInputInfo>();
            try
            {
                var waveInMap = ConstruirMapaWaveIn();
                using var enumerator = new MMDeviceEnumerator();
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    int idx = BuscarIndiceWaveIn(d.FriendlyName, waveInMap);
                    result.Add(new AudioInputInfo(d.ID, d.FriendlyName, idx));
                    Log($"AUDIO_DEVICE_DETECTED | type=input nome={d.FriendlyName} waveInIdx={idx}");
                }
            }
            catch (Exception ex) { Log($"AUDIO_DEVICE_ENUM_ERROR | input: {ex.Message}"); }
            return result;
        }

        public static List<AudioOutputInfo> ListarDispositivosSaida()
        {
            var result = new List<AudioOutputInfo>();
            try
            {
                var waveOutMap = ConstruirMapaWaveOut();
                using var enumerator = new MMDeviceEnumerator();
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    int idx = BuscarIndiceWaveOut(d.FriendlyName, waveOutMap);
                    result.Add(new AudioOutputInfo(d.ID, d.FriendlyName, idx));
                    Log($"AUDIO_DEVICE_DETECTED | type=output nome={d.FriendlyName} waveOutIdx={idx}");
                }
            }
            catch (Exception ex) { Log($"AUDIO_DEVICE_ENUM_ERROR | output: {ex.Message}"); }
            return result;
        }

        public static bool EhHeadsetIntelbras(string nome)
        {
            var n = (nome ?? string.Empty).ToLowerInvariant();
            return _intelbraskeywords.Any(k => n.Contains(k));
        }

        public static bool EhHeadset(string nome)
        {
            var n = (nome ?? string.Empty).ToLowerInvariant();
            return _headsetKeywords.Any(k => n.Contains(k));
        }

        public static bool EhSpeaker(string nome)
        {
            var n = (nome ?? string.Empty).ToLowerInvariant();
            return _speakerKeywords.Any(k => n.Contains(k));
        }

        /// <summary>
        /// Preenche apenas os campos de áudio que estiverem vazios em config.
        /// Regra: se headset Intelbras conectado → mic+call=Intelbras, toque=speaker.
        /// Se não → tudo padrão do sistema (campo vazio).
        /// Também faz migração do audio-config.json antigo.
        /// </summary>
        public static void AplicarSelecaoAutomatica(SipConfig config)
        {
            MigrarConfigLegada(config);

            var inputs  = ListarDispositivosEntrada();
            var outputs = ListarDispositivosSaida();

            var intelbrasMic = inputs.FirstOrDefault(d => EhHeadsetIntelbras(d.Nome));
            var intelbrasSpeaker = outputs.FirstOrDefault(d => EhHeadsetIntelbras(d.Nome));
            bool temIntelbras = intelbrasMic != null || intelbrasSpeaker != null;

            if (temIntelbras)
                Log($"AUDIO_HEADSET_INTELBRAS_DETECTED | mic={intelbrasMic?.Nome ?? "none"} speaker={intelbrasSpeaker?.Nome ?? "none"}");

            // Alto-falante candidato para toque: não-headset, prioridade: Realtek > Alto-falantes > Intel > qualquer
            Log("RING_DEVICE_AUTO_SELECT_START | AplicarSelecaoAutomatica");
            var naoHeadsets = outputs.Where(d => !EhHeadset(d.Nome)).ToList();
            foreach (var d in outputs)
            {
                if (EhHeadset(d.Nome)) Log($"RING_DEVICE_REJECTED_HEADSET | nome={d.Nome}");
                else                   Log($"RING_DEVICE_CANDIDATE | nome={d.Nome}");
            }

            AudioOutputInfo? speakerCandidato = null;
            var prioridades = new[]
            {
                new[] { "realtek" },
                new[] { "alto-falante", "alto falante" },
                new[] { "speaker", "speakers" },
                new[] { "intel", "high definition audio" },
                _speakerKeywords
            };
            foreach (var grupo in prioridades)
            {
                speakerCandidato = naoHeadsets.FirstOrDefault(d =>
                {
                    var n = d.Nome.ToLowerInvariant();
                    return grupo.Any(k => n.Contains(k));
                });
                if (speakerCandidato != null) break;
            }
            speakerCandidato ??= naoHeadsets.FirstOrDefault();

            if (speakerCandidato != null) Log($"RING_DEVICE_SELECTED | nome={speakerCandidato.Nome}");
            else                          Log("RING_DEVICE_FALLBACK_DEFAULT | nenhum candidato nao-headset");

            // AudioInputDevice (microfone da ligação)
            if (string.IsNullOrEmpty(config.AudioInputDevice))
            {
                if (temIntelbras && intelbrasMic != null)
                {
                    config.AudioInputDevice = intelbrasMic.Nome;
                    Log($"AUDIO_AUTO_INPUT_SELECTED | dispositivo={intelbrasMic.Nome} (Intelbras)");
                }
                else
                {
                    config.AudioInputDevice = string.Empty;
                    Log("AUDIO_AUTO_INPUT_SELECTED | dispositivo=<padrão do sistema>");
                }
            }

            // CallOutputDevice (áudio da ligação)
            if (string.IsNullOrEmpty(config.CallOutputDevice))
            {
                if (temIntelbras && intelbrasSpeaker != null)
                {
                    config.CallOutputDevice = intelbrasSpeaker.Nome;
                    Log($"AUDIO_AUTO_CALL_OUTPUT_SELECTED | dispositivo={intelbrasSpeaker.Nome} (Intelbras)");
                }
                else
                {
                    config.CallOutputDevice = string.Empty;
                    Log("AUDIO_AUTO_CALL_OUTPUT_SELECTED | dispositivo=<padrão do sistema>");
                }
            }

            // RingOutputDevice (toque) — NUNCA usar headset se houver speaker disponível
            if (string.IsNullOrEmpty(config.RingOutputDevice))
            {
                if (speakerCandidato != null)
                {
                    config.RingOutputDevice = speakerCandidato.Nome;
                    Log($"AUDIO_AUTO_RING_OUTPUT_SELECTED | dispositivo={speakerCandidato.Nome}");
                }
                else
                {
                    config.RingOutputDevice = string.Empty;
                    Log("AUDIO_AUTO_RING_OUTPUT_SELECTED | dispositivo=<padrão do sistema>");
                }
            }

            if (config.RingVolume <= 0)
            {
                config.RingVolume = 80;
                Log("AUDIO_RING_VOLUME_SET | volume=80 (default)");
            }
        }

        private static void MigrarConfigLegada(SipConfig config)
        {
            try
            {
                var audioPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WavenVoIP", "audio-config.json");

                if (!File.Exists(audioPath)) return;

                var audio = System.Text.Json.JsonSerializer.Deserialize<ConfiguracaoAudio>(File.ReadAllText(audioPath));
                if (audio == null) return;

                bool migrou = false;

                if (string.IsNullOrEmpty(config.AudioInputDevice) &&
                    !string.IsNullOrEmpty(audio.Microfone) &&
                    audio.Microfone != "Padrão do sistema")
                {
                    config.AudioInputDevice = audio.Microfone;
                    migrou = true;
                }

                if (string.IsNullOrEmpty(config.CallOutputDevice) &&
                    !string.IsNullOrEmpty(audio.AltoFalante) &&
                    audio.AltoFalante != "Padrão do sistema")
                {
                    config.CallOutputDevice = audio.AltoFalante;
                    migrou = true;
                }

                if (string.IsNullOrEmpty(config.RingOutputDevice) &&
                    !string.IsNullOrEmpty(audio.DispositivoToqueNome))
                {
                    config.RingOutputDevice = audio.DispositivoToqueNome;
                    migrou = true;
                }

                if (migrou)
                    Log($"AUDIO_LEGACY_CONFIG_MIGRATED | input={config.AudioInputDevice} call={config.CallOutputDevice} ring={config.RingOutputDevice}");
            }
            catch { }
        }

        public static int IndiceWaveInPorNome(string nome)
        {
            if (string.IsNullOrEmpty(nome)) return -1;
            var map = ConstruirMapaWaveIn();
            return BuscarIndiceWaveIn(nome, map);
        }

        public static int IndiceWaveOutPorNome(string nome)
        {
            if (string.IsNullOrEmpty(nome)) return -1;
            var map = ConstruirMapaWaveOut();
            return BuscarIndiceWaveOut(nome, map);
        }

        public static string? BuscarIdDispositivoPorNome(string nome, bool saida)
        {
            if (string.IsNullOrEmpty(nome)) return null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var flow = saida ? DataFlow.Render : DataFlow.Capture;
                return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.Equals(nome, StringComparison.OrdinalIgnoreCase))
                    ?.ID;
            }
            catch { return null; }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static Dictionary<string, int> ConstruirMapaWaveIn()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                for (int i = 0; i < WaveIn.DeviceCount; i++)
                {
                    var prod = WaveIn.GetCapabilities(i).ProductName ?? string.Empty;
                    if (!map.ContainsKey(prod)) map[prod] = i;
                }
            }
            catch { }
            return map;
        }

        private static Dictionary<string, int> ConstruirMapaWaveOut()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                for (int i = 0; i < WaveOut.DeviceCount; i++)
                {
                    var prod = WaveOut.GetCapabilities(i).ProductName ?? string.Empty;
                    if (!map.ContainsKey(prod)) map[prod] = i;
                }
            }
            catch { }
            return map;
        }

        private static int BuscarIndiceWaveIn(string nomeFriendly, Dictionary<string, int> map)
        {
            foreach (var kv in map)
            {
                if (nomeFriendly.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.StartsWith(nomeFriendly, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return -1;
        }

        private static int BuscarIndiceWaveOut(string nomeFriendly, Dictionary<string, int> map)
        {
            foreach (var kv in map)
            {
                if (nomeFriendly.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.StartsWith(nomeFriendly, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return -1;
        }

        private static void Log(string msg)
        {
            try
            {
                var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WavenVoIP");
                Directory.CreateDirectory(pasta);
                File.AppendAllText(Path.Combine(pasta, "ui_flow_debug.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
