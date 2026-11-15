#!/bin/sh
# Uptime check for NFR-6 (07:00-21:00 school days). Run every minute from cron on the VPS or any
# other machine:  * 7-20 * * 1-6 /opt/tams/scripts/uptime-check.sh https://tams.example.com
# Exit code 1 and a line in the system log when /health is not 200. Hook the alerting of your choice
# (mail, a free uptime service) onto the failure line; this script only detects and records.
url="${1:?usage: uptime-check.sh https://domain}"
if ! curl -fsS --max-time 10 "$url/health" > /dev/null; then
  logger -t tams-uptime "FAIL $url/health"
  echo "$(date -Is) FAIL $url/health" >> "${TAMS_UPTIME_LOG:-/var/log/tams-uptime.log}"
  exit 1
fi
