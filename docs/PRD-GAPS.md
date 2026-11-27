# PRD gaps: backend work still to do

> **Reminder.** When all the features below are implemented, review this file, `docs/PRD-GAP-LOG.md` and `docs/FLOW.md` before the team meeting where hardware and web confirm the flow. Update the Status lines in the gap log first.

Written 2026-11-15 from a check of the PRD (`../TAMS PRD ...md`), `docs/STATUS.md`, `BACKLOG.md`, the deployment and biometric documents, and the code on `dev`. Estimates are rough and mine, not the team's. Updated 2026-11-17 after the backend work was built and merged (see the Status column and "Build notes" below).

## Work index (single list; details live where noted)

This is the one list of backend work still to do. Each item's tasks are written once, in the file named in the last column. Jira tickets were created 2026-11-15 (TAMS-119 to TAMS-132, assigned to Stanleigh). As of 2026-11-17 TAMS-119 to 128 are Done in Jira and TAMS-129 to 132 are open with comments on what is left. 325 tests pass on `dev` and a Compose smoke run passed.

| ID | Item | Size | Needs first | Details | Status |
|---|---|---|---|---|---|
| E6 | Refuse DAMAGED items at the kiosk; show item name and condition | 0.5 d | none | `PRD-GAP-LOG.md` | Built, merged PR #15, TAMS-119 Done |
| E7 | Undo the last tapped item (kiosk Cancel) and web removal of a wrongly lent item | 1 d | none | `PRD-GAP-LOG.md` | Built, merged PR #20, TAMS-120 Done |
| E8 | Several items per checkout and return, staff setting (default 1), "Scan item or OK" loop | 3 d | E6, E7 | `PRD-GAP-LOG.md` (gap S1) | Built, merged PR #23, TAMS-121 Done |
| E1 | Event borrower record, flag with override reason, coordinator freeze, liability | 2.5 d | none | Part 2, Q1 below | Built, merged PR #18, TAMS-122 Done |
| E5 | Damage liability against the last borrower, chosen by staff | 1 d | E1 | Part 2, Q1 below | Built, merged PR #22, TAMS-123 Done |
| E4 | Push anomalies, transit timeouts and stations offline to staff, with full details | 1 d | none | Part 2, Q4 below | Built, merged PR #13, TAMS-124 Done (real FCM untested) |
| E3 | Excel export and import for checklists and audit reports | 1 d | none | Part 2, Q3 below | Built, merged PR #12, TAMS-125 Done |
| E9 | Admin reports list and detail (`/reports`), a scope addition the PRD does not list | 1 d | none | `docs/REPORTS.md` | Built, merged PR #39, TAMS-134 Done. PRD §6.3 and the web screens table updated 2026-11-27. 354 tests pass |
| E2 | Reword FR-6.5 (holds stay until staff lift) and tell web | 0.25 d | none | Part 2, Q2 below | Built (docs and a test only), merged PR #14, TAMS-126 Done. PRD wording still to change |
| G2 | Station credentials for room stations | 1.5 d | none | Part 1 | Built, merged PR #19, TAMS-127 Done. Hardware must load the key |
| G1 | Real fingerprint matcher (SourceAFIS on the sensor image, decided) | 4 to 6 d | real captures from TAMS-52 | Part 1 | Built phases 2 to 4, merged PR #17 and #25, TAMS-128 Done. Phase 1 (real captures, threshold) NOT done |
| G4 | Deployment and hardening | 2.5 d | TAMS-19 | Part 1 | Repo side merged PR #11. Server, domain, TLS, disk encryption NOT done. TAMS-129 open |
| G3 | Backups and restore | 1.5 d | G4 | Part 1 | Repo side merged PR #16, local restore drill passed. Bucket, VPS run, real-backup drill NOT done. TAMS-130 open |
| G5 | Data retention and deletion | 2 d | G4 | Part 1 | Repo side merged PR #21. Real end-of-project purge NOT run. TAMS-131 open |
| G6 | Measure NFR-1 to NFR-5 | 1.5 d | G1, G4 | Part 1 | Scripts merged PR #24, laptop smoke run only. Official NFR numbers NOT measured. TAMS-132 open |

About 23 to 25 days in all (estimate). Suggested order was: E6, E7, E8, E1, E5, E4, E3, E2, then G2, G1, G4, G3, G5, G6.

**Other open items that already live elsewhere (not repeated here)**
- `docs/STATUS.md` open decisions #3 (custodian fingerprint rights, FR-9.4 says admin-initiated) and #4 (who can read `/users`).
- `docs/STATUS.md` known gaps: `_user_id` column names versus the PRD for web and mobile, missing timestamps on sections and categories, roster CSV columns that are our own design, settings ranges that are guard rails, TAMS-32's wrong Jira description, and several tickets without acceptance criteria.
- Pending PRD wording changes and the team meeting: see the table in `docs/PRD-GAP-LOG.md`, Part 2.
- Not backend: the letter to the school (new date, adviser endorsement, region check, fallback for students who do not enroll).

**Already done, so removed from the older `BACKLOG.md`:** push for other notices (TAMS-97, merged), and the smoke test of refresh tokens, roster import and the QR sheet (2026-11-14).

## Build notes (2026-11-17): where the code differs from the plan

- **G1 SourceAFIS.** The kiosk uploads raw 8-bit gray (`image` base64, `width`, `height`, 64 to 1024 pixels a side, 512 KB at most). PNG and JPEG are refused on purpose, because SourceAFIS 3.14.0 pulls in ImageSharp 2.1.3 (known advisories). We pin 2.1.13, the newest Apache-licensed release (3.x and 4.x need a paid licence for Release builds). Five advisories remain in code we never call. The match threshold (`Kiosk:SourceAfisThreshold`, default 40) is a guess until real captures exist. Old `RAW_V1` rows still match through the old path. Hardware TAMS-52 and 53 must send images.
- **G2 station keys.** The grace period is an on/off setting (`station_key_required`, default 0), not a time window. With it at 0 a station that has a key can still scan without sending it, so the key only protects once the setting is 1. TECH_STAFF can rotate a key.
- **E7 void.** The void operation covers event loans. A student's undo before the loan exists just removes the item from the session.
- **E8 items.** TECH_STAFF can edit only `max_items_per_transaction`. Penalties dedupe by transaction (loans sharing the session). A long item code on the confirm screen is cut to fit 20 columns ("3 items +2 more").
- **E1/E5.** `liabilities.student_id` is now nullable (check: exactly one of student, user or name is set). A loan can carry one MISSING and one DAMAGE liability (index `(loan_id, reason)`, decided 2026-11-20); settling the damage one does not lift the hold or freeze. The flag and override rule applies to event borrowers only.
- **E4.** Station-offline notices now also reach custodians.
- **G3/G4/G5/G6.** See the NOT DONE lists in the Jira comments and `docs/STATUS.md`. No NFR is claimed as met.

Two parts:

1. **Six features** that the PRD asks for and the backend does not have (G1 to G6), each split into phases and tasks.
2. **Four open questions** (Q1 to Q4) about built behaviour that the PRD leaves undecided. All four are answered; each has tasks (E1 to E4).

## Part 1: Features

| ID | Feature | PRD | Pri | Est. | Blocked by |
|---|---|---|---|---|---|
| G1 | Real fingerprint matcher (SourceAFIS on the sensor image, decided) | FR-1.3, NFR-4 | M | 4 to 6 d | Hardware TAMS-52 for real captures |
| G2 | Station credentials for room stations | FR-3.1, NFR-10 | M | 1.5 d | G2-D1 decision. Blocks TAMS-80, 111 |
| G3 | Backups and restore | NFR-6, biometric request s5 | M | 1.5 d | G4 (a server to run on), TAMS-19 |
| G4 | Deployment and hardening | NFR-6, NFR-9, NFR-11 | M | 2.5 d | TAMS-19 (hosting, domain, owner) |
| G5 | Data retention and deletion | NFR-11, biometric request s6 | M | 2 d | G5-D1 decision, TAMS-19 |
| G6 | Performance proof | NFR-1 to NFR-5 | S | 1.5 d | G4 for a realistic server, G1 for NFR-4 |

Suggested order: G2 (small, unblocks hardware) → G1 spike → G4 → G3 → G5 → G6, with G1 finishing once real captures exist.

---

### G1. Real fingerprint matcher

**Why.** FR-1.3 wants 1:N matching in PostgreSQL over 2,000 templates in 1.5 s (NFR-4). Today `FingerprintMatcher` scores the Hamming distance of raw template bytes. That works on the simulator's made-up bytes but not on real AS608 captures, so STATUS.md says it blocks the pilot. TAMS-58 is Done against the placeholder.

**Decision G1-D1: decided 2026-11-15, option A.** The kiosk reads the fingerprint **image** from the AS608 and uploads it over HTTPS. The API builds a template with SourceAFIS in memory, matches it 1:N, stores only the encrypted template, and discards the image at once. Nothing is written to disk, the database, logs or backups. This keeps FR-1.3 (matching in PostgreSQL, not on the sensor) and NFR-4.
- **Rejected:** matching on the sensor (breaks FR-1.3; the AS608 holds about 162 templates, not 2,000) and keeping raw-byte matching as the final answer (the sensor's file format is vendor-specific and byte comparison is unlikely to be reliable).
- **Letter to the school (yours to send):** section 5 says "never fingerprint images". Replace that row with: "Biometric handling. The fingerprint sensor's image is sent over HTTPS to our server, converted into an irreversible template in memory, and discarded immediately. Images are never saved to disk, the database or backups. Only the encrypted template is stored." Also check sections 3 and 6 for the same promise.
- **PRD change:** FR-1.3 and NFR-11 add "the image is processed in memory and never stored"; §6.2 step 2 sends an image, not a template.
- **Hardware:** TAMS-52 (capture and upload) and TAMS-53 (enrollment mode) change from uploading templates to uploading images. Image transfer from the sensor is slow, so they must keep the fingerprint step inside the 2-second target (NFR-1) or tell us the real time.

**Phase 1: spike (1 d)**
- [ ] Capture 20 or more real AS608 samples (5 fingers, 4 scans each) once TAMS-52 gives captures.
- [ ] Run SourceAFIS on the captured images: record false accept and false reject rates at several thresholds and pick the threshold.
- [ ] As a check on the decision, run the current raw-byte matcher on the same captures for comparison.
- [ ] Measure the image read and upload time on the real kiosk against the 2-second step target.
- Done when: a results table in this file. If SourceAFIS cannot reach acceptable error rates on real captures, reopen the decision.

**Phase 2: contract and storage (1 d)**
- [ ] Decide the upload format with hardware (image size, encoding). Update `docs/SIMULATOR.md` and the kiosk README.
- [ ] Keep encrypted template storage as it is; add a `template_format` value so old and new rows can coexist.
- [ ] Re-enrollment plan for students already enrolled with raw templates.

**Phase 3: implement (1.5 d)**
- [ ] A `SourceAfisMatcher` behind the existing matcher interface; replace `KioskSessions.MatchAsync`.
- [ ] Keep `TemplateCache`, rebuilt for the new format.
- [ ] Enrollment (`/kiosk/enrollment/sessions/{id}/fingerprint`) extracts and stores the template; the image is never written to disk or the database.
- [ ] Keep "3 failed scans deny the session".

**Phase 4: prove it (1 d)**
- [ ] Unit tests with fixture images (match, no match, same finger twice).
- [ ] Timing test: 2,000 templates, p95 under 1.5 s (feeds G6).
- [ ] Threshold set from the Phase 1 numbers; document it.

---

### G2. Station credentials for room stations

**Why.** Room stations (`POST /stations/{code}/scans` and `/heartbeat`) identify only by the station code in the path. Anyone who knows a code can post scans. There is an optional IP check (`Kiosk:EnforceIp`), which breaks when stations move to the pocket router or LTE modem (see deployment-decision.md). The PRD defines no credential. FR-1.4 says the *kiosk* holds no credentials, so this applies to room stations only.

**Decision G2-D1.** Per-station secret in a header (`X-Station-Key`), stored hashed, shown once when created. Alternative: a shared key for all stations (simpler, weaker). Recommendation: per-station.

**Phase 1: backend (1 d)**
- [ ] Add `key_hash` to stations (migration). Generate the secret on create and on `POST /stations/{id}/rotate-key`; return it once.
- [ ] Check the key in `StationAuth` for scans and heartbeats; constant-time compare; reject with `STATION_NOT_AUTHENTICATED`.
- [ ] Setting or config to allow a grace period so existing stations keep working until keys are loaded.
- [ ] Audit-log key creation and rotation.

**Phase 2: tests and docs (0.5 d)**
- [ ] Tests: valid key, wrong key, missing key, rotated key, deleted station.
- [ ] Update `docs/SIMULATOR.md`, `docs/TEST-DATA.md`, README; tell hardware (TAMS-80, 111).
- Done when: a scan without the right key is refused and the simulator still works with a key.

---

### G3. Backups and restore

**Why.** The deployment decision and the biometric letter promise encrypted backups kept only while the project runs. Nothing in the repo takes a backup.

**Phase 1: job (1 d)**
- [ ] A backup container or cron service in `docker-compose.yml`: nightly `pg_dump`, compressed.
- [ ] Encrypt before upload (age or gpg); key kept off the server.
- [ ] Upload to the off-site bucket named in the deployment decision (R2 or B2); keep 14 days, delete older.
- [ ] Log success and failure; failure raises an alert (reuse the notification path or a health flag on `/health`).

**Phase 2: restore proof (0.5 d)**
- [ ] `docs/RUNBOOK.md`: how to restore into a fresh Compose stack.
- [ ] A drill: restore last night's backup on a clean machine and log in. Record the date and result.
- Done when: a restore drill passes and is written down.

---

### G4. Deployment and hardening

**Why.** Compose and Caddy exist for local use. The pilot needs a server that matches section 5 of the biometric letter. Depends on TAMS-19 (provider, domain, who owns it after the defense).

**Phase 1: server (1 d)**
- [ ] Provision the VPS; key-only SSH, password login off, firewall allowing only 80 and 443 (and SSH from known addresses).
- [ ] PostgreSQL not published to the internet (confirm the Compose file does not publish 5432).
- [ ] Domain and HTTPS through Caddy.

**Phase 2: secrets and config (0.5 d)**
- [ ] Real `Jwt__Key`, database password and `Push__Fcm__CredentialsJson` from environment or secret files, not committed. Remove reliance on `docker-compose.dev.yml` in production.
- [ ] `docs/DEPLOY.md` with the exact steps.

**Phase 3: encryption at rest (0.5 d)**
- [ ] Encrypted volume or provider disk encryption for the database volume; document how it was verified.

**Phase 4: operations (0.5 d)**
- [ ] Restart policy, log rotation, an uptime check on `/health` for the 7:00 to 21:00 window (NFR-6).
- [ ] Revisit the kiosk and station IP allowlist for the pocket router (deployment-decision.md section 7 note).
- Done when: the pilot URL runs over HTTPS from the server, and `DEPLOY.md` reproduces it.

---

### G5. Data retention and deletion

**Why.** The letter to the school promises: keep data until the defense, then delete all biometric templates and checkout photos within 30 days, with written proof. Nothing deletes data today except the manual fingerprint delete.

**Decision G5-D1.** What else has a retention period: ordinary checkout photos during the project (keep all, or purge after N days), notification rows, device tokens, old kiosk sessions? The PRD gives no number. Recommendation: keep photos for the project (admins audit them, FR-1.8) and purge only at the end; clean up expired kiosk sessions and stale device tokens on a schedule.

**Phase 1: deletion tools (1 d)**
- [ ] Admin-only "purge biometrics" operation: delete all fingerprint templates and enrollment sessions.
- [ ] Admin-only "purge checkout photos": delete photo files and null the links; keep the ledger rows.
- [ ] Both write an audit-log entry with counts (audit log is append-only; inserts are fine) and return a summary admins can print as the deletion evidence.

**Phase 2: scheduled clean-up (0.5 d)**
- [ ] Sweeper step: delete device tokens FCM reported dead (already done on send), kiosk sessions older than N days, read notifications older than N days (N in settings).

**Phase 3: backups and tests (0.5 d)**
- [ ] Backup retention (G3) matches the 30-day promise; document that backups age out.
- [ ] Tests: purge removes data, keeps ledger and audit rows, is admin-only.
- Done when: running the purge on a test database leaves no template or photo, and a summary is produced.

---

### G6. Performance proof

**Why.** NFR-1 to NFR-5 are targets nobody has measured. Only the match time of 2,000 templates is known (2 ms warm, 30 ms cold, on the placeholder).

**Phase 1: scripts (1 d)**
- [ ] A load script (k6 or a small Python script in `tests/perf/`) that drives the kiosk flow through the Compose stack: card, fingerprint, asset, photo, approve, confirm.
- [ ] Seed 2,000 enrolled students for the fingerprint step.

**Phase 2: measure and record (0.5 d)**
- [ ] NFR-1: p95 per kiosk step at or under 2 s.
- [ ] NFR-2: full checkout, excluding approval wait, at or under 60 s.
- [ ] NFR-3: station scan to SignalR dashboard event at or under 3 s.
- [ ] NFR-4: 1:N match over 2,000 real-format templates at or under 1.5 s (after G1).
- [ ] NFR-5: sweep lateness at or under 15 s.
- [ ] Results table in `docs/PERFORMANCE.md` with date, hardware and commit. Coordinate with QA TAMS-83.
- Done when: each NFR has a measured number or a stated reason it fails.

---

## Part 2: Decisions on the four open questions (answered 2026-11-15)

Each decision now has tasks. These are separate from G1 to G6 and are all backend.

### Q1. Event loan penalties: decided
In an event loan the **coordinator** (the card that unlocks batch mode) is the accountable person. The kiosk records no student, fingerprint or photo for event loans (FR-5.3). The event's creator is not penalized unless they are also the coordinator.

**Borrower record.** An event always needs a staff member at the web PWA, so staff register who is borrowing there, before the items leave: the borrower's **full name and ID number** (staff check it against a real ID). The coordinator is still the card on the loan; the borrower is who actually holds the items.

**Matching.** When staff enter a borrower, the system looks them up:
1. Name and ID number match a registered student: link the loan to that student record. The student's existing standing then applies (block, hold, marks, unsettled liability).
2. ID number matches a registered student or user but the name differs: show "possible match", staff confirm or correct. Nothing is applied until they confirm.
3. No match: treat as an outside borrower and check earlier event loans and liabilities recorded under the same ID number (name as fallback).

**Flag.** If the matched person has an open problem (blocked, on hold, overdue or missing item, unsettled liability), the web screen flags it with details, the borrowing is refused, and technical staff get a notification. Staff may override **only with a written reason**; the override is saved in the audit log with who, when and why. This applies to normal students and to event borrowers alike.

**Penalties.**
- Overdue event loan: the coordinator is frozen (blocked from starting a new batch) until staff lift it with a reason. A linked student also gets the normal student ladder (mark and hold).
- Missing event loan: the liability goes to the borrower recorded on the loan (a linked student, or the typed name), or to the coordinator if no borrower was recorded.
- A coordinator with an account is already blocked through the freeze; the name check is the same rule for people without accounts.

**E1 tasks (2.5 d)**
- [ ] Migration: `loans.borrower_name`, `loans.borrower_id_no`, `loans.borrower_student_id` (nullable); `liabilities.student_id` nullable plus `liable_user_id` and `liable_name`; check that exactly one is set.
- [ ] `PUT /loans/{id}/borrower` and a batch version for all loans of an event: runs the matching above and returns the flag details.
- [ ] Refuse a flagged borrower with `BORROWER_FLAGGED` unless the request carries an `overrideReason`; audit log entry for every override.
- [ ] Notify technical staff when a flagged borrower is entered.
- [ ] Standing check endpoint, `GET /borrowers/check?name=&idNo=`, so the web can warn before submitting.
- [ ] Overdue event loan freezes the coordinator; the kiosk refuses a frozen coordinator in batch mode (same denial as a frozen instructor).
- [ ] `POST /loans/{id}/mark-missing` picks the liable party as above.
- [ ] Tests: match, possible match, no match, flagged refusal, override with and without a reason, freeze and blocked batch, missing liability for each liable type.
- [ ] Update STATUS.md and PERMISSIONS.md.

**Open (one question):** see "Broken items" below.

### Broken items (new, from Q1)
Staff do a monthly inventory and condition check, scanning the QR or RFID tag of an item and recording its condition (GOOD, FAIR, DAMAGED) by hand. The PRD already has the `DAMAGED` condition and says condition is recorded manually by staff (section 10.1). It has **no** penalty or liability for damage; only missing items create one.

**Decided:** damage is **not** charged automatically. At the monthly inventory check, when staff mark an item DAMAGED, the screen shows the item's last borrower (from its most recent loan: a student, an event borrower or a coordinator). Staff decide whether to hold that person liable. If they do, they enter the repair cost and a note, and a liability is recorded against that person. The same flag and override rules as above then apply to that person's next borrowing. If staff do not assign anyone, the condition is only recorded.

**E5 tasks (1 d)**
- [ ] When an asset's condition is set to DAMAGED, return its last borrower and last loan with the response (read only; nothing is charged yet).
- [ ] `POST /assets/{id}/damage-liability` with `loanId`, `amount` and `notes`; staff roles only; creates the liability against that loan's borrower, student or coordinator (needs the E1 liability columns).
- [ ] Audit log entry for the condition change and for the liability.
- [ ] Settling uses the existing `/liabilities/{id}/settle`.
- [ ] Tests: damaged with no liability, damaged with a student, event borrower and coordinator as liable, non-staff refused.

### Q2. Holds: decided
Staff always lift holds. A student who returns the item stays on hold until technical staff lift it with a reason. This is how it works today.

**E2 tasks (0.25 d)**
- [ ] Reword FR-6.5 in the PRD to "the hold stays until staff lift it".
- [ ] Confirm the web screen lists returned-but-held students so staff can find them (tell the web team).

### Q3. Reports and exports: decided
Add Excel (`.xlsx`) alongside CSV and PDF.

**E3 tasks (1 d)**
- [ ] One small library for `.xlsx` (ClosedXML or MiniExcel).
- [ ] `GET /rooms/{id}/checklist:export?format=xlsx` next to CSV.
- [ ] `POST /audits/import` accepts a completed `.xlsx` as well as CSV (FR-7.6).
- [ ] `GET /audits/{id}/report?format=xlsx` next to pdf and csv.
- [ ] Tests for each format; update TEST-DATA.md.
- Not included: storing finished reports as files (`audits.report_file_id` stays null).

### Q4. Push to staff: decided
Staff who use the mobile app (technical personnel and custodians) also receive pushes for events that need someone to walk over: wrong-room scans, unborrowed taps, transit timeouts and stations going offline. The approving instructor keeps getting every notice about their loans. Staff do not get every overdue or warning notice (FR-2.6).

All of these notices carry full details: event name and coordinator for event loans, item name and code, loan number, student (for normal loans), room and station, and the photo link where the loan has one.

**E4 tasks (1 d)**
- [ ] On those four events, create one notification row per staff recipient (users with the technical-personnel or custodian role) and let the existing push delivery (TAMS-97) send them.
- [ ] Build the payload with the details above; for event loans include the event title, coordinator and the other items in that event that are out.
- [ ] Do not create a second notice for the same open anomaly (the dedupe that exists for anomalies applies).
- [ ] Add the same details to the existing notices for instructors.
- [ ] Tests: staff recipient gets it, instructor-only notices stay instructor-only, payload fields present.

---

## Jira drafts

One ticket per feature, backend label, assigned to you:

- `[Backend] - Real fingerprint matcher for AS608 captures` (G1)
- `[Backend] - Room station credentials` (G2)
- `[Backend] - Nightly encrypted backup and restore drill` (G3)
- `[Backend] - Production deployment and hardening` (G4)
- `[Backend] - Data retention and deletion for biometrics and photos` (G5)
- `[Backend] - Measure NFR-1 to NFR-5 on the Compose stack` (G6)
- `[Backend] - Event loan borrower record, flag and penalties` (E1)
- `[Backend] - Damage liability against the last borrower` (E5)
- `[Backend] - Excel export and import for checklists and audit reports` (E3)
- `[Backend] - Push anomalies and transit timeouts to staff, with full details` (E4)

Each phase above can be a subtask or a checklist in the ticket description.
