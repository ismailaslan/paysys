# DR-014 Card numbers isolated, token never shown

**Status:** Accepted. Recorded on 2026-11-23, after the decision was built.
**Owner:** Ismail Aslan.

## Context

Card numbers must not be stored, logged or spread through the code. The first UI showed the token once
after a card was added or issued, though nothing in the app used it.

## Decision

- Only `Paysys.Tokenization` handles the number; a test lists the few files allowed to name it.
- The number and CVV are shown once, only for an issued demo card. They are never stored.
- The token is not shown to users. Send money picks a card from a list.

## Consequences

- Evidence: `TokenizationEvidenceTests`, `CardIsolationTests`.
- The number is still processed in memory at tokenization, so real cards would put that endpoint in PCI
  scope (compliance-mapping.md).
- Isolation is by code structure, not by process or network separation.
