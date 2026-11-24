# Data retention (proposed)

**Status:** Proposed on 2026-11-24 for the owner's approval. Not yet a decision record. Paysys is a demonstration system with no real money; this is the rule it would follow and the rule a real service would start from.

| Data | Kept | Removed when |
| --- | --- | --- |
| Username, password hash, PIN hash | While the account exists | The person deletes the account (the row is anonymised at once) |
| Email and phone (optional, unverified) | While the account exists, or until the person clears them | The person clears them or deletes the account |
| Card tokens (token, last four, brand, expiry) | While the account exists | Cards are closed on deletion; the closed record stays with the account |
| Accounts, payments, holds | Financial records: kept after deletion without the person's name | 3 years (owner's working figure, 24 Nov 2026; may change, and a real business is bound by financial-record law) |
| Audit log | Append-only and hash-chained, so entries are not edited or removed; entries carry an internal id, not a name | Not removed; after erasure the id points at an anonymous row |
| Gateway payment requests and processed-event records | 30 days | Deleted automatically by the gateway |
| API logs | Hosted by Render; no application-level rule | Not defined |

Open decisions for the owner: whether unused accounts expire (and after how long), and a log retention period.
