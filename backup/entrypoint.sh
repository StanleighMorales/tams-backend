#!/bin/sh
# Default: loop forever, one backup a day at BACKUP_HOUR (local time in TZ, default 02:00 Asia/Manila).
# `restore <file> <identity>` and `backup-now` run once and exit (see docs/RUNBOOK.md).
case "${1:-}" in
  restore) shift; exec restore.sh "$@" ;;
  backup-now) exec backup.sh ;;
esac
hour="${BACKUP_HOUR:-02}"
while true; do
  now=$(date +%s)
  next=$(date -d "$(date +%Y-%m-%d) $hour:00:00" +%s)
  [ "$next" -le "$now" ] && next=$((next + 86400))
  echo "next backup at $(date -d "@$next" -Iseconds)"
  sleep $((next - now))
  backup.sh || true   # failure is already logged and recorded in the status file; keep looping
done
