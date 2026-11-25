# TAMS Backend: Status

Last updated: 2026-11-07. Branch of record: `pre-dev` (d6dc7f5), with `feature/prd-transaction-core` on top of it (the rest of the PRD backend, below). `dev` is still the Compose skeleton and has not been promoted.

**Read this first:** `feature/prd-transaction-core` has been run against PostgreSQL 17. Both migrations applied cleanly on an empty database, and an 80-check end-to-end run passed. It covered seeding through the pre-dev endpoints (rooms, stations, categories, assets, users, cards, sections, students, settings), enrollment, checkout, approval, room check-in, return, transit timeout, overdue, missing, settlement, events, audits, notifications and the SignalR handshake. The sweeper paths were forced by moving deadlines in the database. Builds with 0 warnings, 11 tests pass. The fingerprint matcher is a placeholder (see Open decisions #1), so the kiosk cannot be piloted with a real sensor yet.

## Branches

`dev` is clean. `pre-dev` is the integration branch: everything below is merged there and tested together. Each ticket also keeps its own branch so it can be checked out and fixed on its own.

| Ticket | Branch | What it is |
|---|---|---|
| TAMS-27 | `feature/TAMS-27-schema-v1-migrations` | EF Core, `TamsDbContext`, all 29 PRD 7.3 tables, enums, first migration |
| TAMS-44 | `feature/TAMS-44-swagger-error-format` | RFC 7807 problem details with stable `code`, OpenAPI + Scalar, Bearer scheme |
| TAMS-31 | `feature/TAMS-31-staff-authentication` | Login (email only), JWT, refresh, logout, change-password, lockout, dev admin seed |
| TAMS-39 | `feature/TAMS-39-role-based-access` | Policies, default-deny fallback, `PERMISSIONS.md`, endpoint coverage test |
| TAMS-33 | `feature/TAMS-33-account-management-api` | Staff and instructor accounts, reset-password, restore |
| TAMS-38 | `feature/TAMS-38-assets-categories-crud` | Assets, categories, typed tag UIDs, asset photo, restore |
| TAMS-32 | `feature/TAMS-32-rooms-stations-cards-crud` | Rooms, stations, health summary, authorization cards |
| TAMS-40 | `feature/TAMS-40-student-roster-csv-import` | Sections, students, `POST /students:import` |
| TAMS-41 | `feature/TAMS-41-fingerprint-enrollment-api` | Enrollment session state machine, encrypted templates |
| TAMS-125 | `feature/TAMS-125-xlsx-export-import` | `.xlsx` (ClosedXML) for `GET /rooms/{id}/checklist:export?format=xlsx`, `GET /audits/{id}/report?format=xlsx` and `POST /audits/import` (CSV or xlsx, detected by content) |
| TAMS-42 | `feature/TAMS-42-settings-business-rules-api` | Typed settings catalogue and `ISettings` |
| TAMS-43 | `feature/TAMS-43-asset-qr-endpoint` | `GET /assets/{id}/qr`, `POST /assets/qr-sheet` (PDF) |
| (fixes) | `fix/conformance-api-behaviour` | Post-audit API fixes, `GET /audit-log` |
| (fixes) | `fix/conformance-schema-auth-enrollment` | Migration 2, refresh-token rotation, enrollment fixes |
| (PRD rest) | `feature/prd-transaction-core` | From `pre-dev`. Kiosk checkout, return and event batch/return sessions (F1, F5), approvals (F2), room scans (F3), loans, anomalies, dashboard and occupancy (F4), penalties and resolution (F6), audits, reports, checklist export/import (F7), heartbeats and health sweep (F8), `POST /students`, notifications, SignalR hub. No new migration. |

Only `dev` and `main` exist on origin. Everything else is local until pushed.

Jira (project TAMS): TAMS-19 and TAMS-21 are in Deep Review and assumed approved. Statuses of the tickets above have not been updated; they are still Backlog or In Progress.

## Decisions and why

- **Schema v1 follows `schema-v1-decisions.md`** (in the planning folder): no `middle_name` or `department`, login by email only, change-password yes, forgot-password deferred in favour of admin reset, typed tag UIDs instead of a tap session, admin-only restore, asset photo, `school_year` on late-arrival warnings.
- **Second migration (`SchemaV1Conformance`)** came out of a PRD audit: DB-level defaults from PRD 7.3, checks for STANDARD-loan photo and BORROWABLE tag, partial unique indexes (`WHERE deleted_at IS NULL`) so a soft-deleted record stops blocking its key, `checkout_sessions.finger_index`, one open enrollment session per student and one open session per station, and a `refresh_tokens` table. Create and update checks look at live rows only, so `RESTORE_CONFLICT` can now actually fire.
- **Refresh tokens** are opaque, stored as SHA-256, rotated on every use. Presenting a revoked token revokes all of that user's tokens (`REFRESH_TOKEN_REUSED`). Logout revokes the presented token, or all if none is given. Access tokens live 15 minutes and are not checked against the database per request, so a deactivated user keeps access until the token expires.
- **Routes** are served under `/api` through `UsePathBase`. Both `/api/x` and `/x` route, which keeps the Compose healthcheck and local curl working.
- **Kiosk routes** (`/kiosk/...`) are anonymous to the JWT pipeline and allow-listed in `Policies.AnonymousRoutes`. They identify the station by an `X-Station-Code` header. The IP check is optional and off by default (`Kiosk:EnforceIp`, `Kiosk:AllowedIps`).
- **Fingerprint templates** are encrypted with ASP.NET Data Protection. The key ring is on the `dp-keys` volume. **Lose that volume and every stored template is unreadable.**
- **Audit log** `entity_type` values are lowercase snake_case (`asset`, `asset_category`, `student`, `user`, ...). Keep it that way; `GET /audit-log` filters on exact match.
- **Error codes** worth knowing are in `Http/ErrorCodes.cs`.
- **Authorization** is explicit per endpoint (`.RequireRoles(...)` or a policy). The coverage test (`tests/Tams.Api.Tests`) fails if any endpoint lacks it or is anonymous without being allow-listed.

### Transaction core (`feature/prd-transaction-core`)

- **Kiosk protocol** (PRD 6.2) lives in `Kiosk/`. Every kiosk call sends `X-Station-Code`. A denial is a `FAILED` session with a `failureCode` and the 4 LCD lines, returned with HTTP 200, so the firmware always reads one shape. Problem responses mean protocol errors (bad input, wrong state). `nextStep` tells the kiosk what to collect next. Every mutation locks the session row (`SELECT ... FOR UPDATE`), so the kiosk, the phone and the sweeper never race.
- A new card tap or RETURN start **supersedes** whatever the kiosk had open. A staff-started enrollment blocks the kiosk (`STATION_BUSY`) until it ends. The idle kiosk polls `GET /kiosk/{code}/session` to pick it up.
- The item is held **`AT_COUNTER`** from the tag tap until the loan is created. Any other end (fail, decline, cancel, expiry) puts it back to `IN_STORAGE`.
- **Return** closes the loan on the tag tap, with no confirm step (US-03). It works from `ACTIVE`, `OVERDUE` and `MISSING`. Holds, freezes and liabilities stay for staff: a hold stays after the return until technical staff lift it with a reason (`POST /holds/{id}/lift`), which is how FR-6.5 reads together with FR-6.8 (TAMS-126). `GET /violations` still lists a student whose hold outlives the return (`hasActiveHold`), so staff can find them.
- **Event batch**: a coordinator's card during their running event opens `EVENT_BATCH`; `type` in the start body forces `CHECKOUT` or `EVENT_RETURN`. Each tap is a loan due at event end + grace. Without a station at the venue, custody is `NOT_REQUIRED` and the asset shows `IN_ROOM_VERIFIED`, because there is no "at event" asset status. Bulk return closes each loan as it is tapped, and Done ends the session.
- **Penalties** (F6) live in `Loans/LoanRules.cs`. One warning per loan. Warning n > `late_arrival_warning_limit` also brings a mark and a freeze. Overdue brings a mark, a hold and a freeze, each at most once per loan. Missing brings a liability at the replacement cost (or the amount given), plus a hold and a freeze if none are active. Standing is `BLOCKED` at `marks_before_block` active marks, `WARNED` with any active warning or mark, otherwise `GOOD`. The cached counters on `students` are recomputed after every change. Nothing lifts automatically, except that settling a liability lifts that loan's hold and freeze (US-10). Every penalty action writes an `audit_log` row.
- **Station keys** (TAMS-127, G2-D1): each room station has its own secret in `X-Station-Key`, checked on scans and heartbeats (`StationAuth.IsRoomStationAuthenticated`). Only the SHA-256 hash is stored (`stations.key_hash`); the key is returned once by `POST /stations` and `POST /stations/{id}/rotate-key`, both audited without the secret. Comparison is constant time. Setting `station_key_required` is the grace period: 0 (default) lets a station with no key sent keep working, 1 refuses it; a wrong key is refused either way with `STATION_NOT_AUTHENTICATED`. Counter kiosks hold no credentials (FR-1.4). Set it to 1 after every room station has its key loaded (TAMS-80, 111).
- **School term** is derived from the local date (Aug-Dec = 1, Jan-May = 2, Jun-Jul = 3, Asia/Manila). `reset-term` clears active warnings from earlier terms and leaves `clear_reason` empty, per schema-v1-decisions section 3.
- **Sweeper** (`LoanSweeper`): every 5 s it expires kiosk sessions and recomputes station health, so a station shows Offline within seconds (US-15). Every 15 s it handles transit timeouts, overdue loans and events that have started. A room tap that beats the sweeper runs the timeout first, so the warning is still issued.
- **Station health** allows 3 s of slack per window (`StationHealthRules.SlackSeconds`, a calibration knob). The station IP stored is the address the API sees, not one the device reports.
- **SignalR** hub at `/hubs/live`. JWT goes in `?access_token=`. Groups are `staff` (ADMIN, TECH_STAFF, CUSTODIAN) and `user:{id}`. Events are sent after commit, with the PRD 6.4 names.
- `max_active_loans_per_student` (FR-1.9, default 1) was added to settings. This resolves the old open decision on that setting.
- Enrollment sessions also answer on the PRD's `/kiosk/sessions/{id}/...` paths. Enrollment views now return `UPPER_SNAKE` status like the rest of the API, which is a change from PascalCase.

### Damaged items at the kiosk (TAMS-119)

- A DAMAGED asset is refused at the tag tap in checkout (`FAILED`, `ASSET_DAMAGED`, LCD "Item damaged / See lab staff", the item stays `IN_STORAGE`) and in event batch (same LCD text, the batch carries on). Return is unchanged. The failed checkout is the FR-1.6 log row (session with its failure code).
- Every kiosk response after a tag tap carries `item` (`assetCode`, `name`, `condition`) and the second LCD line is "name - CONDITION". `checkout_sessions.asset_id` now records the last tapped item in every session type. Hardware (TAMS-76, 78) needs the new text.
- Integration tests build a whole kiosk with `KioskRig` (tests/Tams.Api.Tests/Integration).

### Void an event loan (TAMS-120)

- New `loan_status` label `CANCELLED` and `ledger_event` label `VOIDED` (migration `20261116100000_VoidLoans`, which adds the labels with `ALTER TYPE ... ADD VALUE`; EF does not generate that). A voided loan is kept, never deleted.
- `LoanRules.VoidAsync` is the one void rule: only an EVENT loan that is `ACTIVE`, not verified and in custody `AWAITING_ARRIVAL` or `NOT_REQUIRED`. It sets the loan `CANCELLED`, returns the asset to `IN_STORAGE`, appends a `VOIDED` ledger row and a `LOAN_VOIDED` audit row (actor = the coordinator at the kiosk, or the staff user on the web), and refreshes room occupancy.
- Kiosk: Cancel in `EVENT_BATCH` voids the session's last active loan and the session continues ("Removed LP-014"); with none left Cancel ends the session. If that loan already reached its venue the LCD says "Cannot undo". Batch counts ignore cancelled loans.
- Web: `POST /loans/{id}/void` `{reason}` (ADMIN, TECH_STAFF; 400 without a reason, 409 `LOAN_NOT_VOIDABLE`). The event detail `loanCounts` gains `cancelled`; the loans list accepts `status=CANCELLED`.
- Student loans cannot be voided; E8 (TAMS-121) reuses the same rule for items not yet loaned.
- Open question for the web team: a "remove item" button on event loan detail calling the new endpoint.

### Several items per checkout and return (TAMS-121)

- Setting `max_items_per_transaction` (default 1, range 1 to 10, `SettingDefinition.TechStaffCanEdit`). TECH_STAFF may now call `PUT /settings` and `PUT /settings/{key}` for that key only; any other key from TECH_STAFF is 403 `SETTING_ADMIN_ONLY` (a mixed bulk body is refused whole). `GET /settings` shows `techStaffCanEdit`.
- New table `checkout_session_items` (migration `20261116101000_CheckoutSessionItems`): the items a checkout session holds `AT_COUNTER`. `checkout_sessions.asset_id` still means the last tapped item. Ending a session without loans (fail, decline, cancel, expiry) releases every held item.
- Effective limit = min(`max_items_per_transaction`, what is left of `max_active_loans_per_student`). At 1 the flow is the PRD one (first tap goes to the photo, no new LCD text). Above 1: OK (`confirm`) ends the item step, reaching the limit does it automatically, Cancel undoes the last item, a refused tap carries on once an item exists (own checks per item, a repeated tag is `Already added`), and a refusal with no item yet denies the borrow. LCD text is the table in `docs/PRD-GAP-LOG.md` (branch `docs/prd-gaps-and-flow`).
- Views: `items` on every kiosk session view; `items` on the approval request, `GET /approvals/pending` and the FCM `request` (with `asset` = first item, and `itemCount` on the push message). The confirm screen reads "3 items: LP-014 +2" (cut to "3 items +2 more" when the codes are long).
- Student Confirm creates one loan per item (own loan number, ledger row and room check-in) sharing room, class length, due time and approval. Loan numbers are allocated one save at a time.
- RETURN with the setting above 1 keeps the session open after each tap ("Returned LP-014 / 1 returned / Tap next item / OK when done"); OK is Done. A refused tap after the first return carries on. If the student never presses OK the session expires, but the returns already stand.
- Penalties per transaction (S1-D1): a late-arrival warning is skipped when another loan of the same session already has one, and the overdue mark likewise. Each item still gets its own anomaly, hold and instructor freeze (holds are per loan); the warning and mark do not list the other items.
- Decisions I made: undo before the loans exist removes the item from the session (no loan to void); the penalty dedupe keys on the shared `session_id` of the loans.

### Approval push (TAMS-62)

- Right after the photo commits (session enters `AWAITING_APPROVAL`), `ApprovalPushDispatcher` sends the PRD 6.4 `approval.requested` payload to the card owner's registered devices, in the background. Failures are logged and never reach the kiosk. Tokens FCM reports as unregistered are deleted from `device_tokens`.
- Mobile registers a token with `POST /device-tokens` `{token, platform: ANDROID|IOS|WEB}` (upsert by token, any signed-in user) and removes it on sign-out with `POST /device-tokens/unregister` `{token}`. Uses the existing `device_tokens` table; no migration.
- FCM data message: `type=approval.requested`, `sessionId`, and `payload` (JSON string: `sessionId`, `studentName`, `photoUrl`, `asset`, `roomOptions`, `classLengthOptionsHours` [1.5,2,2.5,3], `classLengthOptionsMinutes`, `expiresAt`, `deadlineSeconds` (120), `request` = the full SignalR payload). `photoUrl` is relative and needs the bearer token to fetch.
- Config: `Push:Provider` = `Fcm` | `Fake` | `None`. Unset means `Fake` in Development and `Fcm` elsewhere. `Fcm` needs `Push:Fcm:CredentialsJson` (service-account JSON, e.g. env `Push__Fcm__CredentialsJson`) or `Push:Fcm:CredentialsPath`; optional `Push:Fcm:ProjectId` overrides the JSON's `project_id`. Without credentials it logs an error and disables itself. `Fake` logs the payload and emits it over SignalR as `approval.push` to `user:{id}`, so no Firebase account is needed.

### Production deployment (TAMS-129)

- **In the repo:** `docker-compose.prod.yml` overlay (required `JWT_KEY`, `CORS_ORIGINS`, `TAMS_DOMAIN`; demo seed and bootstrap admin forced off; FCM service account from a file as a Compose secret), log rotation on every service, security headers in the `Caddyfile`, `scripts/uptime-check.sh` for the 07:00-21:00 `/health` check, and `docs/DEPLOY.md` with the exact server steps. PostgreSQL has no published port (checked in `docker compose config`).
- **NOT DONE (needs a real server, domain and owner, TAMS-19):** VPS provisioning, key-only SSH and UFW, the domain and the issued certificate, encrypted database volume and how it was verified, the real secrets, running the uptime check, the pocket-router IP allowlist decision, ownership after the defense. The verification table at the end of `docs/DEPLOY.md` is empty on purpose; "Done when: the pilot URL runs over HTTPS from the server" is not met.

### Event loan borrower record (TAMS-122, PRD-GAPS Q1 / E1)

- **Borrower record.** Staff register who actually holds event items: `PUT /loans/{id}/borrower` or `PUT /events/{id}/borrower` (all open loans of the event), body `{name, idNo, confirmPossibleMatch?, overrideReason?}`. Stored on the loan as `borrower_name`, `borrower_id_no`, `borrower_student_id`. Audit rows `LOAN_BORROWER_SET`; the loan DTO has `borrower`.
- **Matching** (`Loans/BorrowerRules.cs`). Student number equals `idNo` and the name matches (case, spacing and "Last, First" order ignored): `STUDENT`, linked to the student. Same ID, different name: `POSSIBLE_MATCH`, refused with 409 `BORROWER_POSSIBLE_MATCH` until the request carries `confirmPossibleMatch: true` (or the name is corrected). `idNo` equal to a staff `employee_no` with a different name is also a possible match, but a confirmed staff match is not linked to anything (outside borrower). No match: `NONE`, an outside borrower, checked against earlier event loans by ID number, or by name when that ID has no earlier loan.
- **Flag.** Open problems: student blocked, on hold, overdue or missing loans, unsettled liability (outside borrowers: overdue, missing or unsettled event loans). A flagged borrower gets 409 `BORROWER_FLAGGED` with `check.flags`; every active technical staff user gets a `BORROWER_FLAGGED` notification (pushed like other notices) and the staff group a `borrower.flagged` SignalR event. Resending with a non-blank `overrideReason` saves the borrower and writes `BORROWER_FLAG_OVERRIDDEN` (actor, reason, flags) to the audit log. A blank reason is the same as none.
- `GET /borrowers/check?name=&idNo=` runs the same matching and flags without saving.
- **Overdue event loan:** the coordinator is frozen (`approval_freezes`, same table and lift endpoint as instructor freezes); a student linked as borrower also gets the mark and hold. The kiosk refuses a frozen coordinator at an `EVENT_BATCH` tap (`INSTRUCTOR_FROZEN`); `EVENT_RETURN` stays allowed so items can come back.
- **Missing event loan:** `POST /loans/{id}/mark-missing` charges the linked student, else the typed borrower (`liabilities.liable_name`), else the coordinator (`liable_user_id`). Exactly one liable column is set (`ck_liabilities_one_liable`; `student_id` is now nullable). Hold only for students. `GET /violations` has `otherLiabilities` for the non-student ones. Settling works as before.
- **Done at the kiosk** writes `EVENT_BATCH_CLOSED` or `EVENT_RETURN_CLOSED` to the audit log with the card owner and station; nobody is made liable for pressing it.
- Not done: the flag check for ordinary kiosk checkouts is unchanged (the kiosk already refuses blocked students); there is no staff screen for lifting a coordinator freeze beyond the existing `/freezes/{id}/lift`.
- Migration `20261116200000_EventBorrowerRecord`. Tests: `tests/Tams.Api.Tests/Integration/EventBorrowerTests.cs`.

### Damage liability (TAMS-123, PRD-GAPS Q1 / E5)

- Damage is never charged automatically. `PUT /assets/{id}` with `condition: DAMAGED` returns the asset plus `lastBorrower` (read only): the asset's most recent loan (`loan`: id, loanNo, type, status, checkedOutAt, returnedAt), the `borrower` (`STUDENT` incl. a student linked to an event loan, `EVENT_BORROWER` typed name and ID, or `COORDINATOR`) and `hasLiability`. The field is absent for other conditions or an asset that was never lent. The condition change is audit-logged as `ASSET_UPDATED` (before and after).
- `POST /assets/{id}/damage-liability` `{loanId, amount, notes?}` (ADMIN, TECH_STAFF) creates an Outstanding liability with `reason = DAMAGE` against that loan's borrower (same selection as mark-missing, `LoanRules.Liable`) and writes `LIABILITY_CREATED`. Needs the asset to be DAMAGED (`ASSET_NOT_DAMAGED`), the loan to belong to the asset (`LOAN_NOT_FOUND`) and amount above 0. No hold, mark or freeze is created. Not assigning creates nothing.
- New `liabilities.reason` (`liability_reason`: MISSING, DAMAGE, default MISSING); shown in the student standing and in `GET /violations` (`otherLiabilities`). Settling uses `/liabilities/{id}/settle`. An unsettled damage liability flags the person at their next event borrowing (E1 rules).
- Decided 2026-11-20: a loan can carry one MISSING and one DAMAGE liability, so a person who loses an item and also returns it damaged owes both. The unique index is now `(loan_id, reason)` (migration `20261119211910_LiabilityPerReason`). A second damage liability on the same loan is still refused (`LIABILITY_EXISTS`); declaring missing twice still creates one MISSING liability. `lastBorrower` now also carries `hasDamageLiability` and `hasMissingLiability` (`hasLiability` stays as "either"). Settling a liability lifts the loan's hold and freeze only when it is the MISSING one; settling a damage liability never does. Web: show both liabilities on a loan and let staff settle each one.
- Migration `20261116210000_DamageLiabilityReason`. Tests: `DamageLiabilityTests.cs` (`EventScenario` is shared with the E1 tests).

### Add card at the kiosk (card enrollment)

- Staff (ADMIN, TECH_STAFF) press Add card on an instructor (`POST /auth-cards/enrollment`); the idle counter kiosk shows it in `cardEnrollment` on `GET /kiosk/{code}/session` and registers the next card tapped (`POST /kiosk/{code}/card-enrollment/{id}/tap`). Full spec, errors and limits: `docs/CARD-ENROLLMENT.md`.
- Requests are in memory (90 s, one at a time, one API instance only). The audit log's `AUTH_CARD_ISSUED` actor is the staff member who pressed Add card.
- `GET /users` and `GET /users/{id}` now return `authCards` (every status, newest first); write responses return null.
- `auth_cards.card_uid` is unique among ACTIVE cards only (migration `ActiveCardUidUnique`), so a revoked or lost card's UID can be issued again as a new row.
- **Not done:** the kiosk firmware's card enrollment mode (hardware); PRD wording (see `docs/PRD-GAP-LOG.md` row 9). The simulator has an Add card button and scenario.

## Open decisions (not made, behaviour documented in `PERMISSIONS.md`)
1. **Fingerprint matching (SourceAFIS built in TAMS-128; threshold PROVISIONAL, still blocks the pilot until calibrated):** the kiosk uploads the sensor image (`image` base64 of raw 8-bit gray, plus `width` and `height`); the API builds a SourceAFIS template in memory, matches it 1:N and discards the image. Only the encrypted template is stored, with `fingerprint_templates.template_format` = `SOURCEAFIS_V1` (older rows are `RAW_V1`). Nothing is written to disk, the database or logs. **Phase 1 of the plan (20+ real AS608 captures, false accept and reject rates, threshold choice, kiosk upload timing) has not been done: it needs hardware TAMS-52.** Until then `Kiosk:SourceAfisThreshold` defaults to 40 (SourceAFIS's documented 1-in-100,000 false-match point), a guess for real sensor images; tests use synthetic fixture images, which prove the pipeline, not the error rates. Timing test: 2,000 templates match in about 60 ms median on a 12-core laptop (limit 1.5 s, NFR-4). Three failed scans in a session still deny it. The legacy path (`FingerprintMatcher`, Hamming distance, `Kiosk:MatchThreshold` 0.85) stays for `RAW_V1` rows and the simulator's template mode. **Re-enrollment:** a student enrolled with a raw template cannot match an image scan; staff list the fingers (`GET /students/{id}/fingerprints` now shows `templateFormat`), delete `RAW_V1` ones and enroll again from the kiosk. The pilot has no real enrollments yet, so nothing is lost. Known: SourceAFIS 3.14.0 depends on SixLabors.ImageSharp 2.1.3 (NU1902 advisories); we only send raw pixels, never an encoded image, so that decoder is not reached. Encoded PNG/JPEG upload is not supported for that reason. PRD wording still to change: FR-1.3 and NFR-11 "image processed in memory and never stored", section 6.2 step 2 "sends an image". **Dependency warning:** SourceAFIS 3.14.0 pulls in `SixLabors.ImageSharp` 2.1.3, which has known advisories. `Tams.Api.csproj` now pins 2.1.13, the newest Apache-licensed release (3.x and 4.x need a paid Six Labors licence for Release builds). The advisories still listed by NuGet (NU1902/NU1903) are in ImageSharp's histogram, TIFF encoder and similar processing code, which we never call: the kiosk sends raw pixels and SourceAFIS only reads them. Revisit if SourceAFIS ships a release on a newer ImageSharp or if encoded images are ever accepted.
2. **Push notifications (TAMS-62, TAMS-97):** approval requests are pushed (see "Approval push" below). Stored notices (warning, overdue, missing, freeze, wrong-room anomaly) are pushed by the sweeper every 15 s through the same provider, within 10 minutes of being created, and `delivered_at` is stamped; Fake emits `notice.push` over SignalR. A recipient with no device token keeps `delivered_at` null. Staff alerts (TAMS-124): wrong-room scans, unborrowed taps, transit timeouts and stations going offline also create one notice per active TECH_STAFF and CUSTODIAN user (types ANOMALY_RAISED, TRANSIT_TIMEOUT, STATION_OFFLINE); the approving instructor keeps notices about their loans, and overdue, warning and missing notices stay instructor-only. Notice payloads carry a `details` object (item, loan number, student, room, station, photo link; for event loans the title, coordinator and the other items still out). The open-anomaly dedupe means no second notice. Not tested against real FCM.
3. **Custodian fingerprint rights:** custodians can start enrollments and delete fingerprints; FR-9.4 says "admin-initiated".
4. **`/users` reads:** ADMIN and TECH_STAFF can read (employee numbers included). The original matrix said admin-only.
5. ~~**Event loans:** no coordinator freeze or liability~~ Resolved by TAMS-122 (see "Event loan borrower record" below).

### Backup and restore (TAMS-130)

- **In the repo:** `backup` Compose service (`backup/`): nightly `pg_dump`, age encryption to a public key (private key off the server), the Data Protection key ring backed up alongside, 14-day retention locally and in an S3-compatible bucket, `backup.json` status that `/health` reports as `"backup":"ok|failed|stale|missing"`. `docs/RUNBOOK.md` has backup, restore and the drill log.
- **Done locally:** a restore drill on Docker Desktop (backup, destroy volumes, restore into an empty stack, admin login) passed on 2026-11-16 and is recorded in the RUNBOOK. It used a fresh backup and local storage.
- **NOT DONE:** the bucket (R2 or B2) account and credentials, running the upload against a real bucket, the nightly job on the VPS, an alert on failure beyond the `/health` field (a monitor must watch it), and the drill the ticket asks for (last night's real backup, clean machine). The `media` volume is not in the nightly backup.

## Known gaps

- Not built: room RFID tags and station credentials (the PRD defines neither), stored report files (`audits.report_file_id` stays null; reports render on request, decided in the gap plan Q3).
- Column names use a `_user_id` suffix where PRD 7.3 says `enrolled_by`, `started_by`, etc. Web and mobile need the final names.
- `sections` and `asset_categories` have no `created_at` or `updated_at`; the PRD is ambiguous.
- Roster CSV columns and limits are our own design (the PRD has none). Check the student-number pattern against the school's real format.
- Several settings ranges are guard rails, not PRD values; only grace (15-30), heartbeat (30-60) and class length (90/120/150/180) come from the PRD.
- Audit-log `from` and `to` bind as `DateTimeOffset`; send an explicit offset.
- TAMS-32's Jira description is a copy of the assets ticket and should be corrected. TAMS-31, 33, 39, 40, 41, 42 and 44 have no acceptance criteria.
- TAMS-41 stores a single merged template per capture. The PRD says "scan twice"; confirm the AS608 merges on the sensor.

### Data retention (TAMS-131)

- ADMIN-only `POST /data-retention/purge-biometrics`, `POST /data-retention/purge-checkout-photos` (need `{"confirm":"DELETE"}`), `GET /data-retention/purges/{id}/summary` (printable HTML). Audit entries with counts; ledger, loan and audit rows kept. Migration `20261116700000_RetentionPurge` adds `loans.photo_purged_at`, relaxes `ck_loans_standard_photo` to allow it, and lets the ledger trigger permit only `photo_file_id` to NULL. Daily `RetentionSweeper` plus settings `kiosk_session_retention_days` (30) and `notification_retention_days` (90). Details in `docs/DATA-RETENTION.md`. Tests in `RetentionTests`.
- **NOT DONE:** the actual end-of-project purge (nothing was run on real data), confirming the 14-day backup expiry in a real bucket, handing the summaries to the school. Decision G5-D1 followed the recommendation (keep photos until the end), not confirmed with the school.

### Performance scripts (TAMS-132)

- **In the repo:** `tests/perf/` (`seed.py` seeds 2,000 enrolled students, kiosks, items and an instructor card; `kiosk-flow.js` is a k6 load script with the NFR-1 and NFR-2 thresholds; `nfr3_nfr5.py` measures scan-to-SignalR and sweep lateness) and `docs/PERFORMANCE.md`.
- A smoke run on a developer laptop (loopback, Docker Desktop) is recorded in `docs/PERFORMANCE.md` only to show the scripts work.
- **NOT DONE:** the NFR-1 to NFR-5 measurement on the target hardware or VPS, through Caddy and over a real network, with the results table filled in; NFR-4 with real-format templates (blocked on G1, the real matcher); coordination with QA (TAMS-83). No NFR is claimed as met.

## Next steps

1. ~~Pick the fingerprint matcher and replace `KioskSessions.MatchAsync`~~ Done in TAMS-128 (SourceAFIS). Still to do: calibrate `Kiosk:SourceAfisThreshold` on real AS608 captures once TAMS-52 delivers them (Open decisions #1).
2. Write the kiosk and room-station firmware against `Kiosk/` (README "Kiosk and stations").
3. ~~Extend push beyond approval requests~~ Done in TAMS-97; try it with real FCM credentials.
4. ~~Still unexercised from pre-dev~~ Verified 2026-11-14 against the `docker compose up` stack through Caddy (`https://localhost/api`): refresh rotation, reuse detection (the reused token and its successor both give 401 `REFRESH_TOKEN_REUSED`), logout; roster import (dry run, create/update/unchanged, auto-created section, quoted comma field, lenient 200 and strict 422, wrong columns and no file 400); QR PNG/SVG, QR sheet PDF by ids and by category, 400/404/401 cases.
5. Settle the remaining open decisions.
6. Push `pre-dev`, the ticket branches and `feature/prd-transaction-core` to origin; open the PRs into `dev`.
7. Update the Jira statuses.
