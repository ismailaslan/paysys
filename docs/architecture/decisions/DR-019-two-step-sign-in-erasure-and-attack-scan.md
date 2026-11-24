# DR-019 Two-step sign-in, self-service erasure and export, and an attack scan

**Status:** Accepted. Recorded on 2026-11-24, with the change that built it.
**Owner:** Ismail Aslan.

## Context

The compliance mapping listed three gaps the code could close without outside help: no second sign-in factor, no way for a person to download or erase their data, and no attack testing of the running API.

## Decision

- **Two-step sign-in, optional, per person.** A time-based code (RFC 6238) from an authenticator app. Set-up from Profile: the API makes a secret and keeps it encrypted (AES-GCM, key derived from the API signing key); the person proves the app works by typing a code; only then is it on, and eight one-time recovery codes are shown once (stored as SHA-256 hashes). With it on, `POST /api/auth/login` answers 202 with a signed 5-minute challenge instead of a token, and `POST /api/auth/login/mfa` takes the challenge and a code (or a recovery code). A code works once (the newest used step is stored), one step of drift either side is allowed, and wrong codes count toward the same lockout as wrong passwords. Turning it off needs the password and a code. New columns on Users: `TotpSecret`, `TotpEnabledAt`, `TotpLastStep`, `RecoveryCodeHashes` (migration `20261126000000_AddTotp`).
- **Export and erasure.** `GET /api/profile/export` returns the person's data as JSON (no hashes, no card numbers). `POST /api/profile/delete` (individuals only, password required, accounts empty, no open hold) anonymises the user row (name, contact, PIN, sign-in, two-step secret), closes cards and clears payee codes; accounts, payments and the hash-chained audit log stay and point at the anonymous row.
- **Privacy page** at `/privacy` and a proposed retention rule (`retention-policy.md`).
- **Attack scan.** A manual workflow (`scan.yml`) starts a throwaway API and database on the CI runner and runs the OWASP ZAP API scan, signed in as a normal user. Correction (24 Nov 2026, CR-019): the workflow had the `-S` option, which makes the scan a passive baseline that sends no attacks, so the text "active attacks" was wrong; the workflow now runs active attacks and also scans the gateway. The first run (passive) found malformed ids answered with 500 (now 400) and missing `X-Content-Type-Options` and `Cross-Origin-Resource-Policy` headers (now set); one low-risk warning remains.

## Consequences

- Two-step sign-in is not required for anyone, admins included, so a stolen admin password still signs in alone unless that admin turned it on. Making it mandatory for admin and bank logins is a one-line policy change and not done.
- If the API signing key is replaced, stored secrets cannot be read: each person sets two-step sign-in up again (their recovery codes still work for signing in until then).
- The anonymous user row keeps its Id; the account name field is not cleared; merchants, banks and admins are closed by the operator, not by themselves.
- The scan is automatic and unauthenticated-by-design for everything except one normal user; it is not a penetration test and finds only what its rules look for.
