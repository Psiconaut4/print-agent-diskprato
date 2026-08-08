namespace PrintAgent.Transport;

/// <summary>
/// Normaliza o código de pareamento digitado pelo lojista antes de mandar
/// para <c>POST /pair</c> (§6.1): maiúsculo, sem espaços/hífens, e mapeia
/// `O`→`0`, `I`→`1` — o alfabeto Crockford base32 usado pelo backend não tem
/// I/L/O/U, então essas duas confusões visuais comuns são corrigidas em vez
/// de deixar a API recusar com PRINT_AGENT_PAIRING_CODE_INVALID.
/// </summary>
public static class PairingCodeNormalizer
{
    public static string Normalize(string rawCode)
    {
        ArgumentNullException.ThrowIfNull(rawCode);

        Span<char> buffer = rawCode.Length <= 64 ? stackalloc char[rawCode.Length] : new char[rawCode.Length];
        var len = 0;

        foreach (var ch in rawCode)
        {
            if (ch is ' ' or '-') continue;

            var upper = char.ToUpperInvariant(ch);
            upper = upper switch
            {
                'O' => '0',
                'I' => '1',
                _ => upper,
            };

            buffer[len++] = upper;
        }

        return new string(buffer[..len]);
    }
}
