# Performance scripts (NFR-1 to NFR-5)

Results and the rules for recording them live in `docs/PERFORMANCE.md`. Needs k6 and Python 3 (stdlib only).
Run against a throwaway database: the seed adds thousands of rows and the NFR-5 run penalises its test students.

```
# 1. a stack with an admin (Development: BOOTSTRAP_ADMIN_EMAIL / BOOTSTRAP_ADMIN_PASSWORD), e.g.
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build

# 2. seed 2,000 students with one fingerprint each, 10 kiosks, 10 items, an instructor card (about 2 minutes)
python tests/perf/seed.py --base http://localhost:8080/api --admin <email> --password <pw> --students 2000 --vus 10

# 3. NFR-1, NFR-2 (and the server side of NFR-4): the kiosk flow under load, thresholds are the NFR limits
k6 run -e VUS=10 -e ITERATIONS=20 -e BASE=http://localhost:8080/api tests/perf/kiosk-flow.js

# 4. NFR-3 and NFR-5 (NFR-5 needs database access through --psql)
python tests/perf/nfr3_nfr5.py --base http://localhost:8080/api --admin <email> --password <pw> --runs 20 \
  --psql "docker compose exec -T db psql -U tams -d tams -tA -c"
```

Against the Caddy stack use `https://localhost/api` with `k6 --insecure-skip-tls-verify` and `--insecure` on the Python scripts.
`perf-data.json` (written by the seed, git-ignored) holds the generated cards, tags and templates.

| Script | Measures | Notes |
|---|---|---|
| `kiosk-flow.js` | per-step time (card, fingerprint, asset, photo, confirm, return steps), checkout total without the approval wait | one VU = one kiosk; the instructor approves through the API and that wait is not timed |
| `nfr3_nfr5.py` | scan to SignalR `asset.statusChanged`; `overdue_at - due_at` | NFR-5 sets `due_at` two seconds ahead and waits for the sweeper |

Limits to keep in mind: templates are random bytes against the placeholder matcher (STATUS.md, open decision 1), so
the NFR-4 number is only the cost of searching 2,000 templates, not of real minutiae matching. The k6 numbers include
no sensor, LCD or camera time. Say where the scripts ran when you record a result.
