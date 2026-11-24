# DR-023: Closing the single-instance gaps

Status: Accepted, 24 Nov 2026 (recorded when built). Scope: a demonstration system on the current infrastructure (Render free tier, Neon). Change request: CR-022.

## Context

After DR-022 the remaining things that tied the system to one instance, or that the documents listed as gaps, were: no way to end all of a person's sessions, rate limits that would count the proxy as one user, migrations run by hand, an audit status that differed per instance, ticket limits that two servers could pass, large live pushes that were not relayed, and nothing that compared the bank's record of what it sent the gateway with the gateway's record of what it applied.

## Decisions

- **Sign out everywhere.** Each user has `TokenVersion` (column on `Users`, migration `20261127040000_AddTokenVersion`). Every token carries a stamp made from the password hash and that number (version 0 gives the old stamp, so existing sessions stay valid). `POST /api/profile/sign-out-everywhere` asks for the password (the same lockout as sign-in), raises the number with one `UPDATE ... TokenVersion + 1`, and writes an audit entry without the password. All the person's tokens stop working at once, including the one that asked. Profile page: a button under Password.
- **Forwarded headers.** `ForwardedHeaders:TrustedNetworks` (CIDR list). Empty (the default) keeps the old rule: the connection's own address, the header never read. Set, the API uses the last `X-Forwarded-For` entry (`ForwardLimit = 1`, the one the trusted proxy added), and only when the connection comes from a listed network. A value that does not parse stops the API at startup.
- **Migrations.** Workflow `migrate`: on a change under `Migrations/` on main, or by hand; needs the owner's secret `NEON_CONNECTION_STRING`; stops without it. Optional `RENDER_DEPLOY_HOOK_URL` asks Render to deploy after the migration (for that, Render's auto-deploy has to be off). Without the hook it runs beside the deploy; every migration so far only adds things, which the running version tolerates.
- **Audit status.** Table `AuditChainChecks` (one row, migration `20261127050000_AddAuditChainCheck`) holds the newest check by any instance; an older check never replaces a newer one. The admin endpoint reports the newer of the database row and the instance's own result; if the database cannot be read, the instance's own.
- **Reconciliation.** The gateway answers `POST /v1/bank/events/known` (bank key, at most 200 ids) with which event ids it has applied. The bank's `GatewayReconciliationService` (every 10 minutes) checks the last day's messages: a message the bank marked delivered that the gateway does not know is queued again (the gateway ignores a repeated id); a message the bank holds as undelivered that the gateway knows is marked delivered. The notifier now names (first 50) the transaction ids it drops after 24 hours.
- **Ticket limits and large pushes** are in DR-022.

## Not done

- Nothing is resent after 24 hours: a payment whose message was dropped is named in the log, not repaired.
- Reconciliation compares recent messages only, not the history of all payments.
- None of this was tried on the deployed services. The two new columns/tables need SQL on Neon before the new API starts (given to the owner).
