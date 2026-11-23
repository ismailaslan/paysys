using System.Threading.RateLimiting;

namespace Gateway.Api.Security;

// Limits how often a terminal can call the gateway (security review finding 8). Two limits apply to the
// terminal paths (/v1/payments...): one per terminal id, and one for all terminals together, so a flood of
// made-up ids cannot get around the first.
//
// The partition is the X-Terminal-Id header, not the client address: behind the host's proxy every request
// shows the proxy's address, and trusting a forwarded address is a decision the Paysys Api also leaves
// alone. The cost is that someone who knows a terminal id can use up its allowance; the terminal then
// waits a minute.
public static class TerminalRateLimit
{
    public const int PerTerminalPerMinute = 60;
    public const int AllTerminalsPerMinute = 600;

    public static PartitionedRateLimiter<HttpContext> Create(
        int perTerminal = PerTerminalPerMinute, int overall = AllTerminalsPerMinute)
    {
        return PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!IsTerminalPath(context.Request.Path))
                    return RateLimitPartition.GetNoLimiter("not-a-terminal-path");

                var id = context.Request.Headers["X-Terminal-Id"].ToString();
                if (id.Length > 50)
                    id = id[..50];

                return RateLimitPartition.GetFixedWindowLimiter("terminal:" + id, _ => Window(perTerminal));
            }),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsTerminalPath(context.Request.Path)
                    ? RateLimitPartition.GetFixedWindowLimiter("all-terminals", _ => Window(overall))
                    : RateLimitPartition.GetNoLimiter("not-a-terminal-path")));
    }

    public static bool IsTerminalPath(PathString path) =>
        path.StartsWithSegments("/v1/payments", StringComparison.OrdinalIgnoreCase);

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };
}
