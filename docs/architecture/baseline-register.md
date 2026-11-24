# Baseline and approval register

**Owner:** Ismail Aslan. **Updated:** 24 November 2026. The Notion copy is tab 12 of the architecture package.

Nothing is approved yet. Every Signed cell is empty on purpose: only the owner signs, with the date and his initials.

## How a baseline works

1. A document is a candidate baseline when the owner has read it and it is marked Draft for approval.
2. The owner signs it here. The version and date signed become the baseline.
3. After that, a change goes through a change request. The document is updated in the same change, the version is raised, and a line is added here.
4. A document that is not signed stays a draft, even when it is accurate.

## Package documents (Notion)

| Tab | Document | Status as marked | Brought up to the build | Signed |
| --- | --- | --- | --- | --- |
| 1 | Principles | Not re-read on 24 Nov | Not checked | |
| 2 | Statement of Work | Draft for approval | 24 Nov 2026 | |
| 3 | Vision | Draft | 24 Nov 2026 | |
| 4 | Requirements | Not re-read on 24 Nov | Not checked | |
| 5 | Definition | Not re-read on 24 Nov | Not checked; as-is.md updated 24 Nov | |
| 6 | Roadmap | Not re-read on 24 Nov | Section 10 added 24 Nov | |
| 7 | Contract | Draft; two proposed changes need the signature | 24 Nov 2026 | |
| 8 | Compliance | Not re-read on 24 Nov | Fifth re-check 24 Nov | |
| 9 | Change Requests | Living | CR-018 added 24 Nov | not applicable |
| 10 | Repository | Not re-read on 24 Nov | Not checked | |
| 11 | Client Acquisition (B2B) | Not re-read on 24 Nov | Not checked | |

## Repository documents

| File | Status in the file | Signed |
| --- | --- | --- |
| 05-definition/as-is.md | Draft, version 2 | |
| 05-definition/compliance-mapping.md | Re-checked 24 Nov 2026 (a mapping, not an assessment) | |
| 05-definition/retention-policy.md | Proposed 24 Nov 2026, waiting for the owner's retention period | |
| decisions/DR-005, DR-007, DR-010 to DR-021 | Accepted (recorded when built) | |
| security-review-2026-11-23.md | Review record | not applicable |

## What blocks a signature

- Tabs 1, 4, 5, 10 and 11 have not been read against the build since 23 Nov or earlier.
- The financial-record retention period is the owner's to set.
- Compliance cannot be baselined as an assessment (no external review). It can be baselined as a mapping with its gaps listed: the gateway is not in the attack scan, no reconciliation between bank and gateway, no penetration test.
- The Contract changes proposed on 24 Nov take effect only when signed.
