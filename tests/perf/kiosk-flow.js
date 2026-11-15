// k6 load script for NFR-1 (p95 per kiosk step <= 2 s), NFR-2 (full checkout excluding approval wait <= 60 s)
// and, through the fingerprint step with 2,000 enrolled students, the server side of NFR-4 (1:N match <= 1.5 s).
//
//   python tests/perf/seed.py ...                       (writes tests/perf/perf-data.json)
//   k6 run -e VUS=10 -e ITERATIONS=20 tests/perf/kiosk-flow.js
//   k6 run --insecure-skip-tls-verify -e BASE=https://localhost/api ...   (local Caddy certificate)
//
// Each VU is one counter kiosk with its own item; per iteration it does card -> fingerprint -> asset -> photo ->
// (instructor approves through the API, not timed) -> confirm, then a RETURN session (card, fingerprint, asset).
// Students are spread so no student is in two sessions at once. The numbers are server round trips from the
// machine running k6: run it from a second machine to include the network, and say where it ran in PERFORMANCE.md.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { SharedArray } from 'k6/data';
import { Trend, Counter } from 'k6/metrics';

const data = JSON.parse(open('./perf-data.json'));
const students = new SharedArray('students', () => data.students);
const BASE = __ENV.BASE || data.base;
const VUS = parseInt(__ENV.VUS || '10');
const ITERATIONS = parseInt(__ENV.ITERATIONS || '20');

const steps = ['card', 'fingerprint', 'asset', 'photo', 'confirm', 'return_card', 'return_fingerprint', 'return_asset'];
const trend = Object.fromEntries(steps.map((s) => [s, new Trend(`step_${s}`, true)]));
const checkoutTotal = new Trend('checkout_total_excl_approval', true);
const approvalWait = new Trend('approval_wait', true);
const failures = new Counter('flow_failures');

export const options = {
  scenarios: {
    kiosks: { executor: 'per-vu-iterations', vus: VUS, iterations: ITERATIONS, maxDuration: '30m' },
  },
  thresholds: {
    // NFR-1: every kiosk step, p95 <= 2 s.
    ...Object.fromEntries(steps.map((s) => [`step_${s}`, ['p(95)<2000']])),
    // NFR-2: whole checkout without the approval wait, p95 <= 60 s.
    checkout_total_excl_approval: ['p(95)<60000'],
    flow_failures: ['count==0'],
  },
};

export function setup() {
  const r = http.post(`${BASE}/auth/login`, JSON.stringify({ email: data.instructor.email, password: data.instructor.password }),
    { headers: { 'Content-Type': 'application/json' } });
  if (r.status !== 200) throw new Error(`instructor login failed: ${r.status} ${r.body}`);
  return { token: r.json('accessToken') };
}

const JPEG = new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0, 16, 0x4a, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xff, 0xd9]).buffer;

function post(step, url, body, headers) {
  const r = http.post(`${BASE}${url}`, body, { headers: headers, tags: { step } });
  trend[step].add(r.timings.duration);
  return r;
}

export default function (setupData) {
  const vu = __VU - 1;
  const code = data.kiosks[vu % data.kiosks.length];
  const asset = data.assets[vu % data.assets.length];
  const student = students[(vu * ITERATIONS + __ITER) % students.length];
  const station = { 'X-Station-Code': code, 'Content-Type': 'application/json' };
  const json = (o) => JSON.stringify(o);
  const fail = (what) => { failures.add(1); console.error(`VU ${__VU} iter ${__ITER}: ${what}`); };

  // ---- checkout
  let total = 0;
  let r = post('card', `/kiosk/${code}/sessions`, json({ cardUid: data.instructor.cardUid }), station);
  total += r.timings.duration;
  if (r.status !== 200) return fail(`card ${r.status} ${r.body}`);
  const id = r.json('sessionId');

  r = post('fingerprint', `/kiosk/sessions/${id}/fingerprint`, json({ template: student.template }), station);
  total += r.timings.duration;
  if (r.json('status') === 'FAILED') return fail(`fingerprint denied ${r.json('failureCode')}`);

  r = post('asset', `/kiosk/sessions/${id}/asset`, json({ tagUid: asset.tagUid }), station);
  total += r.timings.duration;
  if (r.json('status') === 'FAILED') return fail(`asset denied ${r.json('failureCode')}`);

  r = post('photo', `/kiosk/sessions/${id}/photo`, { file: http.file(JPEG, 'photo.jpg', 'image/jpeg') }, { 'X-Station-Code': code });
  total += r.timings.duration;
  if (r.status !== 200) return fail(`photo ${r.status} ${r.body}`);

  // Approval (not part of NFR-2): the instructor approves, the kiosk polls until it can confirm.
  const t0 = Date.now();
  const ap = http.post(`${BASE}/approvals/${id}/approve`, json({ destinationRoomId: data.destinationRoomId, classLengthMinutes: 90 }),
    { headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${setupData.token}` }, tags: { step: 'approve' } });
  if (ap.status !== 200) return fail(`approve ${ap.status} ${ap.body}`);
  let next = '';
  for (let i = 0; i < 30 && next !== 'CONFIRM'; i++) {
    const p = http.get(`${BASE}/kiosk/sessions/${id}`, { headers: station, tags: { step: 'poll' } });
    next = p.json('nextStep');
    if (next !== 'CONFIRM') sleep(0.5);
  }
  approvalWait.add(Date.now() - t0);
  if (next !== 'CONFIRM') return fail('never reached CONFIRM');

  r = post('confirm', `/kiosk/sessions/${id}/confirm`, null, station);
  total += r.timings.duration;
  if (!check(r, { 'confirmed': (x) => x.json('status') === 'COMPLETED' })) return fail(`confirm ${r.body}`);
  checkoutTotal.add(total);

  // ---- return, so the item and student are free for the next iteration
  r = post('return_card', `/kiosk/${code}/sessions`, json({ cardUid: data.instructor.cardUid, type: 'RETURN' }), station);
  if (r.status !== 200) return fail(`return card ${r.status} ${r.body}`);
  const rid = r.json('sessionId');
  r = post('return_fingerprint', `/kiosk/sessions/${rid}/fingerprint`, json({ template: student.template }), station);
  if (r.json('status') === 'FAILED') return fail(`return fingerprint denied ${r.json('failureCode')}`);
  r = post('return_asset', `/kiosk/sessions/${rid}/asset`, json({ tagUid: asset.tagUid }), station);
  if (r.json('status') !== 'COMPLETED') return fail(`return not completed ${r.body}`);
}
