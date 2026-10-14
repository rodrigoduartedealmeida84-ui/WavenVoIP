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

        // A mesma ligação mantém o linkedid mesmo quando o PABX troca o estado do canal.
        public static string Chave(ChamadaAtivaInfo c) =>
            string.IsNullOrWhiteSpace(c.Linkedid) ? c.CanalCliente : c.Linkedid + "|" + c.CanalCliente;

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

            _chavePorCanal.Clear();
            var mostrar = new List<ChamadaAtivaInfo>();
            var chavesMostradas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in ativas)
            {
                var k = Chave(c);
                _chavePorCanal[c.CanalCliente] = k;
                if (!FilaComAviso(c))
                {
                    // saiu da fila (atendida, URA, outra fila…): a espera acabou; se voltar, é outra
                    _leiturasNaFila.Remove(k);
                    _dispensados.Remove(k);
                    continue;
                }
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
