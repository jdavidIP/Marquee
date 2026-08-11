// Iteration 6 — the realistic Premiere load test.
//
// MARQUEE_PLAN.md asks for "thousands of participants clapping within a 60-minute window, with a
// burst at the start", and for the run to show no lost claps and no duplicate opens.
//
// Shape of the traffic. A Premiere is a BeReal-style "everyone shows up at once" event, so the load
// is not a flat rate: it spikes hard the moment the Premiere is announced, then decays as people
// drift off. That is modelled with a ramping-arrival-rate scenario — arrival rate rather than fixed
// VUs, because what is being simulated is people arriving, and holding VUs constant would quietly
// throttle the burst to whatever the API could keep up with, hiding exactly the contention this
// test exists to create.
//
// Participants are anonymous sessions rather than registered accounts. That is both realistic (an
// anonymous visitor can clap, per Iteration 5) and the only way to get thousands of distinct
// participants without registering thousands of accounts first — and distinct participants is the
// point, since the per-participant cap means one identity cannot generate contention on its own.
//
// Run it against a running stack:
//   docker run --rm -i -e API_BASE=http://host.docker.internal:5080/api \
//     grafana/k6 run - < premiere-rush.js
//
// Verify afterwards in Postgres, which is the authoritative record — see the README.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import exec from 'k6/execution';

const API = __ENV.API_BASE || 'http://host.docker.internal:5080/api';
const ADMIN_USER = __ENV.ADMIN_USER || 'admin';
const ADMIN_PASS = __ENV.ADMIN_PASS || 'seed-me-locally-1';

// Scaled down by default so the test runs on a laptop beside Postgres, Redis, RabbitMQ, Jaeger and
// both services. Raise PEAK_RATE for a real run; the shape stays the same.
const PEAK_RATE = Number(__ENV.PEAK_RATE || 400);
const BURST_SECONDS = Number(__ENV.BURST_SECONDS || 20);
const TAIL_SECONDS = Number(__ENV.TAIL_SECONDS || 40);

const clapsAccepted = new Counter('marquee_claps_accepted');
const clapsCapped = new Counter('marquee_claps_capped');
const clapsThrottled = new Counter('marquee_claps_throttled');
const clapsRejected = new Counter('marquee_claps_rejected');
const clapsFailed = new Counter('marquee_claps_failed');
const opensObserved = new Counter('marquee_opens_observed');
const sessionsIssued = new Counter('marquee_sessions_issued');
const clapLatency = new Trend('marquee_clap_latency', true);

// The highest counter value the API ever reported. Its max is the number to compare against
// Postgres afterwards: the Redis counter only moves forward, so if the durable TotalClaps comes out
// below this, claps were counted and then lost.
const reportedTotal = new Trend('marquee_reported_total_claps');

// A closed Premiere answering 409, and a throttled caller answering 429, are correct outcomes -- not
// failures. Left at the default, k6 counts every 4xx as a failed request, and since the great
// majority of a burst arrives after the Premiere has opened, http_req_failed would sit near 100% on
// a run where nothing whatsoever went wrong. Declaring the expected statuses is what keeps that
// metric meaningful for the failures that would matter.
http.setResponseCallback(http.expectedStatuses(200, 201, 409, 429));

export const options = {
  scenarios: {
    // The burst: arrivals climb to the peak almost immediately, then decay over the tail. This is
    // the "everyone shows up at once" hook expressed as a load profile.
    premiere_rush: {
      executor: 'ramping-arrival-rate',
      startRate: Math.max(1, Math.round(PEAK_RATE / 10)),
      timeUnit: '1s',
      preAllocatedVUs: 200,
      maxVUs: 1500,
      stages: [
        { target: PEAK_RATE, duration: '5s' },
        { target: PEAK_RATE, duration: `${BURST_SECONDS}s` },
        { target: Math.round(PEAK_RATE / 8), duration: `${TAIL_SECONDS}s` },
      ],
    },
  },
  thresholds: {
    // The headline acceptance criterion. More than one open means the exactly-once guard failed.
    marquee_opens_observed: ['count<=1'],
    // A clap must never 5xx. Being capped or throttled is a correct answer; failing is not.
    marquee_claps_failed: ['count==0'],
    http_req_failed: ['rate<0.01'],
  },
};

export function setup() {
  const login = http.post(
    `${API}/auth/login`,
    JSON.stringify({ usernameOrEmail: ADMIN_USER, password: ADMIN_PASS }),
    { headers: { 'Content-Type': 'application/json' } },
  );

  if (login.status !== 200) {
    throw new Error(`Admin login failed: ${login.status} ${login.body}`);
  }
  const token = login.json('token');

  // A window long enough that the scheduler's auto-open cannot fire mid-run and be mistaken for a
  // threshold open — the run must be the only thing that opens this Premiere.
  const created = http.post(
    `${API}/premieres`,
    JSON.stringify({ durationMinutes: 60 }),
    { headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` } },
  );

  if (created.status >= 300) {
    throw new Error(`Creating the Premiere failed: ${created.status} ${created.body}`);
  }

  const premiere = created.json();
  console.log(
    `Premiere ${premiere.id}: threshold ${premiere.threshold}, ` +
    `registered cap ${premiere.registeredClapCap ?? '?'}, anonymous cap ${premiere.anonymousClapCap ?? '?'}`,
  );

  return { premiereId: premiere.id, threshold: premiere.threshold, token };
}

// One anonymous session per VU, created on first use and reused after. Re-issuing per iteration
// would measure the session endpoint instead of the clap path, and would also defeat the
// per-participant cap that makes this a contention test rather than a single-identity spam test.
let session = null;

// Highest myClaps this VU's session has been told it has. Per-VU rather than global because each VU
// holds its own anonymous session and therefore its own cap.
let lastMyClaps = 0;

function ensureSession() {
  if (session) return session;

  const response = http.post(`${API}/sessions/anonymous`, null, {
    headers: { 'Content-Type': 'application/json' },
    tags: { name: 'sessions/anonymous' },
  });

  if (response.status !== 200 && response.status !== 201) {
    return null;
  }

  sessionsIssued.add(1);
  session = response.json('token');
  return session;
}

export default function (data) {
  const token = ensureSession();
  if (!token) {
    sleep(0.5);
    return;
  }

  const response = http.post(`${API}/premieres/${data.premiereId}/clap`, null, {
    headers: {
      'X-Anon-Session': token,
      // A fresh key per clap: these are genuinely distinct claps, not retries of one.
      'Idempotency-Key': `${exec.vu.idInTest}-${exec.vu.iterationInInstance}`,
      'X-Correlation-Id': `loadtest-${exec.vu.idInTest}-${exec.vu.iterationInInstance}`,
    },
    tags: { name: 'premieres/clap' },
  });

  clapLatency.add(response.timings.duration);

  if (response.status === 200) {
    const body = response.json();
    if (body && body.opened === true) {
      opensObserved.add(1);
      console.log(`open observed at totalClaps=${body.totalClaps} threshold=${body.threshold}`);
    }
    if (body) {
      reportedTotal.add(body.totalClaps);

      // capReached is true both on the clap that *reaches* the cap and on every one refused
      // afterwards, so a single response cannot distinguish counted from refused. myClaps can:
      // it is monotonic for a session and only advances when a clap was actually counted.
      if (body.myClaps > lastMyClaps) {
        lastMyClaps = body.myClaps;
        clapsAccepted.add(1);
      } else {
        clapsCapped.add(1);
      }
    }
  } else if (response.status === 429) {
    clapsThrottled.add(1);
  } else if (response.status === 409) {
    // The Premiere closed — expected for everything still arriving after the open.
    clapsRejected.add(1);
  } else {
    clapsFailed.add(1);
  }
}

export function teardown(data) {
  const final = http.get(`${API}/premieres/${data.premiereId}`, {
    headers: { Authorization: `Bearer ${data.token}` },
  });

  if (final.status === 200) {
    const premiere = final.json();
    console.log(
      `FINAL premiere=${data.premiereId} status=${premiere.status} ` +
      `totalClaps=${premiere.totalClaps} threshold=${premiere.threshold}`,
    );
    console.log('Confirm against Postgres — the API response is not the authoritative record.');
  }
}
