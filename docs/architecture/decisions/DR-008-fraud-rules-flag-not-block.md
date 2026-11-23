# DR-008 Fraud rules flag, they do not block

**Status:** Accepted. Recorded on 2026-11-23, after the decision was built.
**Owner:** Ismail Aslan.

## Context

Two velocity rules exist: RapidRepeat (another attempt from the same account within 10 seconds) and
HighValueRepeat (two payments of 10,000 or more within a minute).

## Options

1. Block a payment when a rule fires.
2. Complete it and record the flag on the audit entry.

## Decision

Option 2. A false positive must not stop a customer paying, and the rules are not calibrated against real
traffic. Every attempt writes one ledger row, failed ones included.

## Consequences

- A legitimate quick retry is flagged. Nobody is alerted: a flag is only recorded.
- Detection quality is not evidenced, only behaviour (`FraudRuleTests`, 12 tests).
- Blocking, alerts or a review queue are future decisions, with real data.
