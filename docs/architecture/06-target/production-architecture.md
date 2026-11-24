# Paysys: target architecture for production

**Status:** target, not built. Draft for the owner's approval, written 24 Nov 2026.
**Scope:** today's build is a demonstration system on the current infrastructure (Render free tier, Neon). This document describes what it would take to run the same system as a production service on a container platform such as Kubernetes or a managed container service. It is not a promise that any of it exists.

## 1. What is ready now

- Both services (bank API, gateway) have a Dockerfile: non-root user, port 10000, all settings from environment variables. They run unchanged in a container on any host, as one instance of each.
- Liveness and readiness are separate. `/healthz` (bank API) and `/health` (gateway) say the process is up and do not touch the database. `/readyz` on both says the service can reach its database (200, or 503 with no detail). Tests cover both.
- The state that must survive a restart is in the database: the bank-to-gateway queue, payment codes, amount bindings, the audit chain and its anchor, the sign-in counter.
- The audit chain uses a database advisory lock, so several instances can append to it safely.
- A repeated gateway message is ignored by event id, so resending is safe.
- The background jobs are safe on several instances. The demo bank changes a card only if it is still waiting. The audit anchor only moves forward. The gateway's cleanup deletes old rows. The cost of several instances is repeated work, not wrong data.
- Migrations are plain files. The gateway creates its own schema on start.
- Key rotation for the bank-to-gateway link is supported (DR-020).

## 2. What blocks more than one instance (found in the code)

- Kept in each server's memory: payments waiting for their PIN, the PIN counter, the registration limiter, the access-denial limiter, the demo top-up limiter, and the gateway's live-stream tickets. A second instance does not see them, so a confirm sent to the other instance would not find its payment.
- The sign-in counter is saved to the database, but each server keeps its own copy, so instances can disagree for a while.
- The gateway's live push (SignalR) works only inside one instance. Several instances need a shared backplane or sticky sessions.
- The audit-chain status shown to the admin is kept per instance. That is correct (each instance checks the chain), but the admin sees the status of whichever instance answers.
- Migrations on the bank API are applied by hand, not on start.
- The bank API's rate limiters use the connection's own address. Behind a load balancer every user would look like one address, so forwarded headers with a trusted proxy list must be configured. The gateway limits by terminal id, not by address, and is not affected.

## 3. Target layout

- **Container platform.** Kubernetes or a managed container service. Two or more instances per service, rolling updates, liveness probe on `/healthz` and `/health`, readiness probe on `/readyz`.
- **Database.** Managed Postgres on a private network only, with automated backups, point-in-time recovery and a standby in a second zone. The recovery targets are the owner's to set.
- **Shared state.** One Redis for pending payments, the PIN, registration, denial and top-up counters, the sign-in counter's live copy, and gateway tickets. The same Redis as the SignalR backplane. This revisits DR-003, which was set aside for a single instance.
- **Jobs.** Audit verification runs on every instance. The demo-bank confirmer exists only outside production. Gateway cleanup and queue delivery stay on every instance.
- **Migrations.** A separate step in the release pipeline, run before the new version starts.
- **Edge.** TLS at the ingress, forwarded headers accepted from the trusted ingress only, request size and rate limits at the edge, a web application firewall.
- **Secrets.** A secret manager, not environment text in a dashboard.
- **Observability.** Central logs, metrics and alerts (errors, queue length, audit-chain failure, failed payments), and an on-call route.
- **Environments.** Development, staging with a copy of the schema, and production.

## 4. Outside the code (needed before real money)

- A licensed bank or payment institution behind Paysys.
- Card numbers: use a certified tokenization provider, or go through a PCI assessment. Today a number is held in memory briefly during tokenization.
- A qualified review of the compliance mapping, a penetration test, and a lawful-basis and retention review (retention is a working figure of 3 years).

## 5. Order of work

No time estimates: none have been measured.

1. Migration step in the pipeline; forwarded headers with a trusted proxy list.
2. Move the in-memory state to Redis; SignalR backplane.
3. Move to a container platform with two instances in staging; run the scan and load workflows against it.
4. Backups, standby, monitoring, alerts, secret manager.
5. The items in section 4.

Already done: readiness checks on both services (24 Nov 2026).

## 6. Done when

Two instances serve traffic with one stopped and restarted in the middle of a payment, and no payment is lost or doubled; a restore from backup is tested; the scan and load workflows pass on staging; section 4 is closed by the owner.
