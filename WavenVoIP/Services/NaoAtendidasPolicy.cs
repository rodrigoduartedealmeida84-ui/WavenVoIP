using System;
using System.Collections.Generic;
using System.Linq;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    // v2.5.0 — "Não atendidas" do Histórico: chamadas RECEBIDAS que terminaram sem atendimento.
    // Soma três classificações, que continuam separadas na lista (cada uma com o seu rótulo):
    //   Perdida          = tocou até o fim e ninguém atendeu;
    //   Cliente desistiu = desligou enquanto os ramais ainda tocavam;
    //   Abandono na fila = desligou aguardando na fila.
    // Chamada REALIZADA sem resposta (TipoHistoricoLigacao.NaoAtendida) não entra aqui.
    public enum NaoAtendidaMotivo { Nenhum, Perdida, ClienteDesistiu, AbandonoFila }

    public static class NaoAtendidasPolicy
    {
        public static NaoAtendidaMotivo Motivo(HistoricoLigacaoItem? i)
        {
            if (i == null || i.Tipo != TipoHistoricoLigacao.Perdida) return NaoAtendidaMotivo.Nenhum;
            if (i.EhDesistenciaRapida) return NaoAtendidaMotivo.ClienteDesistiu;
            if (i.EhAbandonoFila) return NaoAtendidaMotivo.AbandonoFila;
            return NaoAtendidaMotivo.Perdida;
        }

        public static bool EhNaoAtendida(HistoricoLigacaoItem? i) => Motivo(i) != NaoAtendidaMotivo.Nenhum;

        public static (int total, int perdidas, int desistiu, int abandono) Contar(IEnumerable<HistoricoLigacaoItem> itens)
        {
            int p = 0, d = 0, a = 0;
            foreach (var i in itens)
            {
                switch (Motivo(i))
                {
                    case NaoAtendidaMotivo.Perdida: p++; break;
                    case NaoAtendidaMotivo.ClienteDesistiu: d++; break;
                    case NaoAtendidaMotivo.AbandonoFila: a++; break;
                }
            }
            return (p + d + a, p, d, a);
        }

        // Canal de retorno de uma chamada não atendida: sempre o mesmo por onde o cliente entrou.
        // 0800 é só de entrada (não existe rota de saída por ele) — o retorno vai pela Operadora.
        // null = canal de entrada desconhecido: quem chama abre o seletor, sem presumir nada.
        public static SaidaChamada? SaidaDeRetorno(string? canalEntrada) => (canalEntrada ?? string.Empty).Trim() switch
        {
            "WhatsApp TIM"  => SaidaChamada.WhatsAppTim,
            "WhatsApp Vivo" => SaidaChamada.WhatsAppVivo,
            "Operadora"     => SaidaChamada.Operadora,
            "0800"          => SaidaChamada.Operadora,
            _               => null
        };

        public static string NomeDoCanal(SaidaChamada saida) => saida switch
        {
            SaidaChamada.WhatsAppTim  => "WhatsApp TIM",
            SaidaChamada.WhatsAppVivo => "WhatsApp Vivo",
            _                         => "Operadora"
        };

        // Numero que a tela de WhatsApp do app recebe (sem 55 e sem prefixo de rota), ou null
        // quando nao e telefone externo (ramal, fila, 0800). E o mesmo criterio do botao
        // WhatsApp do Historico.
        public static string? NumeroParaWhatsApp(string? numero)
        {
            var n = DialPlanService.RemoverPrefixoDeRota(PhoneNumberNormalizer.NormalizeForDial(numero ?? string.Empty));
            return DialPlanService.IsTelefoneExternoValido(n) ? n : null;
        }

        // Destino final da conversa (formato da API: 55 + DDD + numero, com o 9o digito).
        public static string? DestinoWhatsApp(string? numero)
        {
            var n = NumeroParaWhatsApp(numero);
            if (n == null) return null;
            try { return WhatsAppService.NormalizarTelefoneParaEnvio(n); } catch { return null; }
        }

        // (66) 99999-0002 / (66) 3521-0003; fora desses tamanhos devolve so os digitos.
        public static string FormatarTelefone(string? numero)
        {
            var d = new string((numero ?? string.Empty).Where(char.IsDigit).ToArray());
            if (d.StartsWith("55") && d.Length >= 12) d = d[2..];
            return d.Length switch
            {
                11 => $"({d[..2]}) {d[2..7]}-{d[7..]}",
                10 => $"({d[..2]}) {d[2..6]}-{d[6..]}",
                _ => d.Length == 0 ? (numero ?? string.Empty) : d
            };
        }
    }
}
