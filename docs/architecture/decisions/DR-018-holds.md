# DR-018 Holds: reserve now, capture later (bank side)

**Status:** Accepted. Recorded on 2026-11-24, with the change that built it.
**Owner:** Ismail Aslan.

## Context

A ride, a hotel or a rental does not know its final price when the customer agrees to pay. The payee needs to know the money is there, and take only what the service really cost. Paysys had only an immediate payment.

## Decision

- A hold reserves money on the payer's account (`Account.HeldAmount`). The balance does not change; what can be spent is `Balance - HeldAmount`, and every payment is checked against that.
- The payer places a hold through the same two steps as a payment (`POST /api/payments` with `hold`, then the PIN). The rate is fixed when the hold is placed, so the payee knows the most it can take, in its own currency.
- The payee (a merchant's terminal account) captures any amount up to the held amount, or releases the hold. The payer is debited the matching share of the reservation (at least 0.01); the rest is free again at once. A capture is one ordinary transaction with its audit entry.
- A hold nobody captures or releases ends after 7 days. A background sweep (every 60 s, 50 at a time) gives the money back; an overdue hold cannot be captured even before the sweep runs.
- Reservation, capture and release are single conditional updates inside one database transaction with the audit entry, so two calls cannot reserve or spend the same money. The database also refuses a reservation above the balance (`CK_Accounts_HeldAmount_WithinBalance`).
- A hold lives within one bank. A capture does not re-check the card's status (the payer agreed to the amount when the card was usable).

## Consequences

- Gateway: a terminal asks for a hold with `POST /v1/holds`; the gateway asks the bank for a hold payment code (`hold` flag, amount required). A payer who enters such a code can only reserve exactly its amount, whatever they tick. The bank tells the gateway with a signed `Held` message (the hold's id is the event id), and the terminal then captures (`POST /v1/holds/{id}/capture`) or releases (`/release`). The gateway passes these to the bank (`/api/gateway/holds/...`, shared key, and the hold must be on the terminal's own account); it never moves money. A `Held` request is checked with the bank on every read, because the bank also ends a hold (merchant page, seven-day sweep). The gateway adds columns to its own table on start; no migration to run.
- Web: the payer can tick "Reserve this amount instead of paying it" on Send money and sees the result; a Holds page lists holds for both sides, and a merchant takes or releases there. The demo terminal can ask for a hold.
- Not covered by the attack scan (the gateway was added to the workflow in CR-019 and has not been run): the gateway service and its hold calls (tests cover wrong key, another terminal, another account).
- Money held for 7 days is unavailable to the payer for that time; there is no way for the payer to cancel a hold themselves, only the payee releases it.
- The bank part needed a database migration (`20261125000000_AddHolds`), already applied. The gateway part needs none.
