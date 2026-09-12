# Paysys — Project Context for Claude Code

## What this is
Paysys is a payment processing system (tokenization, transaction handling, 
settlement) built as a portfolio/demonstration project. It replaces an 
earlier attempt (LavuayaApi) — this is a from-scratch rebuild on the same stack.

## Stack
- Frontend: Blazor WASM
- Backend: ASP.NET Core Web API
- Database: Neon Postgres (serverless Postgres)
- ORM: Entity Framework Core

## Architecture principles
- Separate concerns cleanly: API layer / business logic / data access — no 
  business logic in controllers.
- Anything touching card data, tokens, or money movement must be isolated 
  into its own service with no shortcuts — no logic embedded directly in 
  endpoints for these paths.
- Prefer explicit, auditable code over clever abstractions. This is a 
  finance-adjacent system; traceability matters more than brevity.

## Benchmark criteria (finish-line checklist — not MVP gate)
The system should eventually satisfy these seven, in priority order:
1. Tokenization security
2. Settlement speed
3. Auditability (can every transaction be traced end-to-end?)
4. Compliance (PCI-DSS-aware design even if not certified)
5. Fraud detection
6. Uptime / reliability
7. Integration cost (how easy for a third party to integrate against this API)

## Working rules for Claude Code
- Do NOT modify or delete existing code without asking first and explaining why.
- Before writing code for a new feature, output a short plan (files to 
  create/modify, order of operations) and wait for confirmation.
- One unit of work per task — don't bundle unrelated changes into one diff.
- Flag any assumption you're making explicitly, rather than silently picking 
  a default.
- For anything touching tokenization, authentication, or transaction 
  integrity: write the code, then separately explain what could go wrong 
  with it (edge cases, failure modes) before moving on.

## Current status
Empty repo. No existing code. First task is project scaffolding.