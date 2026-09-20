using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Paysys.DAL.Persistence;

// Recognizes a Postgres unique-constraint violation on a specific index, so callers can
// treat "someone else got there first" differently from every other save failure.
// Lives in the DAL because it depends on the database provider.
public static class UniqueViolation
{
    // EF's default name for the unique index on Transaction.IdempotencyKey.
    public const string TransactionIdempotencyKeyIndex = "IX_Transactions_IdempotencyKey";

    // EF's default name for the unique index on Account.PayeeCode.
    public const string AccountPayeeCodeIndex = "IX_Accounts_PayeeCode";

    public static bool IsOn(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: var constraint,
        } && constraint == indexName;
}
