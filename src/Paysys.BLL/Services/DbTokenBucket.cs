using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

// A token bucket kept in the database, so every server draws from the same one. One statement takes the token or
// refuses, so two servers cannot both take the last one. A bucket that is not there yet starts full.
//   Tokens now = the stored count + (seconds since it was stored / seconds per token), at most the burst.
public static class DbTokenBucket
{
    // Takes one token. Returns null when one was taken, otherwise how long until one is available.
    public static TimeSpan? TryTake(PaysysDbContext db, string scope, string key, int burst, TimeSpan refillEvery)
    {
        var now = DateTimeOffset.UtcNow;
        var refill = refillEvery.TotalSeconds;
        var args = new (string, object?)[]
        {
            ("scope", scope), ("key", key), ("burst", (double)burst), ("first", (double)(burst - 1)),
            ("refill", refill), ("now", now),
        };

        var taken = RawSql.Scalar(db, """
            INSERT INTO "RateBuckets" ("Scope", "Key", "Tokens", "UpdatedAt")
            VALUES (@scope, @key, @first, @now)
            ON CONFLICT ("Scope", "Key") DO UPDATE SET
                "Tokens" = LEAST(@burst, "RateBuckets"."Tokens" + GREATEST(0, EXTRACT(EPOCH FROM (@now - "RateBuckets"."UpdatedAt")))::double precision / @refill) - 1,
                "UpdatedAt" = @now
            WHERE LEAST(@burst, "RateBuckets"."Tokens" + GREATEST(0, EXTRACT(EPOCH FROM (@now - "RateBuckets"."UpdatedAt")))::double precision / @refill) >= 1
            RETURNING "Tokens"
            """, args);

        if (taken is not null)
            return null;

        var current = RawSql.Scalar(db, """
            SELECT LEAST(@burst, "Tokens" + GREATEST(0, EXTRACT(EPOCH FROM (@now - "UpdatedAt")))::double precision / @refill)
            FROM "RateBuckets" WHERE "Scope" = @scope AND "Key" = @key
            """, args);

        var tokens = current is null ? 0d : Convert.ToDouble(current);
        var seconds = Math.Max(1d, Math.Ceiling((1d - tokens) * refill));
        return TimeSpan.FromSeconds(seconds);
    }
}
