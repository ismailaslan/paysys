using System.Net;
using System.Net.Http.Headers;

namespace Paysys.Web.Auth;

public class AuthTokenHandler : DelegatingHandler
{
    private readonly AuthState _auth;

    public AuthTokenHandler(AuthState auth)
    {
        _auth = auth;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _auth.Token;
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken);

        // A 401 on a request that carried a token means the token was rejected
        // (expired or invalid) - drop it so the layout sends the user back to sign in.
        // A 401 on the login call itself carries no token, so wrong passwords don't land here.
        if (response.StatusCode == HttpStatusCode.Unauthorized && token is not null)
            _auth.SignOut();

        return response;
    }
}
