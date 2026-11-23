# Paysys code walkthrough: one journey at a time

Status: 23 November 2026. Written for the owner's own review of the code. It follows what a person does and names the files that run, in order. File names and function names are given, line numbers are not, because they move. Where a step rests on a comment or on tests and I did not read every line, it says so.

## How to read this

1. Start with the map below, then pick one journey and open the files in the order listed.
2. For each journey, read the tests named at the end of it: they say what the code promises.
3. Questions to ask yourself while reading: where is the user checked, where is the money moved, where is it written to the audit, what happens if it fails halfway.

## The map

| Folder | What lives there |
| --- | --- |
| `src/Paysys.Web` | The browser app (Blazor). Pages: Login, Signup, Home (Send money), Accounts, Cards, Collect (merchant), Bank, Banks, Platform, Audit, History, Flow |
| `src/Paysys.Api` | The host. `Program.cs` wires everything; `Endpoints/` holds one file per area; `Auth/` login, tokens, registration, throttles; `Gateway/` what the bank sends to and accepts from the gateway; `Diagnostics/` the audit chain checker |
| `src/Paysys.BLL` | Business rules: `Services/TransactionProcessingService.cs` (the payment), `TransactionAuditLogService.cs` (the audit chain), stores for payment codes, grants, pending payments, PIN throttle |
| `src/Paysys.Tokenization` | The only project that handles card numbers: `CardTokenizationService.cs` |
| `src/Paysys.DAL` | Database entities (`Entities/`) and the context (`Persistence/PaysysDbContext.cs`), with migrations |
| `src/Paysys.Shared` | Request and response types shared by the API and the web app |
| `gateway/Gateway.Api` | The separate gateway service: `Endpoints/TerminalEndpoints.cs` (what a terminal calls), `Endpoints/BankEndpoints.cs` (what a bank calls), `Hubs/` (live push), `Security/` (keys, signatures, tickets), `Services/BankClient.cs` |
| `tests/*`, `gateway/Gateway.Tests` | Tests per layer; `Paysys.TestSupport` holds the real-Postgres helpers |

Request path in the API (`Program.cs`): HTTPS redirect, CORS, authentication, request-outcome logging, authorization, then the endpoint. Endpoints are mapped by `Map...Endpoints()` calls, one per file.

## Journey 1: sign up and sign in

1. `Pages/Signup.razor` posts to `POST /api/auth/register` (`Endpoints/AuthEndpoints.cs`, `MapRegisterEndpoint`).
2. The endpoint first takes a token from `Auth/RegistrationRateLimiter.cs` (per client address), then calls `Auth/UserRegistrationService.cs`.
3. `UserRegistrationService.RegisterAsync` validates the username (ASCII only) and password, hashes it (PBKDF2, Identity format), and inserts a `User` row. A unique index on the normalized username decides duplicates, so there is no check-then-insert gap. The role is only `user` or `merchant`; there is no path to `admin` or `bank`. It stops at 2000 users, and it creates the new user's first empty account at the lowest-id bank.
4. `Pages/Login.razor` posts to `POST /api/auth/login`. The endpoint checks `Auth/LoginThrottle.cs` first (a locked name is refused before any database or hashing work), then `UserAuthenticator`. The right password at the wrong door (a merchant on the Individual tab) is refused like a wrong password. Success returns a JWT from `Auth/TokenService.cs`.
5. Every later request carries the JWT; `user.GetUserId()` (`Auth/ClaimsPrincipalExtensions.cs`) reads who is calling.

Read the tests: `RegisterEndpointTests`, `LoginEndpointTests`, `LoginHardeningTests`, `LoginThrottleTests`, `UserRegistrationServiceTests`.

## Journey 2: get a card (issue or add)

1. `Pages/Cards.razor` calls `POST /api/cards/issue` (demo card) or `POST /api/tokenize` (add your own number). Both are in `Endpoints/TokenizationEndpoints.cs`.
2. Issue: the endpoint counts the account's open cards (limit 5), makes a number, CVV and expiry with `BLL/Services/CardIssuer.cs`, then tokenizes the number like any other card.
3. `Paysys.Tokenization/CardTokenizationService.cs` checks the account belongs to the caller, validates the number (length, Luhn) and expiry, and stores only a token, last four digits, brand and expiry. The number is not stored or hashed.
4. Bad card details raise `InvalidCardException`; the endpoint logs only the reason (never the number) and records a rejected attempt. The audit entry `Card.Tokenized` is written by `RecordTokenizedAsync`.
5. For an issued card the number and CVV go back once in the response; the Cards page shows them and they are not shown again.

Read the tests: `Paysys.Tokenization.Tests` (`CardIsolationTests` lists the only files allowed to name the number) and `TokenizationEvidenceTests` in the API tests (searches every table and log for a test number).

## Journey 3: pay with a card and a PIN

1. `Pages/Home.razor` (Send money). The payer types a payment code in **To** and clicks Look up: `GET /api/payees/lookup` (`Endpoints/PayeeEndpoints.cs`) → `BLL/Services/PaymentCodeService.LookupAsync`. The code is used up on lookup. If it carried an amount, a grant is stored in `PayeeGrantStore` for this payer and account, and the amount field is fixed to it.
2. The payer sets a PIN once (`POST /api/pin`, `Endpoints/PaymentEndpoints.cs`; stored as a hash).
3. Step one: `POST /api/payments` validates the request (PIN exists, amount, idempotency key, the card belongs to the caller and is Active, the destination exists) and parks it in `BLL/Services/PendingPaymentStore.cs` for 5 minutes. Nothing moves.
4. Step two: `POST /api/payments/{id}/confirm` checks the PIN through `PinThrottle` (3 wrong tries cancel the pending payment, 5 lock PIN checks, and the lock is restored from the audit log after a restart), takes the parked payment, and calls `TransactionEndpoints.ProcessAndRespondAsync`.
5. `ProcessAndRespondAsync` calls `TransactionProcessingService.ProcessAsync` and turns each failure into the right HTTP answer.
6. `ProcessAsync` is the core. In order: amount rules; replay by idempotency key; resolve card to account and bank (`CardToAccountResolver`); the card must belong to the caller and be Active; the destination must exist; a Business account cannot be a source; cross-bank approval through `CrossBankRoutingClient` when banks differ; if a payment code carried an amount, the converted amount must match it; exchange rate and conversion; fraud rules (flag only); the payer is debited under the `xmin` version check; the destination is credited with `balance = balance + amount`; the audit entry is appended. The debit, credit and audit entry are in one database transaction.
7. After the commit, if the destination is a terminal account, a message goes to the gateway (journey 5).

Read the tests: `TransactionProcessingTests`, `FraudRuleTests`, `PaymentCodeAmountTests`, `PaymentFlowEndpointTests`, and `ConcurrentCreditTests` in the BLL tests.

## Journey 4: the audit trail

1. Every state change calls `BLL/Services/TransactionAuditLogService.AppendAndSaveAsync`.
2. It opens a transaction, takes a Postgres advisory lock (one writer at a time), runs the optional step inside the transaction (the destination credit), reads the newest entry, builds a new entry that stores the previous entry's id and hash, computes its SHA-256 hash, saves and commits.
3. `Api/Diagnostics/AuditChainVerificationService.cs` runs in the background: it walks the whole chain, checks every hash and link, and keeps the newest entry it saw as an anchor in memory so the next run can tell if the newest entries were deleted. `VerifyChainIntegrityAsync` in the audit service does the walking.
4. `GET /api/audit/integrity` and `GET /api/audit/entries` (`Endpoints/AuditEndpoints.cs`) show the status and entries; `Pages/Audit.razor` displays them.
5. A foreign key on the previous-entry id makes the database refuse deleting a middle entry.

Read the tests: `AuditChainTests`.

## Journey 5: terminal, gateway and live merchant page

1. A terminal calls the gateway: `POST /v1/payments` (`gateway/Gateway.Api/Endpoints/TerminalEndpoints.cs`) with its id and key. The gateway checks the key hash, limits open payments per terminal, creates the request id, and asks the bank for a code through `Services/BankClient.cs` (with the request id as the reference).
2. The bank side is `POST /api/gateway/payment-codes` (`Api/Endpoints/GatewayEndpoints.cs`): it checks the shared gateway key, makes sure the account is a terminal, and creates a code in `BLL/Services/PaymentCodeStore.cs` with the amount and reference. The terminal shows the code.
3. The payer pays (journey 3). When the lookup consumes the code, the reference is stored with the grant. After the payment commits, `ProcessAndRespondAsync` builds a `GatewayPaymentEvent` (`Api/Gateway/GatewayNotifier.cs`) whose event id is the transaction id and which carries the reference, and queues it.
4. `GatewayNotifier` sends it from a background loop, signed with HMAC-SHA256 over timestamp and body, with 3 retries.
5. The gateway receives it at `POST /v1/bank/events` (`gateway/Gateway.Api/Endpoints/BankEndpoints.cs`): checks the bank code, the signature and the 5-minute window, refuses a transaction already applied, saves the event id and completes the matching request (found by request id, terminal, amount, currency, still pending) in one transaction, then pushes `PaymentReceived` through the SignalR hub to the merchant.
6. The merchant page `Pages/Collect.razor` asked its bank (`POST /api/merchant/stream-ticket`) for a one-use ticket, and connects to the hub with it (`gateway/Gateway.Api/Hubs/`). The terminal can also poll `GET /v1/payments/{id}`.

Read the tests: `gateway/Gateway.Tests` (`BankEventEndpointTests` runs the real gateway against Postgres: matching by reference, replay, the atomic update, the open-request cap; also `RetentionTests`, `TerminalRateLimitTests`, `SecretsTests`, `TicketStoreTests`, `TicketStoreLimitTests`, `MultiBankTests`, `PaymentMatchingTests`) and, on the bank side, `GatewayEventTests`, `PaymentCodeReferenceTests`. Not covered by tests: the 64 KB body limit, the live push through SignalR, and the terminal's own request path through the bank (it was run by hand end to end).

## Journey 6: a bank blocks or unblocks a card, an owner pauses it

1. Owner: `POST /api/cards/{id}/pause`, `resume`, or `DELETE` (`Endpoints/CardEndpoints.cs`). Bank: `POST /api/bank/cards/{id}/block` and `unblock`, only for cards on accounts at the bank linked to the caller's login.
2. Both go through `ChangeAsOwnerAsync` or `ChangeAsBankAsync`, which find the card with the ownership or bank filter (anything else is a plain 404), then `ApplyAsync`.
3. `ApplyAsync` runs the state change on the entity (`DAL/Entities/CardToken.cs`: Pause, Resume, Block, Unblock, Close; wrong order raises an error), then `CardStatusChange.SaveAsync` saves it with a conditional update inside the audit transaction. If the card's status changed meanwhile, the change is refused with 409.
4. An unblocked card becomes Paused; the owner resumes it. Payments only run on Active cards (checked in `ProcessAsync` and in the create-payment endpoint).

Read the tests: `CardStatusTests` (state rules) and `CardStatusConcurrencyTests` (two changes at once).

## Journey 7: a merchant sets up terminals

1. `Pages/Collect.razor` calls `POST /api/merchant/terminals` (`Endpoints/CardEndpoints.cs`): up to 5 per merchant, each is a Business account with a terminal id.
2. `POST /api/merchant/terminals/{accountId}/gateway-key` (`Endpoints/GatewayEndpoints.cs`) asks the gateway to register the terminal (`Api/Gateway/GatewayClient.cs` → `POST /v1/bank/terminals`) and returns the key once. The gateway stores only a hash.

## Where to look when something breaks

| Symptom | Start here |
| --- | --- |
| 401 on sign-in | `AuthEndpoints.cs` role-and-door check, `LoginThrottle` |
| Payment rejected | the `catch` list in `TransactionEndpoints.ProcessAndRespondAsync` maps each rule to an answer |
| Money moved but audit missing | not possible by design: both are in one transaction in `AppendAndSaveAsync` |
| Gateway request stays Pending | `gateway/.../BankEndpoints.cs` event handler: signature, reference match, terminal and amount |
| Audit chain reported invalid | `AuditChainVerificationService` and `TransactionAuditLogService.VerifyChainIntegrityAsync` |

## Known places where the code does not follow the layering

Registration, card status, bank administration and some account endpoints use the database context directly in the endpoint instead of a BLL service (decision DR-015, `as-is.md` section 12.1).
