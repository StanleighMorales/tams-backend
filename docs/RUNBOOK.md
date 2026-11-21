# TAMS runbook

Operations for the Compose stack. Server setup is in `docs/DEPLOY.md`.

## 1. Backups

The `backup` service (`backup/`) runs every night at `BACKUP_HOUR` (default 02:00 Asia/Manila):

1. `pg_dump -Fc` of the database.
2. Encrypts it with [age](https://age-encryption.org) to the **public** key in `AGE_RECIPIENT`. The private key
   is never on the server, so a stolen server or bucket gives no readable data.
3. Does the same for the ASP.NET Data Protection key ring (`dp-keys`). The database holds fingerprint templates
   encrypted with that ring; without it a restored database has templates nobody can read.
4. Keeps the files in the `backups` volume and, when `S3_BUCKET` is set, uploads them to the S3-compatible bucket
   (Cloudflare R2 or Backblaze B2, `S3_ENDPOINT`). Files older than `BACKUP_KEEP_DAYS` (14) are deleted in both
   places. This is also how backups age out for the 30-day deletion promise: after a purge (TAMS-131) old backups still hold the data until they age out.
5. Writes `backup.json` (ok or failed, time) to the `backup-status` volume and logs the result
   (`docker compose logs backup`). With no `AGE_RECIPIENT` it records a failure and writes nothing.

**Alert on failure:** `GET /health` includes `"backup":"ok"` when the last backup succeeded in the last 36 hours,
and `"failed"`, `"stale"` or `"missing"` otherwise (HTTP stays 200 so the API is not pulled from rotation).
Point the uptime monitor at `/health` with a keyword check for `"backup":"ok"`.

**Not covered:** the `media` volume (kiosk photos, QR sheets) is not in the nightly backup, only the database and
key ring. Photos are covered by the provider VM snapshot (deployment-decision.md section 13).

### One-time setup

```
docker compose run --rm --entrypoint age-keygen backup
```

Prints `Public key: age1...` and the private key. Put the public key in `.env` as `AGE_RECIPIENT`. Store the
private key in two places that are not the server (a team member's password manager and an offline copy). Lose
it and every backup is unreadable. Then fill the `S3_*` variables and `docker compose up -d backup`.

Run one now: `docker compose run --rm backup backup-now`.

## 2. Restore into a fresh Compose stack

1. On the new machine: clone the repo, `cp .env.example .env`, set `POSTGRES_PASSWORD`, `JWT_KEY` (use the same
   `JWT_KEY` as before or all users must log in again), `TAMS_DOMAIN`. Leave `SEED_DEMO` false.
2. Start only the database: `docker compose up -d db`.
3. Fetch the two files (same timestamp) from the bucket, e.g.
   `aws s3 cp s3://<bucket>/tams-<ts>.dump.age . --endpoint-url <endpoint>` and the matching `.keys.tar.age`.
4. Have the private key file available for this step only, then:
   ```
   docker compose run --rm -v "$PWD/restore:/in:ro" -v "/path/to/age-key.txt:/id:ro" backup \
     restore /in/tams-<ts>.dump.age /id /in/tams-<ts>.keys.tar.age
   ```
   (Put the two files in `./restore`.) It replaces the database contents and unpacks the key ring into `dp-keys`.
5. `docker compose up -d`, then `curl https://<domain>/health` and sign in with a real admin account. Delete the
   private key from the machine afterwards.

Restoring only the database (no third argument) works for everything except fingerprint templates.

## 3. Restore drill log

A drill is only valid when it ends with a login on the restored stack. Record each one here.

| Date | Where | Backup used | Result |
|---|---|---|---|
| 2026-11-16 | Developer laptop, Docker Desktop (not the VPS). Stack A: `db`, `api`, `backup` with demo seed; one backup taken, volumes then destroyed. Stack B: new project, empty volumes, `db` only, restore, then `api`. | `tams-20261115T213257Z` (database + key ring), age key generated for the drill | Passed. `/health` ok, admin login succeeded on stack B, 5 users and 4 students present, matching stack A. The backup file was confirmed to be age ciphertext (`pg_restore` refused it). |
| (pending) | Clean machine or the VPS, last night's real backup from the bucket | | **NOT DONE.** The G3 "done when" asks for a restore of a real nightly backup on a clean machine; the laptop drill above used a freshly taken backup and local storage, not the off-site bucket. |

## 4. Other operations

Updating, logs, restart policy and the uptime check are in `docs/DEPLOY.md` section 8.
