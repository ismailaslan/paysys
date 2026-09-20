using System.Security.Cryptography;
using System.Text;

namespace Paysys.BLL.Services;

// Payee codes look like PAY-7K3M2-92QDF: ten random characters from Crockford's base 32
// (no I, L, O or U, so a code read aloud or off a screen is hard to mistype), which is
// 50 bits. Guessing one is not feasible: unknown lookups are rate-limited to a burst of 10
// and then 1 per second per user, i.e. ~2^50 attempts at 1/s.
public static class PayeeCodes
{
    public const string Prefix = "PAY-";

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int GroupLength = 5;
    private const int CodeLength = GroupLength * 2;

    public static string Generate()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];   // unbiased

        return Format(new string(chars));
    }

    // Accepts what a person might type or paste - any case, stray spaces or dashes, the
    // "PAY" prefix or not - and returns the canonical form. Crockford's look-alikes are
    // forgiven (O reads as 0, I and L as 1). Returns false for anything that could not be a
    // code, so a malformed value never reaches the database or the guess budget.
    public static bool TryNormalize(string? input, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var compact = new string(input.ToUpperInvariant().Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray());

        // Strip the prefix only when that leaves exactly a code's worth of characters, so a
        // code that happens to begin with "PAY" is not mistaken for a prefixed one.
        if (compact.Length == "PAY".Length + CodeLength && compact.StartsWith("PAY", StringComparison.Ordinal))
            compact = compact["PAY".Length..];

        if (compact.Length != CodeLength)
            return false;

        var normalized = new StringBuilder(CodeLength);
        foreach (var c in compact)
        {
            var mapped = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (!Alphabet.Contains(mapped))
                return false;

            normalized.Append(mapped);
        }

        code = Format(normalized.ToString());
        return true;
    }

    // A short fingerprint of a code, for audit reasons: lets repeated attempts at the same
    // wrong code be correlated without storing the value a caller typed.
    public static string Fingerprint(string normalizedCode) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode)))[..16].ToLowerInvariant();

    private static string Format(string ten) => $"{Prefix}{ten[..GroupLength]}-{ten[GroupLength..]}";
}
