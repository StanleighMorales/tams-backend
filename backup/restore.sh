#!/bin/sh
# restore.sh <backup.dump.age> <age-identity-file> [keys.tar.age]
# Decrypts and restores into the running Compose database, replacing its contents. With the third argument it
# also unpacks the Data Protection key ring into /keys (the dp-keys volume) so fingerprint templates stay
# readable. The identity (private key) is mounted for this one run only; it is never stored on the server.
# See docs/RUNBOOK.md.
set -eu
file="${1:?usage: restore <backup.dump.age> <identity-file> [keys.tar.age]}"
identity="${2:?usage: restore <backup.dump.age> <identity-file> [keys.tar.age]}"
keys="${3:-}"
export PGPASSWORD="$POSTGRES_PASSWORD"
tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT
age -d -i "$identity" -o "$tmp" "$file"
pg_restore -h db -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists --no-owner "$tmp"
if [ -n "$keys" ]; then
  age -d -i "$identity" -o "$tmp" "$keys"
  tar -C /keys -xf "$tmp"
  echo "key ring restored"
fi
echo "restore finished from $file"
