using System.Linq;

namespace WavenVoIP.Services
{
    public static class PhoneNumberNormalizer
    {
        // ── Normalização Brasil ────────────────────────────────────────────────────

        // Remove prefixo 55 Brasil e adiciona nono dígito em celulares antigos.
        // DDD(2) + 8 dígitos iniciando em 6-9 = celular sem nono → adiciona 9.
        // Ramais (≤5 dígitos), fixos e 0800 não são alterados.
        public static string NormalizeBrazilPhone(string numero)
        {
            var digits = SomenteDigitos(numero);
            if (digits.Length == 0) return numero;

            // Remove prefixo 55 de números com 12-13 dígitos
            if ((digits.Length == 12 || digits.Length == 13) && digits.StartsWith("55"))
                digits = digits.Substring(2);

            // Adiciona nono dígito: DDD(2) + 8 dígitos celular antigo → DDD + 9 + 8 dígitos
            if (digits.Length == 10 && !digits.StartsWith("0") && IsCelularSemNono(digits))
                digits = digits.Substring(0, 2) + "9" + digits.Substring(2);

            return digits;
        }

        // Remove country code 55 and route prefix (1/2/3) before dialing.
        public static string NormalizeForDial(string numero)
        {
            var digits = SomenteDigitos(numero);
            if ((digits.Length == 12 || digits.Length == 13) && digits.StartsWith("55"))
                digits = digits.Substring(2);
            return digits.Length > 0 ? digits : numero;
        }

        // Strips country code 55 for display; returns plain digits.
        public static string NormalizeForDisplay(string numero)
        {
            var digits = SomenteDigitos(numero);
            if ((digits.Length == 12 || digits.Length == 13) && digits.StartsWith("55"))
                digits = digits.Substring(2);
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

        private static void Log(string msg)
        {
            try { LogHelper.Write(msg); }
            catch { }
        }
    }
}
