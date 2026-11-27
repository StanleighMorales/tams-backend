# API response samples (for the mobile and web apps)

The OpenAPI spec lists routes and request bodies but no response types, so this file shows the real responses the mobile
app reads. They were captured from the running API with test data; ids and times differ in your calls, field names do not.
`ResponseShapeTests` fails if a field listed here is renamed, added or dropped, so this file is kept honest: whoever
changes a response updates the test and this file in the same PR and tells the app team.

## Conventions

- JSON field names are camelCase. Ids are UUID strings.
- Enums are UPPER_SNAKE strings: `IN_TRANSIT`, `AWAITING_ARRIVAL`, `WRONG_ROOM`, `MATCHED`.
- Times are ISO 8601 with an offset (`2026-11-27T01:49:30.068801+00:00`). Show them in local time on the device.
- Lists are `{ "items": [...], "page", "pageSize", "total" }`. Query: `page` (from 1), `pageSize` (1 to 100).
- In the responses below an absent value is `null`, not a missing field, so a field you see here is always there.
- Errors are RFC 7807 problem details with a stable `code`: `{ "type", "title", "status", "code", "traceId", "errors"? }`.
  `errors` (field to messages) appears on `VALIDATION_FAILED`. Branch on `code`, not on `title`.
- Roles: ADMIN, TECH_STAFF, CUSTODIAN, INSTRUCTOR. Access per route is in `src/Tams.Api/Auth/PERMISSIONS.md`.

## Login: `POST /api/auth/login`

```json
{
  "accessToken": "<jwt>",
  "accessTokenExpiresAt": "2026-11-27T02:04:30.5864708+00:00",
  "refreshToken": "NIg2udAqk0fdHway8DKUzTGmDBGHTXMo4fm36A3rNf0",
  "user": { "id": "7aaf5da3-...", "email": "instructor@tests.local", "firstName": "INSTRUCTOR", "lastName": "Test",
            "role": "INSTRUCTOR", "isEventCoordinator": true }
}
```

`GET /api/auth/me` returns the full user: `id, email, employeeNo, firstName, lastName, role, isEventCoordinator,
defaultRoomId, status, createdAt, updatedAt, deletedAt, authCards`.

## Approvals waiting: `GET /api/approvals/pending` (INSTRUCTOR)

`frozen` is true while the instructor's approvals are frozen; `freezes` then says why. The same item shape arrives live as
the `approval.requested` event.

```json
{
  "frozen": false,
  "freezes": [],
  "items": [{
    "sessionId": "60c5271c-...",
    "student": { "id": "3a34a2b4-...", "studentNo": "SC758F5EB", "fullName": "First Last", "sectionCode": "BSCS-5455D29E" },
    "asset": { "id": "de6d1704-...", "assetCode": "KT-533E2BF7", "name": "Laptop 533E2BF7" },
    "items": [{ "id": "de6d1704-...", "assetCode": "KT-533E2BF7", "name": "Laptop 533E2BF7", "condition": "GOOD" }],
    "photoUrl": "/api/approvals/60c5271c-.../photo",
    "roomOptions": [{ "id": "fc6034fa-...", "code": "LAB-101", "name": "Computer Lab 1" }],
    "defaultRoomId": null,
    "classLengthOptions": [90, 120, 150, 180],
    "defaultClassLengthMinutes": 90,
    "requestedAt": "2026-11-27T01:49:29.296225+00:00",
    "expiresAt": "2026-11-27T01:51:29.671409+00:00"
  }]
}
```

`asset` repeats the first item; read `items` (a checkout can hold several). Approve with
`POST /api/approvals/{sessionId}/approve` `{ "destinationRoomId", "classLengthMinutes" }`.
`photoUrl` needs the same bearer token.

## Loans: `GET /api/loans`, `GET /api/loans/{id}`

Filters on the list: `status`, `roomId`, `approvedBy=me`. One list item, which is also `loan` in the detail:

```json
{
  "id": "05e17e29-...", "loanNo": "L-2026-000001", "type": "STANDARD", "status": "ACTIVE", "custodyState": "AWAITING_ARRIVAL",
  "asset": { "id": "de6d1704-...", "assetCode": "KT-533E2BF7", "name": "Laptop 533E2BF7", "status": "IN_TRANSIT" },
  "student": { "id": "3a34a2b4-...", "studentNo": "SC758F5EB", "fullName": "First Last", "sectionCode": "BSCS-5455D29E" },
  "approvedBy": { "id": "e989abbf-...", "fullName": "Test CBBC6196" },
  "coordinator": null, "event": null,
  "destinationRoom": { "id": "7d8f6433-...", "code": "R-7B66B7CE", "name": "Room 7B66B7CE" },
  "checkedOutAt": "2026-11-27T01:49:30.068801+00:00", "transitDeadlineAt": "2026-11-27T01:59:30.068801+00:00",
  "verifiedAt": null, "classLengthMinutes": 90, "classEndsAt": "2026-11-27T03:19:30.068801+00:00", "graceMinutes": 15,
  "dueAt": "2026-11-27T03:34:30.068801+00:00", "overdueAt": null, "missingAt": null, "returnedAt": null,
  "borrower": null, "returnedBy": null,
  "photoUrl": "/api/loans/05e17e29-.../photo", "lateArrivalWarningNo": null, "notes": null
}
```

- `status`: `ACTIVE`, `OVERDUE`, `RETURNED`, `MISSING`, `CANCELLED`.
- `custodyState`: `AWAITING_ARRIVAL`, `VERIFIED`, `ANOMALY`, `NOT_REQUIRED`. `AWAITING_ARRIVAL` to `VERIFIED` is the room check-in.
- Event loans have `type: "EVENT"`, `student: null`, and `coordinator` and `event` filled. `borrower` is filled once staff record who took the item.
- `GET /api/loans/{id}` is `{ "loan": {...}, "approval": {...}, "ledger": [...] }`. `approval` is
  `{ id, instructor, decision, destinationRoomId, classLengthMinutes, requestedAt, decidedAt, decidedDevice }`; `ledger` is the
  append-only history of the loan.

## Notifications: `GET /api/notifications?unread=true`

```json
{ "items": [], "page": 1, "pageSize": 25, "total": 0, "unreadCount": 0 }
```

An item is `{ id, type, title, body, loanId, studentId, createdAt, readAt, payload }`. `type` is e.g. `LOAN_OVERDUE`,
`LOAN_MISSING`, `LATE_ARRIVAL_WARNING`, `ANOMALY_RAISED`, `TRANSIT_TIMEOUT`, `STATION_OFFLINE`; `payload` is an object whose fields depend on the type
(it carries a `details` object with the loan number, item, student and room). Mark read with `POST /api/notifications/{id}/read`.

## Push token: `POST /api/device-tokens` `{ "token", "platform" }`

`platform` is `ANDROID`, `IOS` or `WEB` (any case). Answer: `{ "id", "platform", "lastSeenAt" }`.

## Room roster and audit (CUSTODIAN, ADMIN, TECH_STAFF)

**QR content.** A label holds `tams:asset:<asset uuid>`, not the bare UUID. The roster gives each item's exact `qrPayload`, so
the app can match scans offline by string compare. `scans:batch` accepts the raw scanned text (or a bare UUID).

`GET /api/rooms/{id}/roster`:

```json
{
  "room": { "id": "25d00581-...", "code": "R-71E5CD15", "name": "Room 71E5CD15" },
  "items": [{ "id": "de6d1704-...", "assetCode": "KT-533E2BF7", "name": "Laptop 533E2BF7", "category": "Cat-90EED3B9",
              "type": "BORROWABLE", "status": "IN_TRANSIT", "serialNo": null, "qrPayload": "tams:asset:de6d1704-..." }],
  "downloadedAt": "2026-11-27T01:49:31.0562477+00:00"
}
```

`POST /api/audits` `{ "roomId" }` answers 201 `{ "audit", "room", "roster" }`; `audit` is:

```json
{
  "id": "32a4d370-...", "roomId": "25d00581-...", "source": "MOBILE", "status": "IN_PROGRESS",
  "startedByUserId": "f6d77b08-...", "startedAt": "2026-11-27T01:49:31.08228+00:00", "completedAt": null,
  "signedOffByUserId": null, "signedOffAt": null,
  "expectedCount": 2, "matchedCount": 0, "missingCount": 0, "misplacedCount": 0, "unknownCount": 0, "importFileId": null,
  "reportPdfUrl": "/api/audits/32a4d370-.../report?format=pdf",
  "reportCsvUrl": "/api/audits/32a4d370-.../report?format=csv",
  "reportXlsxUrl": "/api/audits/32a4d370-.../report?format=xlsx"
}
```

`status` is `IN_PROGRESS`, `COMPLETED` or `SIGNED_OFF`. `POST /api/audits/{id}/scans:batch` takes
`{ "scans": [{ "clientScanId", "scannedValue", "scannedAt" }] }` (a re-sent `clientScanId` is ignored) and answers:

```json
{
  "auditId": "32a4d370-...", "accepted": 1, "duplicates": 0,
  "results": [{ "clientScanId": "81601a76-...", "result": "MATCHED", "assetId": "de6d1704-...", "duplicate": false,
                "assetCode": "KT-533E2BF7", "name": "Laptop 533E2BF7" }]
}
```

`result` is `MATCHED`, `MISPLACED` (belongs to another room) or `UNKNOWN`. `POST /api/audits/{id}/complete` and
`GET /api/audits/{id}` answer `{ "audit": {...}, "items": [...] }`. Completing records every unscanned roster item as `MISSING`:

```json
{ "id": "cd4328f3-...", "result": "MISSING", "assetId": "523d5a2d-...", "assetCode": "KT-856C1063", "name": "Laptop 856C1063",
  "scannedValue": "tams:asset:523d5a2d-...", "scannedAt": "2026-11-27T01:49:31.293155+00:00", "syncedAt": "2026-11-27T01:49:31.293155+00:00" }
```

For a `MISSING` item, `scannedValue` and `scannedAt` are filled by the server (the completion time), not by a phone scan.
Sign-off and import are ADMIN and TECH_STAFF only.

## Live events (SignalR)

Hub `/hubs/live`, JWT as `?access_token=`. The hub only sends. Every account joins `user:{id}`; ADMIN, TECH_STAFF and
CUSTODIAN also join `staff`. See PRD 6.4 for the payloads.

- An instructor's app receives `approval.requested`, `instructor.frozen`, `instructor.unfrozen`, `warning.issued`,
  `anomaly.raised`, `anomaly.resolved` and `loan.overdue`.
- **Also `loan.created` and `asset.statusChanged` for loans they approved** (an event coordinator gets them for their event
  loans). So a room check-in (`IN_TRANSIT` to `IN_ROOM_VERIFIED`) shows up in My Loans without a reload.
- `asset.statusChanged` for an item that is not on a loan (a counter tap before the loan exists, an undo) goes to `staff` only.
