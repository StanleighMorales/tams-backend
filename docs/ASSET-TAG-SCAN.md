# Asset tag enrollment at the kiosk ("Scan tag")

Backend changes that let staff read an item's RFID tag by tapping it on the counter kiosk, instead of typing the UID
into the asset form. It builds on the "Add card" request flow (see `docs/CARD-ENROLLMENT.md`) and uses the same
in-memory desk and the same kiosk tap route.

| Change | Summary |
|---|---|
| [1. Scan tag at the kiosk](#1-scan-tag-at-the-kiosk) | Staff press **Scan tag** on the asset form; the idle kiosk switches to tag scan mode and hands back the UID of the next tag tapped. |
| [2. Kiosk poll says which kind of request is waiting](#2-kiosk-poll-says-which-kind-of-request-is-waiting) | `cardEnrollment` in `GET /kiosk/{code}/session` gains `kind`: `CARD` or `ASSET_TAG`. |
| [3. Shared desk refactor](#3-shared-desk-refactor) | `CardEnrollmentDesk` holds both kinds; Add card behavior is unchanged. |

**Nothing is saved by a tag scan.** It only reads the UID. The staff member still saves the asset with
`POST /api/assets` or `PUT /api/assets/{id}` and `rfidTagUid` set to that UID, and the existing checks run again then.

No migration, no new settings, no new environment variables.

---

## 1. Scan tag at the kiosk

### Flow

1. On the asset form (new or edit), staff (ADMIN or TECH_STAFF) press **Scan tag**. The web app calls
   `POST /api/assets/tag-scan`, with `assetId` when editing an existing asset.
2. The idle counter kiosk polls `GET /api/kiosk/{code}/session` as usual. While the request is waiting and **no session
   is open**, the response carries it as `cardEnrollment` with `kind: "ASSET_TAG"`, and `lcdLines` become
   `Scan item tag / <label> / Tap the item tag`.
3. The next tag tapped goes to the **same** route as Add card: `POST /api/kiosk/{code}/card-enrollment/{id}/tap`. The
   API checks the UID against the same rule as saving an asset:
   - not an **active** authorization card, and
   - not the tag of another live (not deleted) asset. The asset being edited may keep its own tag.
4. The web app polls `GET /api/assets/tag-scan/{id}` until `COMPLETED`, puts `tagUid` in the form's RFID field, and
   the staff member saves the form.

A tag that is already in use does **not** end the request: it stays `WAITING` with `lastError`, so the next tap can be
a different tag. The kiosk goes back to normal when a tag is read, or when the request is cancelled or expires (90 s),
or a kiosk session opens (an open session always comes first).

### New endpoints

Web app (ADMIN, TECH_STAFF; same roles as `POST`/`PUT /assets`):

| Method | Route | Body | Answer |
|---|---|---|---|
| POST | `/api/assets/tag-scan` | `{"assetId"?, "label"?}` | 201 `TagScan`. Cancels any request still waiting, **of either kind** (one at a time). |
| GET | `/api/assets/tag-scan/{id}` | | 200 `TagScan` |
| POST | `/api/assets/tag-scan/{id}/cancel` | | 200 `TagScan`. Idempotent: a finished request is returned unchanged. |

Body fields, both optional:

- `assetId`: the asset being edited. Its own current tag is then accepted, not treated as "in use". Leave it out for
  a new asset. Must be a live asset, otherwise 404 `ASSET_NOT_FOUND`.
- `label`: the text shown on the kiosk LCD, such as the asset code typed so far. The default is the asset's code when
  `assetId` is given, else `New item`. The LCD shows the first 20 characters.

```jsonc
// TagScan
{
  "id": "9b2f6c1e-...",
  "assetId": null,                // or the asset being edited
  "label": "LP-NEW",
  "status": "WAITING",            // WAITING | COMPLETED | CANCELLED | EXPIRED
  "expiresAt": "2026-11-26T09:12:30Z",
  "tagUid": null,                 // the normalized UID once COMPLETED, e.g. "04A1B2C3D4E5F6"
  "lastError": null               // while WAITING: why the previous tap was refused, e.g. {"code":"TAG_ALREADY_ASSIGNED","detail":"Tag ... is already attached to LP-0003."}
}
```

### Kiosk tap answers

HTTP 200, like other kiosk denials. Same route as Add card, so the kiosk firmware doesn't need to know which kind it
is answering. It shows `lcdLines`.

| Outcome | `status` | `failureCode` | `tagUid` | `lcdLines` |
|---|---|---|---|---|
| Tag read | `COMPLETED` | null | the UID | `Tag read / <UID> / Save it in the / web app` |
| UID is an active card or another asset's tag | `WAITING` | `TAG_ALREADY_ASSIGNED` | null | `Tag already in use / Tap a different / tag` |

For a tag scan, the tap answer has `tagUid` where an Add card answer has `card`.

### Errors

| Status | Code | When |
|---|---|---|
| 403 | `FORBIDDEN` | start, get or cancel by a role other than ADMIN or TECH_STAFF |
| 404 | `ASSET_NOT_FOUND` | start: `assetId` is unknown or deleted |
| 404 | `TAG_SCAN_NOT_FOUND` | get or cancel: unknown id, an Add card id, or finished more than 10 minutes ago |
| 400 | `INVALID_RFID_UID` | tap: UID is not 8 to 20 hex characters |
| 403 | `STATION_NOT_AUTHENTICATED` | tap: unknown station, not a counter kiosk, wrong `X-Station-Code`, or IP allowlist |
| 404 | `CARD_ENROLLMENT_NOT_FOUND` | tap: unknown id. The tap route is shared, so it keeps the Add card code for both kinds. |
| 409 | `CARD_ENROLLMENT_NOT_WAITING` | tap: the request is completed, cancelled or expired, or another tap is being processed |
| 409 | `TAG_ALREADY_ASSIGNED` | **not from these routes**: `POST`/`PUT /api/assets` when the UID is taken at save time (see below) |

Tag scan ids and Add card ids are kept apart: `GET /auth-cards/enrollment/{id}` with a tag scan id answers 404
`CARD_ENROLLMENT_NOT_FOUND`, and the reverse answers 404 `TAG_SCAN_NOT_FOUND`.

### What the web app should do

- Show **Scan tag** next to the asset form's RFID field for ADMIN and TECH_STAFF. Send `assetId` when editing.
- Poll `GET /assets/tag-scan/{id}` every 1 to 2 s. While `WAITING`, show `lastError.detail` if it is set. On
  `COMPLETED`, fill the RFID field with `tagUid`. On `EXPIRED` or `CANCELLED`, close the dialog and offer **Try again**.
- Call `/cancel` if the dialog is closed early.
- **Still handle 409 `TAG_ALREADY_ASSIGNED` when saving the asset.** The check at tap time only gives early warning:
  another asset or card can take the UID between the scan and the save.

---

## 2. Kiosk poll says which kind of request is waiting

`GET /api/kiosk/{stationCode}/session`: the `cardEnrollment` object (present when the kiosk is idle and a request is
waiting) gains a `kind` field.

```jsonc
"cardEnrollment": {
  "id": "9b2f6c1e-...",
  "kind": "ASSET_TAG",            // NEW: CARD (Add card) or ASSET_TAG (Scan tag)
  "userName": "LP-NEW",           // for ASSET_TAG this carries the asset label
  "email": "",                    // empty for ASSET_TAG: don't rely on it being filled
  "expiresAt": "2026-11-26T09:12:30Z"
}
```

The field is additive. Firmware that ignores `kind` still works: it shows `lcdLines`, which already hold the right
prompt, and taps the same route with `cardEnrollment.id`. The simulator at `/sim` works the same way, so its
**Tap a new card** button also answers a tag scan.

---

## 3. Shared desk refactor

`CardEnrollmentDesk` (`src/Tams.Api/Facilities/CardEnrollment.cs`) now holds both kinds of request:

- Each entry has a `Kind` (`CARD` or `ASSET_TAG`), plus `AssetId` and `TagUid` for tag scans. For tag scans,
  `UserName` holds the label.
- The private `Add` method does the "one waiting request at a time" rule for both kinds. `Start` (Add card) and the new
  `StartTagScan` both go through it. **A tag scan cancels a waiting Add card, and the reverse.**
- Lookups are filtered by kind (`Get(id, kind)`), behind `Find`/`FindTagScan` and `Cancel`/`CancelTagScan`.
- New `CompleteTagScan`. `Release` now returns the entry's status string instead of a `CardEnrollment` view, so it
  works for both kinds. The tap handler builds both kinds of answer from that.
- `Claim` carries `Kind` and `AssetId`, and `Tap` branches on the kind. The tag rule is
  **shared, not copied**: `AssetEndpoints.TagProblem` is the one check, called by the asset save (`Collision`) and by the
  tag tap, so the two cannot drift apart.
- `PromptLines` picks the LCD prompt by kind.
- The asset-save conflict message for a tag held by another asset is now "Tag ... is already attached to <asset code>."
  (it used to say "RFID tag ... is already assigned to another asset."). The code and status are unchanged.
- The `SERVER_ERROR` detail is now "The tap could not be saved." (it was "The card could not be saved.").

Add card requests, responses and LCD text are otherwise unchanged. All existing card enrollment tests still pass.

### Design notes and limits

- **In memory, one API instance.** Same as Add card: requests last 90 s and an API restart drops them.
- **One request at a time across both kinds.** Two staff members using Add card and Scan tag at the same moment cancel
  each other's waiting request. The other dialog sees `CANCELLED`.
- **Not tied to a station.** Every idle counter kiosk shows the request, and the first tap wins.
- **Nothing is written to the database** by a tag scan, and there is no audit row. The audit happens when the asset is
  saved, as before.

---

## Files

| File | Change |
|---|---|
| `src/Tams.Api/Facilities/CardEnrollment.cs` | `TagScanStart`, `TagScan` records; `kind` on `CardEnrollmentPrompt`; desk holds both kinds; `/assets/tag-scan` endpoints; tag branch in `Tap`; tag prompt in `PromptLines`; `TAG_SCAN_NOT_FOUND`. |
| `src/Tams.Api/Assets/AssetEndpoints.cs` | The tag part of `Collision` moves into the shared `TagProblem`; `Collision` calls it. Same rule and codes. |
| `tests/Tams.Api.Tests/Integration/FacilityTests.cs` | 3 new tests (below). |

## Tests

New in `FacilityTests`:

- `Tag_scan_reads_the_next_tapped_tag_without_saving_anything`: the idle kiosk shows the `ASSET_TAG` prompt; another
  asset's tag is refused and the request keeps waiting; a fresh tag completes it; no card is created; the id isn't
  visible as a card enrollment.
- `Tag_scan_on_edit_accepts_the_assets_own_tag_and_can_be_cancelled`: with `assetId`, the label defaults to the asset
  code and the asset's own tag is accepted; cancel works; an unknown `assetId` gives 404 `ASSET_NOT_FOUND`; CUSTODIAN
  gets 403.
- `Tag_scan_and_add_card_replace_each_other_and_keep_their_ids_apart`: each kind cancels the other's waiting request,
  the poll shows the right `kind`, and a card id on the tag-scan route gives 404 `TAG_SCAN_NOT_FOUND`.

Full suite: **338 passed, 0 failed** (run locally on 2026-11-27).

## Deploying

- Rebuild the API (`docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build api` locally).
- No migration, no new settings or environment variables.
- Clients: the web app (Scan tag on the asset form) and, optionally, the kiosk firmware (it can read `kind` to show its
  own screen). Older firmware and the current web app keep working unchanged.
