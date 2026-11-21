"""NFR-3 (station scan -> SignalR dashboard event <= 3 s) and NFR-5 (sweep lateness <= 15 s). Stdlib only.

  python tests/perf/nfr3_nfr5.py --admin admin@x.test --password '...' [--runs 20] [--insecure]
         [--psql "docker compose exec -T db psql -U tams -d tams -tA -c"]

Needs tests/perf/perf-data.json from seed.py. For each run it checks out one item through the kiosk (as kiosk-flow.js does),
then:
  NFR-3: a staff SignalR client is already connected; the room station scans the tag, and the time from sending the
         scan to receiving the `asset.statusChanged` event for that asset is recorded.
  NFR-5: sets the loan's due_at a few seconds ahead with --psql (needs database access, so run it where Docker is),
         waits for the sweeper, and reports overdue_at - due_at. Skipped when --psql is not given.
Every run ends with a return so the item is free again. Run against the stack under test and quote the host in PERFORMANCE.md.
"""
import argparse
import base64
import json
import os
import shlex
import socket
import ssl
import statistics
import struct
import subprocess
import threading
import time
import urllib.parse
from datetime import datetime

from tams_client import Api

ap = argparse.ArgumentParser()
ap.add_argument("--data", default=os.path.join(os.path.dirname(__file__), "perf-data.json"))
ap.add_argument("--base")
ap.add_argument("--admin", required=True)
ap.add_argument("--password", required=True)
ap.add_argument("--runs", type=int, default=20)
ap.add_argument("--insecure", action="store_true")
ap.add_argument("--student-offset", type=int, default=100, help="NFR-5 penalises its students (overdue hold); use a higher offset on a re-run against the same data")
ap.add_argument("--psql", help="command prefix that runs one SQL statement, e.g. \"docker compose exec -T db psql -U tams -d tams -tA -c\"")
a = ap.parse_args()
data = json.load(open(a.data))
base = (a.base or data["base"]).rstrip("/")


# ---- minimal SignalR (JSON protocol) over a raw WebSocket
class Hub:
    def __init__(self, url, insecure):
        u = urllib.parse.urlparse(url)
        port = u.port or (443 if u.scheme in ("https", "wss") else 80)
        s = socket.create_connection((u.hostname, port), timeout=30)
        if u.scheme in ("https", "wss"):
            ctx = ssl.create_default_context()
            if insecure:
                ctx.check_hostname, ctx.verify_mode = False, ssl.CERT_NONE
            s = ctx.wrap_socket(s, server_hostname=u.hostname)
        key = base64.b64encode(os.urandom(16)).decode()
        path = u.path + ("?" + u.query if u.query else "")
        s.sendall((f"GET {path} HTTP/1.1\r\nHost: {u.netloc}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                   f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
        head = b""
        while b"\r\n\r\n" not in head:
            head += s.recv(1)
        if b" 101 " not in head.split(b"\r\n")[0] + b" ":
            raise RuntimeError("websocket upgrade failed: " + head.decode(errors="replace")[:200])
        self.s, self.buf = s, b""
        self.send_text('{"protocol":"json","version":1}\x1e')
        self.events = []  # (monotonic receive time, target, first argument)
        threading.Thread(target=self._read, daemon=True).start()

    def send_text(self, text):
        p = text.encode()
        mask = os.urandom(4)
        hdr = bytes([0x81]) + (bytes([0x80 | len(p)]) if len(p) < 126 else bytes([0x80 | 126]) + struct.pack(">H", len(p)))
        self.s.sendall(hdr + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(p)))

    def _exact(self, n):
        out = b""
        while len(out) < n:
            c = self.s.recv(n - len(out))
            if not c:
                raise EOFError
            out += c
        return out

    def _read(self):
        try:
            while True:
                b1, b2 = self._exact(2)
                n = b2 & 0x7F
                if n == 126:
                    n = struct.unpack(">H", self._exact(2))[0]
                elif n == 127:
                    n = struct.unpack(">Q", self._exact(8))[0]
                payload = self._exact(n)
                now = time.monotonic()
                if b1 & 0x0F == 1:
                    for part in payload.decode().split("\x1e"):
                        if part:
                            m = json.loads(part)
                            if m.get("type") == 1:
                                self.events.append((now, m["target"], (m.get("arguments") or [None])[0]))
        except (EOFError, OSError):
            pass


def wait_event(hub, since, target, asset_id, timeout=15):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        for t, tgt, arg in list(hub.events):
            if t >= since and tgt == target and isinstance(arg, dict) and arg.get("assetId") == asset_id:
                return t
        time.sleep(0.005)
    return None


def pct(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(round(p / 100 * (len(xs) - 1))))]


def report(name, xs, limit):
    if not xs:
        print(f"{name}: no samples")
        return
    print(f"{name}: n={len(xs)} p50={statistics.median(xs):.3f}s p95={pct(xs, 95):.3f}s max={max(xs):.3f}s target<={limit}s "
          f"-> {'PASS' if pct(xs, 95) <= limit else 'FAIL'} (p95)")


admin = Api(base, a.insecure)
admin.login(a.admin, a.password)
inst = Api(base, a.insecure)
inst.login(data["instructor"]["email"], data["instructor"]["password"])
ws_base = base.replace("https://", "wss://").replace("http://", "ws://")
hub = Hub(f"{ws_base}/hubs/live?access_token={admin.token}", a.insecure)
time.sleep(0.5)

code, asset = data["kiosks"][0], data["assets"][0]
h = {"X-Station-Code": code}
nfr3, nfr5 = [], []
for run in range(a.runs):
    student = data["students"][-a.student_offset - run]  # overdue penalties (mark, hold) stick to the student, so use a fresh one per run
    c = Api(base, a.insecure)
    first = c.call("POST", f"/kiosk/{code}/sessions", {"cardUid": data["instructor"]["cardUid"]}, headers=h)
    if first.get("status") == "FAILED":
        raise SystemExit(f"card step denied: {first.get('failureCode')} {first.get('lcdLines')}")
    sid = first["sessionId"]
    for step, body_ in (("fingerprint", {"template": student["template"]}), ("asset", {"tagUid": asset["tagUid"]})):
        r_ = c.call("POST", f"/kiosk/sessions/{sid}/{step}", body_, headers=h)
        if r_.get("status") == "FAILED":
            raise SystemExit(f"run {run} student {student['no']} {step} step denied: {r_.get('failureCode')} {r_.get('lcdLines')}")
    boundary = "perfb" + str(run)
    body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"p.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n").encode() \
        + bytes([0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xD9]) + f"\r\n--{boundary}--\r\n".encode()
    c.call("POST", f"/kiosk/sessions/{sid}/photo", raw=body, content_type=f"multipart/form-data; boundary={boundary}", headers=h)
    inst.call("POST", f"/approvals/{sid}/approve", {"destinationRoomId": data["destinationRoomId"], "classLengthMinutes": 90})
    for _ in range(40):
        if c.call("GET", f"/kiosk/sessions/{sid}", headers=h).get("nextStep") == "CONFIRM":
            break
        time.sleep(0.25)
    c.call("POST", f"/kiosk/sessions/{sid}/confirm", headers=h)
    loan = admin.call("GET", f"/loans?assetId={asset['id']}&status=ACTIVE")["items"][0]

    # NFR-3
    t0 = time.monotonic()
    admin.call("POST", f"/stations/{data['roomStationCode']}/scans", {"tagUid": asset["tagUid"]})
    t1 = wait_event(hub, t0, "asset.statusChanged", asset["id"])
    if t1 is None:
        print(f"run {run}: no asset.statusChanged event within 15 s")
    else:
        nfr3.append(t1 - t0)

    # NFR-5
    if a.psql:
        subprocess.run(shlex.split(a.psql) + [f"update loans set due_at = now() + interval '2 seconds' where id = '{loan['id']}'"],
                       check=True, capture_output=True)
        end = time.monotonic() + 60
        while time.monotonic() < end:
            l = admin.call("GET", f"/loans/{loan['id']}")["loan"]
            if l.get("overdueAt"):
                due = datetime.fromisoformat(l["dueAt"].replace("Z", "+00:00"))
                over = datetime.fromisoformat(l["overdueAt"].replace("Z", "+00:00"))
                nfr5.append((over - due).total_seconds())
                # Overdue freezes the instructor and holds the student (F6); lift both so the next run can borrow.
                time.sleep(2)  # the freeze and hold are written right after overdue_at
                q = lambda sql: subprocess.run(shlex.split(a.psql) + [sql], check=True, capture_output=True, text=True).stdout.split()
                for fid in q(f"select id from approval_freezes where loan_id = '{loan['id']}' and status = 'ACTIVE'"):
                    admin.call("POST", f"/freezes/{fid}/lift", {"reason": "perf run"})
                for hid in q(f"select id from profile_holds where loan_id = '{loan['id']}' and status = 'ACTIVE'"):
                    admin.call("POST", f"/holds/{hid}/lift", {"reason": "perf run"})
                break
            time.sleep(1)

    # reset: return the item
    r = c.call("POST", f"/kiosk/{code}/sessions", {"cardUid": data["instructor"]["cardUid"], "type": "RETURN"}, headers=h)["sessionId"]
    c.call("POST", f"/kiosk/sessions/{r}/fingerprint", {"template": student["template"]}, headers=h)
    c.call("POST", f"/kiosk/sessions/{r}/asset", {"tagUid": asset["tagUid"]}, headers=h)

report("NFR-3 scan -> asset.statusChanged", nfr3, 3)
report("NFR-5 overdue sweep lateness", nfr5, 15) if a.psql else print("NFR-5: skipped (no --psql)")
