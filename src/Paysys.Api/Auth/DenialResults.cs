using System.Globalization;
using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;

namespace Paysys.Api.Auth;

public static class DenialResults
{
    // Records the denial, then answers 403 - unless the user is over their denial
    // budget, in which case nothing is recorded and the answer is 429 instead.
    public static async Task<IResult> ForbiddenAsync(
        TransactionAuditLogService audit, Guid userId, string actionType, Guid accountId, string message)
    {
        try
        {
            await audit.RecordAccessDeniedAsync(userId, actionType, accountId);
        }
        catch (AccessDenialRateLimitedException ex)
        {
            return TooManyRequests(ex);
        }

        return Results.Json(new { message }, statusCode: StatusCodes.Status403Forbidden);
    }

    public static IResult TooManyRequests(AccessDenialRateLimitedException ex) => new TooManyRequestsResult(ex);

    private sealed class TooManyRequestsResult : IResult
    {
        private readonly AccessDenialRateLimitedException _exception;

        public TooManyRequestsResult(AccessDenialRateLimitedException exception)
        {
            _exception = exception;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(_exception.RetryAfter.TotalSeconds));

            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            await httpContext.Response.WriteAsJsonAsync(new { message = _exception.Message });
        }
    }
}
