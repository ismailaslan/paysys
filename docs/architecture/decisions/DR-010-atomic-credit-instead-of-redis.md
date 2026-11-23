# DR-010 Credit the destination atomically, no Redis

**Status:** Accepted. Recorded on 2026-11-23, after the decision was built.
**Owner:** Ismail Aslan.

## Context

Many payments into one merchant account conflicted on the account's version check (`xmin`), so
concurrent payments to a hot account failed with 409. DR-003 proposed Redis for idempotency,
balance locking and rate limiting.

## Options

1. Add Redis and lock balances there.
2. Credit the destination in SQL (`balance = balance + amount`) inside the audit transaction, and keep
   the version check only on the payer.

## Decision

Option 2 (CR-008). The payer debit keeps the `xmin` check, so two payments cannot spend one balance.
Idempotency stays a unique index on the key. Rate limiting stays in memory. DR-003 is not needed for a
single instance.

## Consequences

- Load run: 200 of 200 payments into one merchant completed, no conflicts, money conserved.
- Rate limits and some state are lost on restart and do not work across several instances. Moving to
  several instances reopens DR-003.
- Evidence: `ConcurrentPaymentsToOneMerchant_AllComplete_AndTheBalanceAddsUp`, the load workflow.
