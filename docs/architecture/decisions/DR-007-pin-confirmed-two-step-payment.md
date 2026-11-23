# DR-007 Two-step payment confirmed with a PIN

**Status:** Accepted. Recorded on 2026-11-23, after the decision was built.
**Owner:** Ismail Aslan.

## Context

A card token alone should not be enough to move money from a user's account.

## Decision

A payment is created and parked (nothing moves), then confirmed with a 4-digit PIN (CR-007). Three wrong
PINs cancel the pending payment; five lock PIN checks, and the lockout is restored from the audit log
after a restart. A user with a PIN cannot use the one-step endpoint.

## Consequences

- A 4-digit PIN has a small search space: lockout limits online guessing, not an attack on a stolen hash.
- Pending payments and payment codes are lost on a restart.
- No multi-factor authentication (compliance-mapping.md).
