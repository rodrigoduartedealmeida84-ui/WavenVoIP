using System;

namespace WavenVoIP.Services
{
    // v2.5.0 — nivelamento automático do microfone (TX), aplicado ao PCM antes do G.711.
    //
    // Por que existe: o microfone ia "cru" para a rede. Medição em 14 gravações reais de
    // produção (05–11/10/2026, ramais 103/104/109) mostrou o lado do operador entre −27 e
    // −36 dBFS e o lado do cliente entre −14 e −20 dBFS — 9 a 18 dB de diferença, variando
    // por posto (headset/nível do Windows). O Asterisk grava com MixMonitor sem nenhuma opção
    // de ganho e a conferência (ConfBridge) também não nivela ninguém, então o que sai baixo
    // do PC fica baixo na gravação, para o cliente e para a sala.
    //
    // Como funciona (bloco a bloco, tipicamente 20 ms):
    //  • mede o nível só nos blocos com fala (acima do piso de ruído estimado) — silêncio e
    //    ruído de fundo nunca fazem o ganho subir;
    //  • leva a fala para o alvo (−18 dBFS) com ganho entre −6 e +18 dB, subindo devagar
    //    (4 dB/s) e descendo rápido (30 dB/s); o ganho fica parado durante o silêncio, então
    //    não há "respiração" do ruído entre as frases;
    //  • o teto de ganho cai quando o piso de ruído é alto, para não transformar chiado em
    //    ruído audível;
    //  • limitador por bloco: o ganho do bloco é reduzido para o pico nunca passar de −1 dBFS
    //    (sem clipping, sem distorção).
    public sealed class MicAutoGain
    {
        public const double AlvoDb       = -18.0;
        // v2.5.0 (13/10/2026) — medição real: com o microfone do notebook a fala chegou a −42 dBFS;
        // +18 dB levava só a −24 e, como o ganho recomeçava do zero a cada abertura do app e
        // subia 4 dB por segundo de fala, uma ligação curta terminava antes de nivelar (ganho
        // médio de 9 dB → operador ~10 dB abaixo do cliente na gravação). Agora o teto é +30 dB
        // (sempre limitado pelo ruído de fundo e pelo limitador), a subida é 3× mais rápida e o
        // ganho aprendido é guardado em disco para a próxima abertura do app.
        public const double GanhoMaxDb   = 30.0;
        public const double GanhoMinDb   = -6.0;
        private const double SubidaDbPorSeg  = 12.0;
        private const double DescidaDbPorSeg = 30.0;
        private const double PicoMaximo  = 0.891; // −1 dBFS
        private const double RuidoMaxPosGanhoDb = -42.0;

        // ── Supressão de eco acústico (v2.5.0) ─────────────────────────────────────
        // Caso real (12/10/2026, ramal 100): o headset configurado não foi encontrado, a chamada
        // usou o alto-falante e o microfone do notebook, e o microfone passou a captar a voz do
        // cliente saindo do alto-falante. Para o nivelamento aquilo era "fala baixa" (−40 dBFS):
        // ele subiu o ganho até +18 dB e devolveu ao cliente a própria voz amplificada — eco.
        //
        // Agora o nivelamento sabe quando o outro lado está falando (nível do áudio recebido):
        //  • aprende o acoplamento alto-falante→microfone (quanto do áudio recebido volta pelo
        //    microfone) — com headset esse valor é praticamente nulo e nada muda;
        //  • enquanto o outro lado fala, o que o microfone capta e não passa claramente do eco
        //    esperado NÃO é tratado como fala: não treina o ganho, não é amplificado e ainda é
        //    atenuado em 12 dB;
        //  • a fala do operador (bem acima do eco esperado) continua nivelada normalmente.
        // É um supressor (não um cancelador): em viva-voz, falar ao mesmo tempo que o cliente
        // fica atenuado; com headset não há diferença.
        private const double FarAtivoDb = -45.0;
        private const double MargemFalaSobreEcoDb = 8.0;
        private const double AtenuacaoEcoDb = 12.0;
        private const int JanelaFarBlocos = 30;                 // ~600 ms: atraso do alto-falante + cauda do eco
        private static double _acoplamentoInicialDb = -20.0;    // aprendido na chamada anterior
        private readonly double[] _farRecente = new double[JanelaFarBlocos];
        private int _farPos;
        private double _acoplamentoDb;
        private double _extraDb;                                // atenuação de eco em vigor (≤ 0)
        private long _blocosEco;

        // v2.5.0 (13/10/2026) — a proteção de eco confundia a fala do operador com retorno
        // (gravações reais do ramal 100: fala inteira marcada como eco, ganho ~0 e −12 dB).
        // Regras novas:
        //  • o corte de 12 dB só existe com vazamento COMPROVADO do alto-falante para o microfone
        //    (acoplamento aprendido acima de −38 dB = viva-voz). Com headset (microfone isolado) ou
        //    enquanto o acoplamento ainda não foi aprendido, um bloco "parecido com retorno" fica
        //    NEUTRO: passa como chegou, sem ganho e sem corte — a voz nunca é cortada;
        //  • quem já estava falando continua sendo tratado como fala quando o outro lado
        //    interrompe ("aham", fala simultânea): 300 ms de sustentação com headset, 100 ms em
        //    viva-voz;
        //  • o acoplamento só é medido com o outro lado falando alto e o operador calado, converge
        //    rápido no início e é guardado por microfone.
        private const double AcoplamentoViVaVozDb = -38.0;
        private const int AtualizacoesParaAprender = 100;
        private static bool _acoplamentoInicialAprendido;
        private int _acopAtualizacoes;
        private int _blocosDesdeFalaConfirmada = int.MaxValue / 2;
        private int _blocosFalaBaixaSeguidos;
        private long _blocosNeutros;

        // O ganho da última chamada é o melhor palpite para o início da próxima (mesmo posto,
        // mesmo headset) — evita os primeiros segundos baixos a cada ligação.
        // Guardado POR MICROFONE (arquivo "nome<TAB>ganho"): headset e microfone do notebook têm
        // níveis muito diferentes; trocar de um para o outro não leva o ganho errado junto.
        private static double _ganhoInicialDb;
        private static bool _persistir = true;
        private static string _dispositivo = string.Empty;
        private static readonly object _arquivoLock = new();

        private static string ArquivoGanho => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WavenVoIP", "mic-autogain.txt");

        private static System.Collections.Generic.Dictionary<string, (double ganho, double? acoplamento)> LerGanhos()
        {
            var d = new System.Collections.Generic.Dictionary<string, (double ganho, double? acoplamento)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!System.IO.File.Exists(ArquivoGanho)) return d;
                foreach (var linha in System.IO.File.ReadAllLines(ArquivoGanho))
                {
                    // nome<TAB>ganho[<TAB>acoplamento]
                    var p = linha.Split('\t');
                    if (p.Length < 2 || p[0].Length == 0) continue;
                    if (!double.TryParse(p[1].Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
                    double? ac = null;
                    if (p.Length >= 3 && double.TryParse(p[2].Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var a)) ac = Math.Clamp(a, -70.0, 0.0);
                    d[p[0]] = (Math.Clamp(v, 0.0, GanhoMaxDb), ac);
                }
            }
            catch { }
            return d;
        }

        // Chamado antes de cada chamada com o nome do microfone em uso.
        public static void DefinirDispositivo(string nome)
        {
            nome = (nome ?? string.Empty).Trim();
            lock (_arquivoLock)
            {
                if (string.Equals(nome, _dispositivo, StringComparison.OrdinalIgnoreCase) && _dispositivo.Length > 0) return;
                _dispositivo = nome;
                _persistir = true;
                var salvo = LerGanhos().TryGetValue(nome, out var g) ? g : (0.0, (double?)null);
                _ganhoInicialDb = salvo.Item1;
                _acoplamentoInicialAprendido = salvo.Item2.HasValue;
                _acoplamentoInicialDb = salvo.Item2 ?? -45.0;   // microfone novo: aprende do zero
            }
        }

        private static void SalvarGanho(double? ganho, double? acoplamento)
        {
            lock (_arquivoLock)
            {
                if (!_persistir || _dispositivo.Length == 0) return;
                try
                {
                    var d = LerGanhos();
                    var atual = d.TryGetValue(_dispositivo, out var x) ? x : (0.0, (double?)null);
                    d[_dispositivo] = (ganho ?? atual.Item1, acoplamento ?? atual.Item2);
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    System.IO.File.WriteAllLines(ArquivoGanho, System.Linq.Enumerable.Select(d,
                        kv => kv.Key + "\t" + kv.Value.ganho.ToString("F1", inv) +
                              (kv.Value.acoplamento.HasValue ? "\t" + kv.Value.acoplamento.Value.ToString("F1", inv) : string.Empty)));
                }
                catch { }
            }
        }

        private double _ganhoDb;
        private double _ganhoLinAnterior;
        private double _nivelFalaDb = double.NaN;
        private double _ruidoDb = double.NaN;

        // Estatística da chamada (só para o log de diagnóstico)
        private long _blocosFala;
        private double _somaEntradaDb, _somaGanhoDb;
        private double _ganhoPicoDb = double.NegativeInfinity;
        private long _blocosLimitados;

        public MicAutoGain()
        {
            _ganhoDb = Math.Clamp(_ganhoInicialDb, 0.0, GanhoMaxDb);
            _ganhoLinAnterior = DbParaLin(_ganhoDb);
            _acoplamentoDb = _acoplamentoInicialDb;
            _acopAtualizacoes = _acoplamentoInicialAprendido ? AtualizacoesParaAprender + 100 : 0;
            for (int i = 0; i < _farRecente.Length; i++) _farRecente[i] = -120.0;
        }

        public double AcoplamentoDb => _acoplamentoDb;

        // Segurança do ganho alto: +30 dB só com o microfone isolado do alto-falante (headset,
        // acoplamento ≤ −40 dB). Em viva-voz (alto-falante vazando para o microfone, ≥ −30 dB)
        // o teto volta a +18 dB, porque um retorno confundido com fala seria amplificado junto.
        internal static double TetoPorAcoplamento(double acoplamentoDb)
        {
            if (acoplamentoDb <= -40.0) return GanhoMaxDb;
            if (acoplamentoDb >= -30.0) return 18.0;
            return 18.0 + (GanhoMaxDb - 18.0) * (-30.0 - acoplamentoDb) / 10.0;
        }

        // Estatística da chamada, para o ajuste do nível do microfone no Windows.
        public double FalaEntradaMediaDb => _blocosFala > 0 ? _somaEntradaDb / _blocosFala : double.NaN;
        public long BlocosFala => _blocosFala;
        public long BlocosLimitados => _blocosLimitados;

        public double GanhoAtualDb => _ganhoDb;

        // farDb = nível (dBFS) do áudio que está chegando do outro lado neste instante;
        // −120 (padrão) = silêncio/desconhecido — o comportamento é o do nivelamento puro.
        public void Processar(short[] pcm, int sampleRate, double farDb = -120.0)
        {
            if (pcm == null || pcm.Length == 0 || sampleRate <= 0) return;

            double soma = 0; int pico = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                int v = pcm[i];
                soma += (double)v * v;
                int a = v < 0 ? -v : v;
                if (a > pico) pico = a;
            }
            var rms = Math.Sqrt(soma / pcm.Length) / 32768.0;
            var blocoDb = 20.0 * Math.Log10(Math.Max(rms, 1e-7));
            var segundos = pcm.Length / (double)sampleRate;

            // Piso de ruído: desce na hora, sobe ~0,5 dB/s.
            // Começa pelo primeiro bloco: se ele já for fala, o piso fica alto (ganho contido) até a
            // primeira pausa — errar para o lado seguro, nunca amplificar ruído no início.
            if (double.IsNaN(_ruidoDb) || blocoDb < _ruidoDb) _ruidoDb = blocoDb;
            else _ruidoDb += 0.5 * segundos;
            _ruidoDb = Math.Clamp(_ruidoDb, -75.0, -40.0);

            var ehFala = blocoDb > Math.Max(_ruidoDb + 12.0, -52.0);

            // Outro lado falando agora ou há instantes (o eco chega atrasado)?
            _farRecente[_farPos] = double.IsNaN(farDb) ? -120.0 : farDb;
            _farPos = (_farPos + 1) % _farRecente.Length;
            var farMax = -120.0;
            for (int i = 0; i < _farRecente.Length; i++) if (_farRecente[i] > farMax) farMax = _farRecente[i];
            var emRetorno = farMax > FarAtivoDb;
            var ehEco = false;      // retorno comprovado (viva-voz): corta
            var neutro = false;     // parecido com retorno, sem comprovação: passa sem ganho
            var aprendido = _acopAtualizacoes >= AtualizacoesParaAprender;
            var vivaVoz = aprendido && _acoplamentoDb > AcoplamentoViVaVozDb;

            // O operador já estava falando (fala confirmada há instantes) e continua no nível dele:
            // segue como fala mesmo que o outro lado tenha começado a falar.
            var sustentacao = vivaVoz ? 5 : 15;
            var operadorFalando = ehFala && _blocosDesdeFalaConfirmada <= sustentacao &&
                                  !double.IsNaN(_nivelFalaDb) && blocoDb >= _nivelFalaDb - 12.0;
            var parecidoComRetorno = false;
            if (emRetorno)
            {
                // Acoplamento = (nível no microfone) − (nível recebido), medido com o outro lado
                // falando e o operador calado. Rastreador de mediana, rápido no início.
                if (!operadorFalando && farDb > FarAtivoDb)
                {
                    var dif = blocoDb - farMax;
                    var passoAcop = _acopAtualizacoes < 100 ? 0.5 : 0.12;
                    _acoplamentoDb += dif > _acoplamentoDb ? passoAcop : -passoAcop;   // passos iguais = mediana
                    _acoplamentoDb = Math.Clamp(_acoplamentoDb, -70.0, 0.0);
                    _acopAtualizacoes++;
                }
                var acopUso = aprendido ? _acoplamentoDb : Math.Max(_acoplamentoDb, -20.0);
                parecidoComRetorno = blocoDb <= farMax + acopUso + MargemFalaSobreEcoDb;
                if (parecidoComRetorno && !operadorFalando)
                {
                    var tinhaSom = ehFala;
                    ehFala = false;
                    if (vivaVoz) { ehEco = true; if (tinhaSom) _blocosEco++; }
                    else         { neutro = true; if (tinhaSom) _blocosNeutros++; }
                }
            }
            if (ehFala && !parecidoComRetorno) _blocosDesdeFalaConfirmada = 0;
            else if (_blocosDesdeFalaConfirmada < int.MaxValue / 2) _blocosDesdeFalaConfirmada++;

            // v2.5.0 (13/10/2026, ligação real com headset) — sons baixos entre as frases (respiração,
            // teclado, vozes ao fundo, 10+ dB abaixo da fala) contavam como fala: o nível de
            // referência caía, o ganho subia até +27 dB e a frase seguinte saía alta demais até o
            // limitador segurar (61 blocos limitados). Agora esses blocos não treinam o nível nem
            // o ganho — só depois de 3 s seguidos assim é que valem como "a pessoa passou a
            // falar mais baixo".
            if (ehFala && !double.IsNaN(_nivelFalaDb) && blocoDb < _nivelFalaDb - 10.0)
            {
                if (++_blocosFalaBaixaSeguidos < 150) ehFala = false;
            }
            else if (ehFala) _blocosFalaBaixaSeguidos = 0;

            if (ehFala)
            {
                if (double.IsNaN(_nivelFalaDb)) _nivelFalaDb = blocoDb;
                else _nivelFalaDb += (blocoDb - _nivelFalaDb) * (blocoDb > _nivelFalaDb ? 0.20 : 0.02);

                var teto = Math.Min(Math.Clamp(RuidoMaxPosGanhoDb - _ruidoDb, 6.0, GanhoMaxDb), TetoPorAcoplamento(_acoplamentoDb));
                var desejado = Math.Clamp(AlvoDb - _nivelFalaDb, GanhoMinDb, teto);
                // faixa morta de 1 dB: sem ela a subida mais rápida faria o ganho "bombear"
                var difGanho = desejado - _ganhoDb;
                if (difGanho > 1.0)       _ganhoDb = Math.Min(desejado, _ganhoDb + SubidaDbPorSeg * segundos);
                else if (difGanho < -1.0) _ganhoDb = Math.Max(desejado, _ganhoDb - DescidaDbPorSeg * segundos);

                _blocosFala++;
                _somaEntradaDb += blocoDb;
                _somaGanhoDb += _ganhoDb;
                if (_ganhoDb > _ganhoPicoDb) _ganhoPicoDb = _ganhoDb;
            }

            // Eco: sem ganho positivo e 12 dB abaixo; entra na hora, sai em ~100 ms.
            // Neutro: só retira o ganho positivo (passa como chegou).
            var extraAlvo = ehEco ? -(AtenuacaoEcoDb + Math.Max(_ganhoDb, 0.0))
                          : neutro ? -Math.Max(_ganhoDb, 0.0) : 0.0;
            if (extraAlvo < _extraDb) _extraDb = extraAlvo;
            else _extraDb = Math.Min(extraAlvo, _extraDb + 300.0 * segundos);

            var ganhoLin = DbParaLin(_ganhoDb + _extraDb);

            // Limitador: nenhum pico deste bloco passa de −1 dBFS.
            if (pico > 0)
            {
                var maxLin = PicoMaximo * 32767.0 / pico;
                if (ganhoLin > maxLin)
                {
                    ganhoLin = maxLin;
                    if (!ehEco && !neutro) _ganhoDb = Math.Max(GanhoMinDb, 20.0 * Math.Log10(ganhoLin));
                    _blocosLimitados++;
                }
                if (_ganhoLinAnterior > maxLin) _ganhoLinAnterior = maxLin;
            }

            // Rampa linear entre o ganho do bloco anterior e o deste — sem degrau audível.
            var passo = (ganhoLin - _ganhoLinAnterior) / pcm.Length;
            var g = _ganhoLinAnterior;
            for (int i = 0; i < pcm.Length; i++)
            {
                g += passo;
                var v = pcm[i] * g;
                pcm[i] = (short)(v > 32767.0 ? 32767 : v < -32768.0 ? -32768 : Math.Round(v));
            }
            _ganhoLinAnterior = ganhoLin;
        }

        // Chamado ao encerrar a chamada: guarda o ganho para a próxima e devolve o resumo.
        public string EncerrarEResumir()
        {
            if (_blocosFala > 50)
            {
                _ganhoInicialDb = Math.Clamp(_ganhoDb, 0.0, GanhoMaxDb);
                SalvarGanho(_ganhoInicialDb, null);
            }
            if (_acopAtualizacoes >= AtualizacoesParaAprender)
            {
                _acoplamentoInicialDb = Math.Clamp(_acoplamentoDb, -70.0, 0.0);
                _acoplamentoInicialAprendido = true;
                SalvarGanho(null, _acoplamentoInicialDb);
            }
            var modo = _acopAtualizacoes < AtualizacoesParaAprender ? "nao_aprendido" : _acoplamentoDb > AcoplamentoViVaVozDb ? "viva_voz" : "isolado";
            var eco = FormattableString.Invariant($"blocos_eco_suprimidos={_blocosEco} blocos_neutros={_blocosNeutros} acoplamento_db={_acoplamentoDb:F1} microfone={modo}");
            if (_blocosFala == 0) return "sem_fala_detectada " + eco;
            return FormattableString.Invariant(
                $"fala_entrada_dbfs={_somaEntradaDb / _blocosFala:F1} ganho_medio_db={_somaGanhoDb / _blocosFala:F1} ganho_max_db={_ganhoPicoDb:F1} ruido_dbfs={_ruidoDb:F1} blocos_fala={_blocosFala} blocos_limitados={_blocosLimitados} ") + eco;
        }

        public static void ZerarGanhoInicialParaTeste() { _ganhoInicialDb = 0.0; _acoplamentoInicialDb = -20.0; _acoplamentoInicialAprendido = false; _persistir = false; _dispositivo = string.Empty; }

        private static double DbParaLin(double db) => Math.Pow(10.0, db / 20.0);
    }
}
