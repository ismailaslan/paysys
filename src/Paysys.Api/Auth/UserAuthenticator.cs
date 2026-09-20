using Microsoft.AspNetCore.Identity;

namespace Paysys.Api.Auth;

public class UserAuthenticator
{
    private readonly AuthOptions _options;
    private readonly PasswordHasher<DemoUser> _hasher = new();
    private readonly DemoUser _dummyUser = new() { Username = "" };
    private readonly string _dummyHash;

    public UserAuthenticator(AuthOptions options)
    {
        _options = options;

        // Verified against when the username doesn't exist, so an unknown user
        // costs the same hashing work as a wrong password and the response time
        // doesn't reveal which usernames are real.
        _dummyHash = _hasher.HashPassword(_dummyUser, Guid.NewGuid().ToString());
    }

    public DemoUser? Authenticate(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return null;

        var user = _options.Users.FirstOrDefault(u =>
            string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

        if (user is null)
        {
            _hasher.VerifyHashedPassword(_dummyUser, _dummyHash, password);
            return null;
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : user;
    }
}
