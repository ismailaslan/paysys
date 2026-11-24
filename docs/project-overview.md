# Paysys: project and evidence summary

Status: 23 November 2026. Author and owner: Ismail Aslan (Enterprise Architect). Repository: github.com/ismailaslan/paysys. Live demo: ismailaslan.github.io/paysys.

## What it is

Paysys is a payment processing system built as a demonstration of enterprise architecture and engineering practice. It has banks, merchants and individuals; card tokenization; a payment confirmed with a PIN; settlement with currency conversion; a tamper-evident audit trail; and a separate payment gateway that connects merchant terminals to a bank and notifies the merchant live. It runs on .NET 10 (API, Blazor web client), Postgres, and free-tier hosting. It is a demo: no real money, no real card data, no real customers.

## How it was built

Paysys was designed, scoped and directed by the owner and written with an AI coding assistant (Claude), in a pair-programming loop: the owner decides what to build and change, the assistant implements and pushes, and the owner tests the result himself on the deployed system. The architecture is documented in the repository (TOGAF-style package, decision records, change requests), and every claim below points to a test, a measurement or a documented limit.

## Evidence against seven criteria

| Criterion | What is shown | What is not |
| --- | --- | --- |
| Tokenization | Card numbers are never found in any database column or log line (7 tests, including a search of every table); card handling sits in one project and a test enforces it | Memory and transport are not covered; no formal review. The number is still handled in memory when a card is tokenized |
| Settlement speed | Load run on a shared CI runner: 187.6 payments per second with separate merchants (p95 62 ms), 306.1 per second into one merchant, 200 of 200 completed, money conserved | A floor, not capacity; no production-scale test |
| Auditability | Hash-chained audit log written in the same transaction as the payment; tests show edited, removed or forged entries are detected (10 tests) | The detection anchor for deleted newest entries lives in memory and is lost on restart |
| Fraud detection | Two velocity rules flag without blocking; 12 tests cover both rules, their time windows and failed attempts | Behaviour only. Detection quality is unmeasured (no real traffic); a quick legitimate retry is flagged; nobody is alerted |
| Compliance | A written mapping of existing controls to PCI DSS scope and GDPR basics, with gaps listed | Not an assessment or certification; nobody qualified has reviewed it. No multi-factor authentication, no data export or erasure, no penetration test |
| Integration cost | A second bank is added to the gateway by five settings and no code (6 tests) | A second bank has not been deployed and run live |
| Reliability | Concurrent payments never lose an update (tests and the load run); payer overdraft protection by version check; an uptime check runs on a schedule | Single instance, no failover; free tier sleeps when idle |

## Quality controls

364 automated tests run on every change against a real Postgres (the web pages are rendered in memory by bUnit tests), with secret scanning. A read-only security review of the gateway and card-status code found 12 issues; eleven are fixed and the twelfth is a list of notes; each fix has tests except the 64 KB body limit, and the statuses are listed in the review. The gateway flow was run by hand on the deployed services after the first fixes (23 Nov); the later changes (card re-check, rate limit, cleanup, audit of denied access, terminal limit) are covered by tests; the terminal payment was run by hand again on the deployed services after them.

## What it would take to become a business

A licensed partner (payment institution or bank), because Paysys holds balances; a decision on whether card numbers ever touch the system (the usual route is a certified provider); data export and erasure that work with the append-only audit log; alerting on audit and fraud flags; multi-factor authentication; an external penetration test; and the policies and assessments in the compliance mapping.

## Where to look

`docs/architecture/05-definition/as-is.md` (architecture), `compliance-mapping.md`, `docs/architecture/security-review-2026-11-23.md`, `docs/architecture/decisions/` (decision records).
