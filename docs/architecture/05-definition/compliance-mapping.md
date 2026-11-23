# Compliance mapping (PCI DSS scope, GDPR basics)

Status: 23 November 2026. Owner: Ismail Aslan.

**What this is.** A mapping of controls that exist in Paysys to the areas PCI DSS and GDPR care about, and a list of what is missing. **What this is not.** It is not an assessment, an attestation or a certification, and nobody qualified (QSA, DPO, lawyer) has reviewed it. Paysys is a demo with no real card data and no real customers. Where this says "evidenced", a test in CI covers it; where it says "reading", it rests on code reading only.

## 1. Data Paysys holds

| Data | Where | Note |
| --- | --- | --- |
| Username, role | Users table | The only identifying fields. No email, name, address or phone |
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
| Req 7/8: access control, authentication | JWT, ownership and role checks, login lockout, per-user and registration rate limits, 6-digit PIN with lockout restored from the audit log, no public bank sign-up | Partly evidenced (tests per layer); reading for the rest | No multi-factor authentication. A 6-digit PIN is weak by design (demo) |
| Req 10: logging and monitoring | Hash-chained audit entry for every state change, chain verified by a background service, tamper tests, tail anchor against deletion of the newest entries | Evidenced (AuditChainTests) | The anchor is in memory only; nobody is alerted on a failure; no log retention policy |
| Req 6: secure development | Tests per layer in CI, secret scanning (gitleaks, local hook, platform push protection), SSRF checks on bank URLs | Evidenced (CI) | No dependency-vulnerability scan, no penetration test, no code review by a second person |
| Req 11: security testing | Load test (not a security test) | n/a | No vulnerability scan or penetration test |
| Req 12: policies | None | n/a | No security policy, incident response plan or staff training |
| Req 2/9: configuration, physical | Provided by the hosting companies (Render, Neon, GitHub) | Not assessed | Their compliance reports were not reviewed |

## 3. GDPR basics

**Position.** The personal data is small (a username and ids), but a username can identify a person and transaction history is personal data once tied to one, so GDPR would apply to real users in the EU.

| GDPR topic | What exists | Gap |
| --- | --- | --- |
| Data minimisation (Art. 5) | No email, name, address or phone collected (reading of the User entity) | Usernames may be real names if users choose so |
| Security (Art. 32) | Hashed passwords and PINs, access control, audit chain, rate limits (see section 2) | No encryption-at-rest control of our own; relies on the host, not verified |
| Lawful basis, privacy notice (Art. 6, 13) | None | No privacy notice, no stated basis |
| Access and portability (Art. 15, 20) | None | No endpoint or process to export a user's data |
| Erasure (Art. 17) | None | No delete-account function. The audit log is append-only and hash-chained by design, so erasure needs a decided approach (for example, audit entries keep only a random id and the user row is removed, so the id no longer points at a person) |
| Retention (Art. 5) | The gateway deletes payment requests and processed-event records after 30 days | Nothing else has a retention period (the bank side keeps everything). Financial-record retention law would also apply to a real business |
| Processors and transfers (Art. 28, 44) | Render, Neon, GitHub are used | No data-processing agreements reviewed; data location not verified |
| Breach notification (Art. 33) | None | No process; no alerting |
| Records of processing, DPIA (Art. 30, 35) | None | Not produced |

## 4. Beyond these two: what a real business needs

Paysys moves value between accounts it holds, which in practice means it needs a licensed partner (payment institution or bank), as planned. The partner's licence, AML/KYC obligations, PSD2/SCA, safeguarding of funds and local reporting are separate regimes and are not covered here.

## 5. Next steps if this becomes a business (in order)

1. Choose the partner and settle who is the cardholder-data owner; decide whether card numbers ever touch Paysys.
2. Add erasure/export design that works with the append-only audit log.
3. Alerting on audit-chain failure and flagged transactions; persist the chain anchor.
4. Multi-factor authentication; dependency scan; an external penetration test.
5. Policies: security, incident response, retention, privacy notice; review processor agreements.
6. Only then engage a QSA / DPO for a real assessment.
