# Card enrollment at the kiosk ("Add card") and related card changes

Backend changes for registering instructor authorization cards by tapping them on the counter kiosk, instead of
typing UIDs into the web app. Also covers two card fixes found along the way.

| Change | Summary |
|---|---|
| [1. Add card at the kiosk](#1-add-card-at-the-kiosk) | Staff press **Add card** on an instructor; the idle kiosk switches to card enrollment and registers the next card tapped. |
| [2. Users include their cards](#2-users-include-their-cards) | `GET /users` and `GET /users/{id}` now return `authCards`. The web app showed "No card registered" for everyone before. |
| [3. Revoked cards can be issued again](#3-revoked-cards-can-be-issued-again) | A card UID is unique among **active** cards only. Migration `20261124183403_ActiveCardUidUnique`. |

---

## 1. Add card at the kiosk

### Flow

1. Staff (ADMIN or TECH_STAFF) press **Add card** on an instructor. The web app calls `POST /api/auth-cards/enrollment`.
2. The idle counter kiosk polls `GET /api/kiosk/{code}/session` as usual. While a request is waiting and **no session is
   open**, the response carries it as `cardEnrollment`, and `lcdLines` become `Add card for / <name> / Tap the new card`.
   The kiosk firmware treats that as **card enrollment mode**.
3. The next card tapped goes to `POST /api/kiosk/{code}/card-enrollment/{id}/tap`. The API issues the card exactly like
   `POST /api/auth-cards` (same UID and owner checks) and writes `AUTH_CARD_ISSUED` with the **staff member who pressed
   Add card** as the actor.
4. The web app polls `GET /api/auth-cards/enrollment/{id}` until `COMPLETED`, then refreshes the user.

The kiosk is back in normal mode as soon as the card is registered, the request is cancelled or expires (90 s), or a
kiosk session opens (an open session always comes first). A card that is already in use does **not** end the
request: it stays `WAITING` with `lastError`, so the next tap can be a different card.

### New endpoints

Web app (ADMIN, TECH_STAFF; same roles as `POST /auth-cards`):

| Method | Route | Body | Answer |
|---|---|---|---|
| POST | `/api/auth-cards/enrollment` | `{"userId"}` | 201 `CardEnrollment`. Cancels any request still waiting (one at a time). |
| GET | `/api/auth-cards/enrollment/{id}` | | 200 `CardEnrollment` |
| POST | `/api/auth-cards/enrollment/{id}/cancel` | | 200 `CardEnrollment`. Idempotent: a finished request is returned unchanged. |

Kiosk (anonymous, gated by `X-Station-Code` like the other `/kiosk` routes):

| Method | Route | Body | Answer |
|---|---|---|---|
| POST | `/api/kiosk/{stationCode}/card-enrollment/{id}/tap` | `{"cardUid"}` | 200 `{id, status, failureCode, card, lcdLines}` |

```jsonc
// CardEnrollment
{
  "id": "0441867c-...",
  "userId": "58a1dca2-...",
  "userName": "Ian Instructor",
  "email": "instructor2@tams.test",
  "status": "WAITING",            // WAITING | COMPLETED | CANCELLED | EXPIRED
  "expiresAt": "2026-11-24T17:48:20Z",
  "card": null,                   // the issued card once COMPLETED (same shape as /auth-cards items)
  "lastError": null               // while WAITING: why the previous tap was refused, e.g. {"code":"TAG_ALREADY_ASSIGNED","detail":"..."}
}
```

Kiosk tap answers (HTTP 200, like other kiosk denials):

| Outcome | `status` | `failureCode` | `lcdLines` |
|---|---|---|---|
| Card registered | `COMPLETED` | null | `Card registered / <name> / <UID>` |
| UID already an active card or an asset tag | `WAITING` | `TAG_ALREADY_ASSIGNED` | `Card already in use / Tap a different / card` |
| Owner no longer valid (inactive, not an instructor) | `WAITING` | e.g. `USER_INACTIVE` | `Card not added / <code> / See the web app` |

Errors:

| Status | Code | When |
|---|---|---|
| 400 | `VALIDATION_FAILED` | `userId` missing |
| 400 | `USER_NOT_FOUND`, `USER_INACTIVE`, `CARD_OWNER_NOT_INSTRUCTOR` | start: the user cannot hold a card |
| 400 | `INVALID_RFID_UID` | tap: UID is not 8 to 20 hex characters |
| 403 | `STATION_NOT_AUTHENTICATED` | tap: unknown station, not a counter kiosk, wrong `X-Station-Code`, or IP allowlist |
| 404 | `CARD_ENROLLMENT_NOT_FOUND` | unknown id, or finished more than 10 minutes ago |
| 409 | `CARD_ENROLLMENT_NOT_WAITING` | tap on a request that is completed, cancelled or expired |

### Changed endpoint

`GET /api/kiosk/{stationCode}/session` has a new field, `cardEnrollment`: `null`, or `{id, userName, email, expiresAt}`
when the kiosk is idle and a request is waiting (then `lcdLines` show the Add card prompt). Additive; existing clients
can ignore it.

### Design notes and limits

- **Requests are in memory** (`CardEnrollmentDesk`, a singleton). They last 90 s, so losing one to an API restart only
  means pressing Add card again. This assumes **one API instance** (true for the Docker and Railway setups); several
  instances would need the requests in the database.
- **One request at a time.** A new one cancels the one still waiting. It is not tied to a station: every idle counter
  kiosk shows it and the first tap wins.
- **Double taps are safe.** A tap claims the request before writing; a second tap during that write gets
  `CARD_ENROLLMENT_NOT_WAITING`.
- The kiosk holds no user credentials (FR-1.4 unchanged); the station code check is the same as for checkouts.

---

## 2. Users include their cards

`GET /api/users` and `GET /api/users/{id}` now include `authCards`: every card of the user, newest first, all statuses,
same shape as `/api/auth-cards` items (`id, cardUid, userId, status, issuedAt`). Write responses (`POST`/`PUT /users`)
return `authCards: null`.

Before this, the web app's People screen read `user.authCards`, which the API never sent, so every instructor showed
"No card registered" even with a card.

---

## 3. Revoked cards can be issued again

Before: `auth_cards.card_uid` had a unique index over **all** rows. Cards are never deleted (revoked and lost cards
keep their row as history), so a physical card could never be registered again after being revoked.

Now:

- Migration **`20261124183403_ActiveCardUidUnique`** replaces `ix_auth_cards_card_uid_uq` with a partial unique index
  `WHERE status = 'ACTIVE'`. It runs at API start like the other migrations. (Its `Down` restores the full index and
  fails if a UID by then has several rows.)
- Issuing a revoked or lost UID again creates a **new row**; the old row stays `REVOKED`/`LOST`, so who held the UID
  before stays on record.
- "UID in use" checks count active cards only: `POST /auth-cards`, card replace, Add card, and the asset tag check in
  `POST`/`PUT /assets`.
- The kiosk looks a UID up preferring the active card, else the newest row. A UID that is only revoked is still refused
  with `CARD_INACTIVE`.
- `PUT /auth-cards/{id}` with `{"status":"ACTIVE"}` on an old row whose UID is active elsewhere now answers
  409 `TAG_ALREADY_ASSIGNED` instead of a 500.
- Demo seeding (`DevSeed`) still skips a UID that has any row, so revoked demo cards are not recreated on restart.

---

## Files

| File | Change |
|---|---|
| `src/Tams.Api/Facilities/CardEnrollment.cs` | **New.** `CardEnrollmentDesk` (in-memory requests) and `CardEnrollmentEndpoints`. |
| `src/Tams.Api/Facilities/AuthCards.cs` | Card issuing moved into shared `IssueAsync` (used by `POST /auth-cards` and the kiosk tap); `CheckOwner` became `OwnerProblem` returning a `CardProblem`; active-only `UidInUse`; unique violation on status update answers 409. |
| `src/Tams.Api/Facilities/Support.cs` | `Support.ActorId`; `Support.Audit` takes an optional actor; maps the card enrollment endpoints. |
| `src/Tams.Api/Kiosk/KioskEndpoints.cs` | Idle session poll returns `cardEnrollment` and its LCD lines; card lookup prefers the active row. |
| `src/Tams.Api/Users/UserDtos.cs`, `UserEndpoints.cs` | `UserCardDto`; `authCards` on list and detail. |
| `src/Tams.Api/Assets/AssetEndpoints.cs` | Asset tag vs card collision counts active cards only. |
| `src/Tams.Api/Data/TamsDbContext.cs` | Partial unique index on `card_uid`. |
| `src/Tams.Api/Data/Migrations/20261124183403_ActiveCardUidUnique*.cs`, snapshot | **New** migration. |
| `src/Tams.Api/Program.cs` | Registers `CardEnrollmentDesk` as a singleton. |
| `tests/Tams.Api.Tests/Integration/FacilityTests.cs` | 6 new tests (below). |

## Tests

New in `FacilityTests`:

- `Card_enrollment_switches_the_idle_kiosk_and_registers_the_next_tapped_card`
- `Card_enrollment_keeps_waiting_after_a_taken_card_and_can_be_cancelled`
- `Card_enrollment_new_request_replaces_the_waiting_one`
- `Card_enrollment_is_for_instructors_staff_and_known_kiosks`
- `A_revoked_card_can_be_issued_again_and_the_old_row_stays_as_history`
- `The_kiosk_refuses_a_revoked_card_and_accepts_it_once_issued_again`

Full suite: 334 passed, 0 failed.

## Deploying

- Rebuild the API (`docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build api` locally). The
  migration applies at start.
- No new settings or environment variables.
- Clients that use it: the web app (`CardEnrollmentButton` on the user detail) and the kiosk firmware
  (`tams-kiosk-esp32`, card enrollment mode). Older kiosk firmware keeps working; it just never enters card
  enrollment mode.
