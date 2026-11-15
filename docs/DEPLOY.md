# Deploying TAMS to the VPS

Written for TAMS-129 (gap G4). **Nothing in this file has been run on a real server yet**: the VPS, the domain
and the owner after the defense are still open (TAMS-19, deployment-decision.md sections 5, 10 and 16). The
Compose side (overlay, Caddyfile, config validation) is checked locally; the server steps are the plan to follow
and must be ticked off with real evidence in section 10.

Target: Ubuntu 24.04 LTS, Singapore region, one domain, Compose with `proxy`, `api`, `db`.

## 1. Server

1. Create a non-root sudo user and copy your SSH public key to it.
2. Key-only SSH. In `/etc/ssh/sshd_config.d/99-tams.conf`:
   ```
   PasswordAuthentication no
   PermitRootLogin no
   KbdInteractiveAuthentication no
   ```
   Then `sudo sshd -t && sudo systemctl reload ssh`. Test a second session before closing the first.
3. Firewall: allow only SSH, 80 and 443.
   ```
   sudo ufw default deny incoming && sudo ufw default allow outgoing
   sudo ufw allow 80/tcp && sudo ufw allow 443/tcp
   sudo ufw allow from <known address> to any port 22 proto tcp   # or plain `allow 22/tcp`
   sudo ufw enable
   ```
   Docker publishes ports around UFW, so the real protection is that Compose publishes only 80 and 443.
4. `sudo apt install unattended-upgrades` and enable it (`dpkg-reconfigure -plow unattended-upgrades`).
5. Install Docker Engine and the Compose plugin from Docker's apt repository.

## 2. Encrypted database volume (NFR-11)

Use provider disk encryption if the plan offers it, otherwise put Docker's data root on a LUKS volume:

```
sudo cryptsetup luksFormat /dev/<spare disk>      # passphrase kept off the server
sudo cryptsetup open /dev/<spare disk> tamsdata
sudo mkfs.ext4 /dev/mapper/tamsdata
sudo mount /dev/mapper/tamsdata /var/lib/docker
```

Do this before the first `docker compose up`. Verify and record the evidence in section 10:
`lsblk -f` shows `crypto_LUKS`, and `findmnt /var/lib/docker` shows the mapper device. With provider encryption,
keep the screenshot or plan page that states it. After a reboot the volume must be opened by hand (or by a
key file stored elsewhere), so note who holds the passphrase.

## 3. Domain and HTTPS

Point an A record for the domain at the VPS. Put it in `.env` as `TAMS_DOMAIN`. Caddy requests and renews the
Let's Encrypt certificate by itself once ports 80 and 443 reach it. The `Caddyfile` also sends HSTS and basic
security headers.

## 4. Secrets and configuration

```
git clone <repo> /opt/tams && cd /opt/tams
cp .env.example .env && chmod 600 .env
mkdir -p /opt/tams-secrets && chmod 700 /opt/tams-secrets    # outside the repo; `secrets/` is also gitignored
```

Fill `.env`:

| Variable | Value |
|---|---|
| `POSTGRES_PASSWORD` | long random (`openssl rand -base64 32`) |
| `JWT_KEY` | `openssl rand -base64 48` |
| `TAMS_DOMAIN` | the real domain |
| `CORS_ORIGINS` | the deployed web admin, e.g. `https://tams.example.com` |
| `FCM_CREDENTIALS_FILE` | `/opt/tams-secrets/fcm.json`, the Firebase service-account JSON, mode 600 |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `KIOSK_ENFORCE_IP`, `KIOSK_ALLOWED_IPS` | see section 7 |

Leave `BOOTSTRAP_ADMIN_*` and `SEED_DEMO` empty or false: the overlay forces them off, and the demo data and
bootstrap admin only exist in Development. Create the first admin the way the team agrees (see the README);
change that password at first login. Never use `docker-compose.dev.yml` on the server: its signing key is public
and it publishes the API port.

## 5. Start

```
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
docker compose -f docker-compose.yml -f docker-compose.prod.yml ps
curl https://<domain>/health        # {"status":"ok","database":"up"}
```

Check that PostgreSQL is not exposed: `docker compose ps` shows no `0.0.0.0:5432`, and
`ss -tlnp | grep 5432` prints nothing. From another machine, `nc -vz <domain> 5432` must fail.

Shortcut: `export COMPOSE_FILE=docker-compose.yml:docker-compose.prod.yml` so plain `docker compose` uses both.

## 6. Backups

Follow `docs/RUNBOOK.md` (backup service, restore drill). Backups are not part of the stack until the age public
key and bucket settings are in `.env`.

## 7. Kiosk and station IP allowlist (pocket router)

`Kiosk__EnforceIp` is off by default. The kiosk and room stations authenticate by station code; the IP check is
an extra layer. With the pocket router or a USB LTE modem (deployment-decision.md section 14) the stations leave
through a carrier address that can change. So: leave it `false` while the stations use mobile data, and turn it
on (`KIOSK_ENFORCE_IP=true`, `KIOSK_ALLOWED_IPS=<campus egress>,<router egress>`) only when the egress addresses
are known and stable. Revisit on the day the router is chosen. Behind Caddy the API reads the client address from
`X-Forwarded-For`, so the allowlist sees the real address.

## 8. Operations

- Restart policy: every service has `restart: unless-stopped`; Docker starts on boot (`systemctl enable docker`).
- Log rotation: each container keeps at most 5 files of 10 MB (`x-logging` in `docker-compose.yml`).
- Uptime check for NFR-6 (07:00 to 21:00, school days): `crontab -e` on the VPS or another machine:
  ```
  * 7-20 * * 1-6 /opt/tams/scripts/uptime-check.sh https://<domain>
  ```
  It logs a line to syslog and `/var/log/tams-uptime.log` on each failure. Alerting (mail or a free uptime
  service pointed at `/health`) is the owner's choice; a service that e-mails on failure is the easiest.
- Updating: `git pull && docker compose ... up -d --build`. Migrations run at start.
- Logs: `docker compose logs -f api`.

## 9. Ownership

Who pays for and holds the server and domain after the defense is undecided (deployment-decision.md section 16).
Record the owner, the registrar account and the passphrase holder in section 10 when known.

## 10. Verification log (fill in with real results)

| Item | Done | Date | Who | Evidence |
|---|---|---|---|---|
| VPS provisioned |  |  |  |  |
| SSH key-only, root and password login off |  |  |  |  |
| UFW allows 22 (known addresses), 80, 443 only |  |  |  |  |
| 5432 not reachable from the internet |  |  |  |  |
| Domain resolves, HTTPS certificate issued |  |  |  |  |
| Secrets in `.env` and the FCM file only, none committed |  |  |  |  |
| Database volume encrypted at rest (how verified) |  |  |  |  |
| `/health` uptime check running |  |  |  |  |
| Backups running (RUNBOOK) |  |  |  |  |
| Restore drill passed (RUNBOOK) |  |  |  |  |
