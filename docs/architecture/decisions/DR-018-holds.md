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

- Not done yet: holds through the payment gateway (`/v1/holds`, hold kind on payment codes) and the payer's and merchant's web screens. The API is complete; the web does not show holds yet.
- Money held for 7 days is unavailable to the payer for that time; there is no way for the payer to cancel a hold themselves, only the payee releases it.
- Applying this change needs a database migration (`20261125000000_AddHolds`).
