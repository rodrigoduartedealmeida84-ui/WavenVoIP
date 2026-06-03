namespace WavenApi.Services;

public static class PhoneNormalizer
{
    public static string Normalize(string numero)
    {
        var digits = SomenteDigitos(numero);
        if (digits.Length == 0) return numero;

        // Remove prefixo de rota (1/2/3) antes de tratar o código do país
        if (digits.Length >= 13 && digits[0] is '1' or '2' or '3')
        {
            var semRota = digits[1..];
            var normalizado = RemoverPrefixo55(semRota);
            if (normalizado.Length != semRota.Length)
                digits = digits[0] + normalizado;
        }

        // Remove código do país 55 quando resultado é número BR válido (10-11 dígitos)
        digits = RemoverPrefixo55(digits);

        // Adiciona nono dígito em celular antigo: DDD(2) + 8 dígitos onde 3º ∈ [6,9]
        if (digits.Length == 10 && !digits.StartsWith('0') && digits[2] is >= '6' and <= '9')
            digits = string.Concat(digits.AsSpan(0, 2), "9", digits.AsSpan(2));

        return digits;
    }

    private static string RemoverPrefixo55(string digits)
    {
        if (digits.Length is 12 or 13 && digits.StartsWith("55"))
        {
            var sem = digits[2..];
            if (sem.Length is >= 10 and <= 11 && sem[0] != '0')
                return sem;
        }
        return digits;
    }

    private static string SomenteDigitos(string valor) =>
        new(valor.Where(char.IsDigit).ToArray());
}
