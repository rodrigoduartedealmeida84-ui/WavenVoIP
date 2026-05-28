using System.Linq;

namespace WavenVoIP.Services
{
    public static class PhoneNumberNormalizer
    {
        // ── Normalização Brasil ────────────────────────────────────────────────────

        // Remove prefixo 55 (código do país) quando aplicável e adiciona nono dígito
        // em celulares antigos.
        // Regras v1.2.14:
        //   • Remove +, espaços, (, ), *, .  (via SomenteDigitos)
        //   • Remove prefixo de rota (1/2/3) ANTES de verificar 55
        //   • Remove 55 APENAS quando o resultado seria número BR válido (10-11 dígitos, sem 0 inicial)
        //   • Ramais internos (≤5 dígitos) e internacionais não-BR NÃO são alterados
        //   • Adiciona nono dígito em celular antigo DDD+8 onde 3º char ∈ [6,9]
        public static string NormalizeBrazilPhone(string numero)
        {
            var digits = SomenteDigitos(numero);
            if (digits.Length == 0) return numero;

            // Remove prefixo de rota 1/2/3 antes da normalização de país
            // (evita 1556684671226 → sem remover prefixo → tentar remover 55 errado)
            if (digits.Length >= 13 && (digits[0] == '1' || digits[0] == '2' || digits[0] == '3'))
            {
                var semRota = digits.Substring(1);
                var normalizado = RemoverPrefixo55SeBrasileiro(semRota);
                if (normalizado.Length != semRota.Length)
                    digits = digits[0] + normalizado;
                // else: sem prefixo 55 após a rota — mantém como está
            }

            // Remove prefixo 55 em números 12-13 dígitos sem prefixo de rota
            digits = RemoverPrefixo55SeBrasileiro(digits);

            // Adiciona nono dígito: DDD(2) + 8 dígitos celular antigo
            if (digits.Length == 10 && !digits.StartsWith("0") && IsCelularSemNono(digits))
                digits = digits.Substring(0, 2) + "9" + digits.Substring(2);

            return digits;
        }

        // Remove country code 55 before dialing. Handles route prefix (1/2/3) + 55.
        public static string NormalizeForDial(string numero)
        {
            var digits = SomenteDigitos(numero);
            if (digits.Length == 0) return numero;

            // Route prefix + 55: remove 55, preserve route digit
            if (digits.Length >= 13 && (digits[0] == '1' || digits[0] == '2' || digits[0] == '3'))
            {
                var semRota = digits.Substring(1);
                var normalizado = RemoverPrefixo55SeBrasileiro(semRota);
                if (normalizado.Length != semRota.Length)
                    return digits[0] + normalizado;
                return digits;
            }

            return RemoverPrefixo55SeBrasileiro(digits);
        }

        // Strips country code 55 for display; returns plain digits.
        public static string NormalizeForDisplay(string numero)
        {
            var digits = SomenteDigitos(numero);
            digits = RemoverPrefixo55SeBrasileiro(digits);
            return digits.Length == 0 ? numero : digits;
        }

        // Returns all digits, no other transformation.
        public static string NormalizeForSearch(string numero)
            => SomenteDigitos(numero);

        // ── Predicates ────────────────────────────────────────────────────────────

        // Ramal interno: 2-5 dígitos all-numeric.
        public static bool IsExtension(string numero)
        {
            var n = SomenteDigitos(numero);
            return n.Length >= 2 && n.Length <= 5;
        }

        // Telefone externo válido para WhatsApp/ligação: ≥8 dígitos, não começa com 0, não é ramal.
        public static bool IsExternalPhone(string numero)
        {
            var n = NormalizeBrazilPhone(numero);
            if (n.Length < 8 || n.Length > 13) return false;
            if (n.StartsWith("0")) return false;
            if (IsExtension(n)) return false;
            return true;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        // Remove 55 (código do país BR) quando:
        //   - O número tem 12 ou 13 dígitos totais
        //   - Começa com "55"
        //   - O resultado (sem 55) tem 10-11 dígitos e não começa com "0"
        //   - Isso garante que não afeta ramais, internacionais não-BR ou números curtos
        private static string RemoverPrefixo55SeBrasileiro(string digits)
        {
            if (digits.Length >= 12 && digits.Length <= 13 && digits.StartsWith("55"))
            {
                var semPrefixo = digits.Substring(2);
                // Resultado deve ser número BR válido: 10-11 dígitos, não começar com 0
                if (semPrefixo.Length >= 10 && semPrefixo.Length <= 11 && semPrefixo[0] != '0')
                    return semPrefixo;
            }
            return digits;
        }

        // DDD + 8 dígitos onde 3º dígito ∈ [6,9] = celular antigo sem nono dígito.
        private static bool IsCelularSemNono(string digits10)
        {
            if (digits10.Length != 10) return false;
            var terceiro = digits10[2];
            return terceiro >= '6' && terceiro <= '9';
        }

        private static string SomenteDigitos(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return string.Empty;
            return new string(valor.Where(char.IsDigit).ToArray());
        }
    }
}
