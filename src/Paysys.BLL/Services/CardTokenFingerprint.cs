using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Paysys.BLL.Services;

// Turns a "card token" a caller presented into something safe to write to the
// immutable audit log.
//
// The field is easy to misuse: people paste a card NUMBER into it. A hash of a card
// number is not safe to store - card numbers are short and structured, so an
// unsalted hash can be brute-forced back to the number. So only a value shaped like
// a token this system issues (tok_ + 32 hex, i.e. 128 random bits) is fingerprinted;
// anything else is recorded as "unrecognized" plus its length, and nothing about its
// content survives.
public static partial class CardTokenFingerprint
{
    [GeneratedRegex("^tok_[0-9a-f]{32}$")]
    private static partial Regex TokenShape();

    public static string Of(string? presented)
    {
        if (presented is not null && TokenShape().IsMatch(presented))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
            // 16 hex chars is plenty to tell attempts apart and correlate repeats,
            // and is not the token itself.
            return "sha256:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }

        return $"unrecognized(len={presented?.Length ?? 0})";
    }
}
