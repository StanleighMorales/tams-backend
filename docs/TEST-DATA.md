# Test data for every endpoint on `dev`

Sections 1 to 13 are also automated: `dotnet test` starts a throwaway PostgreSQL in Docker (Docker Desktop must be
running) and runs them against the real API (`tests/Tams.Api.Tests/Integration`, one file per area). Use this page
for manual checks in Scalar.

Every body below was run against the merged `dev` build (PostgreSQL 17, Development) and gave the status shown.
Base URL locally: `http://localhost:5111/api` (Scalar UI at `http://localhost:5111/scalar`). Use the **Authorize** box with
`Bearer <accessToken>`. Run the sections in order: later ones use ids returned by earlier ones.

Passwords here are throwaway. Never reuse them outside a test database.

## 0. Admin to log in with

The first ADMIN is seeded on startup in Development when both are set:
`Bootstrap__AdminEmail` and `Bootstrap__AdminPassword` (Compose: `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD`).
Password rule: 8 to 128 characters.

## 1. Auth

| Call | Body | Expect |
|---|---|---|
| `POST /auth/login` | `{"email":"admin@verify.test","password":"Verify#Pass123"}` | 200 `accessToken`, `refreshToken` |
| `POST /auth/login` | wrong password | 401 |
| `POST /auth/refresh` | `{"refreshToken":"<from login>"}` | 200, new token pair |
| `POST /auth/refresh` | the **old** token again | 401 `REFRESH_TOKEN_REUSED`, all sessions revoked |
| `POST /auth/change-password` | `{"currentPassword":"Verify#Pass123","newPassword":"Verify#Pass456"}` | 200 |
| `POST /auth/change-password` | new = current | 400 |
| `POST /auth/logout` | `{"refreshToken":"<token>"}` (body optional; without it all tokens are revoked) | 200 |
| `GET /health` | none | 200 `{"status":"ok","database":"up"}` |

## 2. Users (ADMIN writes, ADMIN/TECH_STAFF read)

Roles: `ADMIN`, `TECH_STAFF`, `CUSTODIAN`, `INSTRUCTOR`.

`POST /users` (all 201):
```json
{"email":"tech@tams.test","password":"Tech#Pass123","employeeNo":"E-1002","firstName":"Tess","lastName":"Technician","role":"TECH_STAFF"}
{"email":"custodian@tams.test","password":"Cust#Pass123","employeeNo":"E-1003","firstName":"Joy","lastName":"Custodian","role":"CUSTODIAN"}
{"email":"instructor@tams.test","password":"Inst#Pass123","employeeNo":"E-1004","firstName":"Ina","lastName":"Instructor","role":"INSTRUCTOR","isEventCoordinator":true}
{"email":"instructor2@tams.test","password":"Inst#Pass123","employeeNo":"E-1005","firstName":"Ian","lastName":"Instructor","role":"INSTRUCTOR"}
```
Errors: duplicate email 409; password `abc` 400; `isEventCoordinator:true` on a CUSTODIAN 400.

Others: `GET /users?role=INSTRUCTOR&q=ina&page=1&pageSize=10`, `GET /users/{id}`,
`PUT /users/{id}` (full replacement, no password): `{"email":"tech@tams.test","employeeNo":"E-1002","firstName":"Tessa","lastName":"Technician","role":"TECH_STAFF"}`,
`POST /users/{id}/deactivate`, `/activate`, `/reset-password` (returns `temporaryPassword`; the old password stops working),
`DELETE /users/{id}`, `GET /users?includeDeleted=true`, `POST /users/{id}/restore`.

## 3. Rooms (any signed-in user reads; ADMIN/TECH_STAFF write)

`POST /rooms` (201):
```json
{"code":"STORE-1","name":"Equipment Storage","building":"Main","floor":1,"isStorage":true}
{"code":"LAB-101","name":"Computer Lab 1","building":"Main","floor":1,"isStorage":false}
{"code":"LAB-102","name":"Computer Lab 2","building":"Main","floor":2,"isStorage":false}
```
Errors: duplicate `code` 409; `floor` 101 gives 400 (allowed 0 to 100).
Others: `GET /rooms?q=lab&sort=code&page=1&pageSize=10`, `GET /rooms/{id}`,
`PUT /rooms/{id}` `{"code":"LAB-102","name":"Computer Lab 2 (renamed)","building":"Main","floor":2,"isStorage":false,"status":"ACTIVE"}`,
`DELETE /rooms/{id}`, `POST /rooms/{id}/restore` (ADMIN).

## 4. Stations

Types: `COUNTER_KIOSK`, `ROOM_STATION`. `POST /stations` (201):
```json
{"stationCode":"KIOSK-1","type":"COUNTER_KIOSK","roomId":"<STORE-1 id>","name":"Counter Kiosk"}
{"stationCode":"ROOM-101","type":"ROOM_STATION","roomId":"<LAB-101 id>","name":"Lab 1 Reader"}
```
Creating a `ROOM_STATION` also returns `"key"` (64 hex characters) once; later reads show only `"hasKey": true`. Counter kiosks get no key.
Errors: duplicate `stationCode` 409.
Others: `GET /stations?type=ROOM_STATION&roomId=<id>`, `GET /stations/health` (counts of online/warning/offline),
`GET /stations/{id}`, `PUT /stations/{id}` (same shape plus `"status":"ACTIVE"`), `DELETE /stations/{id}`, `POST /stations/{id}/restore`,
`POST /stations/{id}/rotate-key` (200, new `"key"` once, the old one stops working; ADMIN, TECH_STAFF; 400 `STATION_KEY_NOT_APPLICABLE` for a kiosk, 404 for a deleted station).
Room-station calls (`POST /stations/{code}/scans` and `/heartbeat`, no JWT) send `X-Station-Key: <key>`. A wrong key gives 403 `STATION_NOT_AUTHENTICATED`; a missing key does too once `station_key_required` is 1 (section 11). In the demo data ROOM-101 has the key `demo-room-101-key`.

## 5. Authorization cards

Cards can only belong to **instructors or event coordinators**. A custodian owner gives 400 `CARD_OWNER_NOT_INSTRUCTOR`.
UIDs are hex, 8 to 20 characters, even count; `:`, `-` and spaces are ignored and the value is stored uppercase.

| Call | Body | Expect |
|---|---|---|
| `POST /auth-cards` | `{"cardUid":"04:A1:B2:C3","userId":"<instructor id>"}` | 201 |
| `POST /auth-cards` | `{"cardUid":"ZZZ","userId":"<instructor id>"}` | 400 |
| `POST /auth-cards` | same UID, other instructor | 409 `TAG_ALREADY_ASSIGNED` |
| `GET /auth-cards?userId=<id>&status=ACTIVE` | | 200 |
| `GET /auth-cards/{id}` | | 200 |
| `PUT /auth-cards/{id}` | `{"status":"ACTIVE"}` | 200 (statuses `ACTIVE`, `LOST`, `REVOKED`) |
| `POST /auth-cards/{id}/replace` | `{"newCardUid":"04D1E2F3","oldStatus":"LOST"}` | 200 `{oldCard,newCard}` |
| `POST /auth-cards/{id}/revoke` | `{"status":"REVOKED"}` | 200 |

## 6. Asset categories (read: ADMIN/TECH_STAFF/CUSTODIAN; write: ADMIN/TECH_STAFF)

`POST /asset-categories` (201):
```json
{"name":"Laptops","description":"Borrowable laptops","isBorrowable":true}
{"name":"Desktop PCs","description":"Fixed lab PCs","isBorrowable":false}
```
Errors: duplicate name 409.
Others: `GET /asset-categories?q=lap&sort=name&page=1&pageSize=10`, `GET /asset-categories/{id}`, `PUT /asset-categories/{id}`, `DELETE /asset-categories/{id}`.

## 7. Assets

Types: `BORROWABLE`, `FIXED`. Statuses: `IN_STORAGE`, `AT_COUNTER`, `IN_TRANSIT`, `IN_ROOM_VERIFIED`, `LOCATION_ANOMALY`, `OVERDUE`, `MISSING`, `MAINTENANCE`, `RETIRED`.
Conditions: `GOOD`, `FAIR`, `DAMAGED`. A BORROWABLE asset needs an `rfidTagUid`. The kiosk refuses a `DAMAGED` asset at the tag tap
(`ASSET_DAMAGED`) in checkout and event batch; to test it, create an extra BORROWABLE asset with `"condition":"DAMAGED"`.

`POST /assets` (201):
```json
{"assetCode":"LT-001","name":"Dell Latitude 5440","categoryId":"<Laptops id>","type":"BORROWABLE","brand":"Dell","model":"Latitude 5440","serialNo":"SN-LT-001","rfidTagUid":"E2000017221101441890","homeRoomId":"<STORE-1 id>","status":"IN_STORAGE","condition":"GOOD","replacementCost":45000.00,"acquiredAt":"2026-06-15","notes":"Charger included"}
{"assetCode":"LT-002","name":"Lenovo ThinkPad E14","categoryId":"<Laptops id>","type":"BORROWABLE","brand":"Lenovo","model":"E14","serialNo":"SN-LT-002","rfidTagUid":"04A1B2C3D4E5F6","homeRoomId":"<STORE-1 id>","condition":"GOOD","replacementCost":38000}
{"assetCode":"PC-101-01","name":"Lab 1 Desktop 01","categoryId":"<Desktop PCs id>","type":"FIXED","brand":"HP","model":"ProDesk 400","serialNo":"SN-PC-101-01","homeRoomId":"<LAB-101 id>","condition":"GOOD","replacementCost":32000}
```
Errors: duplicate `assetCode` 409; duplicate `rfidTagUid` 409; BORROWABLE without a tag 400; `{"categoryId":""}` 400.

| Call | Notes |
|---|---|
| `GET /assets?q=dell&categoryId=<id>&homeRoomId=<id>&status=IN_STORAGE&type=BORROWABLE&sort=name&page=1&pageSize=10` | `pageSize` max 100 |
| `GET /assets/{id}`, `PUT /assets/{id}` | PUT sends the full body again |
| `POST /assets/{id}/image` | multipart, field `file`, a PNG or JPEG |
| `GET /assets/{id}/image`, `DELETE /assets/{id}/image` | |
| `GET /assets/{id}/qr` | PNG; payload is `tams:asset:<uuid>` |
| `GET /assets/{id}/qr?format=svg&size=400` | SVG |
| `POST /assets/qr-sheet` | `{"assetIds":["<id>","<id>"]}` or `{"categoryId":"<id>"}` gives a PDF |
| `DELETE /assets/{id}`, `GET /assets?includeDeleted=true`, `POST /assets/{id}/restore` | restore is ADMIN only |

## 8. Sections

`POST /sections` (201). `schoolYear` must be `YYYY-YYYY` with consecutive years.
```json
{"code":"BSCS-3A","program":"BSCS","yearLevel":3,"schoolYear":"2026-2027","adviserUserId":"<instructor id>"}
{"code":"BSIT-1B","program":"BSIT","yearLevel":1,"schoolYear":"2026-2027"}
```
Errors: duplicate code in the same school year 409; `"schoolYear":"2026-2028"` 400.
Others: `GET /sections?schoolYear=2026-2027&program=BSCS&q=3a`, `GET /sections/{id}`, `PUT /sections/{id}`.

## 9. Students and roster import

`POST /students:import` (multipart, field `file`; add `?dryRun=true` to preview, `?strict=true` to reject the whole file on any bad row).
Header row must be exactly these columns: `student_no,first_name,last_name,section_code,program,year_level,school_year`.
Max 2 MB and 5000 rows. `docs/sample-roster.csv` is a ready file; this one gives 4 created and 0 errors:
```csv
student_no,first_name,last_name,section_code,program,year_level,school_year
2024-0001,Juan,Dela Cruz,BSCS-3A,BSCS,3,2026-2027
2024-0002,Maria,"Santos, Jr.",BSCS-3A,BSCS,3,2026-2027
2024-0003,Ana,Reyes,BSIT-1B,BSIT,1,2026-2027
2024-0004,Pedro,Garcia,BSCS-3A,BSCS,3,2026-2027
```
Results seen: dry run `committed:false, created:4`; real run `created:4`; second run `created:0, unchanged:4`.

Bad file (empty `student_no`, `year_level` 99, one good row):
```csv
student_no,first_name,last_name,section_code,program,year_level,school_year
,NoNumber,X,BSCS-3A,BSCS,3,2026-2027
2024-0009,Bad,Year,BSCS-3A,BSCS,99,2026-2027
2024-0010,Good,Row,BSCS-3A,BSCS,3,2026-2027
```
Lenient: 200, `created:1, skipped:2`. `?strict=true`: 422, nothing committed. Wrong columns (`id,name`): 400. No file: 400.

Others: `GET /students?q=juan&sectionId=<id>&schoolYear=2026-2027&status=ACTIVE&page=1&pageSize=10`, `GET /students/{id}`,
`PUT /students/{id}` `{"studentNo":"2024-0001","firstName":"Juan","lastName":"Dela Cruz Jr.","sectionId":"<id>"}`,
`DELETE /students/{id}`, `GET /students?includeDeleted=true`, `POST /students/{id}/restore` (ADMIN).

> Student `status` (`ACTIVE`, `INACTIVE`) and `standing` (`GOOD`, `WARNED`, `BLOCKED`) are UPPER_SNAKE text like every other enum.
> A number or a wrong word in a body or query gives 400 `VALIDATION_FAILED`.

## 10. Fingerprint enrollment

Kiosk routes need the header `X-Station-Code: KIOSK-1` and no JWT. A missing or wrong header gives 403.

| Step | Call | Body | Expect |
|---|---|---|---|
| 1 | `POST /enrollment/sessions` (staff) | `{"studentId":"<id>","stationId":"<KIOSK-1 id>","fingerIndex":1}` | 201 |
| | same with `fingerIndex` 10 | | 400 (allowed 0 to 9) |
| | a second session for the same student while one is open | | 409 |
| 2 | `GET /enrollment/sessions/{id}` (staff) | | 200 |
| 3 | `GET /kiosk/enrollment/sessions/{id}` | | 200 |
| 4 | `POST /kiosk/enrollment/sessions/{id}/fingerprint` | `{"template":"<base64>","sensorModel":"AS608","qualityScore":92}` | 200 |
| 5 | `POST /kiosk/enrollment/sessions/{id}/confirm` | none | 200 |
| 6 | `GET /students/{studentId}/fingerprints` | | 200, the raw template is never returned |
| | `POST /kiosk/enrollment/sessions/{id}/cancel` or `POST /enrollment/sessions/{id}/cancel` | | 200 |
| | `DELETE /students/{studentId}/fingerprints/1` | | 200 |

A sample base64 template: `ZmFrZS1hczYwOC10ZW1wbGF0ZS0wMDAx` (any base64 text works; the matcher is still a placeholder).

## 11. Settings (read: ADMIN/TECH_STAFF/CUSTODIAN; write: ADMIN, and TECH_STAFF for `max_items_per_transaction` only)

| Key | Default | Allowed |
|---|---|---|
| `transit_window_minutes` | 10 | 1 to 60 |
| `class_length_default_minutes` | 90 | 90, 120, 150, 180 |
| `grace_minutes` | 15 | 15 to 30 |
| `kiosk_poll_interval_seconds` | 2 | 1 to 10 |
| `approval_timeout_seconds` | 120 | 30 to 600 |
| `kiosk_idle_timeout_seconds` | 60 | 15 to 300 |
| `heartbeat_interval_seconds` | 30 | 30 to 60 |
| `station_warning_missed_windows` | 1 | 1 to 5 |
| `station_offline_missed_windows` | 2 | 1 to 10 |
| `late_arrival_warning_limit` | 3 | from 0 |
| `marks_before_block` | 2 | 1 to 10 |
| `max_items_per_transaction` | 1 | 1 to 10 |
| `enrollment_session_timeout_seconds` | 120 | see `/settings` |
| `station_key_required` | 0 | 0 or 1 (0 = grace period: room stations without a key still work) |

`GET /settings`, `GET /settings/grace_minutes`, `PUT /settings/grace_minutes` `{"value":20}` (200),
`{"value":99}` (400), unknown key `PUT /settings/nope` (404),
`PUT /settings` `{"grace_minutes":15,"transit_window_minutes":12,"class_length_default_minutes":120}` (200).

## 12. Audit log (ADMIN only)

`GET /audit-log?entityType=asset&page=1&pageSize=10`, and
`GET /audit-log?actorUserId=<id>&from=2026-01-01T00:00:00%2B00:00&to=2027-01-01T00:00:00%2B00:00`.
Send an explicit offset in `from` and `to`; `entityType` values are lowercase snake_case (`asset`, `asset_category`, `student`, `user`).

## 13. Role checks (log in as each user from section 2)

| Role | Allowed | Expect 403 |
|---|---|---|
| TECH_STAFF | `GET /users` | `POST /users`, `PUT /settings/{key}`, `GET /audit-log`, `POST /assets/{id}/restore` |
| CUSTODIAN | `GET /assets`, `GET /students` | `GET /users`, `POST /rooms`, `GET /audit-log` |
| INSTRUCTOR | `GET /rooms` | `GET /assets`, `GET /students`, `GET /settings`, `GET /users` |
| none | `GET /health`, `/auth/login`, `/auth/refresh` | everything else gives 401 |

## 14. Excel checklist and report (TAMS-125)

As ADMIN, with a room that has assets: `GET /api/rooms/{id}/checklist:export?format=xlsx` returns a workbook (columns asset_id, asset_code, name, category, serial_no, found, notes). Type `Y` in `found` for some rows and send it to `POST /api/audits/import` (multipart: `file`, `roomId`); a CSV still works, the format is detected from the file. Then `GET /api/audits/{id}/report?format=xlsx` (sheets Summary and Items), next to `pdf` and `csv`. Any other format is 400; a corrupt workbook is 400 `CSV_MALFORMED`.

## 15. Not on `dev` yet

Kiosk checkout and return, approvals, room scans, loans, anomalies, dashboard, events, inventory audits, notifications,
violations, `POST /students` and the SignalR hub are on `feature/prd-transaction-core` and have no tickets yet.
