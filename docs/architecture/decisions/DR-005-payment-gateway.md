# DR-005 Payment gateway as a separate project

**Status:** Accepted (separate project); location and database open, see below.
**Date:** 2026-11-23. **Owner:** Ismail Aslan.

## Context

Terminals today are only an extra Business account with a terminal id. A merchant sees
payments by polling every 3 seconds. A real card gateway sits between terminals and banks,
routes to the right bank and pushes results. Paysys is the bank-side API.

## Options

1. Add gateway behaviour inside Paysys.
2. A separate gateway project in front of Paysys.

## Decision

Option 2. The gateway is its own project. Paysys stays the bank-side API and the gateway
calls banks through their API. Phase 1 covers: terminal authentication by API key, routing a
payment request to the terminal's bank, a signed completion message from the bank, and live
notifications to the merchant (SignalR or SSE) in place of polling.

## Consequences

- Paysys needs a small gateway contract: create a payment code for a merchant account, send
  a signed completion message, and mint a stream ticket after its own owner check.
- A payment code carries no amount today, so an optional amount on the code is added.
- Both services keep state in memory, so each runs as one instance; Render free tier sleeps,
  so a catch-up request stays in the design.
- Deferred to phase 2: card-present charging (the terminal sending a card and PIN through
  the gateway).

## Open

- Own repository, or a `gateway/` folder in this repository.
- Own database, or its own tables in the same Neon database.
