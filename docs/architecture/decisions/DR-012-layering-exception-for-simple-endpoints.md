# DR-012 Layering exception for simple endpoints

**Status:** Accepted. Recorded on 2026-11-23, after the decision was built.
**Owner:** Ismail Aslan.

## Context

Principle N1 says dependencies run Web, API, BLL, DAL. Registration, card pause/resume/block and bank
administration endpoints use the database context directly.

## Decision

Accepted for endpoints that only check ownership and update a record. The payment path goes through BLL
services.

## Consequences

- No test enforces the layering for these endpoints.
- Business rules added to them later should move into BLL services first. Recorded in as-is.md 12.1.
