using System.Globalization;

namespace Paysys.Api.Auth;

// A 429 with a Retry-After header (whole seconds, rounded up, at least 1).
public sealed class RetryAfterResult : IResult
{
    private readonly TimeSpan _retryAfter;
    private readonly string _message;

    public RetryAfterResult(TimeSpan retryAfter, string message)
    {
        _retryAfter = retryAfter;
        _message = message;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(_retryAfter.TotalSeconds));

        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await httpContext.Response.WriteAsJsonAsync(new { message = _message });
    }
}
