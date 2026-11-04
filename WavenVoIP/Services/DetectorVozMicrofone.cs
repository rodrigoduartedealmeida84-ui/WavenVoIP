using System;
using System.Globalization;
using System.IO;

namespace WavenVoIP.Services
{
    // v2.6.1 — percebe, DURANTE a chamada, que o cliente pode não estar ouvindo o operador.
    //
    // Recebe o nível de cada bloco (20 ms) do microfone ANTES de qualquer ganho e diferencia:
    //  • mudo intencional e espera  → pausado: nunca alerta (é escolha do operador);
    //  • silêncio normal            → já houve voz nesta chamada e o microfone continua com sinal;
    //  • sem sinal                  → o Windows entrega zeros absolutos (mudo do Windows, privacidade,
    //                                 dispositivo parado) — desde o atendimento ou depois de já ter sinal;
    //  • sem voz desde o atendimento→ há sinal (ruído), mas nenhuma fala foi detectada nos primeiros
    //                                 segundos (microfone errado/longe, volume quase zerado).
    // Não guarda áudio: só números (nível em dBFS e tempos).
    public enum SuspeitaMicrofone { Nenhuma, SemSinalDesdeInicio, SinalPerdido, SemVozDesdeAtendimento }

    public sealed class DetectorVozMicrofone
    {
        public const double SemSinalInicialSeg = 4.0;     // zeros absolutos desde o atendimento
        public const double SinalPerdidoSeg    = 5.0;     // zeros absolutos depois de já ter tido sinal
        public const double SemVozInicialSeg   = 8.0;     // nenhuma fala desde o atendimento
        public const double VozMinimaDb        = -50.0;   // fala precisa passar disso…
        public const double VozSobreRuidoDb    = 12.0;    // …e ficar 12 dB acima do ruído de fundo
        public const double FarAtivoDb         = -45.0;   // cliente falando

        private readonly object _lock = new();
        private bool _ativo;
        private double _monitorado, _zeroRun, _ruidoDb = double.NaN;
        private bool _houveSinal, _houveVoz;
        private SuspeitaMicrofone _atual = SuspeitaMicrofone.Nenhuma;

        // estatística da chamada (só números)
        public double SegundosMonitorados { get; private set; }
        public double SegundosComVoz { get; private set; }
        public double SegundosSemSinal { get; private set; }
        public double SegundosPausado { get; private set; }
        public double SegundosClienteFalando { get; private set; }
        public double PicoDb { get; private set; } = -120.0;
        private double _somaFalaDb; private long _blocosFala;
        public double FalaMediaDb => _blocosFala > 0 ? _somaFalaDb / _blocosFala : double.NaN;
        public int Suspeitas { get; private set; }
        public string PrimeiraSuspeita { get; private set; } = string.Empty;

        // Último nível do bloco, para o medidor da tela (thread-safe por leitura atômica de double).
        private double _ultimoNivelDb = -120.0;
        public double UltimoNivelDb => System.Threading.Volatile.Read(ref _ultimoNivelDb);

        public bool Ativo { get { lock (_lock) return _ativo; } }
        public SuspeitaMicrofone Atual { get { lock (_lock) return _atual; } }
        public bool HouveVoz { get { lock (_lock) return _houveVoz; } }

        public void Iniciar()
        {
            lock (_lock) { _ativo = true; _monitorado = 0; _zeroRun = 0; _atual = SuspeitaMicrofone.Nenhuma; }
        }

        public void Parar() { lock (_lock) _ativo = false; }

        // Retorna a suspeita atual (Nenhuma = tudo certo ou ainda cedo).
        public SuspeitaMicrofone Processar(double blocoDb, bool zeroAbsoluto, double segundos, bool pausado, double farDb = -120.0)
        {
            System.Threading.Volatile.Write(ref _ultimoNivelDb, pausado ? -120.0 : blocoDb);
            lock (_lock)
            {
                if (!_ativo || segundos <= 0) return SuspeitaMicrofone.Nenhuma;
                if (pausado)
                {
                    // Mudo/espera: zera as contagens; ao voltar, recomeça a carência normalmente.
                    SegundosPausado += segundos;
                    _monitorado = 0; _zeroRun = 0;
                    return _atual = SuspeitaMicrofone.Nenhuma;
                }

                _monitorado += segundos;
                SegundosMonitorados += segundos;
                if (farDb > FarAtivoDb) SegundosClienteFalando += segundos;

                if (zeroAbsoluto) { _zeroRun += segundos; SegundosSemSinal += segundos; }
                else
                {
                    _zeroRun = 0;
                    _houveSinal = true;
                    if (double.IsNaN(_ruidoDb) || blocoDb < _ruidoDb) _ruidoDb = blocoDb;
                    else _ruidoDb += 0.5 * segundos;
                    _ruidoDb = Math.Clamp(_ruidoDb, -100.0, -40.0);
                    if (blocoDb > PicoDb) PicoDb = blocoDb;
                    if (blocoDb > Math.Max(_ruidoDb + VozSobreRuidoDb, VozMinimaDb))
                    {
                        _houveVoz = true;
                        SegundosComVoz += segundos;
                        _somaFalaDb += blocoDb; _blocosFala++;
                    }
                }

                SuspeitaMicrofone nova;
                if (!_houveSinal && _monitorado >= SemSinalInicialSeg) nova = SuspeitaMicrofone.SemSinalDesdeInicio;
                else if (_houveSinal && _zeroRun >= SinalPerdidoSeg) nova = SuspeitaMicrofone.SinalPerdido;
                else if (!_houveVoz && _monitorado >= SemVozInicialSeg) nova = SuspeitaMicrofone.SemVozDesdeAtendimento;
                else nova = SuspeitaMicrofone.Nenhuma;

                if (nova != SuspeitaMicrofone.Nenhuma && _atual == SuspeitaMicrofone.Nenhuma)
                {
                    Suspeitas++;
                    if (PrimeiraSuspeita.Length == 0) PrimeiraSuspeita = nova.ToString();
                }
                return _atual = nova;
            }
        }

        // Abaixo disso o bloco é "sem sinal". Medido em 04/11/2026: microfone no MUDO do Windows
        // não entrega zeros, entrega um resíduo de até −98 dBFS; um microfone ligado em sala
        // silenciosa fica entre −75 e −50 dBFS (headset Intelbras: ruído ≈ −75).
        public const double SemSinalDb = -85.0;

        // Nível RMS (dBFS) de um bloco PCM 16 bits e se ele está "sem sinal" (zeros ou resíduo digital).
        public static (double db, bool zero) Medir(short[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return (-120.0, true);
            double soma = 0; bool tudoZero = true;
            for (int i = 0; i < pcm.Length; i++) { int v = pcm[i]; if (v != 0) tudoZero = false; soma += (double)v * v; }
            if (tudoZero) return (-120.0, true);
            var rms = Math.Sqrt(soma / pcm.Length) / 32768.0;
            var db = 20.0 * Math.Log10(Math.Max(rms, 1e-7));
            return (db, db < SemSinalDb);
        }

        public string Resumo()
        {
            lock (_lock)
            {
                var pct = SegundosMonitorados > 0 ? 100.0 * SegundosComVoz / SegundosMonitorados : 0;
                return FormattableString.Invariant(
                    $"monitorado_s={SegundosMonitorados:F1} voz_operador_s={SegundosComVoz:F1} ({pct:F0}%) fala_media_dbfs={(double.IsNaN(FalaMediaDb) ? -120 : FalaMediaDb):F1} pico_dbfs={PicoDb:F1} sem_sinal_s={SegundosSemSinal:F1} pausado_s={SegundosPausado:F1} cliente_falando_s={SegundosClienteFalando:F1} suspeitas={Suspeitas} primeira={(PrimeiraSuspeita.Length == 0 ? "-" : PrimeiraSuspeita)}");
            }
        }
    }

    // Medições de áudio por chamada, só números, em %LOCALAPPDATA%\WavenVoIP\audio-medicoes.csv
    // (o coletor de diagnóstico lê este arquivo). Arquivo limitado a ~1 MB (gira para .old).
    public static class MedicoesAudioChamada
    {
        private static readonly object _lock = new();
        public static string Arquivo => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WavenVoIP", "audio-medicoes.csv");

        public static void Registrar(string ramal, string microfone, string situacaoDispositivo, DetectorVozMicrofone d, string alerta)
        {
            try
            {
                lock (_lock)
                {
                    var arq = Arquivo;
                    Directory.CreateDirectory(Path.GetDirectoryName(arq)!);
                    if (File.Exists(arq) && new FileInfo(arq).Length > 1_000_000)
                    {
                        var old = Path.ChangeExtension(arq, ".old.csv");
                        try { File.Delete(old); } catch { }
                        File.Move(arq, old);
                    }
                    if (!File.Exists(arq))
                        File.AppendAllText(arq, "data_hora;ramal;microfone;situacao_dispositivo;monitorado_s;voz_operador_s;fala_media_dbfs;pico_dbfs;sem_sinal_s;pausado_s;cliente_falando_s;suspeitas;alerta\n");
                    static string L(string s) => (s ?? string.Empty).Replace(";", ",").Replace("\n", " ");
                    var c = CultureInfo.InvariantCulture;
                    File.AppendAllText(arq, string.Join(";",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", c), L(ramal), L(microfone), L(situacaoDispositivo),
                        d.SegundosMonitorados.ToString("F1", c), d.SegundosComVoz.ToString("F1", c),
                        (double.IsNaN(d.FalaMediaDb) ? -120 : d.FalaMediaDb).ToString("F1", c), d.PicoDb.ToString("F1", c),
                        d.SegundosSemSinal.ToString("F1", c), d.SegundosPausado.ToString("F1", c), d.SegundosClienteFalando.ToString("F1", c),
                        d.Suspeitas.ToString(c), L(alerta)) + "\n");
                }
            }
            catch { }
        }
    }
}
