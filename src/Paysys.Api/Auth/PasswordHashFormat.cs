namespace Paysys.Api.Auth;

// A shape check for ASP.NET Core Identity PasswordHasher strings, used by the config user
// importer so a hash that login cannot read never reaches the Users table (review finding 2).
//
// It checks only what PasswordHasher itself reads first: the string is base64, and its first
// byte is a known format marker (0x00 = Identity V2, 0x01 = Identity V3). It does not prove
// the hash is complete or correct; a hash that passes can still fail every login.
public static class PasswordHashFormat
{
    private const byte IdentityV2Marker = 0x00;
    private const byte IdentityV3Marker = 0x01;

    public static bool IsWellFormed(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
            return false;

        // Base64 never decodes to more bytes than 3/4 of its length.
        var buffer = new byte[(hash.Length * 3 / 4) + 3];
        if (!Convert.TryFromBase64String(hash, buffer, out var written) || written < 1)
            return false;

        return buffer[0] is IdentityV2Marker or IdentityV3Marker;
    }
}
