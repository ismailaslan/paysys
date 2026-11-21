# Plan: user sign-up

**Most likely reading:** anyone who can reach the web app can create their own account with role `user`. The account is a new row in the existing `Users` table, made through `POST /api/auth/register` and a `/signup` page in `Paysys.Web`. After signing up, the person signs in on the existing Sign in page. The same change fixes three findings from the users-table review in the login and import code, and caps login passwords at 128 characters.
**Alternative readings (settled, see the decisions under Open questions):** automatic sign-in after sign-up (rejected, A3); open registration in every environment (rejected, A4); admin-only "create user" (not planned, see Out of scope).

---

## 0. What exists today (read from the code, commit `7751694`, branch `claude`)

| Where | What |
|---|---|
| `src/Paysys.DAL/Entities/User.cs` | The entity. Its constructor checks: non-empty Id, username not blank, no leading or trailing whitespace, at most 64 characters, role is `admin` or `user`, hash not empty and at most 512 characters. `Normalize` = `ToLowerInvariant()`, not trimmed. All setters are private and there is **no method to change role or hash** |
| `src/Paysys.DAL/Persistence/PaysysDbContext.cs` (lines 172-187) | Unique index `IX_Users_NormalizedUsername` and check constraint `CK_Users_Role`. `Id` is `ValueGeneratedNever` |
| `src/Paysys.DAL/Persistence/UniqueViolation.cs` | Constants for the idempotency-key index and the payee-code index only. There is no constant for the Users index |
| `src/Paysys.DAL/DependencyInjection/DataAccessServiceCollectionExtensions.cs` | `AddDataAccess` reads `ConnectionStrings:Paysys` **when services are registered**, before `Build()`, and throws if it is missing |
| `src/Paysys.Api/Auth/UserAuthenticator.cs` | Scoped. Uses a static `PasswordHasher<User>`. The dummy hash is built in a static constructor. Lookup by `NormalizedUsername`. An unknown user is checked against the dummy hash. Null or empty input returns null without hashing |
| `src/Paysys.Api/Auth/ConfigUserImporter.cs` | One-shot, runs in one transaction. A row with the same Id and the same username is **skipped**. An Id or username clash stops the import |
| `src/Paysys.Api/Auth/LoginThrottle.cs` | Per username, in memory. Its key is `Trim().ToLowerInvariant()` cut to 64 characters. Locks after 5 failures. **Not changed by this plan** |
| `src/Paysys.Api/Endpoints/AuthEndpoints.cs` | Only `POST /api/auth/login`. Order: throttle check, then authenticate; **any null result calls `RecordFailure` and returns the same 401**; success calls `RecordSuccess` and issues the token |
| `src/Paysys.Api/Auth/AuthServiceCollectionExtensions.cs` | Binds `Jwt` and `Auth` **when services are registered**, and requires `Jwt:SigningKey` of at least 32 characters. Also holds the JWT bearer setup and the `Admin` policy. There is no rate-limiting middleware and no `UseForwardedHeaders` anywhere |
| `src/Paysys.Api/Program.cs` | Top-level statements with no `public partial class Program`. Pipeline: exception handler, then `UseHttpsRedirection`, then CORS, then authentication, then request-outcome logging, then authorization. `MapOpenApi` only in Development. `--seed-demo` and `--import-users` branches |
| `src/Paysys.Api/Diagnostics/AuditChainVerificationService.cs` | A hosted service. It waits `AuditVerification:InitialDelay` (30 s by default) before its first database query |
| `src/Paysys.Api/Properties/launchSettings.json` | Both profiles set `ASPNETCORE_ENVIRONMENT=Development`. User-secrets (which hold the Neon connection string) are loaded only in Development |
| `src/Paysys.Api/Paysys.Api.csproj` | ASP.NET Core packages pinned at `10.0.12`, `net10.0` |
| `src/Paysys.BLL/Services/AccessDenialRateLimiter.cs` | A `PartitionedRateLimiter` token-bucket pattern to copy. Its comment says: "Worst case is (users x 1/s)… If accounts multiply, add a global cap too" |
| `src/Paysys.Shared/Auth/` | `LoginRequest(Username, Password)` and `LoginResponse(Token, ExpiresAt, Username, Role)` |
| `src/Paysys.Web/Pages/Login.razor` | Markup is `split-layout` > `form-panel panel` > `panel-heading`, `mb-3` fields, `btn btn-primary`, `alert alert-danger` |
| `src/Paysys.Web/Layout/MainLayout.razor` | `IsLoginPage` = path starts with `login`. **Every other page redirects a signed-out user to `login`**, so `/signup` cannot be reached unless this changes |
| `tests/Paysys.Api.Tests/Paysys.Api.Tests.csproj` | xunit 2.9.3 and references to Api and TestSupport. **No `Microsoft.AspNetCore.Mvc.Testing`** |
| `tests/Paysys.TestSupport` | `PostgresFact` and `PostgresTestBase`, with one throwaway migrated database per test on `PAYSYS_TEST_PG`. The connection string field is private, but `Db.Database.GetConnectionString()` gives it without any change to TestSupport |

### Review findings 1-3, checked against the code

1. **The importer cannot update existing users.** In `ConfigUserImporter.ImportAsync` (lines 91-96), a row with the same Id and the same normalized username is always skipped. A changed `PasswordHash` or `Role` in config is silently ignored, and a changed username for the same Id stops the import (lines 83-85). The only way to update anything today is hand-written SQL against Neon. The test `SecondRun_SkipsEveryone_AndLeavesTheStoredHashAlone` locks the skip behaviour in.
2. **A malformed stored hash makes login return 500.** `UserAuthenticator` line 57 calls `VerifyHashedPassword`. In ASP.NET Core Identity, that starts with `Convert.FromBase64String`, which throws `FormatException` for a string that is not base64. Nothing catches it, so the global handler returns 500. Hashes like that get in because the importer (lines 145-151) and the `User` constructor (lines 55-59) only check that the hash is non-empty and at most 512 characters. Effects:
   - that user cannot sign in;
   - `RecordFailure` is never reached;
   - 500 rather than 401 tells a caller that this username exists.

   This depends on how Identity behaves inside, so it is confirmed first by a failing test (T10).
3. **No username length cap before the database lookup.** `AuthEndpoints` line 27 passes `request.Username` unchanged, and `AuthenticateAsync` lines 41-48 lower-case it and query Postgres with it at any length (up to Kestrel's 30 MB default body). The throttle key is cut to 64 characters, but the lookup is not. The same is true of the password: any length is hashed.

### Where the code and the baseline documents disagree

1. **as-is.md section 5** says "Users are not stored in the database". That has been false since `7751694`.
2. **as-is.md section 7** lists login as the only anonymous auth endpoint. This change adds `/api/auth/register` when it is switched on.
3. **The Login page dev hint is out of date.** `Login.razor` line 34 shows "Dev login: admin / password", but the fallback was removed. Left as it is (decision Q11).
4. **The README "Tests" section** (lines 172-173) still says "placeholder projects". That is out of date.
5. **Small deviation from users-table.md.** That plan said the dummy hash would use a `Lazy`. The code uses a static constructor, which also meets R2 there. No action needed.
6. **DemoSeeder** gives the demo accounts to the oldest `user`-role row, which after this change may belong to a stranger (R14, decision Q9).

This plan does not edit as-is.md.

---

## 1. Goal

Let a visitor create their own `user`-role account from a Sign up page, through a rate-limited registration endpoint that writes one row to `Users`. In the same change:
- let the config importer update a user's role and password hash, but never remove the last admin;
- make login return the normal 401, without a database lookup or hashing the input, for malformed stored hashes, usernames over 64 characters and passwords over 128 characters.

## 2. Files

### Files to create

| File | Purpose |
|---|---|
| `src/Paysys.Shared/Auth/RegisterRequest.cs` | `public record RegisterRequest(string Username, string Password);`. **There is deliberately no Role property.** |
| `src/Paysys.Shared/Auth/RegisterResponse.cs` | `public record RegisterResponse(string Username);` (no token, A3) |
| `src/Paysys.Api/Auth/PasswordRules.cs` | `public static class PasswordRules { public const int MinLength = 12; public const int MaxLength = 128; }`. **This is the one place the password limits are defined.** Registration uses both, login uses `MaxLength`. Lengths are `string.Length` (UTF-16 code units) |
| `src/Paysys.Api/Auth/PasswordHashFormat.cs` | `public static bool IsWellFormed(string hash)`: `Convert.TryFromBase64String` succeeds, at least 1 byte, and the first byte is `0x00` (Identity V2) or `0x01` (V3). Used by the importer |
| `src/Paysys.Api/Auth/UserRegistrationService.cs` | Scoped. `Task<RegistrationResult> RegisterAsync(string? username, string? password)`. In order: (1) check the username: not null, matches `^[A-Za-z0-9._-]{1,64}$`; (2) check the password: not null, `PasswordRules.MinLength <= Length <= PasswordRules.MaxLength`; (3) hash with a static `PasswordHasher<User>`; (4) `new User(Guid.NewGuid(), username, User.UserRole, hash)`. **The role is a constant and there is no role parameter anywhere**; (5) `Add` and `SaveChangesAsync`; (6) `catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, UniqueViolation.UserNormalizedUsernameIndex))`: `ChangeTracker.Clear()` and return `UsernameTaken`. Any other exception propagates. **There is no SELECT before the insert.** Results: `Created(Guid Id, string Username)`, `Invalid(string Message)`, `UsernameTaken` |
| `src/Paysys.Api/Auth/RegistrationRateLimiter.cs` | Singleton, built like `AccessDenialRateLimiter`. A `PartitionedRateLimiter<string>` token bucket per client address: `TokenLimit = 5`, 1 token every 12 minutes, `QueueLimit = 0`. `bool TryAcquire(HttpContext context, out TimeSpan retryAfter)` reads **only** `context.Connection.RemoteIpAddress` and **never reads X-Forwarded-For**. `public static string PartitionKey(IPAddress?)`: IPv4-mapped IPv6 becomes IPv4; IPv6 is keyed by its /64 prefix; IPv4 by the full address; `null` becomes `"unknown"`. `IDisposable` |
| `src/Paysys.Web/Pages/Signup.razor` | `@page "/signup"`, the same markup as `Login.razor`, with panel heading "New Paysys account". Fields: Username (`autocomplete="username"`), Password and Confirm password (`autocomplete="new-password"`). Confirm is checked on the client only and never sent. Results: 201 shows success with a link to `login` and clears the passwords; 400 shows the server `message`; 409 shows "That username is taken."; 429 shows the server `message`; 404 shows "Sign-up is not enabled on this server."; anything else shows "Sign up failed (code)." Footer: "Already have an account? Sign in" |
| `tests/Paysys.Api.Tests/LoginHardeningTests.cs` | T10, T11, T11b |
| `tests/Paysys.Api.Tests/ConfigUserImporterUpdateTests.cs` | T12-T18c |
| `tests/Paysys.Api.Tests/UserRegistrationServiceTests.cs` | T1-T7 |
| `tests/Paysys.Api.Tests/RegistrationRateLimiterTests.cs` | T8, T9 |
| `tests/Paysys.Api.Tests/Http/PaysysApiFactory.cs` | The `WebApplicationFactory<Program>` subclass used by the HTTP tests (section 4.2). Test code only |
| `tests/Paysys.Api.Tests/Http/RegisterEndpointTests.cs` | H1-H9 |
| `tests/Paysys.Api.Tests/Http/LoginEndpointTests.cs` | H10-H13 |
| `tests/Paysys.DAL.Tests/UserUpdateTests.cs` | T19 |

New test files are used instead of editing the existing ones, so the existing tests stay unchanged and keep checking today's behaviour.

### Files to modify

| File | Change | Reason |
|---|---|---|
| `src/Paysys.Api/Auth/UserAuthenticator.cs` | After the existing null or empty check (unchanged), and **before any database query or hashing of caller input**: if `username.Length > User.MaxUsernameLength` (**finding 3**) **or** `password.Length > PasswordRules.MaxLength` (**Q8**), verify a fixed private placeholder password against the dummy hash and return null. The caller's oversized password is never hashed, but the time taken matches a normal failure. **Finding 2:** wrap the real `VerifyHashedPassword` in `try … catch (FormatException)`. On a catch, run the same placeholder dummy verify, log a Warning "Stored password hash for user {UserId} is malformed" (Id only, never the hash), and return null. A `ILogger<UserAuthenticator>` is added to the constructor | Every early refusal returns null. **The login endpoint is not changed**: it already calls `RecordFailure` for every null, so these paths give the same 401 and count towards lockout exactly like a wrong password. `LoginThrottle` is unchanged |
| `src/Paysys.Api/Auth/ConfigUserImporter.cs` | (a) **Finding 1:** `ImportAsync(bool updateExisting = false)` and `RunAsync(TextWriter, TextWriter, bool updateExisting = false)`. Update rules are in A13. In update mode the matched rows are loaded **tracked**, and `UpdateRoleAndPasswordHash` is called only when something differs. Results gain `Updated` (username, Id, names of changed fields) and `Unchanged`. Output never contains values. (b) **Last-admin refusal (Q7):** after working out every change and before `SaveChangesAsync`, if this run changes at least one role from `admin` to `user`, count the `admin` rows that would remain: the current count from the table in the same transaction, minus the demotions, plus the promotions and the added admins. If that is 0, throw `UserImportException("the import would leave no admin user in the Users table; nothing was written.")`, so the **whole import stops and nothing is written** (A14). (c) **Finding 2, root cause:** `ValidateConfiguration` also requires `PasswordHashFormat.IsWellFormed` | With no flag, behaviour is exactly as today, so `SecondRun_SkipsEveryone…` still passes |
| `src/Paysys.DAL/Entities/User.cs` | Add `public void UpdateRoleAndPasswordHash(string role, string passwordHash)`. It applies the constructor's role and hash checks (pulled out into private static helpers that the constructor also calls, with identical messages and exception types) and never changes `Id`, `Username`, `NormalizedUsername` or `CreatedAt`. Update the header comment: rows also come from self-registration, and the importer may update role and hash | With only private setters there is no other way to update. **No migration** |
| `src/Paysys.DAL/Persistence/UniqueViolation.cs` | Add `UserNormalizedUsernameIndex = "IX_Users_NormalizedUsername"` | The registration service has to recognise exactly this violation |
| `src/Paysys.Api/Auth/AuthOptions.cs` | Add `RegistrationOptions { bool Enabled }` and `AuthOptions.Registration` (binds `Auth:Registration:Enabled`, false by default) | The config switch (A4) |
| `src/Paysys.Api/Auth/AuthServiceCollectionExtensions.cs` | Add only `AddScoped<UserRegistrationService>()` and `AddSingleton<RegistrationRateLimiter>()`. **The JwtBearer block, the `AddAuthorization` block and `Validate` are not changed** | DI. The authorization code stays untouched |
| `src/Paysys.Api/Endpoints/AuthEndpoints.cs` | Map `POST /api/auth/register` **only when** `AuthOptions.Registration.Enabled`. Handler order: (1) `limiter.TryAcquire`, which returns `RetryAfterResult` 429 **before any hashing or database work**; (2) `RegisterAsync`; (3) `Created` gives 201 with `RegisterResponse`; `Invalid` gives 400 with `{ message }`; `UsernameTaken` gives 409 with `{ message = "That username is taken." }`. Add `.WithMetadata(new RequestSizeLimitAttribute(8 * 1024))`. Logs: Information "Registration created user {UserId}", Information "Registration refused: username taken", Warning "Registration refused: rate limit". **No password, hash or IP address is logged.** **The login handler is not changed** | The endpoint |
| `src/Paysys.Api/Program.cs` | (a) The `--import-users` branch passes `args.Contains("--update-existing")` to `RunAsync`, and its comment is updated. (b) **Only if step 10a shows `Program` is not reachable from the tests:** add `public partial class Program;` as the last line, with a comment saying it exists for `WebApplicationFactory` (A20) | (a) The update mode. (b) The HTTP tests need it |
| `src/Paysys.Api/appsettings.Development.json` | Add `"Auth": { "Registration": { "Enabled": true } }` | Sign-up is on in local development and off everywhere else unless someone sets it |
| `tests/Paysys.Api.Tests/Paysys.Api.Tests.csproj` | Add `<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />` | Needed for the HTTP tests (Q12). `10.0.12` matches the `net10.0` target and the ASP.NET Core packages in `Paysys.Api.csproj` |
| `src/Paysys.Web/Layout/MainLayout.razor` | Rename `IsLoginPage` to `IsAnonymousPage` and also allow paths starting with `signup` (both places that use it) | Otherwise `/signup` redirects a signed-out visitor away |
| `src/Paysys.Web/Pages/Login.razor` | Add one line under the button: `<div class="mt-3 small">No account? <a href="signup">Sign up</a></div>`. Nothing else changes | The link from Sign in to Sign up that was asked for |
| `README.md` | Under "Signing in": self-registration, `Auth:Registration:Enabled`, new accounts are `user` with no bank accounts, **passwords longer than 128 characters can no longer sign in**. Setup step 4: `--update-existing`, its rules and the last-admin refusal. "Tests": the HTTP tests and `PAYSYS_TEST_PG` | The documented flow changes |

Nothing else may be edited. Specifically **not**:
- `LoginThrottle.cs`, `TokenService.cs`, `ClaimsPrincipalExtensions.cs`, `DenialResults.cs`, `RetryAfterResult.cs`;
- `TransactionAuditLog.cs` (and `ComputeHash` inside it), `TransactionAuditLogService.cs`, `AccessDenialRateLimiter.cs`;
- `DemoSeeder.cs`, other endpoint files, `PaysysDbContext.cs`, migrations and the model snapshot;
- `LoginRequest` and `LoginResponse`, `NavMenu.razor`, `AuthState.cs`, `AuthTokenHandler.cs`, Web `Program.cs`;
- existing test files, `tests/Paysys.TestSupport/*`, any `.csproj` other than `Paysys.Api.Tests`;
- `appsettings.json`, `launchSettings.json`, `as-is.md`.

## 3. Order of work

1. **Login hardening.** Write `LoginHardeningTests` first and confirm that T10 fails with `FormatException` today. If it does not, stop and report. Create `PasswordRules.cs`, then change `UserAuthenticator` (findings 2 and 3, and the password cap). Run T10, T11, T11b and the existing `UserAuthenticatorTests`.
2. **Hash format check in the importer.** Create `PasswordHashFormat.cs` and add the check to `ValidateConfiguration`. Run T17 and the existing `ConfigUserImporterTests`.
3. **Entity update method.** Change `User.cs` and write T19. Run the existing `UserEntityTests` and `UserTableTests`.
4. **Importer update mode and last-admin refusal.** Change `ConfigUserImporter` and part (a) of `Program.cs`, and write T12-T18c.
5. **Unique-violation constant.**
6. **DTOs.**
7. **Rate limiter.** Write T8 and T9.
8. **Registration service.** Write T1-T7.
9. **Wiring.** Change `AuthOptions`, the DI registrations, `AuthEndpoints` and `appsettings.Development.json`. Build.
10. **HTTP tests.**
    - (a) Add the package to the test csproj. Write `PaysysApiFactory` and check that `WebApplicationFactory<Program>` compiles. If `Program` is reported as not accessible, add `public partial class Program;` to `Program.cs` (A20). Never use `InternalsVisibleTo`.
    - (b) Write H1-H13. Run them with and without `PAYSYS_TEST_PG`: the ones without `[PostgresFact]` must pass with no database at all.
11. **Web.** Change `MainLayout.razor`, create `Signup.razor`, add the Login link. Build.
12. **README.**
13. **Full local run:** `dotnet test` with `PAYSYS_TEST_PG` pointing at a **local or CI Postgres, never Neon**. All existing tests, including `AuditChainTests` and `LoginThrottleTests`, must pass unchanged.
14. **Live step. Each command needs the user's go-ahead.** Only what the automated tests cannot cover. Against Neon, with no migration:
    1. Start Api and Web (`--launch-profile http`).
    2. **UI:** open `/signup` while signed out and check it is not redirected. Use both links between the pages. Sign up as a new user, see the success message, sign in, and check the nav shows "(user)".
    3. **413 on real Kestrel:** one `curl` with a body of about 9 KB. Expect 413 (A23).
    4. **Real socket address:** six `curl` sign-ups with an **invalid** body (an 11-character password, so nothing is written to Neon). The 6th gives 429. This confirms that Kestrel provides `RemoteIpAddress` and that requests are not all landing in the shared `"unknown"` bucket.
    5. **Read-only SQL:** `SELECT "Username","Role" FROM "Users" WHERE "NormalizedUsername" = '<name from step 2>';` gives `user`.
    6. The user created in step 2 stays in Neon (decision Q10).
15. **Dev server shutdown:** `taskkill /F /T /PID` for Api and Web, then confirm with `Get-Process`.

Commits: steps 1-2 together (login and import hardening), 3-4 together (importer update), 5-9 together (registration API), 10 alone (HTTP tests and csproj, plus the `Program.cs` line if it was needed), 11 alone (Web), 12 alone (README).

## 4. Tests

### 4.1 Unit and service tests

| # | Behaviour | Project | Needs `PAYSYS_TEST_PG` |
|---|---|---|---|
| T1 | `RegisterRequest` has exactly `Username` and `Password`. JSON that contains `"role":"admin"` deserializes with `JsonSerializerOptions.Web`, and the role is dropped | Api.Tests | No |
| T2 | A valid registration gives one row with `Role == "user"`, a new Id, a lower-case `NormalizedUsername`, a hash that verifies and is at most 512 characters. Also run through the JSON request containing `"role":"admin"`: still `user` | Api.Tests | **Yes** |
| T3 | A newly registered user can sign in with `AuthenticateAsync` with the right password, and not with a wrong one | Api.Tests | **Yes** |
| T4 | Rejected without a database (unreachable context): null or empty username; 65 characters; padded; a space inside; Cyrillic `аdmin`; the Kelvin sign `K`; a zero-width space; null password; 11 and 129 characters | Api.Tests | No |
| T5 | Accepted at the limits: a 64-character username, and 12- and 128-character passwords | Api.Tests | **Yes** |
| T6 | Sequential duplicate: `bob` twice, then `BOB`, both refused with `UsernameTaken`. One row, hash unchanged | Api.Tests | **Yes** |
| T7 | Concurrent duplicate: 8 parallel case variants on separate contexts give exactly 1 `Created` and 7 `UsernameTaken`, no exception, one row. Registering `admin` when an imported admin exists gives `UsernameTaken` and leaves the admin row unchanged | Api.Tests | **Yes** |
| T8 | `PartitionKey`: IPv4 addresses are separate; the same IPv6 /64 is shared and different /64s are separate; IPv4-mapped equals IPv4; null gives `"unknown"`. 5 acquisitions are allowed, the 6th is refused with `retryAfter > 0` | Api.Tests | No |
| T9 | A `DefaultHttpContext` with `RemoteIpAddress = 203.0.113.5` and a different `X-Forwarded-For` on each request: the 6th is refused | Api.Tests | No |
| T10 | **Finding 2:** a user stored with hash `"not-a-hash!"`: `AuthenticateAsync` returns null without throwing. Written first, it must fail on the current code | Api.Tests | **Yes** |
| T11 | **Finding 3:** 65-character and 1,000,000-character usernames with the unreachable context return null without throwing, which proves no query is made. A 64-character existing user still signs in | Api.Tests | The 64-character case **yes**, the rest no |
| T11b | **Password cap:** passwords of 129 and 1,000,000 characters with the unreachable context return null without throwing, which proves no query is made. An existing user with a 128-character password still signs in | Api.Tests | The 128-character case **yes**, the rest no |
| T12 | `updateExisting: true` with a new hash and role: the row is updated; Id, Username and CreatedAt unchanged; the result lists "role" and "password hash"; the output contains no hash | Api.Tests | **Yes** |
| T13 | Without the flag after a config change: rows unchanged | Api.Tests | **Yes** |
| T14 | Update mode with one valid update plus one clash: the import stops and **nothing** is written | Api.Tests | **Yes** |
| T15 | Update mode leaves rows that are not in config (a self-registered user) unchanged | Api.Tests | **Yes** |
| T16 | Config `Admin` against row `admin` with the same Id: matched, and the stored casing is kept | Api.Tests | **Yes** |
| T17 | The importer refuses a config hash that is not base64 or has an unknown marker byte, before any database access (unreachable context) | Api.Tests | No |
| T18 | **Last admin:** the table holds one admin and one user. An update-mode run that demotes the admin **and** changes the user's hash throws `UserImportException`. Afterwards **0 rows have changed**: the admin is still `admin` with its original hash, and the user's hash is unchanged | Api.Tests | **Yes** |
| T18b | With two admins, demoting one is allowed and is applied | Api.Tests | **Yes** |
| T18c | Importing a config that has only `user`-role entries into a table with no admin is allowed, because nothing is demoted (A14) | Api.Tests | **Yes** |
| T19 | `User.UpdateRoleAndPasswordHash` rejects role `superuser` or `Admin`, an empty hash and a 513-character hash, and never changes Id, Username, NormalizedUsername or CreatedAt | DAL.Tests | No |
| T20 | Existing tests pass unchanged: `LoginThrottleTests`, `UserAuthenticatorTests`, `ConfigUserImporterTests`, `UserEntityTests`, `UserTableTests`, `AuditChainTests` | Api / DAL / BLL | Throttle and entity no; the others **yes** |

T21 from the previous version (checking that the route is mapped) is **removed**. H8 tests the same thing over HTTP.

### 4.2 HTTP tests (`WebApplicationFactory<Program>`)

**How the factory works** (`PaysysApiFactory`, test code only; everything here was checked against the startup code above):

- **Reaching `Program`.** ASP.NET Core 10 generates `public partial class Program` for top-level-statement apps, so `WebApplicationFactory<Program>` should compile with no change to `Program.cs`. Step 10a checks this. If it fails, the single fallback is the `public partial class Program;` line listed in the files table. `InternalsVisibleTo` is not used, because it would expose every internal type of the Api to the tests.
- **Environment `Testing`** (`UseEnvironment("Testing")`). This is not Development, so **user-secrets, which hold the Neon connection string, are never loaded**. `appsettings.Development.json` is not loaded either, and `MapOpenApi` is skipped.
- **Settings.** `AddDataAccess` and `AddPaysysAuth` read configuration **when services are registered, before `Build()`**. So settings are supplied with `UseSetting(key, value)`, which goes into host configuration and is visible to code that reads it that early. They are **not** supplied through `ConfigureAppConfiguration`, which with minimal hosting may be applied too late for those reads. **Process environment variables are not used**, because they are shared by tests running in parallel. The settings:
  - `ConnectionStrings:Paysys`: the per-test database from `Db.Database.GetConnectionString()` for `[PostgresFact]` tests, otherwise the existing unreachable `Host=127.0.0.1;Port=1;…` string;
  - `Jwt:SigningKey`: a fixed 40-character test key;
  - `Auth:Registration:Enabled`: `true`, or `false` for H8;
  - `AuditVerification:InitialDelay`: `01:00:00`, so the hosted chain check never queries during a test.
- **Neon guard.** When the factory is created, it resolves `PaysysDbContext` in a scope and checks that `Database.GetConnectionString()` **equals the string it was given**. Otherwise it throws before any request is sent. This catches, for example, a `ConnectionStrings__Paysys` environment variable on the developer's machine that takes priority. Nothing in startup touches the database (there is no migration at startup, and the chain check is delayed), so no connection is opened before the guard runs.
- **Client address.** Under `TestServer`, `Connection.RemoteIpAddress` may be null, which would put every request in the shared `"unknown"` bucket. `ConfigureTestServices` adds an `IStartupFilter` whose middleware runs first and sets `RemoteIpAddress` from a **test-only** header `X-Test-Remote-Ip`. This lives only in the test project. The API itself never reads that header, nor X-Forwarded-For.
- **Limiter isolation.** **One factory per test**, so each test gets its own service provider and therefore its own `RegistrationRateLimiter` and `LoginThrottle` singletons. Tests that send several requests use their own `X-Test-Remote-Ip` values as well.
- **HTTPS redirection.** Clients are created with `BaseAddress = https://localhost` and `AllowAutoRedirect = false`. Because the request scheme is already https, `UseHttpsRedirection` never redirects, and any accidental redirect would show up as a 307 rather than being followed. CORS does nothing because no `Origin` header is sent.
- **Content root.** Mvc.Testing finds `src/Paysys.Api` from the attribute it generates for the referenced project, so `appsettings.json` (Jwt issuer and audience) is loaded. If that lookup fails, the factory sets the content root explicitly with `UseContentRoot`.
- **Request size under `TestServer`.** I could not confirm from this repository that `TestServer` provides the `IHttpMaxRequestBodySizeFeature` that the size limit relies on. So the 8 KB limit is checked in two ways: H9 checks it is set on the endpoint and that a large body is never accepted, and the **real 413 is checked on Kestrel in live step 14.3**.

| # | Behaviour | Needs `PAYSYS_TEST_PG` |
|---|---|---|
| H1 | `POST /api/auth/register` with a valid body gives **201** and `{ username }`. The stored row has role `user` | **Yes** |
| H2 | A body with `"role":"admin"` gives **201**, and the stored role is **`user`** | **Yes** |
| H3 | **400** with a `message` field for a 65-character username, a username with a space, and an 11-character password | No |
| H4 | **409** for `bob` sent twice in a row, and for `BOB` after `bob`. One row | **Yes** |
| H5 | Five invalid bodies from one test address get 400. The **6th gets 429** with a `Retry-After` header (a whole number of seconds, at least 1) and a `message`. This proves the limit is charged before validation | No |
| H6 | The same as H5, but each request carries a different `X-Forwarded-For`. The 6th is still **429** | No |
| H7 | After address A is at 429, address B still gets 400 rather than 429. This proves the test address override works, so H5 and H6 mean something | No |
| H8 | With `Auth:Registration:Enabled=false`, `POST /api/auth/register` gives **404**, and login still works as a route (a bad login gives 401) | No |
| H9 | The register endpoint's metadata (read from the factory's `EndpointDataSource`) holds a request size limit of 8192 bytes. A body of about 9 KB (a 9,000-character password) never gets 201 or 500: it gets 413, or 400 if `TestServer` does not enforce the limit | No |
| H10 | **Login: malformed stored hash.** A user inserted directly with hash `"not-a-hash!"` gets **401** (not 500) and the standard message | **Yes** |
| H11 | **Login: 65-character username** gets **401** (the factory uses the unreachable database, so this also proves no query is made) | No |
| H12 | **Login: 129-character password** gets **401**, same as H11 | No |
| H13 | **Login lockout on the new paths:** five logins with a 129-character password for `alice` get 401, and the **6th gets 429**. This proves the endpoint still calls `RecordFailure` for these refusals | No |

## 5. Assumptions

- **A1** "Sign-up" means anyone can register themselves. There is no approval step or invitation.
- **A2** The role is always `User.UserRole`. An unknown `role` field is ignored, not rejected.
- **A3** No automatic sign-in. The response is 201 with the username and no token (decision Q1).
- **A4** Registration is switched on by config: `Auth:Registration:Enabled`, false when absent, true in Development. When it is off, the route is not mapped and gives 404 (decision Q2).
- **A5** Registration writes nothing to the audit hash chain. Its record is a log line (decision Q3).
- **A6** A registered user gets no bank account. Creating accounts stays admin-only.
- **A7** The username rule at registration is ASCII letters, digits, `.`, `_` and `-`, 1-64 characters, on top of the entity rules (decision Q5).
- **A8** Password: 12 to 128 characters, defined once in `PasswordRules`. No composition rules, no trimming, no Unicode normalization, no breached-password check (decision Q4).
- **A9** Rate limit: per address (IPv6 per /64), a bucket of 5 that refills 1 every 12 minutes, charged on every request that reaches the handler. There is no global cap (decision Q6).
- **A10** The 8 KB body limit is applied by ASP.NET Core routing on Kestrel, and live step 14.3 checks it. If it does not hold, the 128-character check still prevents hashing a large input, but the body is still parsed.
- **A11** The client address is `Connection.RemoteIpAddress`. No `UseForwardedHeaders` is added.
- **A12** Confirm password is checked on the client only.
- **A13** **What "update" means for the importer (decision Q7: kept as written).**
  - Updating happens only with `--import-users --update-existing`.
  - A row is updated only when the Id **and** the normalized username both match.
  - Only `Role` and `PasswordHash` may change. Id, Username (including its casing), NormalizedUsername and CreatedAt never change.
  - Every clash stops the whole import with nothing written.
  - Rows not in config, including every self-registered user, are never touched.
  - Promotion to admin is allowed.
  - The hash is compared as a plain string.
  - One transaction, all or nothing.
- **A14** **The last admin cannot be demoted (decision Q7).**
  - The check runs only when the run changes at least one role from `admin` to `user`. If no `admin` row would then remain, the whole import stops and nothing is written.
  - Reason for that trigger: a literal "no admin remains after the import" rule would also refuse importing a user-only config into a table that never had an admin, and that is not a demotion (T18c).
  - The count uses the table as read inside the import's transaction. A concurrent import is not locked out, so two runs at once could each demote a different admin (R13).
- **A15** Finding 2 is fixed in two places: login treats a malformed hash as a failed login, and the importer refuses malformed hashes. Rows already in Neon are not checked.
- **A16** Every early login refusal (username over 64, password over 128, malformed stored hash) verifies a **fixed private placeholder password** against the dummy hash. The time taken matches a normal failure, and the caller's oversized input is never hashed. The endpoint is unchanged, so `RecordFailure` runs and the response is the standard 401.
- **A17** **Login refuses passwords over `PasswordRules.MaxLength` (128) before any database query or hashing (decision Q8).** The consequence for existing users is R18.
- **A18** Tests are in scope at the user's request.
- **A19** The services stay in `Paysys.Api/Auth`, next to `UserAuthenticator`.
- **A20** `Program` can be reached from the tests through the generated `public partial class Program` in ASP.NET Core 10. If the build shows otherwise, the fallback is one line in `Program.cs`. This is checked in step 10a.
- **A21** In the HTTP tests, `UseSetting` values are seen by the configuration reads that happen during service registration. The Neon guard in the factory checks this on every test, so if the assumption is wrong the tests fail instead of quietly using another database.
- **A22** `TestServer` may leave `RemoteIpAddress` null, which is why the tests set it through a test-only startup filter. The production code is not changed for this.
- **A23** `TestServer` may not enforce the request size limit, so the real 413 is a live check (step 14.3).

## 6. Risks and failure modes (authentication)

- **R1 Username enumeration through registration.** A 409 shows that a username exists, which undoes the care the login endpoint takes. This is accepted (decision Q5). It is limited by the per-address limit and the config switch.
- **R2 Timing.** Every valid sign-up hashes once and attempts one insert. On login, every early refusal and the malformed-hash path run the placeholder dummy verify, so none of them is a fast oracle. One known difference: null or empty input returns without hashing, as it did before this change. That reveals nothing about which users exist.
- **R3 Bypassing the rate limit.**
  - X-Forwarded-For is never read (T9, H6).
  - Rotating addresses still gets around a per-address limit. IPv6 /64 keying narrows this for IPv6 only.
  - `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` on a future host would make the address spoofable.
  - A real proxy without forwarded-header setup would put every client in one bucket.
  - Any deployment must configure `KnownProxies`.
- **R4 Race on the unique index.** Caught by **constraint name** and returned as 409 (T7, H4). Catching every `DbUpdateException` would hide a database outage as "username taken". An index rename would turn 409 into 500, and T7 would catch that. There is no pre-check SELECT, so there is no check-then-insert window.
- **R5 Hash length.** A V3 hash is about 84 characters. If the hasher ever produced one longer than 512, the result would be 500, not a stored hash that has been cut short (T2).
- **R6 Very large passwords.** On sign-up: the 8 KB body limit (A10), the 128-character check before hashing, and the rate limit charged first. On login: the same 128-character check before the lookup and before hashing. A 30 MB login body is still parsed by the framework, because the login endpoint has no size limit (out of scope).
- **R7 Hashing as a CPU DoS.** Each accepted sign-up costs one PBKDF2 run. Every login failure also costs one, including the new refusals, by design (R2). That is the same as before.
- **R8 Unicode usernames.** The ASCII allowlist rules out look-alike characters, characters that lower-case onto ASCII, and invisible characters at sign-up (T4). Imported usernames are not restricted, because they are operator-controlled.
- **R9 Mass assignment of Role.** There is no Role in the DTO, no role parameter, a constant in the code and a database check constraint (T1, T2, H2).
- **R10 Malformed hash, other cases.** Only `FormatException` is caught, so real bugs are not hidden. A hash that says it uses a huge iteration count can only come from the operator.
- **R11 Lockout interaction.** `LoginThrottle` is unchanged.
  - The new refusals (username over 64, password over 128, malformed hash) **count towards lockout** (H13), so they cannot be used to make unlimited attempts.
  - Someone can fail 5 sign-ins on a name before it is registered, which locks the real person out for a while after they register it.
- **R12 Each registered user adds to the denial budget.** Each registered user adds 1 denial per second against the global audit lock. Registration stays off by default, and a global cap is needed before it is turned on in public (decision Q6).
- **R13 Importer update.**
  - Running it by mistake can lock someone out (new hash) or remove admin rights. **Removing the last admin is now refused** (T18).
  - Tokens issued before a role or hash change stay valid for up to 30 minutes.
  - Two imports at the same moment could each demote a different admin and together leave none. Both run in read-committed transactions and `User` has no concurrency token. An operator command run by one person makes this unlikely. Taking a row lock would fix it, but that is not planned.
  - Output never contains a hash (T12).
- **R14 DemoSeeder ownership.** The seeder may give demo accounts to a stranger who registered. Unchanged in this change (decision Q9).
- **R15 Username squatting.** Someone can register a name before the operator imports it, after which the import stops on the clash (fails safe).
- **R16 Web.** The password strings stay in Blazor memory until cleared. `/signup` is the only extra anonymous path.
- **R17 Logging and personal data.** No IP, password, hash or attempted username is logged.
- **R18 Imported users with passwords over 128 characters are locked out.**
  - **Effect:** a config user whose real password is longer than 128 characters can never sign in after this change. Every attempt is a normal 401 and counts towards lockout, which makes it look exactly like a wrong password.
  - **Why the importer cannot catch it:** it only ever sees PBKDF2 hashes, and the length of the original password cannot be recovered from a hash.
  - **What to do instead:** the README states the 128-character limit for config users. The fix for an affected user is a new hash of a password of at most 128 characters, applied with `--import-users --update-existing`.
  - The Warning log is only for malformed hashes. A long-password refusal is indistinguishable from a wrong password, on purpose.
- **R19 Test isolation and Neon.**
  - HTTP tests run in environment `Testing`, so user-secrets are not loaded, and each test names its database explicitly.
  - The guard in the factory stops a test before any request if the connection string the app ended up with is not the test's own.
  - Each test has its own factory, so limiter and throttle state cannot leak between tests.
  - If the guard were ever removed, a machine-level `ConnectionStrings__Paysys` variable could point tests at Neon. The guard must stay.

## 7. Out of scope

- Email verification, password reset or change, user management screens, deleting or disabling users, revoking tokens.
- Any change to `ComputeHash`, `AppendAndSaveAsync`, the chain lock, `AccessDenialRateLimiter`, the authorization policies or ownership checks. Any change to `LoginThrottle`.
- `UseForwardedHeaders` or proxy setup, CAPTCHA, a global registration or denial cap, breached-password checks, reserved usernames.
- A request size limit on login.
- Creating a bank account for a new user. Changes to `DemoSeeder`.
- The out-of-date Login dev hint, a NavMenu "Sign up" item and any other UI changes (separate small plans).
- Checking hashes already stored in Neon. Any migration. Updating `as-is.md`.

---

## Open questions (resolved)

1. **Auto sign-in.** Decision: the default. No token: the page shows "Account created" and the person signs in separately.
2. **Who can register.** Decision: the default. `Auth:Registration:Enabled`, on in Development and off elsewhere.
3. **Audit chain.** Decision: the default. Log line only, no chain entry.
4. **Password policy.** Decision: the default. 12 to 128 characters (PCI DSS 8.3.6) with no composition rules.
5. **Usernames and enumeration.** Decision: the default. ASCII allowlist, and a 409 that shows a name is taken is accepted.
6. **Global cap.** Decision: the default. Not in this change. Needed before registration is turned on in public (R12).
7. **Importer update and the last admin.** Decision: A13 is kept as written. **Demoting the last admin is refused**: the whole import stops and nothing is written (A14, T18).
8. **Login password cap.** Decision: **yes**. Login refuses passwords over `PasswordRules.MaxLength` (128) before any lookup or hashing, still takes as long as a normal failure, and still counts towards lockout (A16, A17, T11b, H12, H13, R18).
9. **DemoSeeder.** Decision: the default. Unchanged here; a separate plan if wanted (R14).
10. **Neon cleanup.** Decision: the default. Users created during the live step stay in Neon.
11. **Login dev hint.** Decision: the default. Left as it is; a separate small plan.
12. **HTTP tests.** Decision: **yes**. Add `Microsoft.AspNetCore.Mvc.Testing` 10.0.12 and the H1-H13 tests with `WebApplicationFactory<Program>`. The real 413 stays in the live step because `TestServer` may not enforce it (A23).
