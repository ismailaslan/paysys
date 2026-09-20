namespace Paysys.BLL.Exceptions;

public class AccessDenialRateLimitedException : Exception
{
    public Guid UserId { get; }
    public TimeSpan RetryAfter { get; }

    public AccessDenialRateLimitedException(Guid userId, TimeSpan retryAfter)
        : base($"Too many denied requests. Retry after {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} second(s).")
    {
        UserId = userId;
        RetryAfter = retryAfter;
    }
}
