"""Seeds a TAMS stack for the NFR measurements and writes perf-data.json (read by kiosk-flow.js and nfr3_nfr5.py).

Creates: one instructor with a card, one destination room (with a room station), one storage room, N counter
kiosks and N borrowable assets (one pair per load-test VU), and STUDENTS students (default 2000) each with
one enrolled fingerprint (random 512-byte template, so the 1:N match step searches the real number of templates).

  python tests/perf/seed.py --base http://localhost:8080/api --admin admin@x.test --password '...' [--students 2000] [--vus 10]

The admin account must already exist (Bootstrap__AdminEmail / Bootstrap__AdminPassword in Development, or an
admin you created). Run against a throwaway database: it adds thousands of rows. Safe to run twice (uses a
fresh tag each time).
"""
import argparse
import base64
import json
import os
import secrets
from concurrent.futures import ThreadPoolExecutor

from tams_client import Api

ap = argparse.ArgumentParser()
ap.add_argument("--base", default="http://localhost:8080/api")
ap.add_argument("--admin", required=True)
ap.add_argument("--password", required=True)
ap.add_argument("--students", type=int, default=2000)
ap.add_argument("--vus", type=int, default=10)
ap.add_argument("--insecure", action="store_true", help="accept the local Caddy certificate")
ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "perf-data.json"))
a = ap.parse_args()

api = Api(a.base, a.insecure)
api.login(a.admin, a.password)
tag = secrets.token_hex(3).upper()
ipw = "Perf#Pass123"

room = api.call("POST", "/rooms", {"code": f"PD-{tag}", "name": "Perf destination", "building": "Main", "floor": 1, "isStorage": False})
store = api.call("POST", "/rooms", {"code": f"PS-{tag}", "name": "Perf storage", "building": "Main", "floor": 1, "isStorage": True})
room_station = api.call("POST", "/stations", {"stationCode": f"PR-{tag}", "type": "ROOM_STATION", "roomId": room["id"], "name": "Perf room reader"})
kiosks = [api.call("POST", "/stations", {"stationCode": f"PK-{tag}-{i}", "type": "COUNTER_KIOSK", "roomId": store["id"], "name": f"Perf kiosk {i}"})
          for i in range(a.vus)]
inst = api.call("POST", "/users", {"email": f"perf-inst-{tag}@tests.local".lower(), "password": ipw, "employeeNo": f"PI-{tag}",
                                   "firstName": "Perf", "lastName": "Instructor", "role": "INSTRUCTOR"})
card_uid = secrets.token_hex(4).upper()
api.call("POST", "/auth-cards", {"cardUid": card_uid, "userId": inst["id"]})
cat = api.call("POST", "/asset-categories", {"name": f"Perf-{tag}", "description": "perf", "isBorrowable": True})
assets = []
for i in range(a.vus):
    tag_uid = secrets.token_hex(10).upper()
    assets.append({"tagUid": tag_uid, "id": api.call("POST", "/assets", {
        "assetCode": f"PA-{tag}-{i}", "name": f"Perf laptop {i}", "categoryId": cat["id"], "type": "BORROWABLE", "brand": "x", "model": "y",
        "serialNo": f"SN-{tag}-{i}", "rfidTagUid": tag_uid, "homeRoomId": store["id"], "condition": "GOOD", "replacementCost": 1000})["id"]})

section = api.call("POST", "/sections", {"code": f"PERF-{tag}", "program": "BSCS", "yearLevel": 3, "schoolYear": "2026-2027"})
nos = [f"P{tag}{i:05d}" for i in range(a.students)]
csv = "student_no,first_name,last_name,section_code,program,year_level,school_year\n" + "".join(
    f"{n},First{n},Last{n},PERF-{tag},BSCS,3,2026-2027\n" for n in nos)
res = api.multipart_file("/students:import", "roster.csv", csv.encode(), "text/csv")
print("roster import:", res if isinstance(res, str) else {k: res.get(k) for k in ("created", "updated", "unchanged", "errors") if k in res})

ids = {}
page = 1
while len(ids) < a.students:
    items = api.call("GET", f"/students?q=P{tag}&page={page}&pageSize=200")["items"]
    if not items:
        break
    ids.update({s["studentNo"]: s["id"] for s in items})
    page += 1
if len(ids) != a.students:
    raise SystemExit(f"expected {a.students} students, found {len(ids)}")

students = [{"no": n, "id": ids[n], "template": base64.b64encode(secrets.token_bytes(512)).decode()} for n in nos]


def enroll(args):
    i, s = args
    k = kiosks[i % len(kiosks)]  # one open session per station, so spread over the kiosks via separate Api objects
    c = Api(a.base, a.insecure)
    c.token = api.token
    sess = c.call("POST", "/enrollment/sessions", {"studentId": s["id"], "stationId": k["id"], "fingerIndex": 1})
    h = {"X-Station-Code": k["stationCode"]}
    c.call("POST", f"/kiosk/enrollment/sessions/{sess['id']}/fingerprint", {"template": s["template"], "sensorModel": "AS608", "qualityScore": 90}, headers=h)
    c.call("POST", f"/kiosk/enrollment/sessions/{sess['id']}/confirm", headers=h)


# Each kiosk takes one enrollment at a time, so give each kiosk its own sequential lane.
lanes = [[(i, s) for i, s in enumerate(students) if i % len(kiosks) == k] for k in range(len(kiosks))]
with ThreadPoolExecutor(len(kiosks)) as ex:
    list(ex.map(lambda lane: [enroll(x) for x in lane], lanes))

with open(a.out, "w") as f:
    json.dump({
        "base": a.base, "instructor": {"email": f"perf-inst-{tag}@tests.local".lower(), "password": ipw, "id": inst["id"], "cardUid": card_uid},
        "destinationRoomId": room["id"], "roomStationCode": room_station["stationCode"],
        "kiosks": [k["stationCode"] for k in kiosks], "assets": assets, "students": students,
        "admin": {"email": a.admin},
    }, f)
print(f"seeded {a.students} students with fingerprints, {a.vus} kiosks, {a.vus} assets -> {a.out}")
