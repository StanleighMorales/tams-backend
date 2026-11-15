# Backup service (docs/RUNBOOK.md): pg_dump + age encryption + optional S3-compatible upload.
FROM postgres:17-alpine
RUN apk add --no-cache age aws-cli tzdata
COPY backup.sh restore.sh entrypoint.sh /usr/local/bin/
RUN chmod +x /usr/local/bin/backup.sh /usr/local/bin/restore.sh /usr/local/bin/entrypoint.sh
ENTRYPOINT ["entrypoint.sh"]
