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

            // Adiciona nono dígito: DDD(2) + 8 dígitos celular antigo.
            // Só com DDD existente: completar o 9 num DDD que não existe (ex.: "2597268679",
            // digitado com um dígito a menos) disfarçava o erro de digitação como um celular
            // aparentemente válido — "25997268679".
            if (digits.Length == 10 && !digits.StartsWith("0") && IsCelularSemNono(digits) &&
                DddExiste(digits.Substring(0, 2)))
                digits = digits.Substring(0, 2) + "9" + digits.Substring(2);

            return digits;
        }

        // ── DDD ───────────────────────────────────────────────────────────────────

        // DDDs em uso no Brasil (plano de numeração da Anatel).
        private static readonly System.Collections.Generic.HashSet<string> _dddsBrasil = new()
        {
            "11","12","13","14","15","16","17","18","19",
            "21","22","24","27","28",
            "31","32","33","34","35","37","38",
            "41","42","43","44","45","46","47","48","49",
            "51","53","54","55",
            "61","62","63","64","65","66","67","68","69",
            "71","73","74","75","77","79",
            "81","82","83","84","85","86","87","88","89",
            "91","92","93","94","95","96","97","98","99"
        };

        public static bool DddExiste(string ddd) => ddd != null && _dddsBrasil.Contains(ddd);

        /// <summary>
        /// DDD de um número nacional já sem prefixo de rota e sem DDI (10 ou 11 dígitos, não
        /// iniciado por 0). Vazio quando o número não tem esse formato (ramal, 0800, número
        /// local sem DDD, internacional) — nesses casos não há DDD para validar.
        /// </summary>
        public static string ExtrairDdd(string numeroNacional)
        {
            var d = SomenteDigitos(numeroNacional);
            return (d.Length == 10 || d.Length == 11) && d[0] != '0' ? d.Substring(0, 2) : string.Empty;
        }

        /// <summary>True quando o número tem formato nacional com DDD e esse DDD não existe.</summary>
        public static bool TemDddInexistente(string numeroNacional)
        {
            var ddd = ExtrairDdd(numeroNacional);
            return ddd.Length == 2 && !DddExiste(ddd);
        }

        /// <summary>
        /// Formata um número nacional para conferência visual: "(55) 99726-8679" ou
        /// "(66) 3199-8716". Qualquer outro formato volta só com os dígitos.
        /// </summary>
        public static string FormatarNacional(string numeroNacional)
        {
            var d = SomenteDigitos(numeroNacional);
            if (ExtrairDdd(d).Length == 0) return d;
            var local = d.Substring(2);
            return $"({d.Substring(0, 2)}) {local.Substring(0, local.Length - 4)}-{local.Substring(local.Length - 4)}";
        }

        // Number as it will actually be dialed: mesma normalização de NormalizeBrazilPhone
        // (remove 55, adiciona 9º dígito em celular antigo). Antes desta correção (v2.4.0),
        // NormalizeForDial só removia o 55 e NUNCA adicionava o 9º dígito — enquanto
        // NormalizeBrazilPhone (usado para identificação/match de contatos) já fazia isso.
        // Essa divergência era a causa raiz de números de celular sem o 9 chegarem intactos
        // até a discagem (Operadora não completa a ligação) mesmo com o contato/histórico já
        // reconhecendo o número corretamente. Ver PhoneNumberNormalizer_v240_tests no
        // harness de testes para os casos cobertos.
        public static string NormalizeForDial(string numero) => NormalizeBrazilPhone(numero);

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
