using System;
using System.Collections.Generic;
using System.Linq;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    // v2.5.0 — regras de AVISO de chamada perdida, separadas do registro no Histórico.
    //
    // Desde a 2.5.0 cada ligação real tem o próprio registro (identidade = linkedid): uma
    // perdida continua no Histórico e nos indicadores mesmo que o cliente ligue de novo e seja
    // atendido. Antes, a perdida era apagada do Histórico nesses casos — e era isso, de
    // tabela, que evitava o popup. Estas regras mantêm o mesmo comportamento de aviso sem
    // apagar nada:
    //  • perdida cujo número já foi atendido depois (o cliente retornou e um operador atendeu,
    //    ou o operador ligou de volta e a chamada completou) não gera popup nem pendência;
    //  • várias perdidas do mesmo número em sequência geram um único popup — as demais contam
    //    no indicador, sem abrir outra janela.
    public static class MissedCallPolicy
    {
        // Mesmo intervalo que a limpeza antiga usava para tratar perdidas seguidas como uma só.
        public static readonly TimeSpan JanelaMesmoNumero = TimeSpan.FromSeconds(180);

        public static string NumeroChave(string? numero) =>
            PhoneNumberNormalizer.NormalizeBrazilPhone(
                DialPlanService.RemoverPrefixoDeRota(new string((numero ?? string.Empty).Where(char.IsDigit).ToArray())));

        // A perdida já foi resolvida por uma ligação POSTERIOR com o mesmo número?
        public static bool FoiAtendidaDepois(HistoricoLigacaoItem perdida, IEnumerable<HistoricoLigacaoItem> itens)
        {
            var chave = NumeroChave(perdida.Numero);
            if (chave.Length < 8) return false;

            foreach (var i in itens)
            {
                if (ReferenceEquals(i, perdida) || i.DataHora <= perdida.DataHora) continue;
                var falouComOperador = i.EhRecebidaPorOperador || i.Tipo == TipoHistoricoLigacao.Realizada;
                if (!falouComOperador) continue;
                if (string.Equals(NumeroChave(i.Numero), chave, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Já houve popup para este número há pouco? (ultimosAvisos: número -> hora do último popup)
        public static bool PopupRecenteParaNumero(string? numero, IReadOnlyDictionary<string, DateTime> ultimosAvisos, DateTime agora)
        {
            var chave = NumeroChave(numero);
            return chave.Length >= 8 &&
                   ultimosAvisos.TryGetValue(chave, out var quando) &&
                   agora - quando < JanelaMesmoNumero;
        }
    }
}
