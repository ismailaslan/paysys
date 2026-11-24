namespace Paysys.DAL.Entities;

// A token bucket for one caller (a user, or a client address) under one limit, shared by every server. Tokens is the
// count as of UpdatedAt; the refill since then is worked out when the bucket is next used.
public class RateBucketEntry
{
    public string Scope { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public double Tokens { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
