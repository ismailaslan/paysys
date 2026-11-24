using System.Security.Cryptography;
using System.Text;

namespace Paysys.Api.Auth;

// What two-step sign-in needs besides the codes themselves:
// - the authenticator secret is stored encrypted (AES-GCM) under a key derived from the API's signing key, so a copy
//   of the database alone does not give the secrets. If the signing key is replaced, secrets can no longer be read
//   and each person sets two-step sign-in up again;
// - the sign-in challenge: a signed value, valid for 5 minutes, that proves the password step was passed;
// - recovery codes: random, shown once, stored only as SHA-256 hashes.
public sealed class MfaCrypto
{
    private const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private readonly byte[] _secretKey;
    private readonly byte[] _challengeKey;

    public MfaCrypto(JwtOptions jwt)
    {
        var material = Encoding.UTF8.GetBytes(jwt.SigningKey);
        _secretKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, info: Encoding.ASCII.GetBytes("paysys-totp-secret"));
        _challengeKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, info: Encoding.ASCII.GetBytes("paysys-mfa-challenge"));
    }

    public string Protect(byte[] secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[secret.Length];
        using var aes = new AesGcm(_secretKey, 16);
        aes.Encrypt(nonce, secret, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    // Null when the value cannot be read (altered, or protected under another key).
    public byte[]? Unprotect(string protectedSecret)
    {
        try
        {
            var all = Convert.FromBase64String(protectedSecret);
            if (all.Length <= 28)
                return null;

            var plain = new byte[all.Length - 28];
            using var aes = new AesGcm(_secretKey, 16);
            aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
            return plain;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public string CreateChallenge(Guid userId, DateTimeOffset now)
    {
        var payload = $"{userId:N}.{now.Add(ChallengeLifetime).ToUnixTimeSeconds()}";
        return payload + "." + Sign(payload);
    }

    public bool TryReadChallenge(string? challenge, DateTimeOffset now, out Guid userId)
    {
        userId = Guid.Empty;
        var parts = challenge?.Split('.');
        if (parts is not { Length: 3 })
            return false;

        var payload = parts[0] + "." + parts[1];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(payload)), Encoding.ASCII.GetBytes(parts[2])))
            return false;

        if (!long.TryParse(parts[1], out var expires) || expires < now.ToUnixTimeSeconds())
            return false;

        return Guid.TryParseExact(parts[0], "N", out userId);
    }

    private string Sign(string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(_challengeKey, Encoding.UTF8.GetBytes(payload)));

    public static string[] NewRecoveryCodes(int count = 8) =>
        Enumerable.Range(0, count).Select(_ =>
        {
            var chars = Enumerable.Range(0, 10).Select(_ => RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)]).ToArray();
            return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
        }).ToArray();

    // Case, spaces and the dash do not matter when a code is typed.
    public static string HashRecoveryCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Replace("-", "").Replace(" ", "").ToUpperInvariant())));
}
