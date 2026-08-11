// Marquee — clap storm (k6). Canonical load test per CLAUDE.md §2.
//
// Fires a burst of concurrent claps at one freshly created Premiere to expose (before the fix) the
// naive read-modify-write counter, and to prove (after the fix) that the Redis counter is exact and
// the open fires exactly once. For a zero-install alternative that also captures the two-scenario
// breakdown used in docs/concurrency-findings.md, see clap-storm.mjs (Node).
//
// Run:   k6 run clap-storm.js
// Env:   API_BASE (default http://localhost:5080/api), USERS (default 300),
//        ADMIN_USER (admin), ADMIN_PASS (seed-me-locally-1)

import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import { SharedArray } from 'k6/data';

const API = __ENV.API_BASE || 'http://localhost:5080/api';
const USERS = parseInt(__ENV.USERS || '300', 10);
const ADMIN_USER = __ENV.ADMIN_USER || 'admin';
const ADMIN_PASS = __ENV.ADMIN_PASS || 'seed-me-locally-1';
const PASSWORD = 'clapstorm-load-1';
const RUN = `${Date.now()}`;

const openedResponses = new Counter('claps_opened_true');
const acceptedClaps = new Counter('claps_accepted');
const serverErrors = new Counter('claps_5xx');

// All VUs clap at once: USERS iterations spread across USERS VUs = one clap per user, maximally contended.
export const options = {
  scenarios: {
    storm: { executor: 'shared-iterations', vus: USERS, iterations: USERS, maxDuration: '2m' },
  },
};

function jsonPost(path, body, token) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) headers['Authorization'] = `Bearer ${token}`;
  return http.post(`${API}${path}`, JSON.stringify(body || {}), { headers });
}

// setup() runs once: seed users, create the target Premiere, hand tokens + id to the VUs.
export function setup() {
  const adminLogin = jsonPost('/auth/login', { usernameOrEmail: ADMIN_USER, password: ADMIN_PASS });
  const adminToken = adminLogin.json('token');

  const tokens = [];
  for (let i = 0; i < USERS; i++) {
    const username = `storm_${RUN}_${i}`;
    const reg = jsonPost('/auth/register',
      { username, email: `${username}@marquee.load`, password: PASSWORD, confirmPassword: PASSWORD });
    tokens.push(reg.status === 200 || reg.status === 201
      ? reg.json('token')
      : jsonPost('/auth/login', { usernameOrEmail: username, password: PASSWORD }).json('token'));
  }

  const prem = jsonPost('/premieres', {}, adminToken);
  return { premiereId: prem.json('id'), threshold: prem.json('threshold'), tokens };
}

export default function (data) {
  const token = data.tokens[(__VU - 1) % data.tokens.length];
  const res = jsonPost(`/premieres/${data.premiereId}/clap`, {}, token);

  if (res.status === 200) {
    acceptedClaps.add(1);
    if (res.json('opened') === true) openedResponses.add(1);
  } else if (res.status >= 500) {
    serverErrors.add(1);
  }
  check(res, { 'clap not 5xx': (r) => r.status < 500 });
}

// After the run: exactly one claps_opened_true, and Postgres TotalClaps must match claps_accepted
// for a below-threshold run. Verify in Postgres (see clap-storm.mjs footer for the exact query).
export function teardown(data) {
  console.log(`storm premiere ${data.premiereId} (threshold ${data.threshold})`);
}
