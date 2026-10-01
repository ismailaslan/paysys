# Paysys

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

Authentication is a seeded user (JWT bearer); authorization is role-based
(`admin` / `user`) combined with per-account ownership checks. Transactions
are appended to an audit log that is structured as a hash chain, so the
sequence of entries can be verified for tampering.

[![Architecture diagram](https://gitdiagram.com/diagram-badge.svg)](https://gitdiagram.com/ismailaslan/paysys)




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

2. **Configure secrets.** The API reads its database connection string and
   JWT signing key from
   [.NET user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets)
   (or the equivalent environment variables), never from a committed file:

   ```bash
   dotnet user-secrets set "ConnectionStrings:Paysys" "<your-postgres-connection-string>" --project src/Paysys.Api
   dotnet user-secrets set "Jwt:SigningKey" "<a-random-string-at-least-32-characters-long>" --project src/Paysys.Api
   ```

   Equivalently, via environment variables: `ConnectionStrings__Paysys` and
   `Jwt__SigningKey`.

   The app fails to start if either is missing or (for the signing key) too
   short — this is intentional, not a bug you need to work around.

3. **Apply migrations:**

   ```bash
   dotnet ef database update --project src/Paysys.DAL
   ```

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

**Signing in:** the API seeds a single user, `admin` (role `admin`). Its
password hash must be set via `Auth:Users:0:PasswordHash` in user-secrets
(or the equivalent environment variable) before the API will accept a
sign-in for it.

## Tests

```bash
dotnet test
```

Each layer has its own xUnit project under `tests/`. At present these are
placeholder projects — they build and run, but contain no assertions yet.

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

**Pending:**

- All seven benchmark criteria above (tokenization security, settlement
  speed, auditability, compliance, fraud detection, uptime/reliability,
  integration cost) — none are claimed as met; see the checklist
- Server-side PIN validation before a transaction is processed — marked as
  an unimplemented hook point in `TransactionProcessingService`
- Client-side transaction confirmation and PIN entry before a request is
  sent — marked as an unimplemented hook point in `Home.razor`
- Real test coverage — the test projects under `tests/` currently contain
  no assertions


