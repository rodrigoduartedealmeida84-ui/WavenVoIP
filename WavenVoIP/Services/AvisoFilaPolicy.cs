using System;
using System.Collections.Generic;
using System.Linq;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    // v2.5.0 — decide quais chamadas vivas merecem o popup "cliente aguardando".
    // Regra: SOMENTE cliente parado numa das filas de atendimento configuradas (padrão 700 e
    // 701): canal do cliente executando Queue no PABX, sem ramal em conversa. URA, mensagens,
    // fora do horário, grupo tocando, caixa postal e ligação em atendimento nunca avisam —
    // continuam visíveis na janela "Puxar Ligação".
    //
    //  • a espera precisa aparecer em duas leituras seguidas do PABX (evita aviso de quem só
    //    passou pela fila e foi atendido na hora);
    //  • cada espera avisa uma vez. Dispensada pelo operador, não volta durante essa espera;
    //  • ligação atendida que VOLTA à fila é uma espera nova: avisa de novo (uma linha só);
    //  • ligação em espera (Hold) continua em ponte com o ramal — é "em atendimento", não fila;
    //  • atendida ou encerrada, sai da lista na leitura seguinte (o popup fecha sozinho).
    //
    // Sem estado de tela: a janela principal chama Avaliar() a cada leitura das chamadas ativas.
    public sealed class AvisoFilaPolicy
    {
        public const int LeiturasParaConfirmar = 2;
        public const string FilasPadrao = "700,701";

        private readonly Dictionary<string, int> _leiturasNaFila = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dispensados = new(StringComparer.OrdinalIgnoreCase);   // só a espera atual
        private readonly Dictionary<string, ChamadaAtivaInfo> _noPopup = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _chavePorCanal = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _filas = ParaConjunto(FilasPadrao);
        // v2.5.1 — espera que tocou no MEU ramal: não vira popup (o operador já está vendo a
        // ligação na tela de chamada recebida). Vale até a espera acabar, como uma dispensa.
        private readonly HashSet<string> _tocaramAqui = new(StringComparer.OrdinalIgnoreCase);
        private string _meuRamal = string.Empty;
        private string _numeroTocandoAqui = string.Empty;

        // Chamado a cada leitura: ramal deste posto e o número da chamada que toca nele agora
        // (vazio quando nada toca). O número vem do INVITE — chega antes da leitura do PABX.
        public void DefinirMeuRamal(string? ramal, string? numeroTocandoAqui)
        {
            _meuRamal = (ramal ?? string.Empty).Trim();
            _numeroTocandoAqui = SoDigitos(numeroTocandoAqui);
        }

        public bool TocaNoMeuRamal(ChamadaAtivaInfo c) =>
            (_meuRamal.Length > 0 && c.RamaisTocando.Any(r => string.Equals(r, _meuRamal, StringComparison.OrdinalIgnoreCase))) ||
            MesmoNumero(c.Numero, _numeroTocandoAqui);

        // Mesmo cliente: compara os 8 últimos dígitos (ignora 0, 55, DDD e prefixo de rota).
        public static bool MesmoNumero(string? a, string? b)
        {
            var x = SoDigitos(a); var y = SoDigitos(b);
            if (x.Length < 8 || y.Length < 8) return false;
            return x[^8..] == y[^8..];
        }

        private static string SoDigitos(string? t) => new((t ?? string.Empty).Where(char.IsDigit).ToArray());

        // A chamada começou a tocar no meu ramal: tira do popup na hora, sem esperar a leitura do PABX.
        public List<string> SuprimirTocandoAqui(string? numero)
        {
            _numeroTocandoAqui = SoDigitos(numero);
            var canais = new List<string>();
            foreach (var (k, c) in _noPopup.ToList())
            {
                if (!MesmoNumero(c.Numero, _numeroTocandoAqui)) continue;
                _dispensados.Add(k); _tocaramAqui.Add(k);
                canais.Add(c.CanalCliente);
            }
            return canais;
        }

        // "700,701" → filas que geram aviso. Vazio/inválido = padrão.
        public void DefinirFilas(string? filas)
        {
            var novo = ParaConjunto(filas);
            _filas = novo.Count > 0 ? novo : ParaConjunto(FilasPadrao);
        }

        private static HashSet<string> ParaConjunto(string? filas) =>
            new((filas ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()),
                StringComparer.OrdinalIgnoreCase);

        public static bool EstaAguardandoNaFila(ChamadaAtivaInfo c) =>
            c.Status == StatusChamadaAtiva.NaFila && string.IsNullOrEmpty(c.RamalAtendente);

        public bool FilaComAviso(ChamadaAtivaInfo c) => EstaAguardandoNaFila(c) && _filas.Contains((c.Fila ?? string.Empty).Trim());

        // A mesma ligação mantém o linkedid mesmo quando o PABX troca o estado ou o nome do canal:
        // v2.5.1 — a chave é só o linkedid (um aviso por ligação, nunca duplicado).
        public static string Chave(ChamadaAtivaInfo c) =>
            string.IsNullOrWhiteSpace(c.Linkedid) ? c.CanalCliente : c.Linkedid;

        public List<ChamadaAtivaInfo> Avaliar(IReadOnlyList<ChamadaAtivaInfo> ativas, out List<ChamadaAtivaInfo> novas) =>
            Avaliar(ativas, out novas, out _);

        // Devolve as chamadas que devem estar no popup agora.
        // "novas" = entraram nesta leitura; "saidas" = saíram nesta leitura, com o motivo.
        public List<ChamadaAtivaInfo> Avaliar(IReadOnlyList<ChamadaAtivaInfo> ativas,
            out List<ChamadaAtivaInfo> novas, out List<(ChamadaAtivaInfo chamada, string motivo)> saidas)
        {
            novas = new List<ChamadaAtivaInfo>();
            saidas = new List<(ChamadaAtivaInfo, string)>();
            var porChave = new Dictionary<string, ChamadaAtivaInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in ativas) porChave[Chave(a)] = a;

            // ligação que acabou: esquece tudo dela (o nome do canal pode ser reaproveitado depois)
            foreach (var k in _leiturasNaFila.Keys.Where(k => !porChave.ContainsKey(k)).ToList()) _leiturasNaFila.Remove(k);
            _dispensados.RemoveWhere(k => !porChave.ContainsKey(k));
            _tocaramAqui.RemoveWhere(k => !porChave.ContainsKey(k));

            _chavePorCanal.Clear();
            var mostrar = new List<ChamadaAtivaInfo>();
            var chavesMostradas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in ativas)
            {
                var k = Chave(c);
                _chavePorCanal[c.CanalCliente] = k;
                if (!FilaComAviso(c))
                {
                    // saiu da fila (atendida, URA, outra fila…): a espera acabou; se voltar, é outra
                    // (só o canal principal decide: outro canal da mesma ligação não zera a espera)
                    if (ativas.Any(o => Chave(o) == k && FilaComAviso(o))) continue;
                    _leiturasNaFila.Remove(k);
                    _dispensados.Remove(k);
                    _tocaramAqui.Remove(k);
                    continue;
                }
                if (!processadas.Add(k)) continue;                      // outro canal da mesma ligação: uma linha só
                if (TocaNoMeuRamal(c)) { _dispensados.Add(k); _tocaramAqui.Add(k); }
                if (_dispensados.Contains(k)) continue;

                _leiturasNaFila.TryGetValue(k, out var n);
                _leiturasNaFila[k] = ++n;
                if (n < LeiturasParaConfirmar) continue;

                mostrar.Add(c);
                chavesMostradas.Add(k);
                if (!_noPopup.ContainsKey(k)) novas.Add(c);
            }

            foreach (var (k, antiga) in _noPopup.Where(p => !chavesMostradas.Contains(p.Key)).ToList())
            {
                string motivo;
                if (!porChave.TryGetValue(k, out var agora)) motivo = "encerrada";
                else if (_tocaramAqui.Contains(k)) motivo = "tocando_no_meu_ramal";
                else if (_dispensados.Contains(k)) motivo = "dispensada";
                else if (agora.Status == StatusChamadaAtiva.EmAtendimento) motivo = "atendida ramal=" + agora.RamalAtendente;
                else motivo = "saiu_da_fila status=" + agora.Status;
                saidas.Add((antiga, motivo));
                _noPopup.Remove(k);
            }
            foreach (var c in mostrar) _noPopup[Chave(c)] = c;
            return mostrar;
        }

        // O operador fechou o aviso (ou já puxou a ligação): não avisar de novo esta espera.
        public void Dispensar(ChamadaAtivaInfo c) => _dispensados.Add(Chave(c));

        // Mesmo efeito, a partir do canal do cliente mostrado no popup.
        public void DispensarCanal(string canalCliente)
        {
            if (!string.IsNullOrWhiteSpace(canalCliente))
                _dispensados.Add(_chavePorCanal.TryGetValue(canalCliente, out var k) ? k : canalCliente);
        }
    }
}
