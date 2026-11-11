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

            // v2.6.2 — o toque NÃO é mais "congelado" num nome de dispositivo: vazio = Automático,
            // resolvido a cada chamada (alto-falante do computador, nunca o headset). Antes o nome
            // escolhido aqui era gravado e, se o aparelho mudasse, o toque ficava preso a ele.
            // Rótulos antigos gravados como se fossem dispositivo ("Automático (Realtek/Speaker)")
            // voltam a ser Automático.
            if (EhNomeAutomatico(config.RingOutputDevice) && !string.IsNullOrEmpty(config.RingOutputDevice))
            {
                Log($"AUDIO_RING_LEGACY_LABEL_CLEARED | valor=\"{config.RingOutputDevice}\"");
                config.RingOutputDevice = string.Empty;
                config.RingOutputDeviceId = string.Empty;
            }
            try
            {
                var (prev, motivoPrev) = EscolherSaidaToque(config.RingOutputDevice, config.RingOutputDeviceId, ListarSaidasParaToque());
                Log($"RING_DEVICE_PREVIEW | configurado=\"{config.RingOutputDevice}\" usaria=\"{prev?.Nome ?? "padrão do Windows"}\" motivo={motivoPrev}");
            }
            catch { }

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

                // v2.6.2 — "Automático (...)" é rótulo de tela, não dispositivo: não migrar
                // (era isso que recolocava o rótulo no sipconfig.json a cada abertura).
                if (string.IsNullOrEmpty(config.RingOutputDevice) &&
                    !EhNomeAutomatico(audio.DispositivoToqueNome))
                {
                    config.RingOutputDevice = audio.DispositivoToqueNome;
                    if (string.IsNullOrEmpty(config.RingOutputDeviceId)) config.RingOutputDeviceId = audio.DispositivoToqueId ?? string.Empty;
                    migrou = true;
                }

                if (migrou)
                    Log($"AUDIO_LEGACY_CONFIG_MIGRATED | input={config.AudioInputDevice} call={config.CallOutputDevice} ring={config.RingOutputDevice}");
            }
            catch { }
        }

        // v2.6.1 — delegam ao MicrofoneService (ID exato do dispositivo; nome sem a numeração
        // "N- " do Windows). Só devolvem índice de dispositivo realmente encontrado; -1 = padrão.
        public static int IndiceWaveInPorNome(string nome)
        {
            if (string.IsNullOrEmpty(nome)) return -1;
            var r = MicrofoneService.Resolver(nome, null, saida: false);
            return r.Situacao is SituacaoDispositivo.Ok or SituacaoDispositivo.Renomeado ? r.IndiceWave : -1;
        }

        public static int IndiceWaveOutPorNome(string nome)
        {
            if (string.IsNullOrEmpty(nome)) return -1;
            var r = MicrofoneService.Resolver(nome, null, saida: true);
            return r.Situacao is SituacaoDispositivo.Ok or SituacaoDispositivo.Renomeado ? r.IndiceWave : -1;
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

        // ── v2.6.2 — Saída do toque ─────────────────────────────────────────────
        //
        // Regra: com headset conectado o toque sai no alto-falante do computador e a conversa no
        // headset; sem headset o toque sai no alto-falante normalmente.
        //
        // Por que não basta o nome nem o "tipo" do Windows: o headset Intelbras CHS 55B se
        // apresenta ao Windows como "Alto-falantes (Intelbras …)" e com forma física SPEAKERS —
        // igual ao Realtek do PC (medido no ramal 100 em 11/11/2026). O que o diferencia é o
        // barramento (USB) e o nome. E cada porta USB gera um ID novo ("3- Intelbras …"), então o
        // dispositivo configurado é reencontrado por ID → nome exato → nome sem a numeração.

        public enum TipoSaida { AltoFalanteInterno, Monitor, Externa, Headset }

        // FormaFisica = PKEY_AudioEndpoint_FormFactor (1=Speakers, 3=Headphones, 5=Headset,
        // 6=Handset, 8=SPDIF, 9=HDMI/DisplayPort, 10=desconhecido; -1=não informado).
        // Barramento = PKEY_Device_EnumeratorName ("HDAUDIO", "INTELAUDIO", "USB", "BTHENUM"…).
        public sealed record SaidaAudio(string Id, string Nome, bool Ativo, int FormaFisica, string Barramento, bool PadraoWindows);

        private static readonly string[] _barramentosBluetooth = { "BTHENUM", "BTHHFENUM", "BTHLEDEVICE", "BTHLE" };
        private static readonly string[] _nomesMonitor = { "hdmi", "displayport", "nvidia high definition", "amd high definition", "nvidia(r) high definition" };

        public static TipoSaida ClassificarSaida(SaidaAudio s)
        {
            var bus = (s.Barramento ?? string.Empty).Trim().ToUpperInvariant();
            var n = (s.Nome ?? string.Empty).ToLowerInvariant();
            if (EhHeadset(s.Nome ?? string.Empty)) return TipoSaida.Headset;
            if (s.FormaFisica is 3 or 5 or 6) return TipoSaida.Headset;
            if (_barramentosBluetooth.Contains(bus)) return TipoSaida.Headset;
            // USB sem nome conhecido: pode ser headset de outra marca (o Intelbras diz que é
            // "Speakers"), pode ser caixinha USB. Só serve se não houver alto-falante interno.
            if (bus == "USB") return TipoSaida.Externa;
            if (s.FormaFisica is 8 or 9 || _nomesMonitor.Any(k => n.Contains(k))) return TipoSaida.Monitor;
            return TipoSaida.AltoFalanteInterno;
        }

        // Rótulos de tela/estados antigos que NÃO são dispositivo: "Automático",
        // "Automático (Realtek/Speaker)" (gravado por versões antigas), "Padrão do sistema".
        public static bool EhNomeAutomatico(string? nome)
        {
            var n = (nome ?? string.Empty).Trim();
            return n.Length == 0
                || n.StartsWith("Automático", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("Automatico", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Padrão do sistema", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Padrao do sistema", StringComparison.OrdinalIgnoreCase);
        }

        // Escolha pura (testável). Devolve o dispositivo e o motivo (vai para o log):
        //   configurado / configurado_renomeado  — o escolhido pelo operador (respeitado)
        //   auto_*                               — Automático
        //   configurado_ausente+auto_*           — o escolhido sumiu; usa o automático
        //   sem_saida                            — nada utilizável (quem chama usa o padrão do Windows)
        public static (SaidaAudio? escolhido, string motivo) EscolherSaidaToque(string? nomeConfigurado, string? idConfigurado, IReadOnlyList<SaidaAudio> saidas)
        {
            var ativos = saidas.Where(s => s.Ativo).ToList();
            var nome = EhNomeAutomatico(nomeConfigurado) ? string.Empty : nomeConfigurado!.Trim();
            var id = nome.Length == 0 ? string.Empty : (idConfigurado ?? string.Empty).Trim();
            var prefixo = string.Empty;

            if (nome.Length > 0)
            {
                var eps = saidas.Select(s => new EndpointAudio(s.Id, s.Nome, s.Ativo, s.PadraoWindows, false, s.Ativo ? "Active" : "NotPresent")).ToList();
                var (sit, esc, _) = MicrofoneService.Escolher(nome, id, eps);
                if (esc != null && sit is SituacaoDispositivo.Ok or SituacaoDispositivo.Renomeado)
                {
                    var s = ativos.First(a => a.Id == esc.Id);
                    return (s, sit == SituacaoDispositivo.Ok ? "configurado" : "configurado_renomeado");
                }
                prefixo = "configurado_ausente+";
            }

            static int Ordem(TipoSaida t) => t switch
            {
                TipoSaida.AltoFalanteInterno => 0,
                TipoSaida.Monitor => 1,
                TipoSaida.Externa => 2,
                _ => 9
            };
            var candidatos = ativos
                .Select(s => (s, tipo: ClassificarSaida(s)))
                .Where(x => x.tipo != TipoSaida.Headset)
                .OrderBy(x => Ordem(x.tipo))
                .ThenBy(x => x.s.FormaFisica == 1 ? 0 : 1)                 // alto-falante de fato
                .ThenBy(x => x.s.PadraoWindows ? 0 : 1)                    // o que o Windows já usa
                .ThenBy(x => x.s.Nome.Contains("realtek", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
            if (candidatos.Count == 0) return (null, prefixo + "sem_saida");
            var (melhor, tipoMelhor) = candidatos[0];
            return (melhor, prefixo + (tipoMelhor switch
            {
                TipoSaida.AltoFalanteInterno => "auto_alto_falante",
                TipoSaida.Monitor => "auto_monitor",
                _ => "auto_saida_usb"
            }));
        }

        private static readonly PropertyKey _pkFormaFisica = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
        private static readonly PropertyKey _pkBarramento = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

        // Saídas ATIVAS do Windows com forma física e barramento. Só leitura.
        public static List<SaidaAudio> ListarSaidasParaToque()
        {
            var lista = new List<SaidaAudio>();
            try
            {
                using var en = new MMDeviceEnumerator();
                string idPadrao = string.Empty;
                try { idPadrao = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        string nome; try { nome = d.FriendlyName; } catch { nome = "?"; }
                        int ff = -1; string bus = string.Empty;
                        try
                        {
                            var p = d.Properties;
                            if (p.Contains(_pkFormaFisica)) ff = Convert.ToInt32(p[_pkFormaFisica].Value);
                            if (p.Contains(_pkBarramento)) bus = p[_pkBarramento].Value?.ToString() ?? string.Empty;
                        }
                        catch { }
                        lista.Add(new SaidaAudio(d.ID, nome, true, ff, bus, d.ID == idPadrao));
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log($"AUDIO_DEVICE_ENUM_ERROR | toque: {ex.Message}"); }
            return lista;
        }

        // Configuração efetiva do toque (SipConfig; se vazio, o audio-config.json antigo).
        public static (string nome, string id) ConfiguracaoToque(SipConfig? sip, ConfiguracaoAudio? legado)
        {
            if (sip != null && !EhNomeAutomatico(sip.RingOutputDevice))
                return (sip.RingOutputDevice.Trim(), sip.RingOutputDeviceId ?? string.Empty);
            if ((sip == null || string.IsNullOrWhiteSpace(sip.RingOutputDevice)) && legado != null && !EhNomeAutomatico(legado.DispositivoToqueNome))
                return (legado.DispositivoToqueNome.Trim(), legado.DispositivoToqueId ?? string.Empty);
            return (string.Empty, string.Empty);
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
