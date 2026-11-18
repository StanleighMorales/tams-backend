# Development host (home PC, TAMS-133)

Interim server for web and mobile integration, before the VPS (TAMS-129). **Synthetic data only**: the school has
not approved off-campus biometric storage, so no real student names, fingerprints or photos go here.

## URLs (private, tailnet only)

Base URL: `https://desktop-j5od7j4.tail39de31.ts.net`

| What | URL |
|---|---|
| Health | `/health` (`{"status":"ok","database":"up", ...}`) |
| OpenAPI document | `/openapi/v1.json` |
| API reference (Scalar) | `/scalar` |
| Live hub (SignalR) | `/hubs/live` |
| Kiosk and room-station simulator | `/sim` |

Only devices signed in to the owner's Tailscale account can open it. A teammate or a phone must install Tailscale
and be invited to the tailnet first. It is not on the public internet (Tailscale Serve, not Funnel).

`backup` in `/health` reads `failed` until `AGE_RECIPIENT` is set in `.env` (TAMS-130). That does not affect the API.

## Accounts and test data

Demo accounts, station codes and tags are in `docs/TEST-DATA.md`. `SEED_DEMO=true` loads them into an empty
database. Push runs as the FAKE provider (SignalR events `approval.push` and `notice.push`) until Firebase
credentials are set (TAMS-73).

## How it runs

```
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build    # API on localhost:8080
tailscale serve --bg --https=443 http://localhost:8080                          # private HTTPS on the tailnet
```

`.env` holds a private `JWT_KEY` (never the public default in `docker-compose.dev.yml`) and the database password.
It is gitignored. The tailnet address is the only way in; no router ports are open and PostgreSQL is not published.

## Update routine

```
git pull
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build api
curl https://desktop-j5od7j4.tail39de31.ts.net/health
```

Then log in once as a demo user. Migrations run at start.

## Restart and what to do if the PC is off

- The host needs the PC on, Docker Desktop running, and Tailscale signed in. Containers use `restart: unless-stopped`,
  and Tailscale Serve keeps its config across restarts.
- Windows power: sleep off. Docker Desktop should start at login.
- If the PC is off, the URL times out for everyone. Tell the owner, who can start it; the database volume keeps its
  data.
- Check Serve: `tailscale serve status`. Turn it off: `tailscale serve --https=443 off`.

## Manual backup

```
docker compose exec db pg_dump -U tams tams > tams-dev-$(date +%F).sql
```

Run it before big changes. The nightly encrypted off-site backup is TAMS-130.

## CORS

Browser apps must have their origin in `CORS_ORIGINS` in `.env` (default `http://localhost:5173`, comma-separated),
then `docker compose ... up -d api`.

## Before this goes public (Tailscale Funnel)

Not done yet. Do these first: replace the known demo passwords, put `/sim` and `/scalar` behind a login or an access
rule, and keep the data synthetic.

## Not tested yet

A reboot of the PC (that Docker, the stack and Serve all come back without manual steps).
