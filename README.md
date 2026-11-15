# TAMS Backend

ASP.NET Core API and PostgreSQL for TAMS — RFID-Based Technical Assets Management System.
Capstone 2026, ACLC College of Mandaue.

Architecture is PRD section 6.1. Hosting is `deployment-decision.md` in the planning folder:
one VPS, three containers, Compose.

## Run it

```bash
cp .env.example .env     # set POSTGRES_PASSWORD
docker compose up --build
```

Then:

```bash
curl -k https://localhost/health
```

Expect `{"status":"ok","database":"up"}`. A 503 with `"database":"down"` means the API
is up but cannot reach PostgreSQL — check `docker compose logs db`.

Caddy redirects port 80 to 443 and signs the localhost certificate with its own local
CA, which is why `-k` is needed locally. On the VPS with a real `TAMS_DOMAIN` the
certificate is publicly trusted and `-k` is not.

## Running it for front-end work

```bash
cp .env.example .env     # set POSTGRES_PASSWORD, BOOTSTRAP_ADMIN_EMAIL and BOOTSTRAP_ADMIN_PASSWORD
docker compose -f docker-compose.yml -f docker-compose.dev.yml up --build
```

Needs only Docker Desktop. The API is then at **`http://localhost:8080/api/...`** (plain HTTP, so no
certificate warning) with the Scalar UI at `http://localhost:8080/scalar`. Sign in with
`POST /api/auth/login` and send `Authorization: Bearer <accessToken>`.

- **CORS:** browsers may call the API from the origins in `CORS_ORIGINS` (default
  `http://localhost:5173`, the Vite dev server). Change it in `.env` if the front end runs elsewhere.
- **Sample data:** with `SEED_DEMO=true` (the `.env.example` default) an empty database is filled with the data from
  `docs/TEST-DATA.md`: rooms, a kiosk, laptops and PCs, two sections and four students. Demo accounts are
  `tech@`, `custodian@`, `instructor@` and `instructor2@tams.test`, all with password `Demo#Pass123`; the admin is
  the `BOOTSTRAP_ADMIN_*` account. It runs only in Development and only once (it skips a database that already has
  the `STORE-1` room). To start over: `docker compose down -v`.
- **Tests:** `dotnet test` runs the endpoint tests against a throwaway PostgreSQL in Docker.

If port 8080 is already in use on your machine (Apache/XAMPP, for example), set `API_PORT=18080` in `.env` and use
that port instead. `docker-compose.dev.yml` also supplies a throwaway JWT key, so `.env` needs no `JWT_KEY` here.
Never use that file on the VPS: its key is public and it exposes the API without Caddy.

## API docs and error format

In Development the OpenAPI spec is at `/openapi/v1.json` and the Scalar UI at `/scalar`
(`dotnet run --project src/Tams.Api`, port 5111). In Production they are off; set
`Docs__Enabled=true` to turn them on. The JWT `Bearer` scheme is declared, so the UI's
Authorize box works once auth lands.

Every error is RFC 7807 `application/problem+json` with a stable `code` and a `traceId`:

```json
{"type":"...","title":"Not Found","status":404,"code":"NOT_FOUND","traceId":"00-..."}
```

Endpoints return coded problems with `Tams.Api.Http.Problems`, e.g.
`Problems.Conflict("RESTORE_CONFLICT", "Tag is already assigned.")`. Unhandled exceptions
become a 500 `INTERNAL_ERROR` with no detail or stack trace outside Development.

## Services

| Service | Image | Port | Notes |
|---|---|---|---|
| `proxy` | `caddy:2-alpine` | 80, 443 | Automatic HTTPS from `TAMS_DOMAIN`; passes WebSocket upgrades for SignalR |
| `api` | built from `Dockerfile` | 8080, internal | ASP.NET Core, non-root |
| `db` | `postgres:17-alpine` | internal only | Not published; the API is the only client |

Volumes: `db-data` (database), `media` (kiosk photos, report PDFs, QR sheets),
`caddy-data` and `caddy-config` (certificates).

## Layout

```
Dockerfile              api image, multi-stage
docker-compose.yml      the three services
docker-compose.dev.yml  local front-end work: publishes the API on :8080
Caddyfile               reverse proxy and TLS
.env.example            copy to .env
Tams.slnx
src/Tams.Api/           minimal API, one folder per feature
tests/Tams.Api.Tests/   endpoint tests, one real PostgreSQL in Docker
src/Tams.Api/Data/      TamsDbContext, entities, enums, migrations (schema v1)
src/Tams.Api/Kiosk/     kiosk sessions, approvals, room scans, heartbeats
src/Tams.Api/Loans/     loan rules and penalties, loans, anomalies, dashboard, LoanSweeper
docs/STATUS.md          what is built, decisions, open questions
docs/SIMULATOR.md      interactive kiosk simulator served at /sim in Development
```

## Kiosk and stations

Devices hold no credentials (FR-1.4). The counter kiosk sends `X-Station-Code: <station_code>` on
every call; room stations name themselves in the path. Every kiosk response has the same shape:
`sessionId`, `type`, `status`, `nextStep` (`FINGERPRINT`, `ASSET`, `PHOTO`, `WAIT`, `CONFIRM`, `DONE`),
the 4 `lcdLines` to show, `failureCode` and `pollIntervalSeconds`. A denial is `status: FAILED` with
the reason already on the LCD lines, not an HTTP error. Once an item tag has been tapped the response also
carries `item` (`assetCode`, `name`, `condition` GOOD, FAIR or DAMAGED) and the LCD shows "Laptop - GOOD". A
DAMAGED item is refused in checkout (`failureCode: ASSET_DAMAGED`, LCD "Item damaged / See lab staff") and in
event batch (same text, the batch carries on); returning a damaged item is still allowed.

**Several items per checkout and return (TAMS-121).** The setting `max_items_per_transaction` (1 to 10, default 1;
ADMIN or TECH_STAFF can change it) is how many items one student takes in one checkout. The limit that applies is the smaller of
it and what is left of `max_active_loans_per_student`. At 1 nothing changes: the first tap goes to the photo. Above 1, after the
fingerprint the LCD says "Up to N items" and each tap adds the item ("Added LP-014 / Laptop - GOOD / 1 of 3   X=undo /
Scan item or OK"); `POST .../confirm` (OK) ends the item step and the kiosk goes to the photo, reaching the limit does the same, and
`POST .../cancel` (X) takes the last item back out (with none left it cancels the checkout). A refused tap (damaged, unknown, not
available, `Already added`) shows the reason and the checkout carries on once there is at least one item; with none it is a denial.
The session view has `items` (`assetCode`, `name`, `condition`). The approval request (`approval.requested`, `GET /approvals/pending`,
the FCM payload) has `items` too, with `asset` still holding the first item. The student's Confirm creates one loan per item with the
same room, class length, due time and approval. A RETURN session keeps taking items when the setting is above 1 and OK is Done.
A multi-item transaction gets one Late Arrival Warning and one Non-Compliance Mark, not one per item.

In event batch, **Cancel undoes the last item lent** (the loan is voided, the item goes back to `IN_STORAGE`, LCD "Removed LP-014");
with nothing left to undo it ends the session. Staff void an unopened event loan from the web with
`POST /api/loans/{id}/void` `{"reason":"..."}` (ADMIN, TECH_STAFF). Only an event loan that is still `ACTIVE` and has not reached
its venue can be voided (`LOAN_NOT_VOIDABLE` otherwise). A voided loan stays as status `CANCELLED`; the append-only ledger gets a
`VOIDED` row and the audit log a `LOAN_VOIDED` row with the actor.

| Step | Call |
|---|---|
| Idle poll (picks up a staff-started enrollment) | `GET /api/kiosk/{code}/session` |
| Card tap (or `{"type":"RETURN"}` to return an item) | `POST /api/kiosk/{code}/sessions` `{"cardUid":"A1B2C3D4"}` |
| Fingerprint (base64 template) | `POST /api/kiosk/sessions/{id}/fingerprint` `{"template":"..."}` |
| Item tag | `POST /api/kiosk/sessions/{id}/asset` `{"tagUid":"04A1B2C3"}` |
| Photo (multipart `file`, JPEG; no file = camera failed) | `POST /api/kiosk/sessions/{id}/photo` |
| Poll every 2 s while `WAIT` | `GET /api/kiosk/sessions/{id}` |
| OK / X buttons (OK is also "Done" in batch modes) | `POST /api/kiosk/sessions/{id}/confirm`, `.../cancel` |
| Room station scan; reply `signal` is `SUCCESS` (green, chime) or `ERROR` (red, low tone) | `POST /api/stations/{code}/scans` `{"tagUid":"..."}` |
| Heartbeat every 30 to 60 s | `POST /api/stations/{code}/heartbeat` `{"firmwareVersion":"1.0.3","rssi":-61,"uptimeS":1200,"freeHeap":180000}` |

The instructor approves from the phone (`GET /api/approvals/pending`, `POST /api/approvals/{sessionId}/approve`).
Live events (PRD 6.4) arrive over SignalR at `/hubs/live?access_token=<jwt>`. Background jobs:
`LoanSweeper` (transit timeouts, overdue, kiosk session expiry, station health).

**The fingerprint matcher is a placeholder** (TAMS-58): bit-similarity over the raw template bytes
against an in-memory cache of decrypted templates. It tolerates noisy rescans of the same bytes but is
not real minutiae matching; see `docs/STATUS.md`. Calibration knob: `Kiosk:MatchThreshold` (default
0.85, range 0 to 1; env `Kiosk__MatchThreshold`). To simulate a noisy rescan, flip about 5% of the bits of
the enrolled template and submit it as base64.

## Database migrations

Schema v1 lives in `src/Tams.Api/Data/Migrations`. In Development the API applies pending
migrations on start; in Production apply them deliberately. The EF tool is pinned in
`dotnet-tools.json`:

```bash
dotnet tool restore
dotnet tool run dotnet-ef migrations add <Name> --project src/Tams.Api --output-dir Data/Migrations
dotnet tool run dotnet-ef database update --project src/Tams.Api
```

`ConnectionStrings__Default` must be set for these commands too.

## Local development without Docker

```bash
dotnet run --project src/Tams.Api
```

Needs a reachable PostgreSQL and `ConnectionStrings__Default` set, so Compose is
usually easier.

## Student roster import

`POST /students:import` (ADMIN, TECH_STAFF), `multipart/form-data` with the CSV in a `file`
field. Query flags: `dryRun=true` (validate and report, save nothing), `strict=true` (any bad
row rejects the whole file with 422). Max 2 MB and 5000 rows. UTF-8 (BOM, CRLF and quoted
commas are fine). See `docs/sample-roster.csv`.

Header (case-insensitive, all required, no other columns accepted):
`student_no,first_name,last_name,section_code,program,year_level,school_year`.
`school_year` looks like `2026-2027`. Students are upserted by `student_no`; the section is
found by `section_code` + `school_year` or created. Bad rows are skipped and listed in
`errors` with their line number and a stable `code` (`ROW_*`); file-level problems are a
400 with a `CSV_*` code.

```bash
curl -H "Authorization: Bearer $TOKEN" -F file=@docs/sample-roster.csv \
  "http://localhost:8080/students:import?dryRun=true"
```
