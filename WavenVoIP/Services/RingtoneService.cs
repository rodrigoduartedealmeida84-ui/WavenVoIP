using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WavenVoIP.Services
{
    // Resultado do Tocar — o botão "Testar toque" mostra onde o toque saiu.
    // v2.6.2 — também o estado da saída no Windows (mudo/volume do dispositivo e do Waven VoIP
    // no Mixer de volume): o toque pode "tocar" no dispositivo certo e mesmo assim não ser ouvido.
    public sealed record ResultadoToque(bool Tocando, string Dispositivo, string Motivo, string? Erro, EstadoSaidaToque? Estado = null);

    // null = não foi possível ler (tratado como OK, sem aviso falso).
    public sealed record EstadoSaidaToque(string Dispositivo, bool? MudoWindows, float? VolumeWindows, bool? MudoMixer, float? VolumeMixer)
    {
        public const float VolumeZero = 0.01f;   // ≤ 1% = sem som na prática

        // Texto para o operador; null = nada a avisar. volumeToqueApp = "Volume do toque" (0–1).
        public string? Aviso(float? volumeToqueApp = null)
        {
            var p = new List<string>();
            if (MudoWindows == true) p.Add($"O alto-falante \"{Dispositivo}\" está no MUDO do Windows.");
            else if (VolumeWindows is { } v && v <= VolumeZero) p.Add($"O volume do alto-falante \"{Dispositivo}\" está em 0% no Windows.");
            if (MudoMixer == true) p.Add($"O Waven VoIP está no mudo no Mixer de volume do Windows (\"{Dispositivo}\").");
            else if (VolumeMixer is { } m && m <= VolumeZero) p.Add($"O volume do Waven VoIP está em 0% no Mixer de volume do Windows (\"{Dispositivo}\").");
            if (volumeToqueApp is { } a && a <= VolumeZero) p.Add("O \"Volume do toque\" do Waven VoIP está em 0.");
            return p.Count == 0 ? null : string.Join(" ", p) + " As chamadas recebidas não vão tocar.";
        }

        public string Resumo() =>
            $"dispositivo=\"{Dispositivo}\" mudo_windows={MudoWindows?.ToString() ?? "?"} volume_windows={(VolumeWindows.HasValue ? (VolumeWindows.Value * 100).ToString("F0") + "%" : "?")} " +
            $"mudo_mixer={MudoMixer?.ToString() ?? "?"} volume_mixer={(VolumeMixer.HasValue ? (VolumeMixer.Value * 100).ToString("F0") + "%" : "?")}";

        // Lê o estado de um dispositivo de saída do Windows; a sessão do Waven VoIP no Mixer
        // (o Windows guarda volume/mudo por aplicativo e por dispositivo) só existe se o app já
        // tocou nele — sem sessão, o Mixer fica no padrão (100%, sem mudo).
        public static EstadoSaidaToque Ler(MMDevice dev)
        {
            string nome; try { nome = dev.FriendlyName; } catch { nome = "?"; }
            bool? mw = null; float? vw = null, vm = null; bool? mm = null;
            try { var ev = dev.AudioEndpointVolume; mw = ev.Mute; vw = ev.MasterVolumeLevelScalar; } catch { }
            try
            {
                var pid = (uint)Environment.ProcessId;
                var ss = dev.AudioSessionManager.Sessions;
                for (int i = 0; i < ss.Count; i++)
                {
                    var s = ss[i];
                    if (s.GetProcessID != pid) continue;
                    var sv = s.SimpleAudioVolume;
                    mm = (mm ?? false) || sv.Mute;
                    vm = vm.HasValue ? Math.Min(vm.Value, sv.Volume) : sv.Volume;
                }
            }
            catch { }
            return new EstadoSaidaToque(nome, mw, vw, mm, vm);
        }

        // Estado da saída que o toque USARIA agora (mesma escolha do RingtoneService).
        public static EstadoSaidaToque? LerSaidaDoToque(string nomeCfg, string idCfg)
        {
            try
            {
                var (esc, _) = AudioDeviceService.EscolherSaidaToque(nomeCfg, idCfg, AudioDeviceService.ListarSaidasParaToque());
                using var en = new MMDeviceEnumerator();
                var dev = esc != null ? en.GetDevice(esc.Id) : en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return Ler(dev);
            }
            catch { return null; }
        }
    }

    // v2.6.2 — toque da chamada recebida e do botão "Testar toque" pelo MESMO caminho:
    //  • dispositivo escolhido por AudioDeviceService.EscolherSaidaToque (ID → nome → nome sem a
    //    numeração da porta USB → Automático = alto-falante do computador, nunca o headset);
    //  • se o dispositivo não abrir, tenta o padrão do Windows (antes: silêncio);
    //  • se o dispositivo sumir DURANTE o toque (headset/caixa USB trocada de porta,
    //    desconectada), o toque recomeça sozinho na melhor saída disponível (antes: parava calado);
    //  • Parar() vence qualquer Tocar()/recomeço em andamento (contador de geração), e o recomeço
    //    nunca roda na thread de áudio do NAudio.
    public sealed class RingtoneService : IDisposable
    {
        private IWavePlayer? _player;
        private AudioFileReader? _reader;
        private MMDeviceEnumerator? _enumerator;
        private volatile bool _disposed;
        private readonly object _gate = new();
        private int _geracao;

        // Parâmetros do toque atual (para o recomeço após perda do dispositivo)
        private string _arquivo = string.Empty, _cfgId = string.Empty, _cfgNome = string.Empty, _fonte = string.Empty;
        private bool _loop;
        private float _volume = 1f;
        private int _recuperacoes;
        private const int MaxRecuperacoes = 5;

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

        public ResultadoToque Tocar(string arquivoToque, string dispositivoId, string dispositivoNome, string fonte, bool loop = true, float volume = 1.0f)
        {
            int g = Interlocked.Increment(ref _geracao);
            lock (_gate)
            {
                if (_disposed || g != Volatile.Read(ref _geracao))
                    return new ResultadoToque(false, string.Empty, "cancelado", null);
                PararImpl();
                _arquivo = arquivoToque ?? string.Empty;
                _cfgId = dispositivoId ?? string.Empty;
                _cfgNome = dispositivoNome ?? string.Empty;
                _fonte = fonte ?? string.Empty;
                _loop = loop;
                _volume = Math.Clamp(volume, 0f, 1f);
                _recuperacoes = 0;
                return Iniciar(g);
            }
        }

        // Chamado com _gate travado.
        private ResultadoToque Iniciar(int g)
        {
            try
            {
                Log($"RING_START | RING_SOURCE={_fonte}");
                Log($"RING_CONFIG_DEVICE_ID={_cfgId}");
                Log($"RING_CONFIG_DEVICE_NAME={_cfgNome}");

                if (!File.Exists(_arquivo))
                {
                    Log($"RING_ERROR=arquivo não encontrado: {_arquivo}");
                    return new ResultadoToque(false, string.Empty, "arquivo_nao_encontrado", "Arquivo de toque não encontrado: " + _arquivo);
                }
                Log($"AUDIO_RING_VOLUME_SET | volume={_volume:F2}");

                var saidas = AudioDeviceService.ListarSaidasParaToque();
                foreach (var s in saidas)
                {
                    var tipo = AudioDeviceService.ClassificarSaida(s);
                    Log(tipo == AudioDeviceService.TipoSaida.Headset
                        ? $"RING_DEVICE_REJECTED_HEADSET | nome={s.Nome} forma={s.FormaFisica} barramento={s.Barramento}"
                        : $"RING_DEVICE_CANDIDATE | nome={s.Nome} tipo={tipo} forma={s.FormaFisica} barramento={s.Barramento} padrao={s.PadraoWindows}");
                }
                var (escolhido, motivo) = AudioDeviceService.EscolherSaidaToque(_cfgNome, _cfgId, saidas);
                Log(escolhido != null
                    ? $"RING_DEVICE_SELECTED | nome={escolhido.Nome} motivo={motivo}"
                    : $"RING_DEVICE_FALLBACK_DEFAULT | motivo={motivo}");
                if (motivo.StartsWith("configurado_ausente", StringComparison.Ordinal))
                    Log($"RING_DEVICE_FOUND=False | RING_FALLBACK_REASON=configurado \"{_cfgNome}\" não está conectado — usando {escolhido?.Nome ?? "padrão do Windows"}");

                _enumerator = new MMDeviceEnumerator();
                var tentativas = new List<(MMDevice dev, string motivo)>();
                if (escolhido != null)
                {
                    try { tentativas.Add((_enumerator.GetDevice(escolhido.Id), motivo)); }
                    catch (Exception ex) { Log($"RING_DEVICE_OPEN_FAIL | nome={escolhido.Nome} erro={ex.Message}"); }
                }
                try
                {
                    var padrao = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (escolhido == null || !string.Equals(padrao.ID, escolhido.Id, StringComparison.OrdinalIgnoreCase))
                        tentativas.Add((padrao, "padrao_windows"));
                }
                catch { }

                string? ultimoErro = null;
                foreach (var (dev, mot) in tentativas)
                {
                    if (g != Volatile.Read(ref _geracao) || _disposed)
                        return new ResultadoToque(false, string.Empty, "cancelado", null);
                    string nomeDev; try { nomeDev = dev.FriendlyName; } catch { nomeDev = "?"; }
                    WasapiOut? player = null;
                    try
                    {
                        _reader = new AudioFileReader(_arquivo) { Volume = _volume };
                        player = new WasapiOut(dev, AudioClientShareMode.Shared, false, 100);
                        player.Init(_reader);
                        var p = player;
                        player.PlaybackStopped += (_, e) => OnPlaybackStopped(g, p, e);
                        _player = player;
                        player.Play();

                        Log($"RING_DEVICE_FOUND=True | RING_DEVICE_USED_NAME={nomeDev}");
                        Log($"RING_DEVICE_USED_ID={dev.ID}");
                        Log("RING_PLAYER=WasapiOut");
                        if (_fonte == "TestButton") Log($"RING_TEST_DEVICE_USED | dispositivo={nomeDev} motivo={mot}");
                        if (_fonte == "IncomingCallWindow") Log($"AUDIO_RING_TEST_PLAYED | dispositivo={nomeDev} motivo={mot}");
                        EstadoSaidaToque? estado = null;
                        try { estado = EstadoSaidaToque.Ler(dev); } catch { }
                        var aviso = estado?.Aviso(_volume);
                        Log(aviso == null ? $"RING_OUTPUT_STATE | {estado?.Resumo()}" : $"RING_OUTPUT_SILENT | {estado!.Resumo()} volume_toque={_volume:F2}");
                        if (aviso != null) LogHelper.Warn($"RING_OUTPUT_SILENT | fonte={_fonte} {estado!.Resumo()} volume_toque={_volume:F2}");
                        return new ResultadoToque(true, nomeDev, mot, null, estado);
                    }
                    catch (Exception ex)
                    {
                        ultimoErro = $"{ex.GetType().Name}: {ex.Message}";
                        Log($"RING_DEVICE_OPEN_FAIL | nome={nomeDev} motivo={mot} erro={ultimoErro}");
                        if (ReferenceEquals(_player, player)) _player = null;
                        try { player?.Dispose(); } catch { }
                        try { _reader?.Dispose(); } catch { }
                        _reader = null;
                    }
                }

                Log($"RING_ERROR=nenhuma saída de áudio abriu | ultimo_erro={ultimoErro ?? "nenhum dispositivo de saída ativo"}");
                return new ResultadoToque(false, string.Empty, "sem_saida", ultimoErro ?? "Nenhuma saída de áudio ativa no Windows.");
            }
            catch (Exception ex)
            {
                Log($"RING_ERROR={ex.GetType().Name}: {ex.Message}");
                PararImpl();
                return new ResultadoToque(false, string.Empty, "erro", ex.Message);
            }
        }

        // Roda na thread de áudio do NAudio: só decide e agenda; o trabalho vai para outra thread.
        private void OnPlaybackStopped(int g, IWavePlayer origem, StoppedEventArgs e)
        {
            if (_disposed || g != Volatile.Read(ref _geracao)) return;
            bool falhou = e.Exception != null;
            if (!falhou && !_loop) return;              // terminou normalmente (teste)
            if (falhou) Log($"RING_DEVICE_LOST | erro={e.Exception!.GetType().Name}: {e.Exception.Message}");

            Task.Run(async () =>
            {
                try
                {
                    if (falhou) await Task.Delay(400).ConfigureAwait(false); // o Windows terminar a troca
                    lock (_gate)
                    {
                        if (_disposed || g != Volatile.Read(ref _geracao) || !ReferenceEquals(_player, origem)) return;
                        if (!falhou)
                        {
                            try { _reader!.Position = 0; origem.Play(); return; }
                            catch (Exception ex) { Log($"RING_LOOP_RESTART_FAIL | {ex.Message}"); }
                        }
                        if (++_recuperacoes > MaxRecuperacoes)
                        {
                            Log($"RING_RECOVERY_GIVEUP | tentativas={_recuperacoes - 1}");
                            PararImpl();
                            return;
                        }
                        var rec = _recuperacoes;
                        PararImpl();
                        var r = Iniciar(g);
                        Log($"RING_RECOVERY | tentativa={rec} tocando={r.Tocando} dispositivo={r.Dispositivo} motivo={r.Motivo}");
                    }
                }
                catch (Exception ex) { Log($"RING_RECOVERY_ERROR | {ex.Message}"); }
            });
        }

        public void Parar()
        {
            Interlocked.Increment(ref _geracao);
            lock (_gate) PararImpl();
        }

        // Chamado com _gate travado.
        private void PararImpl()
        {
            var p = _player; _player = null;
            try { p?.Stop(); } catch { }
            try { p?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            _reader = null;
            // enumerator por último (o player usa o dispositivo dele)
            try { _enumerator?.Dispose(); } catch { }
            _enumerator = null;
        }

        public void Dispose()
        {
            _disposed = true;
            Parar();
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
