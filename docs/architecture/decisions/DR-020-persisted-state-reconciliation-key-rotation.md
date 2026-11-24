# DR-020 Persisted state, redelivery, payer release, required two-step and key rotation

**Status:** Accepted. Recorded on 2026-11-24, with the change that built it (CR-019).

**Scope:** a demonstration system on the current infrastructure (Render free tier, Neon). This decision removes the money-losing consequences of a restart; it does not make the system ready for real money.

## Context

Four things lived only in the server's memory: the queue of messages the bank owes the gateway, the payment codes, the amount a payer must pay after looking up a code, and the audit chain's newest-entry marker. Render restarts the service on every deploy and after it sleeps, so a restart could leave a paid customer looking unpaid at the till, forget the exact-amount rule, and forget which audit entry had been verified.

## Decision

- **Database tables** (migration `20261127000000_PersistInMemoryState`): `GatewayOutbox`, `PaymentCodes`, `PayeeGrants`, `AuditChainAnchors`. The stores keep their methods; with a service scope factory they use the database, without one (unit tests) they use memory with the same rules.
- **Redelivery:** a message is written to `GatewayOutbox` when the payment happens and sent again (2 s, 8 s, 20 s, then every minute) until the gateway accepts it, for up to 24 hours. The gateway already drops an event id it applied, so a second delivery is harmless. This is the reconciliation of the bank's side.
- **Payer release:** the payer can release their own hold (`POST /api/holds/{id}/release`, a button on the Holds page); same rules as the terminal's release.
- **Two-step required:** the setting `Auth:RequireTwoStepRoles` names roles that must use two-step sign-in. The owner decided on 24 Nov 2026 to keep two-step optional for now, so `appsettings.Production.json` sets no roles; to require it, set `Auth__RequireTwoStepRoles__0=admin` (and `__1=bank`) on Render. A person in such a role who has not set it up signs in with a token marked `mfa_setup` that works only for the profile and the two-step setup (15 minutes), then signs in again with a code.
- **Key rotation:** the gateway accepts a previous bank key and webhook secret next to the current ones (`PreviousKeyFromBank`, `PreviousWebhookSecret`), and the bank accepts a previous inbound key (`Gateway:PreviousInboundKey`). Change over: set the previous value on the receiving side, change the sending side, remove the previous value.
- **Attack scan:** `scan.yml` no longer uses `-S` (which made it a passive baseline) and scans the gateway too, from a hand-written description in `gateway/openapi.json`.

## Not done

- No job compares the bank's and the gateway's records beyond the redelivery; a message older than 24 hours is dropped.
- The sign-in counter is also kept in the database (`LoginThrottles`, migration `20261127010000_PersistLoginThrottle`), written through from memory; a database error falls back to memory.
- Still in memory: payments waiting for a PIN, the PIN, registration, access-denial and demo-funds counters, the gateway's stream tickets. A restart resets them; none of them holds money.
- The active scan has not been run; the gateway's scan covers the terminal and bank endpoints from a description, with a terminal key, and the bank link is not exercised in it.
- Someone who can edit the database can edit the audit anchor.
