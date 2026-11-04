using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace WavenVoIP.Services
{
    // v2.6.1 — identificação segura do microfone (e do áudio da ligação) e verificação de saúde.
    //
    // Por que existe: até a 2.6.0 o dispositivo era guardado só pelo NOME. Quando o headset USB
    // muda de porta o Windows o renomeia ("Microfone (Intelbras …)" → "Microfone (3- Intelbras …)"):
    // o nome salvo deixava de existir e a chamada caía em silêncio no microfone padrão do Windows
    // (às vezes o do notebook, uma webcam ou um dispositivo mudo) — o cliente deixava de ouvir o
    // operador e ninguém era avisado. Caso real: ramal 100, 14 trocas em 7 dias (04/11/2026).
    //
    // Agora:
    //  • o dispositivo é guardado pelo ID do Windows + nome; a busca tenta ID, nome exato e nome
    //    sem a numeração "N- " que o Windows acrescenta (mesmo headset em outra porta);
    //  • o índice usado pela chamada (WaveIn/WaveOut) vem do ID exato do dispositivo
    //    (DRV_QUERYFUNCTIONINSTANCEID), não de comparação de nome truncado em 31 caracteres;
    //  • quando o configurado não existe, a chamada ainda funciona com o padrão do Windows, mas
    //    isso é INFORMADO (Situacao=Ausente) para a interface avisar o operador;
    //  • a saúde do microfone em uso é verificada: desconectado, desativado, mudo no Windows,
    //    bloqueado pela privacidade, volume baixo e microfone diferente do fone da ligação.
    public enum SituacaoDispositivo { Ok, Automatico, Renomeado, Ausente, SemDispositivo }

    public sealed record EndpointAudio(string Id, string Nome, bool Ativo, bool PadraoConsole, bool PadraoComunicacao, string Estado);

    public sealed record ResolucaoAudio(SituacaoDispositivo Situacao, string NomeConfigurado, EndpointAudio? Escolhido, int IndiceWave, string Detalhe)
    {
        public string NomeEmUso => Escolhido?.Nome ?? "padrão do Windows";
        public bool UsandoOutroDispositivo => Situacao == SituacaoDispositivo.Ausente || Situacao == SituacaoDispositivo.SemDispositivo;
        public string Resumo() => $"situacao={Situacao} configurado=\"{NomeConfigurado}\" em_uso=\"{NomeEmUso}\" idx={IndiceWave}" +
                                  (string.IsNullOrEmpty(Detalhe) ? "" : $" detalhe={Detalhe}");
    }

    // Aviso para o operador. Grave = o cliente certamente não está ouvindo (vermelho);
    // senão = possível (amarelo). OferecerTroca = mostrar "Usar microfone do computador".
    public sealed record AlertaMicrofone(bool Grave, string Titulo, string Texto, bool OferecerTroca, string Codigo);

    public enum ProblemaMicrofone { SemMicrofone, Desconectado, Desativado, Mudo, Bloqueado, VolumeBaixo, DiferenteDoFone }

    public sealed record SaudeMicrofone(ResolucaoAudio Resolucao, bool? MudoWindows, float? NivelWindows, bool BloqueadoPrivacidade,
                                        string? NomeFoneLigacao, IReadOnlyList<ProblemaMicrofone> Problemas)
    {
        public bool Ok => Problemas.Count == 0;
        public bool Grave => Problemas.Any(p => p is ProblemaMicrofone.SemMicrofone or ProblemaMicrofone.Desconectado
                                                  or ProblemaMicrofone.Desativado or ProblemaMicrofone.Mudo or ProblemaMicrofone.Bloqueado);

        public string Resumo() =>
            $"problemas={(Problemas.Count == 0 ? "nenhum" : string.Join(",", Problemas))} {Resolucao.Resumo()} " +
            $"mudo_windows={(MudoWindows?.ToString() ?? "?")} nivel_windows={(NivelWindows.HasValue ? (NivelWindows.Value * 100).ToString("F0") + "%" : "?")} " +
            $"privacidade_bloqueada={BloqueadoPrivacidade}";

        // Texto curto e direto para o operador (sem jargão), na ordem de gravidade.
        public string TextoParaOperador()
        {
            var cfg = Resolucao.NomeConfigurado;
            var uso = Resolucao.NomeEmUso;
            var partes = new List<string>();
            foreach (var p in Problemas)
            {
                switch (p)
                {
                    case ProblemaMicrofone.SemMicrofone:
                        partes.Add("Nenhum microfone ativo foi encontrado no computador. Conecte o headset."); break;
                    case ProblemaMicrofone.Desconectado:
                        partes.Add($"O microfone \"{cfg}\" não foi encontrado (desconectado)." +
                                   (Resolucao.Escolhido != null ? $" A ligação vai usar \"{uso}\"." : "")); break;
                    case ProblemaMicrofone.Desativado:
                        partes.Add($"O microfone \"{cfg}\" está desativado no Windows." +
                                   (Resolucao.Escolhido != null ? $" A ligação vai usar \"{uso}\"." : "")); break;
                    case ProblemaMicrofone.Mudo:
                        partes.Add($"O microfone \"{uso}\" está no MUDO do Windows."); break;
                    case ProblemaMicrofone.Bloqueado:
                        partes.Add("O Windows está bloqueando o acesso ao microfone (Privacidade > Microfone)."); break;
                    case ProblemaMicrofone.VolumeBaixo:
                        partes.Add($"O volume do microfone \"{uso}\" está muito baixo no Windows ({(NivelWindows ?? 0) * 100:F0}%)."); break;
                    case ProblemaMicrofone.DiferenteDoFone:
                        partes.Add($"O microfone em uso (\"{uso}\") não é o do fone da ligação (\"{NomeFoneLigacao}\")."); break;
                }
            }
            return string.Join(" ", partes);
        }
    }

    public static class MicrofoneService
    {
        public const float NivelMinimoWindows = 0.15f;

        // "Microfone (3- Intelbras CHS 55B V2N)" → "microfone (intelbras chs 55b v2n)"
        private static readonly Regex _numeracaoWindows = new(@"\(\s*\d+\s*-\s*", RegexOptions.Compiled);
        private static readonly Regex _espacos = new(@"\s+", RegexOptions.Compiled);

        public static string NormalizarNome(string? nome)
        {
            if (string.IsNullOrWhiteSpace(nome)) return string.Empty;
            var n = _numeracaoWindows.Replace(nome.Trim(), "(");
            return _espacos.Replace(n, " ").Trim().ToLowerInvariant();
        }

        // Parte entre parênteses = adaptador físico ("intelbras chs 55b v2n", "realtek(r) audio").
        public static string NomeAdaptador(string? nome)
        {
            var n = NormalizarNome(nome);
            var i = n.IndexOf('(');
            if (i < 0) return n;
            var r = n.Substring(i + 1).Trim();
            if (r.EndsWith(")")) r = r.Substring(0, r.Length - 1);
            return r.Trim();
        }

        // ── Escolha pura (testável): qual endpoint usar para a configuração salva ───────────────
        public static (SituacaoDispositivo situacao, EndpointAudio? escolhido, string detalhe) Escolher(
            string? nomeConfigurado, string? idConfigurado, IReadOnlyList<EndpointAudio> endpoints)
        {
            var ativos = endpoints.Where(e => e.Ativo).ToList();
            EndpointAudio? Padrao() => ativos.FirstOrDefault(e => e.PadraoConsole)
                                      ?? ativos.FirstOrDefault(e => e.PadraoComunicacao)
                                      ?? ativos.FirstOrDefault();
            EndpointAudio? Preferido(List<EndpointAudio> c) => c.FirstOrDefault(e => e.PadraoComunicacao)
                                                             ?? c.FirstOrDefault(e => e.PadraoConsole)
                                                             ?? c.First();

            var nome = (nomeConfigurado ?? string.Empty).Trim();
            var id = (idConfigurado ?? string.Empty).Trim();

            if (nome.Length == 0 && id.Length == 0)
            {
                var p = Padrao();
                return p == null ? (SituacaoDispositivo.SemDispositivo, null, "nenhum_dispositivo_ativo")
                                 : (SituacaoDispositivo.Automatico, p, string.Empty);
            }

            // 1) ID exato (mesmo dispositivo, mesma porta)
            if (id.Length > 0)
            {
                var porId = ativos.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
                if (porId != null)
                {
                    var renomeado = nome.Length > 0 && !string.Equals(porId.Nome, nome, StringComparison.OrdinalIgnoreCase);
                    return (renomeado ? SituacaoDispositivo.Renomeado : SituacaoDispositivo.Ok, porId, renomeado ? "mesmo_id_nome_novo" : "id");
                }
            }

            // 2) nome exato
            if (nome.Length > 0)
            {
                var exatos = ativos.Where(e => string.Equals(e.Nome, nome, StringComparison.OrdinalIgnoreCase)).ToList();
                if (exatos.Count > 0)
                    return (SituacaoDispositivo.Ok, Preferido(exatos), exatos.Count > 1 ? $"nome_exato_ambiguo={exatos.Count}" : "nome_exato");

                // 3) mesmo dispositivo com outra numeração do Windows (troca de porta USB)
                var alvo = NormalizarNome(nome);
                var mesmos = ativos.Where(e => NormalizarNome(e.Nome) == alvo).ToList();
                if (mesmos.Count > 0)
                    return (SituacaoDispositivo.Renomeado, Preferido(mesmos), mesmos.Count > 1 ? $"renomeado_ambiguo={mesmos.Count}" : "renomeado_porta_usb");
            }

            // 4) não existe ativo: a chamada usa o padrão, mas isso é um problema a avisar
            var alvoN = NormalizarNome(nome);
            var inativo = endpoints.FirstOrDefault(e => !e.Ativo &&
                ((id.Length > 0 && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) ||
                 (alvoN.Length > 0 && NormalizarNome(e.Nome) == alvoN)));
            var fallback = Padrao();
            var det = inativo != null ? $"estado_windows={inativo.Estado}" : "nao_existe_no_windows";
            return (fallback == null ? SituacaoDispositivo.SemDispositivo : SituacaoDispositivo.Ausente, fallback, det);
        }

        // ── Windows: endpoints, índices WaveIn/WaveOut e privacidade ────────────────────────────

        public static List<EndpointAudio> ListarEndpoints(bool saida)
        {
            var lista = new List<EndpointAudio>();
            using var en = new MMDeviceEnumerator();
            var fluxo = saida ? DataFlow.Render : DataFlow.Capture;
            string idConsole = string.Empty, idComunic = string.Empty;
            try { idConsole = en.GetDefaultAudioEndpoint(fluxo, Role.Console).ID; } catch { }
            try { idComunic = en.GetDefaultAudioEndpoint(fluxo, Role.Communications).ID; } catch { }
            foreach (var d in en.EnumerateAudioEndPoints(fluxo, DeviceState.All))
            {
                try
                {
                    string nome;
                    try { nome = d.FriendlyName; } catch { nome = "?"; }
                    var estado = d.State;
                    lista.Add(new EndpointAudio(d.ID, nome, estado == DeviceState.Active,
                        d.ID == idConsole, d.ID == idComunic, estado.ToString()));
                }
                catch { }
            }
            return lista;
        }

        [DllImport("winmm.dll")] private static extern int waveInMessage(IntPtr h, uint msg, IntPtr p1, IntPtr p2);
        [DllImport("winmm.dll")] private static extern int waveOutMessage(IntPtr h, uint msg, IntPtr p1, IntPtr p2);
        private const uint DRV_QUERYFUNCTIONINSTANCEID = 0x0811, DRV_QUERYFUNCTIONINSTANCEIDSIZE = 0x0812;

        private static string? IdDoIndiceWave(int indice, bool saida)
        {
            var tam = Marshal.AllocHGlobal(4);
            try
            {
                int r = saida ? waveOutMessage((IntPtr)indice, DRV_QUERYFUNCTIONINSTANCEIDSIZE, tam, IntPtr.Zero)
                              : waveInMessage((IntPtr)indice, DRV_QUERYFUNCTIONINSTANCEIDSIZE, tam, IntPtr.Zero);
                if (r != 0) return null;
                int n = Marshal.ReadInt32(tam);
                if (n <= 0 || n > 4096) return null;
                var buf = Marshal.AllocHGlobal(n);
                try
                {
                    r = saida ? waveOutMessage((IntPtr)indice, DRV_QUERYFUNCTIONINSTANCEID, buf, (IntPtr)n)
                              : waveInMessage((IntPtr)indice, DRV_QUERYFUNCTIONINSTANCEID, buf, (IntPtr)n);
                    return r == 0 ? Marshal.PtrToStringUni(buf) : null;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return null; }
            finally { Marshal.FreeHGlobal(tam); }
        }

        // Índice WaveIn/WaveOut do endpoint: pelo ID exato; se o driver não informar, pelo nome
        // truncado (31 caracteres), só quando houver um único candidato.
        public static int IndiceWave(EndpointAudio? e, bool saida)
        {
            if (e == null) return -1;
            try
            {
                int total = saida ? WaveOut.DeviceCount : WaveIn.DeviceCount;
                var porNome = new List<int>();
                for (int i = 0; i < total; i++)
                {
                    var id = IdDoIndiceWave(i, saida);
                    if (id != null && string.Equals(id, e.Id, StringComparison.OrdinalIgnoreCase)) return i;
                    string prod;
                    try { prod = saida ? WaveOut.GetCapabilities(i).ProductName : WaveIn.GetCapabilities(i).ProductName; }
                    catch { continue; }
                    if (!string.IsNullOrEmpty(prod) && e.Nome.StartsWith(prod.TrimEnd(), StringComparison.OrdinalIgnoreCase)) porNome.Add(i);
                }
                return porNome.Count == 1 ? porNome[0] : -1;
            }
            catch { return -1; }
        }

        public static bool PrivacidadeBloqueada()
        {
            static string? Ler(RegistryKey raiz, string caminho)
            {
                try { using var k = raiz.OpenSubKey(caminho); return k?.GetValue("Value") as string; }
                catch { return null; }
            }
            const string baseKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            return string.Equals(Ler(Registry.LocalMachine, baseKey), "Deny", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Ler(Registry.CurrentUser, baseKey), "Deny", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Ler(Registry.CurrentUser, baseKey + @"\NonPackaged"), "Deny", StringComparison.OrdinalIgnoreCase);
        }

        // ── Resolução completa (usa o Windows) ─────────────────────────────────────────────────

        public static ResolucaoAudio Resolver(string? nomeConfigurado, string? idConfigurado, bool saida)
        {
            try
            {
                var endpoints = ListarEndpoints(saida);
                var (sit, esc, det) = Escolher(nomeConfigurado, idConfigurado, endpoints);
                // Automático: mantém o comportamento da 2.6.0 (mapeador do Windows = -1), mas
                // informa qual dispositivo o Windows vai usar.
                var idx = sit == SituacaoDispositivo.Automatico ? -1 : IndiceWave(esc, saida);
                if (sit != SituacaoDispositivo.Automatico && esc != null && idx < 0) det += " indice_nao_encontrado";
                return new ResolucaoAudio(sit, nomeConfigurado ?? string.Empty, esc, idx, det.Trim());
            }
            catch (Exception ex)
            {
                return new ResolucaoAudio(SituacaoDispositivo.SemDispositivo, nomeConfigurado ?? string.Empty, null, -1, "erro " + ex.GetType().Name);
            }
        }

        public static SaudeMicrofone VerificarMicrofone(string? nomeMic, string? idMic, string? nomeFone, string? idFone)
        {
            var res = Resolver(nomeMic, idMic, saida: false);
            var problemas = new List<ProblemaMicrofone>();
            bool? mudo = null; float? nivel = null;

            if (res.Situacao == SituacaoDispositivo.SemDispositivo) problemas.Add(ProblemaMicrofone.SemMicrofone);
            else if (res.Situacao == SituacaoDispositivo.Ausente)
                problemas.Add(res.Detalhe.Contains("Disabled", StringComparison.OrdinalIgnoreCase)
                    ? ProblemaMicrofone.Desativado : ProblemaMicrofone.Desconectado);

            if (res.Escolhido != null)
            {
                try
                {
                    using var en = new MMDeviceEnumerator();
                    var d = en.GetDevice(res.Escolhido.Id);
                    var vol = d.AudioEndpointVolume;
                    mudo = vol.Mute;
                    nivel = vol.MasterVolumeLevelScalar;
                }
                catch { }
                if (mudo == true) problemas.Add(ProblemaMicrofone.Mudo);
                if (nivel.HasValue && nivel.Value < NivelMinimoWindows) problemas.Add(ProblemaMicrofone.VolumeBaixo);
            }

            var bloqueado = PrivacidadeBloqueada();
            if (bloqueado) problemas.Add(ProblemaMicrofone.Bloqueado);

            // Microfone errado: a ligação sai num headset, mas o microfone é de outro aparelho.
            string? nomeFoneUso = null;
            try
            {
                var fone = Resolver(nomeFone, idFone, saida: true);
                nomeFoneUso = fone.Escolhido?.Nome;
                if (res.Escolhido != null && fone.Escolhido != null && AudioDeviceService.EhHeadset(fone.Escolhido.Nome) &&
                    NomeAdaptador(fone.Escolhido.Nome) != NomeAdaptador(res.Escolhido.Nome))
                    problemas.Add(ProblemaMicrofone.DiferenteDoFone);
            }
            catch { }

            return new SaudeMicrofone(res, mudo, nivel, bloqueado, nomeFoneUso, problemas);
        }

        // Toda consulta ao áudio do Windows passa por aqui: nunca trava quem chamou (driver USB
        // instável já travou a interface na 2.4.0 — ver SipService.CriarMediaAsync).
        public static async Task<T?> ComLimiteDeTempo<T>(Func<T?> consulta, int ms = 3000) where T : class
        {
            var t = Task.Run(consulta);
            var venceu = await Task.WhenAny(t, Task.Delay(ms)).ConfigureAwait(false);
            if (venceu != t || !t.IsCompletedSuccessfully) { _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default); return null; }
            return t.Result;
        }

        // ── Avisos de conexão/desconexão de dispositivos (headset entrou/saiu, mudou o padrão) ──

        public static event Action? DispositivosMudaram;
        private static MMDeviceEnumerator? _enumNotif;
        private static NotificadorDispositivos? _notif;
        private static System.Threading.Timer? _debounce;
        private static readonly object _notifLock = new();

        public static void IniciarMonitoramento()
        {
            lock (_notifLock)
            {
                if (_notif != null) return;
                try
                {
                    _enumNotif = new MMDeviceEnumerator();
                    _notif = new NotificadorDispositivos();
                    _enumNotif.RegisterEndpointNotificationCallback(_notif);
                }
                catch { _notif = null; _enumNotif = null; }
            }
        }

        public static void PararMonitoramento()
        {
            lock (_notifLock)
            {
                try { if (_enumNotif != null && _notif != null) _enumNotif.UnregisterEndpointNotificationCallback(_notif); } catch { }
                try { _enumNotif?.Dispose(); } catch { }
                _enumNotif = null; _notif = null;
                _debounce?.Dispose(); _debounce = null;
            }
        }

        // Um único aviso após 1,5 s de calmaria (o Windows dispara vários eventos por troca).
        private static void Agendar()
        {
            lock (_notifLock)
            {
                _debounce?.Dispose();
                _debounce = new System.Threading.Timer(_ => { try { DispositivosMudaram?.Invoke(); } catch { } }, null, 1500, System.Threading.Timeout.Infinite);
            }
        }

        private sealed class NotificadorDispositivos : IMMNotificationClient
        {
            public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Agendar();
            public void OnDeviceAdded(string pwstrDeviceId) => Agendar();
            public void OnDeviceRemoved(string deviceId) => Agendar();
            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Agendar();
            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        }
    }
}
