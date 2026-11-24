# DR-021 Holds removed

**Status:** Accepted. Recorded on 2026-11-24 on the owner's instruction ("Remove").
**Owner:** Ismail Aslan.

## Context

Holds (DR-018) reserved money on a payer's account so a payee could take part of it later. The owner's view: reserving money is the bank's job (a real bank calls it an authorization or provision on its own side), so Paysys, which stands in for the payment layer in front of a bank, should not carry it.

## Decision

Remove the whole feature: the bank API's hold service and endpoints, the gateway's hold endpoints and the hold fields on its payment requests, the Holds page, the Hold option on Send money and on the demo terminal, the hold flag on payment codes and on the amount a payer must pay, `Account.HeldAmount` and the spendable-amount rule that used it, and the tests that covered them. The payer's release of their own hold (CR-019) went with it.

## Data

Migration `20261127020000_RemoveHolds` drops the `Holds` table and the `HeldAmount`, `PayeeGrants.Hold` and `PaymentCodes.Hold` columns. Reserved money was never taken out of `Balance` (the spendable amount was the balance less `HeldAmount`), so dropping the column gives any still-open reservation back to the payer; nothing was paid to a payee for an open hold. The gateway's own table keeps its old columns (`Kind`, `HoldRef`, `CapturedAmount`); the code no longer reads or writes them. Old audit entries named `Hold.*` stay in the chain unchanged.

## Not done

- DR-018 and CR-016 and CR-018 stay as a record; they are marked superseded.
