# Kiosk simulator

A single page, `src/Tams.Api/Sim/kiosk-sim.html`, that plays the counter kiosk, the room reader, the instructor's phone and an
admin screen against the **real** API. Vanilla JS, no build step. The API serves it at `/sim` in **Development only** (the
route is not mapped in any other environment). Same origin as the API, so no CORS.

## Run it

Needs Docker for PostgreSQL. With Compose (demo data on):

```
# .env: JWT_KEY can stay empty with the dev override; set these three
BOOTSTRAP_ADMIN_EMAIL=admin@tams.test
BOOTSTRAP_ADMIN_PASSWORD=Admin#Pass123
SEED_DEMO=true
docker compose -f docker-compose.yml -f docker-compose.dev.yml up --build
```

Open `http://localhost:8080/sim` (or your `API_PORT`). Without Docker for the API: set `ConnectionStrings__Default`,
`Bootstrap__AdminEmail`, `Bootstrap__AdminPassword`, `Seed__Demo=true`, `ASPNETCORE_ENVIRONMENT=Development`, then
`dotnet run --project src/Tams.Api` and open `/sim` on its port. The page's base URL defaults to `/api` (same origin).
The demo seeder and CORS come from `dev` (merged into this branch); the seeder fills rooms, a kiosk, a room reader, assets,
students and demo accounts only when `Seed__Demo=true`.

## Demo credentials and cards

| What | Value |
|---|---|
| Admin (staff panel) | `admin@tams.test` / `Admin#Pass123` (whatever you set in `BOOTSTRAP_*`; the page pre-fills these) |
| Instructor (phone panel) | `instructor@tams.test` / `Demo#Pass123` |
| Other demo accounts | `tech@`, `custodian@`, `instructor2@tams.test`, same password |
| Instructor card UID | `04A1B2C3` |
| Item tags | LT-001 `E2000017221101441890`, LT-002 `04A1B2C3D4E5F6` |
| Stations | counter kiosk `KIOSK-1`, room reader `ROOM-101` (in LAB-101); rooms LAB-101, LAB-102, STORE-1 |
| Students | `2024-0001` Juan, `2024-0002` Maria, `2024-0003` Ana, `2024-0004` Pedro (no fingerprints yet) |

Students have no enrolled finger in the seed. Use **Enrollment mode** (staff login needed), or run any scenario: with the staff
login it enrolls the student first.

## Manual flow

1. Log in the phone (instructor) and staff (admin) panels. The phone connects to `/hubs/live` over SignalR; if the
   SignalR browser client (cdnjs, pinned 8.0.7) cannot load, it falls back to polling `/approvals/pending` every 3 s.
2. Enrollment: Student no. `2024-0001`, finger index 1, "Enrollment mode", then "Place finger", then "OK / Confirm".
3. Checkout: "Tap card", "Place finger", "Tap tag on kiosk". The webcam step runs by itself (generated JPEG, or "Use real
   camera"). The request appears on the phone: pick room and class length, Approve. The LCD says to confirm: "OK / Confirm".
4. Loans panel shows the loan `IN_TRANSIT` (custody `AWAITING_ARRIVAL`). "Tap tag at room door" posts a room scan to `ROOM-101`
   (green LED = `SUCCESS`), and the loan becomes verified in the room.
5. Return: "Start RETURN", "Place finger", "Tap tag on kiosk". No confirm; the loan closes on the tag tap.

Wi-Fi off stops polling and heartbeats; the staff panel shows the station going Warning, then Offline (US-15).

## Panels and the Jira tickets

| Panel | Simulates | Backend | Tickets |
|---|---|---|---|
| Network and heartbeat | ESP32 Wi-Fi and heartbeat task (interval and jitter) | `POST /stations/{code}/heartbeat`, `GET /stations/health` | TAMS-79 (F8, station health) |
| PN532 reader | card and tag reads | `POST /kiosk/{code}/sessions`, `.../asset` | TAMS-76 (PN532 firmware) |
| AS608 fingerprint | sensor, noise knob, wrong finger, enrollment | `.../fingerprint`, `POST /enrollment/sessions` | TAMS-77 (AS608), TAMS-50 (kiosk checkout flow) |
| Counter kiosk (LCD2004, buttons, buzzer, webcam) | 4x20 LCD from `lcdLines`, OK/X, tones per step, camera with retry and fault | `.../photo`, `.../confirm`, `.../cancel`, session polling | TAMS-78 (LCD, buzzer), TAMS-50, TAMS-51 |
| Room reader | room tag tap, LED and tone from `signal` | `POST /stations/{code}/scans` | TAMS-79 and the room check-in story |
| Instructor phone | approve or decline, destination room, class length | `/auth/login`, `/approvals/*`, `/hubs/live` | TAMS-51 (approval) |
| Admin and loans | loan list and detail | `GET /loans`, `GET /loans/{id}` | TAMS-52, TAMS-53 (loans, transit) |
| Scenarios | drive all of the above | all | acceptance checks for 50 to 53 |

The ticket mapping is a best guess from the brief (TAMS-50 to 53 software flow, 76 to 79 hardware); the ticket titles were not looked up in Jira, so correct it there if it is off.

## Scenarios

Happy-path checkout with room check-in, RETURN, unknown card, unknown fingerprint (3 tries), unknown tag, camera error,
instructor declines, approval timeout (sets `approval_timeout_seconds` to 30 as admin, then restores it, and waits), loan limit,
item not on loan, wrong-room scan. Each logs `SCENARIO PASS/FAIL` in the event log. They share state (loans), so run
"Return" between checkouts if one is left open. `/sim#run=<index>` runs one scenario on load and writes the result to
`body[data-result]`, for headless smoke tests.

## Known gaps

- The fingerprint matcher is a placeholder (exact template equality until TAMS-58), so keep the noise slider at 0. The slider and
  "wrong finger" exist so the calibration can be tuned once a real matcher lands. The template bytes are made up.
- The AS608 "scan twice" is one submit. FCM push is not wired; once TAMS-62 lands, the push will also arrive here over SignalR in dev.
- Settings in `localStorage` (base URL, codes, knobs, emails). Passwords are not saved.
- Calling another origin needs that origin in `Cors:Origins`; the default is the Vite port only.
- Event-batch modes (coordinator cards, EVENT_RETURN) are reachable with a custom card UID but have no scenario.
