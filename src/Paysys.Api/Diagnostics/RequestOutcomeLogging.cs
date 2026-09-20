using System.Diagnostics;

namespace Paysys.Api.Diagnostics;

// Logs one line for every response with a status of 400 or above.
//
// Handled failures (validation errors, unknown cards, 401/403/429, a bank that can't be
// reached) return a response and used to leave no trace anywhere. This puts every one of
// them on record with the same fields, in one place, including the ones that never reach
// a handler (unauthenticated, no such route). It carries no request or response bodies,
// query strings or headers - only the route pattern, so nothing a caller typed (a card
// number in a token field, a password) can end up in a log line.
//
// Placed after authentication so the user is known, and before authorization/endpoints so
// it observes their final status.
public static class RequestOutcomeLogging
{
    public static IApplicationBuilder UseRequestOutcomeLogging(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            await next();

            var status = context.Response.StatusCode;
            if (status < 400)
                return;

            var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Paysys.Api.Requests");

            // The matched route pattern ("/api/transactions/{accountId:guid}"), not the raw path:
            // the raw path is caller-controlled and may hold identifiers or junk. Requests that
            // matched no route are logged as such, with no path at all.
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no matching route)";
            var user = context.User.FindFirst("sub")?.Value ?? "anonymous";
            var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var level = status switch
            {
                401 or 403 or 429 or 503 => LogLevel.Warning,   // security-relevant, or the system failing to serve
                >= 500 => LogLevel.Error,
                _ => LogLevel.Information,
            };

            log.Log(level,
                "HTTP {Method} {Route} -> {Status} user={UserId} {ElapsedMs}ms trace={TraceId}",
                context.Request.Method, route, status, user, elapsedMs, context.TraceIdentifier);
        });
}
