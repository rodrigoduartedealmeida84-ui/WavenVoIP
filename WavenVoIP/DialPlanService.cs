using System;
using System.Linq;

namespace WavenVoIP
{
    public static class DialPlanService
    {
        public static string NormalizarNumero(string numero)
        {
            if (string.IsNullOrWhiteSpace(numero)) return string.Empty;
            return RemoverDuplicacaoSequencial(new string(numero.Where(char.IsDigit).ToArray()));
        }


        public static string RemoverDuplicacaoSequencial(string numero)
        {
            if (string.IsNullOrWhiteSpace(numero)) return string.Empty;
            var n = new string(numero.Where(char.IsDigit).ToArray());
            if (n.Length >= 8 && n.Length % 2 == 0)
            {
                var meio = n.Length / 2;
                var primeira = n.Substring(0, meio);
                var segunda = n.Substring(meio);
                if (primeira == segunda) return primeira;
            }
            return n;
        }

        public static bool EhRamalInterno(string numero)
        {
            var numeroLimpo = NormalizarNumero(numero);
            return numeroLimpo.Length >= 2 && numeroLimpo.Length <= 5;
        }

        // Returns true only for real external phone numbers that can receive WhatsApp messages.
        // Rejects: ramais (<=5 digits), queues, IVR codes, 0800/0300, concatenated, names.
        public static bool IsTelefoneExternoValido(string numero)
        {
            if (string.IsNullOrWhiteSpace(numero)) return false;
            // Must be purely numeric (no letters → not a SIP URI or name)
            var digitos = new string(numero.Where(char.IsDigit).ToArray());
            if (digitos.Length == 0) return false;
            if (!string.Equals(digitos, numero.Trim(), StringComparison.Ordinal))
            {
                // Allow only "+" prefix for international format, reject everything else with letters
                if (!numero.Trim().StartsWith("+") && numero.Any(char.IsLetter)) return false;
            }
            // Deduplicate first (catches concatenated numbers)
            var n = RemoverDuplicacaoSequencial(digitos);
            if (n.Length < 8)  return false; // ramais, queues, IVR codes
            if (n.Length > 15) return false; // still-concatenated or garbage
            if (n.StartsWith("0")) return false; // 0800, 0300, local "0" prefix
            return true;
        }

        public static string AplicarRegraDeDiscagem(string numero, SaidaChamada saida)
        {
            var numeroLimpo = NormalizarNumero(numero);
            if (string.IsNullOrWhiteSpace(numeroLimpo)) throw new InvalidOperationException("Número inválido.");
            if (EhRamalInterno(numeroLimpo)) return numeroLimpo;

            // Regra Almeida/Waven:
            // 1 = Operadora, 2 = WhatsApp TIM, 3 = WhatsApp Vivo.
            // Remove o prefixo salvo no histórico antes de aplicar a saída escolhida,
            // evitando duplicar: 266984671226 -> 66984671226 -> 2 + 66984671226.
            numeroLimpo = RemoverPrefixoDeRota(numeroLimpo);
            return ((int)saida).ToString() + numeroLimpo;
        }

        public static bool TemPrefixoDeRota(string numero)
        {
            var n = NormalizarNumero(numero);
            // Threshold 12: número brasileiro padrão tem no máximo 11 dígitos (DDD 2 + celular 9).
            // Um prefixo de rota (1/2/3) adiciona 1 dígito extra → 12+ indica rota prepended.
            if (n.Length >= 12 && (n.StartsWith("1") || n.StartsWith("2") || n.StartsWith("3")))
                return true;
            // Caso especial: 11 dígitos começando com 1/2/3 cujo 3º dígito é '0'.
            // Números brasileiros NUNCA começam com '0' após o DDD, então n[2]=='0' indica
            // prefixo de rota (ex: "18007021200" = rota '1' + "8007021200" tipo 0800).
            if (n.Length == 11 && (n[0] == '1' || n[0] == '2' || n[0] == '3') && n[2] == '0')
                return true;
            return false;
        }

        public static string RemoverPrefixoDeRota(string numero)
        {
            var n = NormalizarNumero(numero);
            return TemPrefixoDeRota(n) ? n.Substring(1) : n;
        }

        public static SaidaChamada? ObterSaidaPeloPrefixo(string numero)
        {
            var n = NormalizarNumero(numero);
            if (n.Length <= 3) return null;
            return n[0] switch
            {
                '1' => SaidaChamada.Operadora,
                '2' => SaidaChamada.WhatsAppTim,
                '3' => SaidaChamada.WhatsAppVivo,
                _ => null
            };
        }

        public static string NomeSaida(SaidaChamada saida)
        {
            return saida switch
            {
                SaidaChamada.Operadora => "Operadora",
                SaidaChamada.WhatsAppTim => "WhatsApp TIM",
                SaidaChamada.WhatsAppVivo => "WhatsApp Vivo",
                _ => string.Empty
            };
        }

        public static string NomeSaidaPeloPrefixo(string numero)
        {
            var saida = ObterSaidaPeloPrefixo(numero);
            return saida == null ? string.Empty : NomeSaida(saida.Value);
        }
    }
}
