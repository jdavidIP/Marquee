// Marquee — iteration 3 acceptance check (zero-dependency, Node 22+).
//
// Verifies the three acceptance criteria from MARQUEE_PLAN.md iteration 3 that need a running
// system (the fourth, the §4.4 schedule constraints, is covered by unit tests):
//
//   A. TWO WATCHERS   — two independent hub connections watching the same Premiere both see the
//                       count move while a third party claps. Also measures how many messages each
//                       watcher received versus how many claps were sent: the broadcast is
//                       throttled, so messages must be far fewer than claps.
//   B. REVEAL         — crossing the threshold pushes exactly one premiereOpened to every watcher,
//                       carrying the movie.
//   C. AUTO-OPEN      — a Premiere that runs out its timer without reaching the threshold opens
//                       anyway with status AutoOpened and reveals the same way (§4.5).
//
// Usage:  node realtime-check.mjs
// Env:    API_BASE (http://localhost:5080/api), HUB_URL (http://localhost:5080/hubs/premieres),
//         USERS (60), ADMIN_USER (admin), ADMIN_PASS (seed-me-locally-1), SKIP_AUTOOPEN (unset)

import { SignalRClient } from './signalr-client.mjs';

const API = process.env.API_BASE ?? 'http://localhost:5080/api';
const HUB = process.env.HUB_URL ?? 'http://localhost:5080/hubs/premieres';
const USERS = parseInt(process.env.USERS ?? '60', 10);
const ADMIN_USER = process.env.ADMIN_USER ?? 'admin';
const ADMIN_PASS = process.env.ADMIN_PASS ?? 'seed-me-locally-1';
const SCOPE = process.env.SCOPE_ID ?? 'global';
const PASSWORD = 'realtime-load-1';
const RUN = Date.now().toString(36);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const line = () => console.log('-'.repeat(72));

let failures = 0;
function check(label, ok, detail) {
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? ` — ${detail}` : ''}`);
  if (!ok) failures++;
}

async function post(path, body, token) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) headers['Authorization'] = `Bearer ${token}`;
  const res = await fetch(`${API}${path}`, { method: 'POST', headers, body: JSON.stringify(body ?? {}) });
  const text = await res.text();
  try { return [res.status, JSON.parse(text)]; } catch { return [res.status, text]; }
}

async function login(usernameOrEmail, password) {
  const [status, body] = await post('/auth/login', { usernameOrEmail, password });
  if (status !== 200) throw new Error(`login ${usernameOrEmail} failed: ${status} ${JSON.stringify(body)}`);
  return body.token;
}

async function registerOne(i) {
  const username = `rt_${RUN}_${i}`;
  const [status, body] = await post('/auth/register', {
    username, email: `${username}@marquee.load`, password: PASSWORD, confirmPassword: PASSWORD,
  });
  if (status === 200 || status === 201) return body.token;
  if (status === 409) return login(username, PASSWORD);
  throw new Error(`register ${username} failed: ${status} ${JSON.stringify(body)}`);
}

async function pooled(items, size, fn) {
  const results = new Array(items.length);
  let next = 0;
  const worker = async () => {
    while (next < items.length) {
      const i = next++;
      results[i] = await fn(items[i], i);
    }
  };
  await Promise.all(Array.from({ length: Math.min(size, items.length) }, worker));
  return results;
}

async function createPremiere(adminToken, durationMinutes) {
  const [status, body] = await post('/premieres', { durationMinutes }, adminToken);
  if (status !== 200 && status !== 201) throw new Error(`create premiere failed: ${status} ${JSON.stringify(body)}`);
  return body;
}

/** A watcher: connects, joins the Premiere group, records everything it is told. */
async function watch(name, premiereId) {
  const client = new SignalRClient(HUB);
  const updates = [];
  const opened = [];
  client.on('clapUpdate', (u) => updates.push({ at: Date.now(), ...u }));
  client.on('premiereOpened', (n) => opened.push({ at: Date.now(), ...n }));
  await client.start();
  client.invoke('JoinPremiere', SCOPE, premiereId);
  await sleep(200); // let the join land before claps start
  return { name, client, updates, opened };
}

async function main() {
  console.log(`Marquee realtime check — API ${API}, hub ${HUB}, users ${USERS}, run ${RUN}`);
  line();

  const adminToken = await login(ADMIN_USER, ADMIN_PASS);
  process.stdout.write(`Registering ${USERS} users... `);
  const tokens = await pooled([...Array(USERS).keys()], 25, (i) => registerOne(i));
  console.log('done.');

  // ---------------------------------------------------------------- A + B --
  line();
  const premiere = await createPremiere(adminToken);
  console.log(`Scenario A/B — TWO WATCHERS + REVEAL`);
  console.log(`  premiere ${premiere.id}  threshold ${premiere.threshold}  cap ${premiere.registeredClapCap}`);

  const watchers = [await watch('watcher-1', premiere.id), await watch('watcher-2', premiere.id)];

  // Clap up to just below the threshold first, so the count is seen moving before it opens.
  const belowThreshold = Math.max(1, premiere.threshold - 1);
  const clapsToSend = Math.min(belowThreshold, tokens.length * premiere.registeredClapCap);
  console.log(`  sending ${clapsToSend} claps (threshold - 1), then one more to cross it`);

  const startedAt = Date.now();
  let sent = 0;
  let accepted = 0;
  outer: for (let round = 0; round < premiere.registeredClapCap; round++) {
    for (const token of tokens) {
      if (sent >= clapsToSend) break outer;
      sent++;
      const [code, body] = await post(`/premieres/${premiere.id}/clap`, {}, token);
      if (code === 200 && !body.capReached) accepted++;
    }
  }
  const clapMs = Date.now() - startedAt;

  // Let at least a couple of broadcast intervals pass so the last batch is flushed.
  await sleep(1200);

  for (const w of watchers) {
    check(
      `${w.name} saw the count move`,
      w.updates.length > 0,
      `${w.updates.length} update(s), last total ${w.updates.at(-1)?.totalClaps ?? 'n/a'}`,
    );
  }

  const [w1, w2] = watchers;
  check(
    'both watchers agree on the latest count',
    w1.updates.at(-1)?.totalClaps === w2.updates.at(-1)?.totalClaps,
    `${w1.updates.at(-1)?.totalClaps} vs ${w2.updates.at(-1)?.totalClaps}`,
  );
  check(
    'watchers see the live contributor count',
    (w1.updates.at(-1)?.contributors ?? 0) > 0,
    `${w1.updates.at(-1)?.contributors} contributor(s)`,
  );
  check(
    'broadcasts are throttled, not one per clap',
    w1.updates.length < accepted,
    `${accepted} claps in ${clapMs} ms produced ${w1.updates.length} message(s)`,
  );

  // Cross the threshold.
  const before = w1.opened.length;
  let crossed = false;
  for (const token of tokens) {
    const [code, body] = await post(`/premieres/${premiere.id}/clap`, {}, token);
    if (code === 200 && body.opened) { crossed = true; break; }
    if (code === 409) break; // already open
  }
  await sleep(1200);

  check('the threshold crossing opened the Premiere', crossed);
  for (const w of watchers) {
    check(
      `${w.name} received the reveal exactly once`,
      w.opened.length - before === 1,
      `${w.opened.length - before} premiereOpened message(s)`,
    );
    const reveal = w.opened.at(-1);
    check(`${w.name} reveal carried the movie`, !!reveal?.movie?.title, reveal?.movie?.title ?? 'no movie');
    check(`${w.name} reveal status is Opened`, reveal?.status === 'Opened', reveal?.status);
  }

  watchers.forEach((w) => w.client.stop());

  // -------------------------------------------------------------------- C --
  if (process.env.SKIP_AUTOOPEN) {
    line();
    console.log('Scenario C — AUTO-OPEN (skipped by SKIP_AUTOOPEN)');
  } else {
    line();
    console.log('Scenario C — AUTO-OPEN ON THE TIMER (§4.5)');
    const shortLived = await createPremiere(adminToken, 1);
    console.log(`  premiere ${shortLived.id}  threshold ${shortLived.threshold}  expires in 1 minute`);

    const timerWatcher = await watch('timer-watcher', shortLived.id);

    // A handful of claps, deliberately well below the threshold.
    const few = Math.min(3, tokens.length);
    for (const token of tokens.slice(0, few)) {
      await post(`/premieres/${shortLived.id}/clap`, {}, token);
    }
    console.log(`  ${few} clap(s) sent (far below threshold ${shortLived.threshold}); waiting for the timer…`);

    const deadline = Date.now() + 130_000;
    while (timerWatcher.opened.length === 0 && Date.now() < deadline) {
      await sleep(2000);
      process.stdout.write('.');
    }
    console.log('');

    const reveal = timerWatcher.opened.at(-1);
    check('the expired Premiere auto-opened', timerWatcher.opened.length === 1, `${timerWatcher.opened.length} reveal(s)`);
    check('auto-open status is AutoOpened', reveal?.status === 'AutoOpened', reveal?.status ?? 'never opened');
    check('auto-open revealed the movie', !!reveal?.movie?.title, reveal?.movie?.title ?? 'no movie');
    check(
      'auto-open still counted the claps it had',
      reveal?.totalClaps === few,
      `${reveal?.totalClaps} clap(s), ${reveal?.contributors} contributor(s)`,
    );

    timerWatcher.client.stop();
  }

  line();
  console.log(failures === 0 ? 'ALL CHECKS PASSED' : `${failures} CHECK(S) FAILED`);
  process.exit(failures === 0 ? 0 : 1);
}

main().catch((err) => { console.error('FATAL', err); process.exit(1); });
