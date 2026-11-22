# Paysys

**Live demo:** [ismailaslan.github.io/paysys](https://ismailaslan.github.io/paysys/) — sign up and try it. Demo project, fake money only.

**Architecture documentation:** [Paysys Architecture Package (TOGAF ADM)](https://climbing-turner-847.notion.site/Paysys-Architecture-Package-TOGAF-ADM-3f31f4d44ad181898786daa406604421)

A payment processing system — account management, card tokenization, and
transaction settlement, including cross-currency conversion and cross-bank
routing.

**Stack:** Blazor WASM, ASP.NET Core, Neon Postgres (via EF Core), .NET 10.

## Architecture

The solution is split into six projects under `src/`, following an N-tier
structure:

| Project | Role |
|---|---|
| `Paysys.Api` | ASP.NET Core minimal-API host: endpoints, authentication, request logging, and startup wiring. No business logic lives here. |
| `Paysys.BLL` | Business logic: transaction processing, cross-bank routing, the audit log, fraud checks, and payee-code lookup. |
| `Paysys.DAL` | Data access: EF Core `DbContext`, entities, and migrations, against Neon Postgres. |
| `Paysys.Tokenization` | Card tokenization, kept as its own project. It references `Paysys.DAL` directly and not `Paysys.BLL`. |
| `Paysys.Shared` | Request/response DTOs shared between the API and the Blazor client. |
| `Paysys.Web` | The Blazor WebAssembly front end. |

`tests/` holds one xUnit project per layer (`Paysys.Api.Tests`,
`Paysys.BLL.Tests`, `Paysys.DAL.Tests`, `Paysys.Tokenization.Tests`).

Authentication is JWT bearer against the `Users` table; authorization is
role-based (`admin`, `user`, `merchant`, `bank`) combined with per-account
ownership checks. The sign-in page has one door per kind (Individual, Merchant,
Bank) and refuses an account that belongs behind another door. Money moves in two
steps: a payment is created and held for 5 minutes, then confirmed with the
payer's 4-digit PIN (stored hashed; 3 wrong PINs cancel the payment, 5 lock PIN
checks for 5 minutes). Transactions and security events are appended to an audit
log that is structured as a hash chain, so the sequence of entries can be
verified for tampering.

[![Architecture diagram](https://gitdiagram.com/diagram-badge.svg)](https://gitdiagram.com/ismailaslan/paysys)




## Tool map

| Concern | Tool | State |
|---|---|---|
| Architecture documents | Notion package (TOGAF ADM), linked at the top of this file | Published as a public Notion site |
| Source code | This GitHub repository | Public |
| Continuous integration | GitHub Actions | Three workflows: build and test (against a Postgres service), gitleaks secret scan, and the GitHub Pages deploy |
| Database | Neon Postgres, applied with EF Core migrations run by hand (`dotnet ef database update`) | In use |
| API and web hosting | API on Render (auto-deploys from `main`), web on GitHub Pages | In use |
| Build agent | Claude Code in VS Code | In use |

## Features (MVP v1)

- Account management
- Transactions, including cross-currency conversion
- Transaction history
- Card tokenization

## Benchmark criteria

This is a finish-line checklist for where the system needs to get to, not a
list of what it has already achieved:

1. Tokenization security
2. Settlement speed
3. Auditability
4. Compliance
5. Fraud detection
6. Uptime / reliability
7. Integration cost

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A Postgres database (the project is developed against
  [Neon](https://neon.tech), but any Postgres instance reachable over the
  connection string below will work)

## Setup

1. **Clone the repository** and restore the local tools it depends on
   (`dotnet-ef`, pinned in `.config/dotnet-tools.json`):

   ```bash
   dotnet tool restore
   ```

2. **Configure secrets.** The API reads its database connection string, JWT
   signing key and user password hashes from
   [.NET user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets)
   (or the equivalent environment variables), never from a committed file:

   ```bash
   dotnet user-secrets set "ConnectionStrings:Paysys" "<your-postgres-connection-string>" --project src/Paysys.Api
   dotnet user-secrets set "Jwt:SigningKey" "<a-random-string-at-least-32-characters-long>" --project src/Paysys.Api
   dotnet user-secrets set "Auth:Users:0:PasswordHash" "<an-ASP.NET-Core-Identity-PasswordHasher-hash>" --project src/Paysys.Api
   ```

   Equivalently, via environment variables: `ConnectionStrings__Paysys`,
   `Jwt__SigningKey` and `Auth__Users__0__PasswordHash`.

   The app fails to start if the connection string or signing key is missing,
   or the signing key is too short. This is intentional, not a bug you need to
   work around. Every user in `Auth:Users` needs a real `PasswordHash`; there
   is no default development password.

3. **Apply migrations:**

   ```bash
   dotnet ef database update --project src/Paysys.DAL
   ```

4. **Import users.** Sign-in checks the `Users` table, not configuration.
   Copy the `Auth:Users` entries (Id, Username, Role from `appsettings.json`,
   PasswordHash from user-secrets) into that table once:

   ```bash
   dotnet run --project src/Paysys.Api -- --import-users
   ```

   It prints the username and Id of each user it added or skipped, and exits.
   A user that is already in the table with the same Id and username is
   skipped and its stored hash is left unchanged. Any invalid entry, or an Id
   or username that clashes with an existing row, stops the import and
   nothing is written. A `PasswordHash` that is not a valid ASP.NET Core
   Identity hash (not base64, or an unknown format marker) is refused too.
   Configuration users are read only by this command; editing `Auth:Users`
   later has no effect on sign-in until it is run again.

   To apply a changed `Role` or `PasswordHash` from configuration to users
   that are already in the table, add `--update-existing`:

   ```bash
   dotnet run --project src/Paysys.Api -- --import-users --update-existing
   ```

   A row is updated only when both its Id and its username (ignoring case)
   match a configured entry, and only `Role` and `PasswordHash` change; the
   Id, the stored username and its casing, and the creation time never do.
   Rows that are not in configuration, such as self-registered users, are
   never touched. Every clash still stops the whole import. The output names
   each user as updated (with the names of the changed fields, never their
   values) or unchanged. A run that would demote an admin and leave the
   table with no admin at all is refused, and nothing is written.

## Running

Start the API first, then the web app, each in its own terminal:

```bash
dotnet run --project src/Paysys.Api --launch-profile http
```

```bash
dotnet run --project src/Paysys.Web --launch-profile http
```

By default (Development launch profiles) the API listens on
`http://localhost:5121` and the web app on `http://localhost:5273`; the web
app is configured to call the API at that address via
`src/Paysys.Web/wwwroot/appsettings.Development.json`.

**Signing in:** you can sign in as any user in the `Users` table. The
committed configuration lists a single user, `admin` (role `admin`); once its
`Auth:Users:0:PasswordHash` is set and `--import-users` has been run (Setup
step 4), the API accepts a sign-in for it. With an empty `Users` table the API
still starts, but every sign-in is refused.

Sign-in refuses usernames longer than 64 characters and **passwords longer
than 128 characters** before looking anything up; they get the normal
"Invalid username or password" answer and count towards the lockout. A
configuration user whose password is longer than 128 characters can
therefore no longer sign in: give them a new hash of a password of at most
128 characters and run `--import-users --update-existing`.

**Signing up:** when `Auth:Registration:Enabled` is `true`, visitors can
create their own account on the web app's Sign up page (`/signup`, linked
from Sign in), which calls `POST /api/auth/register`. It is `true` in
`appsettings.Development.json` and off everywhere else unless configured
(env var `Auth__Registration__Enabled`); when it is off the endpoint answers
404. Rules:

- Username: 1 to 64 characters, ASCII letters, digits, `.`, `_` and `-`.
  A name that is already taken, in any letter case, gets 409.
- Password: 6 to 128 characters, no other rules.
- The account kind is Individual (role `user`) or Merchant (role `merchant`);
  the request cannot choose any other role. Bank logins are created by an admin,
  never by sign-up. Each new account starts with one empty account at the first
  bank, and there is no token: sign in afterwards.
- At most 2000 users in total (a demo limit on top of the per-address limit).
- At most 5 sign-up requests per client address, refilling one every 12
  minutes (IPv6 counted per /64); the client address is the connection's
  own address, never `X-Forwarded-For`. Over the limit the API answers 429
  with `Retry-After`.

## Demo data

To load sample banks, accounts and two test cards into an **empty** database,
first add a user with `Role` `user` (and a `PasswordHash`) to `Auth:Users`
and run `--import-users` (Setup step 4), so the user is in the `Users` table.
The demo accounts are given to the oldest `user`-role row in that table, the
rest to the oldest `admin` row. Then run:

```bash
dotnet run --project src/Paysys.Api -- --seed-demo
```

It prints what it created, including the demo shop's payee code, and exits. It
refuses to run if the database already contains any bank. Setting
`Seed:SecondBankEndpoint` gives the second demo bank an approval endpoint so
cross-bank transfers can be tried; without it, transfers to that bank fail
closed.

## Tests

```bash
dotnet test
```

Each layer has its own xUnit project under `tests/`. Tests that need a
database are marked `[PostgresFact]` and run only when the `PAYSYS_TEST_PG`
environment variable names a Postgres server, for example a throwaway
local one:

```bash
docker run --rm -d --name paysys-test -p 55432:5432 -e POSTGRES_PASSWORD=test postgres:16
PAYSYS_TEST_PG="Host=localhost;Port=55432;Username=postgres;Password=test" dotnet test
docker stop paysys-test
```

Each such test creates and drops its own database on that server. Never
point `PAYSYS_TEST_PG` at Neon or any database you want to keep. Without
it, those tests are reported as skipped. CI (`.github/workflows/ci.yml`)
runs everything against a `postgres:16` service.

`tests/Paysys.Api.Tests/Http` holds HTTP tests that start the API in
process (`Microsoft.AspNetCore.Mvc.Testing`), in environment `Testing`, so
user-secrets are not loaded. Each test passes its own connection string
and the test factory refuses to send a request if the API ended up with a
different one.

## Roadmap

### MVP v2

**Already in the code** (built, but outside the MVP v1 scope above):

- Cross-bank transaction routing, with validation of the destination bank's
  endpoint before it is called
- An audit log whose entries are chained by hash, with a routine that walks
  the chain and checks that linkage, run on a schedule and exposed through
  an admin-only endpoint
- Two fraud-check heuristics — a rapid repeat transfer from the same
  account, and a repeated high-value transfer — that flag a transaction's
  audit entry but do not block the transaction
- Per-username login lockout with increasing backoff after repeated failed
  sign-ins
- Role-based authorization (`admin` / `user`) combined with per-account
  ownership checks
- An opt-in payee-code lookup, letting one user find another's account by a
  short code without seeing that account's full details
- Short one-use payment codes (6 digits, 2 minutes) that a merchant gives the payer
- A 4-digit payment PIN and a two-step payment (create, then confirm with the PIN),
  with a lockout after wrong PINs and audit entries for PIN events
- Roles `merchant` and `bank`, a sign-in door per kind, and a demo shop so one
  account can try a whole payment

**Pending:**

- All seven benchmark criteria above (tokenization security, settlement
  speed, auditability, compliance, fraud detection, uptime/reliability,
  integration cost) — none are claimed as met; see the checklist
- More test coverage — tests exist per layer and for the sign-in door, the PIN and
  the two-step payment, but not for every endpoint
- A gateway project for terminals, with live payment notifications in place of
  the Collect page's 3-second refresh


