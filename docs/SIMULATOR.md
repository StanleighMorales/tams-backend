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
| Network and heartbeat | ESP32 Wi-Fi and heartbeat task (interval and jitter) | `POST /stations/{code}/heartbeat`, `GET /stations/health` | TAMS-50 (Wi-Fi and HTTP client), TAMS-80 (room station heartbeat) |
| PN532 reader | card and tag reads | `POST /kiosk/{code}/sessions`, `.../asset` | TAMS-51 (PN532 firmware) |
| AS608 fingerprint | sensor image (or legacy template), noise knob, wrong finger, enrollment | `.../fingerprint`, `POST /enrollment/sessions` | TAMS-52 (capture and upload), TAMS-53 (enrollment mode) |
| Counter kiosk (LCD2004, buttons, buzzer, webcam) | 4x20 LCD from `lcdLines`, OK/X, tones per step, camera with retry and fault | `.../photo`, `.../confirm`, `.../cancel`, session polling | TAMS-76 (LCD), TAMS-77 (buttons, buzzer), TAMS-78 (checkout flow, 2 s polling) |
| Room reader | room tag tap, LED and tone from `signal` | `POST /stations/{code}/scans` | TAMS-80 (room station build) |
| Instructor phone | approve or decline, destination room, class length | `/auth/login`, `/approvals/*`, `/hubs/live` | TAMS-35, TAMS-74, TAMS-75 (mobile approvals) |
| Admin and loans | loan list and detail | `GET /loans`, `GET /loans/{id}` | TAMS-68, TAMS-69 (web loans) |
| Scenarios | drive all of the above | all | acceptance checks for 50 to 53 |

The ticket column is the Jira ticket whose firmware or client the panel stands in for (TAMS-79, the return-flow firmware, is exercised by the Return scenario).

## Scenarios

Happy-path checkout with room check-in, RETURN, unknown card, unknown fingerprint (3 tries), unknown tag, camera error,
instructor declines, approval timeout (sets `approval_timeout_seconds` to 30 as admin, then restores it, and waits), loan limit,
item not on loan, wrong-room scan, plus three added for the firmware work:

- **Enrollment on the kiosk:** staff starts an enrollment for 2024-0004 finger 8, the kiosk polls it up, one scan and OK, then the
  scenario checks the finger is stored and removes it again.
- **Event batch checkout + bulk return:** admin creates an event running now for the demo coordinator (Ina), her card opens
  `EVENT_BATCH`, two tags are borrowed, OK; then `EVENT_RETURN` returns both. The event is closed afterwards.
- **Wi-Fi drop** (waits about a minute): sends a heartbeat, turns Wi-Fi off, waits for `GET /stations/health` to say Offline, turns
  Wi-Fi back on, and expects Online after the next heartbeat.

Each logs `SCENARIO PASS/FAIL` in the event log. Checkouts leave a loan open until it is returned, so run "Return" between
checkouts you start by hand (the loan-limit and wrong-room scenarios return their own). `/sim#run=<index>` runs one scenario on load
and writes the result to `body[data-result]`, for headless smoke tests.

## One page per device

`/sim?device=kiosk`, `?device=room`, `?device=phone` and `?device=admin` show a single device each, so each can sit in its own
browser tab or window like the real thing. They share the API, not state: the room tab posts scans and heartbeats as
`ROOM-101`, the kiosk tab polls and heartbeats as `KIOSK-1`, the phone tab logs in as the instructor. Phone and admin tabs never
heartbeat or poll as a station. Settings are stored per device. `/sim` with no parameter is the full dashboard and the only page
with the scenarios.

## Making it behave like the hardware

- **Latency, packet loss and HTTP timeout** (Network panel) apply to hardware calls only (kiosk, room reader, heartbeats). A dropped or
  timed-out request is logged and the LCD shows "Server timeout" or "No Wi-Fi", so the firmware's retry logic can be designed against it.
- **AS608 image (default, TAMS-128)** is what the real kiosk will upload: raw 8-bit grayscale, 256 x 288 = 73,728 bytes, one byte per
  pixel row by row, base64 in `image` with `width` and `height` (the API accepts 64 to 1024 each, up to 512 KB; PNG or JPEG is not
  accepted). The page draws a synthetic ridge pattern per student no. and finger index (same recipe as
  `tests/Tams.Api.Tests/FixtureFingerprints.cs`). The noise slider adds pixel noise (noise / 4, up to 60) and each scan shifts a few
  pixels, so a rescan is never identical. The API scores it with SourceAFIS; a match needs at least `Kiosk:SourceAfisThreshold`
  (provisional default 40, not calibrated on real captures). Checked by hand: a clean rescan scores about 56, noise 120 about 46, another
  student 0. The image is processed in memory and never stored. Expected time on the real sensor is the open item: the kiosk must keep the
  fingerprint step inside 2 s or report the real figure (NFR-1).
- **Legacy template** (untick "Send sensor image"): 512 bytes, the sensor's character file size (the API accepts 1 to 4096), matched by
  Hamming distance at `Kiosk:MatchThreshold` (default 0.85). The noise slider flips random bytes: 0 to about 160 still match, 256 does not.
  Template rows (`RAW_V1`) and image rows (`SOURCEAFIS_V1`) only match their own kind. To switch a student, delete the finger
  (`DELETE /students/{id}/fingerprints/{index}`) and enroll again.
- **Download full log (.json)** saves every non-poll call with its station header, request body and response, to hand to whoever writes
  the firmware as the traffic the API expects. Passwords, templates and images are redacted.

## Known gaps

- The simulator's fingerprint images are synthetic, so the noise slider and "wrong finger" show the pipeline and how the threshold behaves, not real error rates. The SourceAFIS threshold (`Kiosk:SourceAfisThreshold`, default 40) is provisional until real AS608 captures are available (TAMS-52, TAMS-128 phase 1).
- The AS608 "scan twice" is one submit. FCM push (TAMS-62) is sent by the API when Firebase is configured; the simulator's phone still gets approval requests over SignalR (or polling).
- Settings in `localStorage` (base URL, codes, knobs, emails). Passwords are not saved.
- Calling another origin needs that origin in `Cors:Origins`; the default is the Vite port only.
