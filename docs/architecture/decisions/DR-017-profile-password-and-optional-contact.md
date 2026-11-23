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
- A changed password ends every token issued before it (added 23 Nov, `c0ee2ec`). Each token carries a stamp (`pws`), a short SHA-256 fingerprint of the stored password hash; on every request the API compares it with the user's current hash and refuses the token if they differ, or if the user no longer exists. The cost is one small read of the Users table per authenticated request. Tokens issued before this change have no stamp and were refused after the deploy, so everyone signed in again once. Known limit: this is the only event that revokes tokens; there is still no "sign out everywhere" button and a deleted role change does not end a token before it expires. After a change the web page signs the person out so they sign in with the new password.
- Password recovery without an admin still needs a mail service, which Paysys does not have.
- Evidence: `ProfileEndpointTests` (details, cleaning and refusal of bad formats, audit only when something changed, wrong current password, lockout, the new password signing in).
