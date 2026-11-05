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
Caddyfile               reverse proxy and TLS
.env.example            copy to .env
Tams.slnx
src/Tams.Api/           minimal API; /health is the only endpoint so far
src/Tams.Api/Data/      TamsDbContext, entities, enums, migrations (schema v1)
```

## What is not here yet

This is the Compose skeleton (TAMS-20). Still to come:

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
