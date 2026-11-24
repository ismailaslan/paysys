# DR-022 Shared state in the database

**Status:** Accepted. Recorded on 2026-11-24 on the owner's instruction ("write them into the database ... plug and play").
**Owner:** Ismail Aslan.

## Context

Six pieces of short-lived state sat in each server's memory, so a second instance could not serve a request that began on the first: a payment waiting for its PIN, the wrong-PIN count, the demo top-up count, the sign-up budget, the access-denial budget, and the gateway's live-stream tickets. The target architecture (`06-target/production-architecture.md`) named this as the first thing that blocks running more than one instance.

## Decision

Keep them in the database, in the same Postgres the services already use, not in Redis. DR-003 (Redis) stays not needed.

- **Pending payments** (`PendingPayments`): created on one server, checked and taken on another. Taking is one delete, so two confirms racing for the same payment cannot both run it. A wrong PIN is counted by one statement; the third deletes the payment.
- **Wrong-PIN count and lock** (`PinThrottles`): the count and the lock are one row per user. Counting a failure is one statement, so two servers counting at once lose none.
- **Demo top-ups** (`DemoFundUses`): one row per top-up, kept an hour; the count and the new row happen under a per-user lock, so two servers cannot both take the tenth place.
- **Sign-up and denial budgets** (`RateBuckets`, one row per caller and limit): a token bucket worked out in one statement from the stored count and the time since it was stored. Same burst and refill as before (sign-up: 5, one back every 12 minutes, per address; denial: 10, one back every second, per user). If the database does not answer, each limiter uses this server's own memory bucket, so a database fault never switches the limit off.
- **Stream tickets** (gateway table `gateway."StreamTickets"`, created by the gateway on start): only a hash of each ticket is stored; redeeming is one delete.

The stores take the service scope factory, as `PaymentCodeStore` already did: with it they use the database, without it (unit tests) they keep the old in-memory rules.

## Data

Migration `20261127030000_SharedState` adds `PendingPayments`, `PinThrottles`, `DemoFundUses` and `RateBuckets`. It is applied by hand on Neon before the new bank API version starts, like the earlier ones. The gateway creates its own table.

A pending payment holds the card token, the same opaque value `CardTokens` already holds, never a card number. The row is deleted when the payment is taken, expires or is cancelled. The card number never reaches this table.

`SharedStateCleanupService` removes expired payments, top-up rows older than an hour, buckets that have long since refilled, and PIN counts older than a day. Every delete is safe to repeat, so it runs on every server.

A pending payment's idempotency key and card token are limited to 255 characters when it is created (the columns and the transaction's own key column hold 255), answered with 400.

## Evidence

CI runs two copies of the whole API over one database: a payment created on one is confirmed on the other, once; two servers confirming the same payment at the same moment move the money once; wrong PINs on different servers add up and cancel the payment; a PIN lock started on one holds on the other. Store-level tests cover the races for each store and the gateway tickets.

## Not done

- The gateway's live push (SignalR) is passed between instances by Postgres NOTIFY (`MerchantPush`), only when `Gateway:PushRelay:Enabled` is `true` (off by default, so one instance behaves as before). It needs a non-pooled database connection (Neon's `-pooler` host does not support LISTEN) and a permanent connection. Tested in CI with two instances over one database; not tried on the deployed services. A message over about 7000 bytes is cut into pieces (base64, about 6000 characters each) that the receiving instance puts back together; pieces that do not all arrive within 30 seconds are dropped, and a message over 1 MB is not relayed. Tested with a 30 KB message that contains multi-byte characters.

## Added after the first version

- The sign-in counter (`LoginThrottles`) now uses the database as the one copy: each failure is a compare-and-set against the row that was read, so two servers failing at once lose none, and the lock and its doubling are the same on every server. If the database does not answer, the server's own memory copy is used. Old rows are removed by the cleanup service. Tested with two instances over one database.
- The cost of the extra database calls on busy paths is not measured.
- Later the same day the ticket limits were made exact: issuing takes a database lock (`pg_advisory_xact_lock`) around "count, then add", so 40 requests at once from 8 servers for one account give exactly the per-account limit. Tested.
- Not tested on the deployed services: the owner makes one payment by hand after the migration is applied.
