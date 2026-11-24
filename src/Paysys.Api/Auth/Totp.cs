using System.Security.Cryptography;
using System.Text;

namespace Paysys.Api.Auth;

// Time-based one-time passwords as authenticator apps make them (RFC 6238, HMAC-SHA1, 6 digits, 30 seconds).
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

#pragma warning disable CA5350 // SHA-1 is what RFC 6238 and every authenticator app use for this.
    public static string Code(byte[] secret, long step)
    {
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counter);

        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }
#pragma warning restore CA5350

    // True when the code is right for the current step or one either side (clock drift), and that step is newer
    // than the last one used: a code cannot be used twice.
    public static bool TryVerify(byte[] secret, string? code, DateTimeOffset now, long? lastStep, out long matchedStep)
    {
        matchedStep = 0;
        if (code is null || code.Length != Digits || !code.All(char.IsAsciiDigit))
            return false;

        var given = Encoding.ASCII.GetBytes(code);
        var current = StepAt(now);
        var found = false;

        // Every step is compared, so the time taken does not show which one matched.
        for (var step = current - 1; step <= current + 1; step++)
        {
            var match = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), given);
            if (match && (lastStep is null || step > lastStep) && !found)
            {
                found = true;
                matchedStep = step;
            }
        }

        return found;
    }

    public static string ToBase32(byte[] data)
    {
        var text = new StringBuilder();
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);

        return text.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
                throw new FormatException("Not a base32 character.");

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }

            buffer &= (1 << bits) - 1;
        }

        return bytes.ToArray();
    }
}
