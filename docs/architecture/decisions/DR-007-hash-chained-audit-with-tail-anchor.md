# DR-007 Hash-chained audit log with a tail anchor

**Status:** Accepted. Recorded on 2026-11-23, after the decision was built.
**Owner:** Ismail Aslan.

## Context

Every state change must leave a record that cannot be altered or removed unnoticed, and the record
must be written in the same database transaction as the money movement.

## Options

1. A plain audit table.
2. A hash chain (each entry holds the previous entry's hash), appended under a Postgres advisory lock.
3. Option 2 plus an anchor: the newest entry seen by the last clean check.

## Decision

Option 3 (CR-002, CR-011). A foreign key on `PreviousEntryId` makes the database refuse deleting a middle
entry. Verification catches edited entries, broken links and forged replacements. The anchor, kept in
memory by the background check, catches deletion of the newest entries between runs.

## Consequences

- Every audit write is serialised by the lock, which caps throughput (measured, see as-is.md).
- The anchor is lost on a restart, and verification loads the whole table. The newest entry's id and
  hash are in the admin status so they can be recorded outside the database.
- Evidence: `AuditChainTests` (10 tests, real Postgres).
