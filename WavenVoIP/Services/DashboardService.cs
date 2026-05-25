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
            var itens = todos
                .Where(i => i.DataHora >= dataInicio && i.DataHora <= dataFim)
                .ToList();

            var recebidas  = itens.Count(i => i.Tipo == TipoHistoricoLigacao.Recebida);
            var realizadas = itens.Count(i => i.Tipo == TipoHistoricoLigacao.Realizada);
            var perdidas   = itens.Count(i => i.Tipo == TipoHistoricoLigacao.Perdida
                                           || i.Tipo == TipoHistoricoLigacao.NaoAtendidaNesseRamal);
            var gravacoes  = itens.Count(i => !string.IsNullOrWhiteSpace(i.GravacaoUrl)
                                           || !string.IsNullOrWhiteSpace(i.GravacaoArquivo));

            var duracoes = itens
                .Where(i => i.Tipo != TipoHistoricoLigacao.Perdida
                         && i.Tipo != TipoHistoricoLigacao.NaoAtendidaNesseRamal)
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
            if (!string.IsNullOrWhiteSpace(item.RamalOrigem))  return item.RamalOrigem;
            if (!string.IsNullOrWhiteSpace(item.RamalDestino)) return item.RamalDestino;
            if (!string.IsNullOrWhiteSpace(item.RamalAtendeu)) return item.RamalAtendeu;
            return string.Empty;
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
