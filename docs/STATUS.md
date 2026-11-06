# TAMS Backend: Status

Last updated: 2026-11-07. Branch of record: `pre-dev` (d6dc7f5). `dev` is still the Compose skeleton and has not been promoted.

**Read this first:** none of this code has run against PostgreSQL. Docker was not available while it was written. It builds with 0 warnings and 0 errors, and the one test passes, but both migrations, every query and every flow are unexercised. The first job is to run the stack (see Next steps).

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
| TAMS-42 | `feature/TAMS-42-settings-business-rules-api` | Typed settings catalogue and `ISettings` |
| TAMS-43 | `feature/TAMS-43-asset-qr-endpoint` | `GET /assets/{id}/qr`, `POST /assets/qr-sheet` (PDF) |
| (fixes) | `fix/conformance-api-behaviour` | Post-audit API fixes, `GET /audit-log` |
| (fixes) | `fix/conformance-schema-auth-enrollment` | Migration 2, refresh-token rotation, enrollment fixes |

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

## Open decisions (not made, behaviour documented in `PERMISSIONS.md`)

1. **`max_active_loans_per_student`:** TAMS-42's Jira text names it, the PRD has no such rule. Add it with an invented default, or drop it from the ticket.
2. **Custodian fingerprint rights:** custodians can start enrollments and delete fingerprints; FR-9.4 says "admin-initiated".
3. **Kiosk enrollment paths:** the code uses `/kiosk/enrollment/sessions/...`; the PRD says enrollment shares `/api/kiosk/sessions/{id}/...`. Firmware will be written against the PRD paths.
4. **`/users` reads:** ADMIN and TECH_STAFF can read (employee numbers included). The original matrix said admin-only.

## Known gaps

- Not built: `POST /students` (import is the only way to create students), room RFID tags and station credentials (the PRD defines neither), the `reset-term` warnings route, `GET /dashboard/summary`, checkout/return flow, SignalR hub, station heartbeat and health sweep.
- Column names use a `_user_id` suffix where PRD 7.3 says `enrolled_by`, `started_by`, etc. Web and mobile need the final names.
- `sections` and `asset_categories` have no `created_at` or `updated_at`; the PRD is ambiguous.
- Roster CSV columns and limits are our own design (the PRD has none). Check the student-number pattern against the school's real format.
- Several settings ranges are guard rails, not PRD values; only grace (15-30), heartbeat (30-60) and class length (90/120/150/180) come from the PRD.
- Audit-log `from` and `to` bind as `DateTimeOffset`; send an explicit offset.
- TAMS-32's Jira description is a copy of the assets ticket and should be corrected. TAMS-31, 33, 39, 40, 41, 42 and 44 have no acceptance criteria.
- TAMS-41 stores a single merged template per capture. The PRD says "scan twice"; confirm the AS608 merges on the sensor.

## Next steps

1. Start Docker, `cp .env.example .env` (set `POSTGRES_PASSWORD`, `JWT_KEY` of 32 or more characters, and optionally `BOOTSTRAP_ADMIN_EMAIL` and `BOOTSTRAP_ADMIN_PASSWORD`), `docker compose up --build`, and check `/health`.
2. Confirm both migrations apply and the tables, partial indexes and checks exist (`psql \dt`, `\d+`).
3. Walk the main flows with the seeded admin: login, refresh and reuse detection, create a user, asset, room and station, import a roster, run an enrollment session, generate a QR sheet.
4. Settle the four open decisions.
5. Push `pre-dev` and the ticket branches to origin; open the PR from `pre-dev` to `dev`.
6. Update the Jira statuses.
