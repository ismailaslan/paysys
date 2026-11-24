# Compliance mapping (PCI DSS scope, GDPR basics)

Status: 24 November 2026 (re-checked after the gateway and hold changes). Scope: a demonstration system on the current infrastructure (Render free tier, Neon); nothing here claims readiness for real money. Owner: Ismail Aslan.

**What this is.** A mapping of controls that exist in Paysys to the areas PCI DSS and GDPR care about, and a list of what is missing. **What this is not.** It is not an assessment, an attestation or a certification, and nobody qualified (QSA, DPO, lawyer) has reviewed it. Paysys is a demo with no real card data and no real customers. Where this says "evidenced", a test in CI covers it; where it says "reading", it rests on code reading only.

## 1. Data Paysys holds

| Data | Where | Note |
| --- | --- | --- |
| Username, role | Users table | Identifying fields from sign-up. No name or address |
| Email, phone | Users table | Optional, added by the user on the profile page, not verified and not used for anything (DR-017). Personal data once filled in |
| Password hash, PIN hash | Users table | ASP.NET Core Identity hasher; never logged (reading) |
| Account ownership (owner id) | Accounts table | A random id, not a name |
| Card token, brand, status | Card tokens table | The card number is not stored (evidenced) |
| Transactions, amounts, currencies | Transactions, audit log, fraud ledger | Linked to people only by ids |
| Actor id on audit entries | Audit log | Append-only and hash-chained |
| Terminal keys | Gateway database | Stored as a hash, shown once (reading) |

## 2. PCI DSS: scope and controls

**Scope position.** Paysys never stores the card number, and the tokenization project is the only code that touches it (evidenced). But the number is still received and processed in memory when a card is tokenized, so the system is not out of scope. In the demo the numbers are test or issued demo numbers. Accepting real card numbers would put the tokenization endpoint inside the cardholder data environment and require a formal scope and an assessment. The usual way to shrink that scope is to let a certified provider take the card number and give Paysys only the token. That is a business decision for later, not something built.

| PCI DSS area | What exists | Level of evidence | Gap |
| --- | --- | --- | --- |
| Req 3: protect stored account data | Card number, first 12 digits and bad-checksum numbers searched for in every column of every table; none found. No column is named like a card number or CVV | Evidenced (TokenizationEvidenceTests) | Evidence covers the test database, not production storage. CVV handling for real cards is not designed |
| Req 3/10: no card data in logs | Tokenize rejections log without the number; rejected responses do not echo it | Evidenced | Hosting-provider logs and request tracing outside the app are not covered |
| Isolation of card handling | No other project references card-handling code; allowed files are listed in a test | Evidenced (CardIsolationTests) | Isolation is by code structure, not by network or process separation |
| Req 4: encryption in transit | HTTPS redirection in the app; hosts serve HTTPS | Reading | TLS settings are the hosts' defaults and were not assessed |
| Req 7/8: access control, authentication | Optional two-step sign-in with an authenticator app (TOTP, RFC 6238 test values, replay refused, 8 one-time recovery codes, secret encrypted in the database, wrong codes count toward the lockout; tests, DR-019), JWT, ownership and role checks, login lockout, per-user and registration rate limits, 6-digit PIN with lockout restored from the audit log, no public bank sign-up | Partly evidenced (tests per layer); reading for the rest | Two-step sign-in (authenticator app code) exists and is optional, so it is off unless a person turns it on; it is not forced for any role, not even admin. A 6-digit PIN is weak by design (demo) |
| Req 10: logging and monitoring | Hash-chained audit entry for every state change, chain verified by a background service, tamper tests, tail anchor against deletion of the newest entries | Evidenced (AuditChainTests) | The anchor is in memory only; nobody is alerted on a failure; no log retention policy |
| Req 6: secure development | Tests per layer in CI, secret scanning (gitleaks, local hook, platform push protection), SSRF checks on bank URLs | Evidenced (CI) | No dependency-vulnerability scan, no penetration test, no code review by a second person |
| Req 11: security testing | Load test (not a security test) | n/a | No vulnerability scan or penetration test |
| Req 12: policies | None | n/a | No security policy, incident response plan or staff training |
| Req 2/9: configuration, physical | Provided by the hosting companies (Render, Neon, GitHub) | Not assessed | Their compliance reports were not reviewed |

### Re-check of the gateway (24 November 2026; holds were built and then removed, CR-020)

| Control | What exists | Level of evidence | Gap |
| --- | --- | --- | --- |
| Messages from the bank to the gateway (PCI Req 4, 6 in spirit) | HMAC-SHA256 over timestamp and body, 5-minute window, each event id once, a payment applied once, the message matches only the request it was made for (same terminal, amount, currency, still open). | Evidenced (BankEventEndpointTests, HoldGatewayTests, PaymentMatchingTests) | The queue is a database table (CR-019): a message is sent again until the gateway accepts it, for up to 24 hours, and then dropped; no job compares the two sides' records beyond that |
| Attack testing | The ZAP scan run on 24 Nov 2026 was a passive baseline only (the workflow had the `-S` option, which skips attacks; the earlier text said active attacks, which was wrong). The workflow now runs active attacks on the bank API and on the gateway (CR-019) | Passive baseline evidenced for the bank API (24 Nov 2026); the active scan is written and not yet run | The gateway service and the new gateway hold calls are not scanned; no penetration test |

## 3. GDPR basics

**Position.** The personal data is small (a username, ids, and an optional email and phone), but a username can identify a person and transaction history is personal data once tied to one, so GDPR would apply to real users in the EU.

| GDPR topic | What exists | Gap |
| --- | --- | --- |
| Data minimisation (Art. 5) | Email and phone are optional and only stored; no name or address is collected (reading of the User entity, DR-017) | Usernames may be real names if users choose so. Email and phone have no use yet, so a real service would not collect them until they do |
| Security (Art. 32) | Hashed passwords and PINs, access control, audit chain, rate limits (see section 2) | No encryption-at-rest control of our own; relies on the host, not verified |
| Lawful basis, privacy notice (Art. 6, 13) | A public Privacy page (24 Nov 2026, `/privacy`, linked in the footer): what is held and not held, why, the person's rights, what stays after deletion, retention (tested for its sections) | No lawful basis stated beyond "to run the demonstration"; not reviewed by a qualified person |
| Access and portability (Art. 15, 20) | `GET /api/profile/export` and Profile, Your data: the person's profile, accounts, cards, payments and activity as a JSON file; no password or PIN hash, no card number (tests) | Lists are limited to the newest 5,000 entries; audit entries are exported as a short activity list, not the raw chain |
| Erasure (Art. 17) | `POST /api/profile/delete` and Profile, Your data (24 Nov 2026): after a password check the user row is anonymised (name, email, phone, PIN and sign-in removed), cards are closed and tokens issued before are refused. Accounts, payments and audit entries stay and point at the anonymous row; audit entries carry only an internal id (tests) | Individuals only; a merchant, bank or admin account is closed by the operator. Refused while an account holds money or a hold is open. The anonymous row keeps its Id, so someone holding other records of that Id could still link it; accounts keep their name field |
| Retention (Art. 5) | The gateway deletes payment requests and processed-event records after 30 days. Personal details are kept while the account exists and removed on deletion; the written rule is in `retention-policy.md` (proposed) | No automatic expiry of unused accounts; the period for financial records is for the owner to set. Financial-record retention law would also apply to a real business |
| Processors and transfers (Art. 28, 44) | Render, Neon, GitHub are used | No data-processing agreements reviewed; data location not verified |
| Breach notification (Art. 33) | None | No process; no alerting |
| Records of processing, DPIA (Art. 30, 35) | None | Not produced |

## 4. Beyond these two: what a real business needs

Paysys moves value between accounts it holds, which in practice means it needs a licensed partner (payment institution or bank), as planned. The partner's licence, AML/KYC obligations, PSD2/SCA, safeguarding of funds and local reporting are separate regimes and are not covered here.

## 5. Next steps if this becomes a business (in order)

1. Choose the partner and settle who is the cardholder-data owner; decide whether card numbers ever touch Paysys.
2. Erasure and export are built (above); the remaining decision is the financial-record retention period.
3. Alerting on audit-chain failure and flagged transactions; persist the chain anchor.
4. Two-step sign-in can now be required per role (CR-019, setting `Auth:RequireTwoStepRoles`); the owner kept it optional for the demo (24 Nov 2026); dependency scan; an external penetration test (an automatic ZAP attack scan runs in CI on demand, see `scan.yml`; it is not a penetration test).
5. Policies: security, incident response, retention, privacy notice; review processor agreements.
6. Only then engage a QSA / DPO for a real assessment.
