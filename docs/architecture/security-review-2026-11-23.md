# Security review: gateway and card status (23 Nov 2026)

Method: a read-only review agent read the gateway, the bank-side gateway code and the card status code, then I checked finding 1 against the code. Nothing was run; findings come from reading. Tests are added as each fix lands.

| # | Severity | Finding | Status |
|---|---|---|---|
| 1 | High | A replayed payment (same idempotency key) enqueues a new gateway event with a fresh id, so another pending request for the same amount can be marked completed | Open |
| 2 | Medium | Event matching is by terminal, amount and currency only, newest request first; not tied to the issued payment code | Open |
| 3 | Medium | Event id recorded before the payment update, so a failure loses the completion | Open |
| 4 | Medium | Card status has no concurrency check, so a bank block can be overwritten, and unblock sets Active even on a card the owner paused | Open |
| 5 | Low | Card status checked once at the start of a transfer, not again before the debit | Open, accepted for now |
| 6 | Low | Out-of-range `X-Timestamp` gives a 500 | Open |
| 7 | Low | One merchant can fill the 5000 ticket slots | Open |
| 8 | Low | No rate limits on terminal and bank paths; 30 MB body limit before authentication; payment-code cap gives a 500 | Open |
| 9 | Low | Denied access to cards and gateway keys is not audited | Open |
| 10 | Low | Card status changes audited by account, not card | Open |
| 11 | Low | Five-terminal limit can be beaten by parallel requests | Open |
| 12 | Info | Smaller notes: event status ignored, event id not scoped per bank, base URL scheme not checked, in-memory event queue drops oldest | Noted |

Found sound by reading: terminal key generation, storage and comparison; bank key and signature checks (constant time, 5-minute window, 8 KB cap); one-use stream tickets; ownership and role checks on card and gateway-key endpoints; card checks before any bank call.

Not reviewed: tokenization, JWT and login (covered by tests and the previous review), infrastructure and hosting settings, dependencies.
