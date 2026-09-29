// Phase 1 (#78) — how much the deployed box takes, through CloudFront.
//
// Not a correctness test: premiere-rush.js and the Iteration 2 scripts own that. This measures the two
// things DEPLOYMENT.md's capacity section could only estimate for a 2 vCPU / 2 GB host with all five
// containers on it:
//
//   SCENARIO=connections  held-open SignalR WebSockets, ramped up, each joined to the scope group the
//                         way the app joins it. Finds where memory or the connection count gives out.
//   SCENARIO=reads        a ramping arrival rate of the two calls every visitor makes on page load.
//                         Finds the request rate at which latency climbs and the box saturates.
//
// Every request comes from one IP, so production's per-IP rate limits would measure the limiter, not
// the box: run it only inside a deliberate window with RateLimiting__Enabled=false (DEPLOYMENT.md §1d).
//
//   docker run --rm -i -e SITE=https://<SiteUrl> -e SCENARIO=connections grafana/k6 run - < capacity.js
//
// Watch the host meanwhile (`docker stats`, CPUCreditBalance): the numbers k6 prints are only half of
// the answer.

import http from 'k6/http';
import ws from 'k6/ws';
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const SITE = (__ENV.SITE || 'http://host.docker.internal:5080').replace(/\/$/, '');
const SCENARIO = __ENV.SCENARIO || 'connections';
const SCOPE = __ENV.SCOPE_ID || 'global';
const RS = '\u001e'; // SignalR JSON protocol record separator

// connections: ramp to PEAK_CONNECTIONS over RAMP_SECONDS, hold, then let them go.
const PEAK_CONNECTIONS = Number(__ENV.PEAK_CONNECTIONS || 1000);
const RAMP_SECONDS = Number(__ENV.RAMP_SECONDS || 180);
const HOLD_SECONDS = Number(__ENV.HOLD_SECONDS || 120);

// reads: page loads per second (each is two calls), ramped to PEAK_RATE in four STAGE_SECONDS stages.
const PEAK_RATE = Number(__ENV.PEAK_RATE || 200);
const STAGE_SECONDS = Number(__ENV.STAGE_SECONDS || 60);

const connected = new Counter('marquee_ws_connected');
const connectFailed = new Counter('marquee_ws_connect_failed');
const droppedEarly = new Counter('marquee_ws_dropped_early');
const handshakeTime = new Trend('marquee_ws_handshake', true);

const scenarios = {
  connections: {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: `${RAMP_SECONDS}s`, target: PEAK_CONNECTIONS },
      { duration: `${HOLD_SECONDS}s`, target: PEAK_CONNECTIONS },
    ],
    gracefulRampDown: '5s',
    exec: 'holdConnection',
  },
  reads: {
    executor: 'ramping-arrival-rate',
    startRate: 5,
    timeUnit: '1s',
    preAllocatedVUs: 50,
    maxVUs: 1000,
    stages: [
      { duration: `${STAGE_SECONDS}s`, target: Math.round(PEAK_RATE / 4) },
      { duration: `${STAGE_SECONDS}s`, target: Math.round(PEAK_RATE / 2) },
      { duration: `${STAGE_SECONDS}s`, target: PEAK_RATE },
      { duration: `${STAGE_SECONDS}s`, target: PEAK_RATE },
    ],
    exec: 'pageLoad',
  },
};

export const options = {
  scenarios: { [SCENARIO]: scenarios[SCENARIO] },
  // Reported, not enforced: the point is to find the limit, so a breach is the finding, not a failure.
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
};

// One visitor holding the realtime connection open, as the Premiere screen does.
export function holdConnection() {
  const negotiate = http.post(`${SITE}/hubs/premieres/negotiate?negotiateVersion=1`, null, {
    tags: { name: 'negotiate' },
  });
  if (!check(negotiate, { 'negotiate 200': (r) => r.status === 200 })) {
    connectFailed.add(1);
    return;
  }

  const token = negotiate.json('connectionToken');
  const url = `${SITE.replace(/^http/, 'ws')}/hubs/premieres?id=${token}`;
  const started = Date.now();
  // Stay connected until the scenario ends: hold time is the rest of the run.
  const holdMs = (RAMP_SECONDS + HOLD_SECONDS) * 1000;
  let handshaken = false;

  const res = ws.connect(url, { tags: { name: 'hub' } }, (socket) => {
    socket.on('open', () => socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS));
    socket.on('message', (data) => {
      if (handshaken) return;
      handshaken = true;
      handshakeTime.add(Date.now() - started);
      connected.add(1);
      socket.send(JSON.stringify({ type: 1, target: 'JoinScope', arguments: [SCOPE] }) + RS);
    });
    // Client pings keep the server's 30 s client timeout from closing an idle socket.
    socket.setInterval(() => socket.send(JSON.stringify({ type: 6 }) + RS), 10000);
    socket.setTimeout(() => socket.close(), holdMs);
    socket.on('close', () => {
      if (Date.now() - started < holdMs - 5000) droppedEarly.add(1);
    });
  });

  if (!check(res, { 'upgraded (101)': (r) => r && r.status === 101 }) || !handshaken) connectFailed.add(1);
}

// The two calls every visitor makes when the Premiere screen loads.
export function pageLoad() {
  const today = http.get(`${SITE}/api/premieres/today`, { tags: { name: 'today' } });
  const active = http.get(`${SITE}/api/premieres/active`, {
    tags: { name: 'active' },
    // 404 is the normal "nothing is running right now" answer, not a failure.
    responseCallback: http.expectedStatuses(200, 404),
  });
  check(today, { 'today 200': (r) => r.status === 200 });
  check(active, { 'active 200/404': (r) => r.status === 200 || r.status === 404 });
}
