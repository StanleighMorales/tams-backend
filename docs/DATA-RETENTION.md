# Data retention and deletion (TAMS-131)

The letter to the school promises: keep data until the defense, then delete all fingerprint templates and
checkout photos within 30 days, with written proof.

## End-of-project purge (ADMIN only)

Both calls are irreversible and need the body `{"confirm":"DELETE"}`. Each writes one `audit_log` row
(`entity_type` `data_purge`, action `BIOMETRICS_PURGED` or `CHECKOUT_PHOTOS_PURGED`, counts in `after`) and
returns the counts plus a `summaryUrl`.

| Call | Deletes | Keeps |
|---|---|---|
| `POST /api/data-retention/purge-biometrics` | every row in `fingerprint_templates`, every enrollment session | students, audit log (including the earlier `FINGERPRINT_DELETED` entries) |
| `POST /api/data-retention/purge-checkout-photos` | every `CHECKOUT_PHOTO` file on disk and its `media_files` row | loans, ledger, audit log, asset images |

For photos the links on `checkout_sessions`, `loans` and `transaction_ledger` are set to null and
`loans.photo_purged_at` records when. Migration `RetentionPurge` makes this possible: the ledger's append-only
trigger now allows exactly one update, `photo_file_id` to NULL with every other column unchanged; any other
UPDATE or DELETE still fails.

`GET /api/data-retention/purges/{purgeId}/summary` returns a printable HTML record (who, when, counts). Open it
from the admin app, which sends the bearer token, and print or save it as PDF; keep it as the written proof.

## Scheduled clean-up

A daily sweeper (`RetentionSweeper`) deletes: kiosk sessions that ended `FAILED`, `DECLINED`, `CANCELLED` or
`EXPIRED` with no loan, approval or photo, older than `kiosk_session_retention_days` (default 30); and read
notifications older than `notification_retention_days` (default 90). Both are settings (`PUT /settings`).
Device tokens FCM reports as dead are already removed when a push is sent. Ordinary checkout photos are kept for
the whole project (admins audit them, FR-1.8) and only go in the final purge (decision G5-D1).

## Backups

Backups taken before a purge still contain the data. They are deleted after `BACKUP_KEEP_DAYS` (14) days locally
and in the bucket, which fits inside the 30-day promise: purge, then wait 14 days and confirm the oldest backup
in the bucket is newer than the purge. `docs/RUNBOOK.md` has the backup details.

## Running the final purge

1. Take a last backup if the team wants one, knowing it will hold the data for 14 days.
2. Call both purge endpoints as ADMIN; save both summary pages.
3. 14 days later, list the bucket and the `backups` volume and confirm nothing older than the purge remains.
4. Hand the summaries to the school.
