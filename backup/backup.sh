#!/bin/sh
# One backup run: pg_dump (custom format, compressed) -> age (public key only; the private key is not
# on this server) -> /backups (kept BACKUP_KEEP_DAYS, default 14) -> S3-compatible bucket if S3_BUCKET is set.
# Result goes to $STATUS_DIR/backup.json, which the API reads for /health.
set -u
umask 077
keep="${BACKUP_KEEP_DAYS:-14}"
dir=/backups
status="${STATUS_DIR:-/status}/backup.json"
stamp=$(date -u +%Y%m%dT%H%M%SZ)
name="tams-$stamp.dump.age"
keysname="tams-$stamp.keys.tar.age"
tmp="$dir/.dump.tmp"

fail() {
  echo "BACKUP FAILED: $1" >&2
  printf '{"ok":false,"at":"%s","error":"%s"}\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$1" > "$status"
  rm -f "$dir/$name" "$dir/$keysname" "$tmp"
  exit 1
}

mkdir -p "$dir" "$(dirname "$status")"
[ -n "${AGE_RECIPIENT:-}" ] || fail "AGE_RECIPIENT is not set"

export PGPASSWORD="$POSTGRES_PASSWORD"
# pipefail is not in POSIX sh, so dump to a temp file first and check each step.
pg_dump -h db -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc -Z 6 -f "$tmp" || fail "pg_dump failed"
age -r "$AGE_RECIPIENT" -o "$dir/$name" "$tmp" || fail "age encryption failed"
rm -f "$tmp"

# The Data Protection key ring encrypts the fingerprint templates in the dump: without it a restored
# database has templates nobody can read. Same recipient, separate file.
tar -C /keys -cf "$tmp" . || fail "key ring archive failed"
age -r "$AGE_RECIPIENT" -o "$dir/$keysname" "$tmp" || fail "age encryption (keys) failed"
rm -f "$tmp"

if [ -n "${S3_BUCKET:-}" ]; then
  for f in "$name" "$keysname"; do
    aws s3 cp "$dir/$f" "s3://$S3_BUCKET/$f" --endpoint-url "$S3_ENDPOINT" || fail "upload failed"
  done
  cutoff=$(date -u -d "-$keep days" +%Y%m%d)
  aws s3 ls "s3://$S3_BUCKET/" --endpoint-url "$S3_ENDPOINT" | awk '{print $4}' | grep '^tams-.*\.age$' |
  while read -r f; do
    d=$(echo "$f" | cut -c6-13)
    [ "$d" -lt "$cutoff" ] && aws s3 rm "s3://$S3_BUCKET/$f" --endpoint-url "$S3_ENDPOINT"
  done
fi

find "$dir" -name 'tams-*.age' -mtime +"$keep" -delete
size=$(wc -c < "$dir/$name")
printf '{"ok":true,"at":"%s","file":"%s","bytes":%s}\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$name" "$size" > "$status"
echo "backup ok: $name ($size bytes)"
