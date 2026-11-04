// Testes da v2.6.1 — microfone sem falha silenciosa.
// Uso:
//   MicTests                      testes puros (identificação, detector, proxy de áudio) — sem dispositivo
//   MicTests --dispositivos       usa o áudio REAL deste PC (só leitura; abre o microfone por ~2 s, sem gravar)
//   MicTests --mudo <nome-mic>    liga o mudo/volume baixo do microfone indicado por instantes e RESTAURA
//   MicTests --telas <pasta>      gera PNGs da tela da chamada com os avisos (nada é exibido)
using System.IO;
using System.Reflection;
using SIPSorcery.Media;
using NAudio.Codecs;
using NAudio.CoreAudioApi;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;
using WavenVoIP.Services;

int total = 0, falhas = 0;
void Check(string nome, bool ok, string detalhe = "")
{
    total++; if (!ok) falhas++;
    Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {nome} {detalhe}");
}
int Fim() { Console.WriteLine($"\n{total - falhas}/{total} OK, {falhas} falha(s)"); return falhas == 0 ? 0 : 1; }

// ════════════════════════════════════════════════════════════════════════════════════════
if (args.Length >= 1 && args[0] == "--dispositivos") return Dispositivos();
if (args.Length >= 2 && args[0] == "--mudo") return MudoTeste(args[1]);
if (args.Length >= 2 && args[0] == "--telas") return Telas(args[1]);

// ── 1. Nome do Windows: mesmo aparelho em outra porta USB ──────────────────────────────
Console.WriteLine("== Identificação do dispositivo (puro)");
Check("normaliza numeração 3-", MicrofoneService.NormalizarNome("Microfone (3- Intelbras CHS 55B V2N)") == MicrofoneService.NormalizarNome("Microfone (Intelbras CHS 55B V2N)"));
Check("normaliza numeração 12 -", MicrofoneService.NormalizarNome("Microfone (12 - USB Audio Device)") == "microfone (usb audio device)");
Check("não confunde aparelhos diferentes", MicrofoneService.NormalizarNome("Microfone (USB Audio Device 2)") != MicrofoneService.NormalizarNome("Microfone (USB Audio Device)"));
Check("não remove números do modelo", MicrofoneService.NormalizarNome("Microfone (Intelbras CHS 55B V2N)").Contains("55b"));
Check("adaptador com parênteses internos", MicrofoneService.NomeAdaptador("Grupo de microfones (Realtek(R) Audio)") == "realtek(r) audio", MicrofoneService.NomeAdaptador("Grupo de microfones (Realtek(R) Audio)"));
Check("adaptador do fone = do microfone (headset)", MicrofoneService.NomeAdaptador("Alto-falantes (3- Intelbras CHS 55B V2N)") == MicrofoneService.NomeAdaptador("Microfone (Intelbras CHS 55B V2N)"));

EndpointAudio Ep(string id, string nome, bool ativo = true, bool console = false, bool comunic = false, string estado = "Active")
    => new(id, nome, ativo, console, comunic, ativo ? "Active" : estado);
var notebook = Ep("{0.0.1}.{nb}", "Grupo de microfones (Realtek(R) Audio)", console: true);
var headsetPorta3 = Ep("{0.0.1}.{h3}", "Microfone (3- Intelbras CHS 55B V2N)", comunic: true);
var headsetAntigo = Ep("{0.0.1}.{h0}", "Microfone (Intelbras CHS 55B V2N)", ativo: false, estado: "NotPresent");
var lista = new List<EndpointAudio> { headsetAntigo, notebook, headsetPorta3 };

var r = MicrofoneService.Escolher("Microfone (Intelbras CHS 55B V2N)", "", lista);
Check("troca de porta USB: reencontra o MESMO headset (não o notebook)", r.situacao == SituacaoDispositivo.Renomeado && r.escolhido?.Id == "{0.0.1}.{h3}", $"{r.situacao} {r.escolhido?.Nome}");
r = MicrofoneService.Escolher("Microfone (Intelbras CHS 55B V2N)", "{0.0.1}.{h3}", lista);
Check("ID exato tem prioridade", r.escolhido?.Id == "{0.0.1}.{h3}" && r.situacao == SituacaoDispositivo.Renomeado, $"{r.situacao} {r.detalhe}");
r = MicrofoneService.Escolher("Microfone (3- Intelbras CHS 55B V2N)", "{0.0.1}.{h3}", lista);
Check("nome e ID atuais = Ok", r.situacao == SituacaoDispositivo.Ok && r.escolhido?.Id == "{0.0.1}.{h3}");
r = MicrofoneService.Escolher("Grupo de microfones (Realtek(R) Audio)", "", lista);
Check("nome exato = Ok", r.situacao == SituacaoDispositivo.Ok && r.escolhido == notebook);

var semHeadset = new List<EndpointAudio> { headsetAntigo, notebook };
r = MicrofoneService.Escolher("Microfone (Intelbras CHS 55B V2N)", "{0.0.1}.{h0}", semHeadset);
Check("headset desconectado: Ausente (NÃO silencioso), usa o padrão", r.situacao == SituacaoDispositivo.Ausente && r.escolhido == notebook && r.detalhe.Contains("NotPresent"), $"{r.situacao} {r.detalhe}");
var desativado = new List<EndpointAudio> { Ep("{d}", "Microfone (Intelbras CHS 55B V2N)", ativo: false, estado: "Disabled"), notebook };
r = MicrofoneService.Escolher("Microfone (Intelbras CHS 55B V2N)", "", desativado);
Check("headset desativado no Windows: Ausente com estado Disabled", r.situacao == SituacaoDispositivo.Ausente && r.detalhe.Contains("Disabled"), r.detalhe);
r = MicrofoneService.Escolher("Microfone (Headset Que Nunca Existiu)", "", lista);
Check("nome inexistente: Ausente", r.situacao == SituacaoDispositivo.Ausente && r.detalhe == "nao_existe_no_windows");
r = MicrofoneService.Escolher("Microfone (Intelbras CHS 55B V2N)", "", new List<EndpointAudio> { headsetAntigo });
Check("nenhum microfone ativo: SemDispositivo", r.situacao == SituacaoDispositivo.SemDispositivo && r.escolhido == null);
r = MicrofoneService.Escolher("", "", lista);
Check("Automático: padrão do Windows (console)", r.situacao == SituacaoDispositivo.Automatico && r.escolhido == notebook);
var dois = new List<EndpointAudio> { Ep("{a}", "Microfone (2- USB Headset)"), Ep("{b}", "Microfone (3- USB Headset)", comunic: true) };
r = MicrofoneService.Escolher("Microfone (USB Headset)", "", dois);
Check("dois headsets iguais: escolhe o de comunicação e marca ambíguo", r.escolhido?.Id == "{b}" && r.detalhe.Contains("ambiguo"), r.detalhe);
r = MicrofoneService.Escolher("Microfone (USB Headset)", "{a}", dois);
Check("dois headsets iguais com ID salvo: escolhe o do ID", r.escolhido?.Id == "{a}");

// ── 2. Detector de voz ──────────────────────────────────────────────────────────────────
Console.WriteLine("\n== Detector de voz (puro)");
SuspeitaMicrofone Rodar(DetectorVozMicrofone d, double segundos, double db, bool zero = false, bool pausado = false, double far = -120)
{
    var s = SuspeitaMicrofone.Nenhuma;
    for (double t = 0; t < segundos - 1e-9; t += 0.02) s = d.Processar(db, zero, 0.02, pausado, far);
    return s;
}
var d1 = new DetectorVozMicrofone(); d1.Iniciar();
Rodar(d1, 1.0, -70); var s1 = Rodar(d1, 1.0, -25);
Check("fala no 2º segundo: sem alerta", s1 == SuspeitaMicrofone.Nenhuma && d1.HouveVoz);
Check("silêncio normal depois de falar (30 s): sem alerta", Rodar(d1, 30, -68) == SuspeitaMicrofone.Nenhuma);

var d2 = new DetectorVozMicrofone(); d2.Iniciar();
Check("zeros por 3,9 s: ainda sem alerta", Rodar(d2, 3.9, -120, zero: true) == SuspeitaMicrofone.Nenhuma);
Check("zeros por 4 s desde o atendimento: SemSinalDesdeInicio", Rodar(d2, 0.2, -120, zero: true) == SuspeitaMicrofone.SemSinalDesdeInicio);
Check("sinal/voz volta: alerta some", Rodar(d2, 0.5, -24) == SuspeitaMicrofone.Nenhuma);

var d3 = new DetectorVozMicrofone(); d3.Iniciar();
Check("só ruído baixo (-68) por 7,9 s: sem alerta", Rodar(d3, 7.9, -68) == SuspeitaMicrofone.Nenhuma);
Check("8 s sem voz desde o atendimento: SemVozDesdeAtendimento", Rodar(d3, 0.2, -68) == SuspeitaMicrofone.SemVozDesdeAtendimento);
Check("microfone longe (fala a -55) continua sem voz", Rodar(d3, 3, -55) == SuspeitaMicrofone.SemVozDesdeAtendimento);

var d4 = new DetectorVozMicrofone(); d4.Iniciar();
Check("mudo intencional por 20 s: nunca alerta", Rodar(d4, 20, -120, zero: true, pausado: true) == SuspeitaMicrofone.Nenhuma);
Check("ao tirar o mudo, carência recomeça (zeros 3 s = sem alerta)", Rodar(d4, 3, -120, zero: true) == SuspeitaMicrofone.Nenhuma);
Check("…e zeros por mais 1 s = SemSinal", Rodar(d4, 1.1, -120, zero: true) == SuspeitaMicrofone.SemSinalDesdeInicio);
Check("espera (mesma regra do mudo): alerta some", Rodar(d4, 5, -120, zero: true, pausado: true) == SuspeitaMicrofone.Nenhuma);
Check("tempo em mudo/espera contabilizado", d4.SegundosPausado >= 24.9, $"{d4.SegundosPausado:F1}s");

var d5 = new DetectorVozMicrofone(); d5.Iniciar();
Rodar(d5, 2, -22);
Check("voz e depois zeros 4,9 s: sem alerta", Rodar(d5, 4.9, -120, zero: true) == SuspeitaMicrofone.Nenhuma);
Check("voz e depois zeros 5 s: SinalPerdido", Rodar(d5, 0.2, -120, zero: true) == SuspeitaMicrofone.SinalPerdido);

var d6 = new DetectorVozMicrofone(); d6.Iniciar();
Rodar(d6, 3, -70, far: -20); Rodar(d6, 2, -24);
Check("estatística: cliente falando e voz do operador", d6.SegundosClienteFalando >= 2.9 && d6.SegundosComVoz >= 1.9, d6.Resumo());
Check("medição não guarda áudio: só números no resumo", !d6.Resumo().Contains("pcm"));

var (dbZ, z) = DetectorVozMicrofone.Medir(new short[160]);
Check("Medir: bloco zerado = zero absoluto", z && dbZ <= -119);
var tom = Enumerable.Range(0, 160).Select(i => (short)(3277 * Math.Sin(2 * Math.PI * 440 * i / 8000))).ToArray();
var (dbT, zt) = DetectorVozMicrofone.Medir(tom);
Check("Medir: tom de -20 dBFS pico ≈ -23 dBFS RMS", !zt && Math.Abs(dbT - (-23.0)) < 0.6, $"{dbT:F1}");

short[] Ruido(double dbfs) { var rnd = new Random(7); var a = 32768 * Math.Pow(10, dbfs / 20) * Math.Sqrt(3); return Enumerable.Range(0, 160).Select(_ => (short)Math.Round((rnd.NextDouble() * 2 - 1) * a)).ToArray(); }
var (dbMudo, zMudo) = DetectorVozMicrofone.Medir(Ruido(-98));
Check("Medir: resíduo do mudo do Windows (≈-98 dBFS) = sem sinal", zMudo, $"{dbMudo:F1}");
var (dbSala, zSala) = DetectorVozMicrofone.Medir(Ruido(-75));
Check("Medir: microfone ligado em sala silenciosa (≈-75 dBFS) = tem sinal", !zSala, $"{dbSala:F1}");

// ── 3. Proxy de áudio (o que vai para o cliente) ─────────────────────────────────────────
Console.WriteLine("\n== Proxy de áudio (sem dispositivo)");
var ep = new WindowsAudioEndPoint(new AudioEncoder(), -1, -1, true, true);   // sem abrir microfone/alto-falante
var relay = typeof(MuteableAudioSource).GetMethod("RelayEncodedSample", BindingFlags.NonPublic | BindingFlags.Instance)!;
byte[] Ulaw(short[] pcm) => pcm.Select(NAudio.Codecs.MuLawEncoder.LinearToMuLawSample).ToArray();
var blocoVoz = Ulaw(tom);
var blocoZero = Enumerable.Repeat((byte)0xFF, 160).ToArray();

var pCru = new MuteableAudioSource(ep, nivelarMicrofone: false);
pCru.SetAudioSourceFormat(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU));
byte[]? saiu = null;
pCru.OnAudioSourceEncodedSample += (_, b) => saiu = b;
pCru.Detector.Iniciar();
relay.Invoke(pCru, new object[] { 160u, blocoVoz });
Check("sem nivelamento: áudio do cliente sai IDÊNTICO (qualidade preservada)", saiu != null && saiu.SequenceEqual(blocoVoz));
Check("detector recebeu o nível do bloco", pCru.Detector.UltimoNivelDb > -30, $"{pCru.Detector.UltimoNivelDb:F1}");
pCru.SetMute(true);
relay.Invoke(pCru, new object[] { 160u, blocoVoz });
Check("mudo: sai silêncio PCMU 0xFF (como na 2.6.0)", saiu!.All(b => b == 0xFF));
Check("mudo: detector pausado (nível -120)", pCru.Detector.UltimoNivelDb <= -119);
pCru.SetMute(false);

var pSusp = new MuteableAudioSource(ep, nivelarMicrofone: false);
pSusp.SetAudioSourceFormat(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU));
pSusp.OnAudioSourceEncodedSample += (_, _) => { };
var suspeitas = new System.Collections.Concurrent.ConcurrentQueue<SuspeitaMicrofone>();
pSusp.SuspeitaMudou += s => suspeitas.Enqueue(s);
pSusp.Detector.Iniciar();
for (int i = 0; i < 210; i++) relay.Invoke(pSusp, new object[] { 160u, Enumerable.Repeat((byte)0xFF, 160).ToArray() });
Thread.Sleep(200);
Check("proxy: 4,2 s de zeros dispara SemSinalDesdeInicio (fora da thread de áudio)", suspeitas.Contains(SuspeitaMicrofone.SemSinalDesdeInicio));
for (int i = 0; i < 5; i++) relay.Invoke(pSusp, new object[] { 160u, Ulaw(tom) });
Thread.Sleep(200);
Check("proxy: voz volta → Nenhuma notificada", suspeitas.LastOrDefault() == SuspeitaMicrofone.Nenhuma);

var pGanho = new MuteableAudioSource(ep, nivelarMicrofone: true);
pGanho.SetAudioSourceFormat(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU));
byte[]? saiuG = null; pGanho.OnAudioSourceEncodedSample += (_, b) => saiuG = b;
var sw = System.Diagnostics.Stopwatch.StartNew();
for (int i = 0; i < 500; i++) relay.Invoke(pGanho, new object[] { 160u, Ulaw(tom) });
sw.Stop();
Check("custo por bloco de 20 ms (medição + nivelamento) < 0,2 ms", sw.Elapsed.TotalMilliseconds / 500 < 0.2, $"{sw.Elapsed.TotalMilliseconds / 500:F3} ms");
Check("com nivelamento: bloco válido do mesmo tamanho", saiuG != null && saiuG.Length == 160);

return Fim();

// ════════════════════════════════════════════════════════════════════════════════════════
int Dispositivos()
{
    Console.WriteLine("== Dispositivos REAIS deste PC (somente leitura)");
    var mics = MicrofoneService.ListarEndpoints(saida: false);
    foreach (var m in mics) Console.WriteLine($"   {(m.Ativo ? "ATIVO " : m.Estado.PadRight(6))} {m.Nome}{(m.PadraoConsole ? " [padrão]" : "")}{(m.PadraoComunicacao ? " [comunicação]" : "")}");
    var ativos = mics.Where(m => m.Ativo).ToList();
    Check("há microfone ativo", ativos.Count > 0);
    foreach (var a in ativos)
    {
        var idx = MicrofoneService.IndiceWave(a, saida: false);
        var prod = idx >= 0 ? NAudio.Wave.WaveIn.GetCapabilities(idx).ProductName : "-";
        Check($"índice WaveIn exato para \"{a.Nome}\"", idx >= 0 && a.Nome.StartsWith(prod.TrimEnd(), StringComparison.OrdinalIgnoreCase), $"idx={idx} waveIn=\"{prod}\"");
    }

    var cfg = WavenVoIP.SipConfig.CarregarSalva();
    Console.WriteLine($"   configurado: microfone=\"{cfg?.AudioInputDevice}\" id=\"{cfg?.AudioInputDeviceId}\" fone=\"{cfg?.CallOutputDevice}\"");
    var res = MicrofoneService.Resolver(cfg?.AudioInputDevice, cfg?.AudioInputDeviceId, saida: false);
    Console.WriteLine($"   resolução do configurado: {res.Resumo()}");
    Check("configurado deste PC é encontrado (Ok/Renomeado/Automático)", res.Situacao is SituacaoDispositivo.Ok or SituacaoDispositivo.Renomeado or SituacaoDispositivo.Automatico, res.Situacao.ToString());
    var antigo = AudioDeviceService_Antigo(cfg?.AudioInputDevice ?? "");
    Console.WriteLine($"   2.6.0 (só nome) teria usado idx={antigo} → {(antigo < 0 ? "padrão do Windows (troca silenciosa)" : "ok")}");

    var bog = MicrofoneService.Resolver("Microfone (Headset Inexistente XYZ)", "", saida: false);
    Check("microfone inexistente → Ausente (aviso) com o padrão em uso", bog.Situacao == SituacaoDispositivo.Ausente && bog.Escolhido != null, bog.Resumo());
    var saude = MicrofoneService.VerificarMicrofone("Microfone (Headset Inexistente XYZ)", "", cfg?.CallOutputDevice, cfg?.CallOutputDeviceId);
    Check("saúde: inexistente gera problema Desconectado + texto para o operador", saude.Problemas.Contains(ProblemaMicrofone.Desconectado) && saude.TextoParaOperador().Contains("não foi encontrado"), saude.TextoParaOperador());

    var foneHeadset = MicrofoneService.ListarEndpoints(saida: true).FirstOrDefault(o => o.Ativo && AudioDeviceService.EhHeadset(o.Nome));
    var adaptadorFone = MicrofoneService.NomeAdaptador(foneHeadset?.Nome);
    var headsetMic = ativos.FirstOrDefault(a => MicrofoneService.NomeAdaptador(a.Nome) == adaptadorFone);
    var outro = ativos.FirstOrDefault(a => MicrofoneService.NomeAdaptador(a.Nome) != adaptadorFone);
    if (headsetMic != null && outro != null && foneHeadset != null)
    {
        var errado = MicrofoneService.VerificarMicrofone(outro.Nome, outro.Id, foneHeadset.Nome, foneHeadset.Id);
        Check("microfone errado: fone no headset e microfone do notebook → DiferenteDoFone", errado.Problemas.Contains(ProblemaMicrofone.DiferenteDoFone), errado.TextoParaOperador());
        var certo = MicrofoneService.VerificarMicrofone(headsetMic.Nome, headsetMic.Id, foneHeadset.Nome, foneHeadset.Id);
        Check("microfone certo (headset + fone do headset) → sem DiferenteDoFone", !certo.Problemas.Contains(ProblemaMicrofone.DiferenteDoFone), certo.Resumo());
        Console.WriteLine($"   saúde do headset: {certo.Resumo()}");
    }
    else Console.WriteLine("   (sem headset + outro microfone ativos: teste de microfone errado pulado)");

    // Captura real por ~1,5 s em cada microfone ativo, pela MESMA classe da chamada (fonte sem
    // alto-falante, como na troca de microfone durante a ligação). Só o nível é calculado.
    foreach (var a in ativos)
    {
        var idx = MicrofoneService.IndiceWave(a, saida: false);
        var fonte = new WindowsAudioEndPoint(new AudioEncoder(), -1, idx, false, true);
        var proxy = new MuteableAudioSource(fonte, nivelarMicrofone: false);
        proxy.SetAudioSourceFormat(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU));
        int blocos = 0; proxy.OnAudioSourceEncodedSample += (_, b) => Interlocked.Increment(ref blocos);
        proxy.Detector.Iniciar();
        proxy.StartAudio().Wait(2000);
        Thread.Sleep(1500);
        proxy.CloseAudio().Wait(2000);
        Check($"captura real \"{a.Nome}\": blocos chegando (~75 em 1,5 s)", blocos >= 40, $"blocos={blocos} {proxy.Detector.Resumo()}");
    }

    // Troca de microfone no meio do áudio (headset reconectado / "usar microfone do computador")
    if (ativos.Count >= 2)
    {
        var i0 = MicrofoneService.IndiceWave(ativos[0], false); var i1 = MicrofoneService.IndiceWave(ativos[1], false);
        var f0 = new WindowsAudioEndPoint(new AudioEncoder(), -1, i0, false, true);
        var proxy = new MuteableAudioSource(f0, nivelarMicrofone: true);
        proxy.SetAudioSourceFormat(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU));
        int antes = 0, depois = 0; bool trocou = false;
        proxy.OnAudioSourceEncodedSample += (_, _) => { if (Volatile.Read(ref trocou)) Interlocked.Increment(ref depois); else Interlocked.Increment(ref antes); };
        proxy.StartAudio().Wait(2000);
        Thread.Sleep(800);
        var f1 = new WindowsAudioEndPoint(new AudioEncoder(), -1, i1, false, true);
        Volatile.Write(ref trocou, true);
        var anterior = proxy.TrocarFonte(f1);
        Thread.Sleep(1200);
        Check("troca de microfone no meio do áudio: o fluxo continua pelo novo", antes >= 20 && depois >= 30 && proxy.FonteExtra == f1 && anterior == null, $"antes={antes} depois={depois}");
        proxy.CloseAudio().Wait(2000);
    }
    return Fim();
}

// Réplica do critério da 2.6.0 (nome com prefixo no mapa WaveIn) — só para comparação.
static int AudioDeviceService_Antigo(string nome)
{
    if (string.IsNullOrEmpty(nome)) return -1;
    for (int i = 0; i < NAudio.Wave.WaveIn.DeviceCount; i++)
    {
        var p = NAudio.Wave.WaveIn.GetCapabilities(i).ProductName ?? "";
        if (nome.StartsWith(p, StringComparison.OrdinalIgnoreCase) || p.StartsWith(nome, StringComparison.OrdinalIgnoreCase)) return i;
    }
    return -1;
}

// ════════════════════════════════════════════════════════════════════════════════════════
int MudoTeste(string nome)
{
    Console.WriteLine($"== Mudo e volume baixo no Windows (\"{nome}\") — estado original é restaurado");
    using var en = new MMDeviceEnumerator();
    var dev = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.FriendlyName == nome);
    if (dev == null) { Check("microfone encontrado", false, nome); return Fim(); }
    var vol = dev.AudioEndpointVolume;
    bool mudoOrig = vol.Mute; float nivelOrig = vol.MasterVolumeLevelScalar;
    Console.WriteLine($"   original: mudo={mudoOrig} nível={nivelOrig * 100:F0}%");
    try
    {
        vol.Mute = true;
        var s = MicrofoneService.VerificarMicrofone(nome, dev.ID, null, null);
        Check("mudo do Windows detectado (grave)", s.Problemas.Contains(ProblemaMicrofone.Mudo) && s.Grave, s.TextoParaOperador());

        // o que a chamada recebe de um microfone no mudo do Windows
        var idx = MicrofoneService.IndiceWave(new EndpointAudio(dev.ID, nome, true, false, false, "Active"), false);
        var fonte = new WindowsAudioEndPoint(new AudioEncoder(), -1, idx, false, true);
        var proxy = new MuteableAudioSource(fonte, false);
        proxy.SetAudioSourceFormat(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU));
        proxy.OnAudioSourceEncodedSample += (_, _) => { };
        var susp = new System.Collections.Concurrent.ConcurrentQueue<SuspeitaMicrofone>();
        proxy.SuspeitaMudou += x => susp.Enqueue(x);
        proxy.Detector.Iniciar();
        proxy.StartAudio().Wait(2000);
        Thread.Sleep(4800);
        proxy.CloseAudio().Wait(2000);
        Check("microfone no mudo do Windows → detector: sem sinal desde o atendimento", susp.Contains(SuspeitaMicrofone.SemSinalDesdeInicio), proxy.Detector.Resumo());
        vol.Mute = mudoOrig;

        vol.MasterVolumeLevelScalar = 0.05f;
        s = MicrofoneService.VerificarMicrofone(nome, dev.ID, null, null);
        Check("volume 5% no Windows → VolumeBaixo (atenção, não grave)", s.Problemas.Contains(ProblemaMicrofone.VolumeBaixo) && !s.Grave, s.TextoParaOperador());
    }
    finally
    {
        vol.Mute = mudoOrig;
        vol.MasterVolumeLevelScalar = nivelOrig;
        Console.WriteLine($"   restaurado: mudo={vol.Mute} nível={vol.MasterVolumeLevelScalar * 100:F0}%");
    }
    Check("estado original restaurado", vol.Mute == mudoOrig && Math.Abs(vol.MasterVolumeLevelScalar - nivelOrig) < 0.01);
    return Fim();
}

// ════════════════════════════════════════════════════════════════════════════════════════
int Telas(string pastaArg)
{
    var pasta = Path.GetFullPath(pastaArg); Directory.CreateDirectory(pasta);
    var th = new Thread(() =>
    {
        if (System.Windows.Application.Current == null) new WavenVoIP.App().InitializeComponent();
        System.Windows.Application.Current!.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        // Mesmo método do V250Tests: mede o conteúdo da janela sem exibi-la.
        void Foto(WavenVoIP.Views.CallWindow w, string nome, double nivelDb, AlertaMicrofone? alerta)
        {
            const double largura = 394;                      // área útil da janela de 410
            var raiz = (System.Windows.FrameworkElement)w.Content;
            void Montar() { raiz.Measure(new System.Windows.Size(largura, double.PositiveInfinity)); raiz.Arrange(new System.Windows.Rect(0, 0, largura, raiz.DesiredSize.Height)); raiz.UpdateLayout(); }
            w.MostrarAlertaMicrofone(alerta);
            Montar();
            w.AtualizarNivelMicrofone(nivelDb, false);      // a barra usa a largura já medida
            Montar();
            var h = raiz.DesiredSize.Height;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)largura, (int)Math.Ceiling(h), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var fundo = new System.Windows.Media.DrawingVisual();
            using (var dc = fundo.RenderOpen()) dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, largura, h));
            bmp.Render(fundo); bmp.Render(raiz);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create(Path.Combine(pasta, nome))) enc.Save(fs);

            // A janela tem 660 de altura (≈ 621 úteis) e cresce o tamanho do aviso + 10: o conteúdo
            // precisa caber, senão o botão Desligar fica cortado.
            var painel = (System.Windows.FrameworkElement)w.FindName("painelAlertaMic");
            var crescimento = painel.Visibility == System.Windows.Visibility.Visible ? painel.ActualHeight + 10 : 0;
            var util = 660 - 39 + crescimento;
            Check($"tela {nome}: conteúdo cabe na janela (botão Desligar visível)", h <= util + 0.5, $"conteúdo={h:F0} útil={util:F0}");
        }

        var c0 = new WavenVoIP.Views.CallWindow("Cliente Exemplo", "Em chamada");
        c0.DefinirMicrofoneEmUso("Microfone (3- Intelbras CHS 55B V2N)");
        Foto(c0, "chamada-normal.png", -22, null);

        var c1 = new WavenVoIP.Views.CallWindow("Cliente Exemplo", "Em chamada");
        c1.DefinirMicrofoneEmUso("Grupo de microfones (Realtek(R) Audio)");
        Foto(c1, "chamada-alerta-grave.png", -120, new AlertaMicrofone(true, "Microfone desconectado durante a ligação",
            "O microfone \"Microfone (3- Intelbras CHS 55B V2N)\" foi desconectado. O cliente NÃO está te ouvindo. Reconecte o headset (o áudio volta sozinho) ou use o microfone do computador.", true, "MIC_DESCONECTADO"));

        var c2 = new WavenVoIP.Views.CallWindow("Cliente Exemplo", "Em chamada");
        c2.DefinirMicrofoneEmUso("Microfone (3- Intelbras CHS 55B V2N)");
        Foto(c2, "chamada-alerta-atencao.png", -66, new AlertaMicrofone(false, "Sua voz não foi detectada",
            "Ainda não detectamos sua voz nesta ligação (microfone \"Microfone (3- Intelbras CHS 55B V2N)\"). Fale normalmente: se a barra do microfone não se mexer, o cliente não está te ouvindo.", true, "MIC_SEM_VOZ"));
    });
    th.SetApartmentState(ApartmentState.STA);
    th.Start(); th.Join();
    Console.WriteLine($"PNGs em {pasta}");
    return Fim();
}
