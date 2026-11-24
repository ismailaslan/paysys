# Paysys: target architecture for production

**Status:** target, not built. Draft for the owner's approval, written 24 Nov 2026.
**Scope:** today's build is a demonstration system on the current infrastructure (Render free tier, Neon). This document describes what it would take to run the same system as a production service on a container platform such as Kubernetes or a managed container service. It is not a promise that any of it exists.

## 1. What is ready now

- Both services (bank API, gateway) have a Dockerfile: non-root user, port 10000, all settings from environment variables. They run unchanged in a container on any host, as one instance of each.
- Liveness and readiness are separate. `/healthz` (bank API) and `/health` (gateway) say the process is up and do not touch the database. `/readyz` on both says the service can reach its database (200, or 503 with no detail). Tests cover both.
- The state that must survive a restart, or be seen by every instance, is in the database (DR-020, DR-022): the bank-to-gateway queue, payment codes, amount bindings, the audit chain and its anchor, the sign-in counter, payments waiting for their PIN, the wrong-PIN count, the demo top-up count, the sign-up and denial budgets, and the gateway's stream tickets. CI runs two copies of the API over one database: a payment created on one is confirmed on the other, once.
- The audit chain uses a database advisory lock, so several instances can append to it safely.
- A repeated gateway message is ignored by event id, so resending is safe.
- The background jobs are safe on several instances. The demo bank changes a card only if it is still waiting. The audit anchor only moves forward. The gateway's cleanup deletes old rows. The cost of several instances is repeated work, not wrong data.
- Migrations are plain files. The gateway creates its own schema on start.
- Key rotation for the bank-to-gateway link is supported (DR-020).

## 2. What still blocks more than one instance (found in the code)

- The gateway's live push (SignalR) reaches other instances through Postgres NOTIFY, but only when `Gateway:PushRelay:Enabled` is `true`; it is off by default. It needs a database connection that holds a session (LISTEN): Neon's pooled (`-pooler`) host does not, so the direct host is needed, and a permanent connection may keep a serverless database awake. Tested in CI with two instances; not tried on the deployed services. With it off, the merchant page's status check catches up.
- Forwarded headers: the bank API's rate limiters use the connection's own address. Behind a load balancer every user would look like one address. `ForwardedHeaders:TrustedNetworks` (CIDR list, off when empty) makes the API use the last `X-Forwarded-For` entry, only from those networks (DR-023). The list for the real platform is the owner's to set; not tried on a real proxy. The gateway limits by terminal id, not by address, and is not affected.
- Migrations: a `migrate` workflow applies pending migrations to Neon (on a migration change on main, or by hand), using the owner's secret `NEON_CONNECTION_STRING` (a direct connection, not `-pooler`). It runs in parallel with Render's own deploy unless auto-deploy is off and `RENDER_DEPLOY_HOOK_URL` is set (DR-023). Not run yet: the secret is not set.
- The audit-chain status the admin sees is the newest check by any instance (table `AuditChainChecks`); each instance still runs its own check (DR-023).
- The extra database calls on busy paths (the denial budget runs on every denied request) are not measured.

## 3. Target layout

- **Container platform.** Kubernetes or a managed container service. Two or more instances per service, rolling updates, liveness probe on `/healthz` and `/health`, readiness probe on `/readyz`.
- **Database.** Managed Postgres on a private network only, with automated backups, point-in-time recovery and a standby in a second zone. The recovery targets are the owner's to set.
- **Shared state.** In Postgres (done, DR-022). Redis is not needed for it. The sign-in counter is shared too (the database is the one copy; memory only if the database does not answer). The live push has an optional Postgres relay (see section 2); Redis would be needed only if measurements show Postgres is too slow on a busy path.
- **Jobs.** Audit verification runs on every instance. The demo-bank confirmer exists only outside production. Gateway cleanup and queue delivery stay on every instance.
- **Migrations.** A separate step in the release pipeline, run before the new version starts.
- **Edge.** TLS at the ingress, forwarded headers accepted from the trusted ingress only, request size and rate limits at the edge, a web application firewall.
- **Secrets.** A secret manager, not environment text in a dashboard.
- **Observability.** Central logs, metrics and alerts (errors, queue length, audit-chain failure, failed payments), and an on-call route.
- **Environments.** Development, staging with a copy of the schema, and production.

## 4. Outside the code (needed before real money)

- A licensed bank or payment institution behind Paysys.
- Card numbers: use a certified tokenization provider, or go through a PCI assessment. Today a number is held in memory briefly during tokenization.
- A qualified review of the compliance mapping, a penetration test, and a lawful-basis and retention review (retention is set at 5 years by the owner, 25 Nov 2026).

## 5. Order of work

No time estimates: none have been measured.

1. Set the `migrate` workflow's secret and the trusted networks of the real platform.
2. Switch on the live push relay on a direct database connection and try it with two gateway instances; measure the database load of the shared state.
3. Move to a container platform with two instances in staging; run the scan and load workflows against it.
4. Backups, standby, monitoring, alerts, secret manager.
5. The items in section 4.

Already done (24 Nov 2026): readiness checks on both services; short-lived state moved into the database, with a two-instance test in CI (DR-022).

## 6. Done when

Two instances serve traffic with one stopped and restarted in the middle of a payment, and no payment is lost or doubled; a restore from backup is tested; the scan and load workflows pass on staging; section 4 is closed by the owner.
