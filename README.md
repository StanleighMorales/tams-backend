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
src/Tams.Api/           minimal API (auth, users, assets, students, enrollment, settings, audit log)
tests/Tams.Api.Tests/   endpoint tests, one real PostgreSQL in Docker
src/Tams.Api/Data/      TamsDbContext, entities, enums, migrations (schema v1)
```

## What is not here yet

Still to come:

- Auth, CRUD, roster import — Sprint 1.
- The SignalR hub — the Caddy side is already correct, so the hub is just a map call.

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
