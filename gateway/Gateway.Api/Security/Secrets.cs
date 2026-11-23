using System.Security.Cryptography;
using System.Text;

namespace Gateway.Api.Security;

// Keys, hashes and signatures. Nothing here logs or returns a secret it was given.
public static class Secrets
{
    public static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);
    // Letters and digits without the look-alikes (l, I, O, 0, 1), built from ranges.
    private static readonly string KeyAlphabet = string.Concat(
        Enumerable.Range('a', 26).Concat(Enumerable.Range('A', 26)).Concat(Enumerable.Range('2', 8))
            .Select(c => (char)c).Where(c => "lIO".IndexOf(c) < 0));

    // A new terminal key. It is shown once; only its hash is stored.
    public static string NewTerminalKey() => "tk_" + RandomNumberGenerator.GetString(KeyAlphabet, 40);

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    // Constant-time comparison of two secrets of any length.
    public static bool Equal(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    public static string Sign(string secret, string timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    // True when the signature matches and the timestamp is recent, so an old message cannot be replayed.
    public static bool VerifySignature(string secret, string timestamp, string body, string signature, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret) || !long.TryParse(timestamp, out var seconds))
            return false;

        var sent = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if ((now - sent).Duration() > SignatureTolerance)
            return false;

        return Equal(signature, Sign(secret, timestamp, body));
    }
}
