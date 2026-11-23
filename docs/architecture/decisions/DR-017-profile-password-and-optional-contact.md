# DR-017 Own password change and optional, unverified contact details

**Status:** Accepted. Recorded on 2026-11-23, with the change that built it (CR-014).
**Owner:** Ismail Aslan.

## Context

A user could not change a password (only an admin could reset one, because there is no email to send a link to), could not change a PIN from any screen, and had no profile page. Email and phone were not collected at all.

## Decision

- A signed-in person can change their own password with `POST /api/profile/password`. The current password is required and wrong guesses count on the same lockout as sign-in (`LoginThrottle`, keyed by username), so guessing it there is no easier than at the login page. The new password follows the same rules as sign-up.
- Email and phone are optional columns on the user, edited with `PUT /api/profile/contact`. The API checks the format and stores a cleaned value. They are **not verified and not used** for sign-in, recovery or notifications, and the screen says so.
- Each change writes an audit entry (`User.PasswordChanged`, `User.ContactChanged`, `Rejected.Password` for a wrong current password). The entries say that something changed and never carry the password or the details.
- The PIN is changed through the existing `POST /api/pin`, which already needs the current PIN and has its own lockout.

## Consequences

- Email and phone are personal data held with no purpose beyond display. They exist so that verification and recovery can be added later without a schema change. Compliance mapping updated (data inventory and data minimisation rows).
- A changed password does not end tokens already issued: a JWT stays valid until it expires. There is no token revocation.
- Password recovery without an admin still needs a mail service, which Paysys does not have.
- Evidence: `ProfileEndpointTests` (details, cleaning and refusal of bad formats, audit only when something changed, wrong current password, lockout, the new password signing in).
