# DR-009 Banks are configured at the gateway, not registered

**Status:** Accepted. Recorded on 2026-11-23, after the decision was built.
**Owner:** Ismail Aslan.

## Context

The gateway must know which banks it talks to, where they are, and which keys and signing secret belong
to each.

## Options

1. Banks register themselves with the gateway (database, API).
2. Banks are settings: `Gateway__Banks__N__Code`, `BaseUrl`, `KeyFromBank`, `KeyToBank`, `WebhookSecret`.

## Decision

Option 2. Addresses and keys are trusted because only the operator can set them, which also avoids the
SSRF risk that open registration of URLs brings. The bank code is a label and whitelist entry; security
rests on the keys and the HMAC signature.

## Consequences

- Adding a bank is five settings and no code (`MultiBankTests`), plus the matching variables on that
  bank's own API instance. A change needs a redeploy.
- Not shown: a second bank deployed and run live.
- Secrets live in the host's environment variables and must be rotated by hand.
