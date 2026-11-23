# DR-013 Idempotency by a unique key

**Status:** Accepted. Recorded on 2026-11-23, after the decision was built.
**Owner:** Ismail Aslan.

## Context

A repeated or parallel submission must not move money twice.

## Decision

`Transaction.IdempotencyKey` has a unique index. A repeated key returns the stored result (after an
ownership check). When two requests race, the loser reads the winner's row.

## Consequences

- No cache or Redis needed.
- A replay returns the result again, so anything triggered after a payment must be triggered only for a
  new payment. Security review finding 1 (a replay re-sent the gateway event) came from missing this.
- Evidence: idempotency tests in `TransactionProcessingTests`.
