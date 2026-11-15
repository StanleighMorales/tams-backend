# Performance: NFR-1 to NFR-5

Scripts and how to run them: `tests/perf/README.md`. Gap G6, ticket TAMS-132.

**Status: the official measurement is NOT DONE.** The only numbers below are a smoke run on a developer laptop, to
prove the scripts work. They are not the NFR evidence. The measurement the ticket asks for (target hardware or the
VPS, a real fingerprint matcher, QA's TAMS-83 run) is still open.

## Targets

| NFR | Target | Script | What it measures |
|---|---|---|---|
| NFR-1 | p95 of each kiosk step <= 2 s | `kiosk-flow.js` (`step_*` trends, thresholds set to 2000 ms) | server round trip per step: card, fingerprint, asset, photo, confirm, and the three return steps |
| NFR-2 | full checkout, excluding approval wait, <= 60 s | `kiosk-flow.js` (`checkout_total_excl_approval`, threshold 60000 ms) | sum of the five checkout steps. No sensor, camera or human time is included, so it is a floor |
| NFR-3 | station scan to SignalR dashboard event <= 3 s | `nfr3_nfr5.py` | time from sending the room scan to receiving `asset.statusChanged` on a staff hub connection |
| NFR-4 | 1:N match over 2,000 real-format templates <= 1.5 s | `kiosk-flow.js` `step_fingerprint` with `seed.py --students 2000` | the placeholder matcher over 2,000 random templates. **Not real-format**: valid only after G1 replaces the matcher and real AS608 templates are enrolled |
| NFR-5 | sweep lateness <= 15 s | `nfr3_nfr5.py --psql ...` | `overdue_at - due_at` after setting `due_at` two seconds ahead |

## Results (official runs)

Fill one row per NFR from a run on the hardware named. Keep the date, host, commit and how the scripts were run.

| NFR | Measured | Result | Date | Hardware / host | Commit | Run by |
|---|---|---|---|---|---|---|
| NFR-1 |  |  |  |  |  |  |
| NFR-2 |  |  |  |  |  |  |
| NFR-3 |  |  |  |  |  |  |
| NFR-4 |  |  |  |  |  |  |
| NFR-5 |  |  |  |  |  |  |

Where a target fails, write the number and the reason instead of leaving the cell empty.

## Smoke run (scripts check only, not NFR evidence)

2026-11-16, one Windows 11 laptop running Docker Desktop (API, PostgreSQL 17) and the scripts together, plain HTTP to the
API port (no Caddy, no network between client and server), branch `feature/TAMS-132-performance-scripts` on top of `dev` a2ac71c,
Development environment, 2,000 seeded students with one random 512-byte template each, 10 kiosks, 10 items.

`k6 run -e VUS=10 -e ITERATIONS=20` (200 checkout and return flows, 0 failures):

| Step | p95 |
|---|---|
| card | 41 ms |
| fingerprint (2,000 templates) | 43 ms warm; 400 ms in an earlier 10-iteration run that included the cold cache load |
| asset | 24 ms |
| photo | 32 ms |
| confirm | 136 ms |
| checkout total excl. approval | 253 ms |

`nfr3_nfr5.py --runs 12`: scan to event p50 38 ms, p95 46 ms, max 57 ms. Overdue lateness p50 9.0 s, p95 9.4 s, max 9.9 s.

What this does and does not say: the code path is fast with a loopback client, so any miss on real hardware will come from
the network, the VPS, TLS or the real matcher, not from these steps. The overdue sweep runs every 15 s, so the worst case
for NFR-5 is close to the 15 s limit even though these 12 samples sat near 9 s; judge it on the VPS with enough runs.

## To finish G6

1. Run the scripts against the Compose stack on the target (VPS or the demo laptop) from a second machine, through Caddy.
2. After G1 (real matcher), re-seed with real-format templates and re-run for NFR-4.
3. Fill the results table above and hand it to QA (TAMS-83).
