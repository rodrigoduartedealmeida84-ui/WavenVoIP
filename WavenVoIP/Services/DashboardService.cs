using System;
using System.Collections.Generic;
using System.Linq;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class DashboardService
    {
        public static DashboardMetrics Calcular(List<HistoricoLigacaoItem> todos, DateTime dataInicio, DateTime dataFim)
        {
            // v2.5.0 — chamadas que só o PABX atendeu (URA abandonada, anúncio, mensagem de fora do
            // horário) vêm do CDR como Recebida/ANSWERED, mas nenhum operador falou com o cliente:
            // não contam em Recebidas, Total de atendimentos nem no tempo médio. O gráfico de
            // chamadas por hora continua mostrando todo o volume que chegou.
            var itens = todos
                .Where(i => i.DataHora >= dataInicio && i.DataHora <= dataFim)
                .Where(i => !i.EhEventoTecnico) // ponta de volta da própria ligação: não é chamada de cliente
                .ToList();

            var recebidas  = itens.Count(i => i.EhRecebidaPorOperador);
            var realizadas = itens.Count(i => i.Tipo == TipoHistoricoLigacao.Realizada);
            // v2.5.0 — três indicadores separados para a chamada recebida sem atendimento:
            // perdida (tocou até o fim), cliente desistiu (desligou tocando) e abandono na fila.
            var perdidas   = itens.Count(i => (i.Tipo == TipoHistoricoLigacao.Perdida && !i.EhDesistenciaRapida && !i.EhAbandonoFila)
                                           || i.Tipo == TipoHistoricoLigacao.NaoAtendidaNesseRamal);
            var desistiu   = itens.Count(i => i.EhDesistenciaRapida);
            var abandono   = itens.Count(i => i.EhAbandonoFila);
            var gravacoes  = itens.Count(i => !string.IsNullOrWhiteSpace(i.GravacaoUrl)
                                           || !string.IsNullOrWhiteSpace(i.GravacaoArquivo));

            var duracoes = itens
                .Where(i => i.Tipo != TipoHistoricoLigacao.Perdida
                         && i.Tipo != TipoHistoricoLigacao.NaoAtendidaNesseRamal
                         && i.Tipo != TipoHistoricoLigacao.Recusada
                         && i.Tipo != TipoHistoricoLigacao.NaoAtendida
                         && !i.EhAtendimentoAutomatico)
                .Select(i => ParseDuracao(i.Duracao))
                .Where(s => s > 0)
                .ToList();

            var tempoMedio = duracoes.Count > 0
                ? FormatarTempo((int)duracoes.Average())
                : "—";

            return new DashboardMetrics
            {
                Recebidas        = recebidas,
                Realizadas       = realizadas,
                Perdidas         = perdidas,
                ClienteDesistiu  = desistiu,
                AbandonoFila     = abandono,
                TotalAtendimentos = recebidas + realizadas,
                TempoMedio       = tempoMedio,
                Gravacoes        = gravacoes,
                ChamadasPorHora  = CalcPorHora(itens),
                PorRamal         = CalcPorRamal(itens),
            };
        }

        private static List<HourBarItem> CalcPorHora(List<HistoricoLigacaoItem> itens)
        {
            var contagens = new int[24];
            foreach (var item in itens)
                contagens[item.DataHora.Hour]++;

            var max = contagens.Max();
            var result = new List<HourBarItem>(24);
            for (int h = 0; h < 24; h++)
            {
                var c = contagens[h];
                var altura = max > 0 ? Math.Round(c / (double)max * 80.0) : 0;
                result.Add(new HourBarItem
                {
                    HoraLabel      = $"{h:D2}",
                    Contagem       = c,
                    AlturaBar      = altura,
                    EspacadorAltura = 80 - altura,
                    Tooltip        = $"{h:D2}h — {c} chamada{(c == 1 ? "" : "s")}",
                });
            }
            return result;
        }

        private static List<RamalMetrica> CalcPorRamal(List<HistoricoLigacaoItem> itens)
        {
            var grupos = itens
                .GroupBy(i => ResolverRamal(i))
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .OrderByDescending(g => g.Count())
                .Take(20);

            var result = new List<RamalMetrica>();
            foreach (var g in grupos)
            {
                var ramal = g.Key;
                var nome = ResolverNome(ramal);
                var rec  = g.Count(i => i.Tipo == TipoHistoricoLigacao.Recebida);
                var real = g.Count(i => i.Tipo == TipoHistoricoLigacao.Realizada);
                var per  = g.Count(i => i.Tipo == TipoHistoricoLigacao.Perdida
                                     || i.Tipo == TipoHistoricoLigacao.NaoAtendidaNesseRamal);
                var duracoes = g
                    .Where(i => i.Tipo != TipoHistoricoLigacao.Perdida
                             && i.Tipo != TipoHistoricoLigacao.NaoAtendidaNesseRamal)
                    .Select(i => ParseDuracao(i.Duracao))
                    .Where(s => s > 0)
                    .ToList();

                result.Add(new RamalMetrica
                {
                    Ramal      = ramal,
                    Nome       = nome,
                    Recebidas  = rec,
                    Realizadas = real,
                    Perdidas   = per,
                    TempoMedio = duracoes.Count > 0 ? FormatarTempo((int)duracoes.Average()) : "—",
                });
            }
            return result;
        }

        private static string ResolverRamal(HistoricoLigacaoItem item)
        {
            switch (item.Tipo)
            {
                case TipoHistoricoLigacao.Realizada:
                case TipoHistoricoLigacao.Recusada:
                case TipoHistoricoLigacao.NaoAtendida:
                    // Saída: quem originou (RamalOrigem = ramal do operador).
                    if (!string.IsNullOrWhiteSpace(item.RamalOrigem))  return item.RamalOrigem;
                    if (!string.IsNullOrWhiteSpace(item.RamalDestino)) return item.RamalDestino;
                    return string.Empty;

                case TipoHistoricoLigacao.Recebida:
                    // Entrada atendida: quem atendeu (RamalAtendeu = agente real, não fila/tronco).
                    // RamalDestino pode ser a fila (600) — não usar como chave principal.
                    // v2.5.0 — só o PABX atendeu (URA/anúncio/fora do horário): não é de nenhum ramal.
                    if (item.EhAtendimentoAutomatico) return string.Empty;
                    if (!string.IsNullOrWhiteSpace(item.RamalAtendeu))  return item.RamalAtendeu;
                    if (!string.IsNullOrWhiteSpace(item.RamalDestino))  return item.RamalDestino;
                    if (!string.IsNullOrWhiteSpace(item.RamalOrigem))   return item.RamalOrigem;
                    return string.Empty;

                case TipoHistoricoLigacao.Perdida:
                case TipoHistoricoLigacao.NaoAtendidaNesseRamal:
                    // Abandonadas: sem prova de atendimento — não atribui à fila/tronco.
                    // Conta apenas se houver ramal confirmado (caso raro de perdida com registro de atendimento).
                    if (!string.IsNullOrWhiteSpace(item.RamalAtendeu)) return item.RamalAtendeu;
                    return string.Empty;

                default:
                    return string.Empty;
            }
        }

        private static string ResolverNome(string ramal)
        {
            try
            {
                var nome = ContatoStorageService.ResolverNomePorNumero(ramal);
                return string.Equals(nome, ramal, StringComparison.OrdinalIgnoreCase) ? ramal : nome;
            }
            catch { return ramal; }
        }

        private static int ParseDuracao(string duracao)
        {
            if (string.IsNullOrWhiteSpace(duracao)) return 0;
            var partes = duracao.Split(':');
            try
            {
                if (partes.Length == 2)
                    return int.Parse(partes[0]) * 60 + int.Parse(partes[1]);
                if (partes.Length == 3)
                    return int.Parse(partes[0]) * 3600 + int.Parse(partes[1]) * 60 + int.Parse(partes[2]);
            }
            catch { }
            return 0;
        }

        private static string FormatarTempo(int totalSegundos)
        {
            if (totalSegundos <= 0) return "—";
            var min = totalSegundos / 60;
            var seg = totalSegundos % 60;
            return min > 0 ? $"{min}m {seg:D2}s" : $"{seg}s";
        }
    }
}
