// Marquee — iteration 4 acceptance check (zero-dependency, Node 22+).
//
// Verifies the three acceptance criteria from MARQUEE_PLAN.md iteration 4, plus the outbox
// guarantee that motivates the whole design:
//
//   A. FAN-OUT        — the API returns from an open without writing library rows; the worker does
//                       it off the request path, and the durable record ends up exactly right
//                       (contributions == contributors, library rows == contributors, tiers per §4.3).
//   B. CRASH          — kill the worker mid-processing and restart it: no duplicate library
//                       entries, no lost contributors. The fan-out either happened or it didn't.
//   C. REPLAY         — republish the same PremiereOpened by hand: the outcome is identical to
//                       publishing once. Two flavours are exercised — same MessageId (caught by the
//                       inbox) and a fresh MessageId (caught by the consumer's own idempotency),
//                       because only the second proves the consumer is idempotent on its own.
//   D. DLQ            — a deliberately poisoned message lands in the _error queue instead of
//                       blocking the queue behind it.
//
// B, C and D publish to RabbitMQ directly through the management API, in MassTransit's envelope
// format. That is on purpose: replaying a message by hand is exactly the operational scenario the
// idempotency requirement exists for, and doing it through a back door in the API would prove
// nothing about the real consumer.
//
// Usage:  node queue-check.mjs
// Env:    API_BASE (http://localhost:5080/api), ADMIN_USER (admin), ADMIN_PASS (admin12345),
//         RABBIT_API (http://localhost:15672), RABBIT_USER (marquee), RABBIT_PASS (marquee),
//         PG_CONTAINER (marquee-postgres), RABBIT_CONTAINER (marquee-rabbitmq),
//         SKIP_CRASH (unset — set it to skip check B),
//         PREMIERE_ID (unset — reuse an already-opened Premiere and skip check A, so the script can
//                      be re-run against a database whose TMDB stub pool is spent)

import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

const API = process.env.API_BASE ?? 'http://localhost:5080/api';
const ADMIN_USER = process.env.ADMIN_USER ?? 'admin';
const ADMIN_PASS = process.env.ADMIN_PASS ?? 'admin12345';
const RABBIT_API = process.env.RABBIT_API ?? 'http://localhost:15672';
const RABBIT_USER = process.env.RABBIT_USER ?? 'marquee';
const RABBIT_PASS = process.env.RABBIT_PASS ?? 'marquee';
const PG_CONTAINER = process.env.PG_CONTAINER ?? 'marquee-postgres';
const RABBIT_CONTAINER = process.env.RABBIT_CONTAINER ?? 'marquee-rabbitmq';
const PASSWORD = 'queuecheck123';
const RUN = Date.now().toString(36);

const FANOUT_EXCHANGE = 'marquee-premiere-fanout';
const FANOUT_QUEUE = 'marquee-premiere-fanout';
const ERROR_QUEUE = 'marquee-premiere-fanout_error';
const MESSAGE_URN = 'urn:message:Marquee.Infrastructure.Messaging:PremiereOpened';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const line = () => console.log('-'.repeat(72));

let failures = 0;
function check(label, ok, detail) {
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? ` — ${detail}` : ''}`);
  if (!ok) failures++;
}

// ---------------------------------------------------------------- API helpers

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
  const username = `q_${RUN}_${i}`;
  const [status, body] = await post('/auth/register', {
    username, email: `${username}@marquee.load`, password: PASSWORD,
  });
  if (status === 200 || status === 201) return { username, token: body.token };
  if (status === 409) return { username, token: await login(username, PASSWORD) };
  throw new Error(`register ${username} failed: ${status} ${JSON.stringify(body)}`);
}

// ------------------------------------------------------------ Postgres helpers
// The source of truth. Every assertion below is made against the durable record, never against
// what an HTTP response claimed.

async function sql(query) {
  const { stdout } = await execFileAsync('docker', [
    'exec', '-e', 'PGPASSWORD=marquee', PG_CONTAINER,
    'psql', '-U', 'marquee', '-d', 'marquee', '-t', '-A', '-F', '|', '-c', query,
  ]);
  return stdout.trim().split('\n').filter(Boolean).map((r) => r.split('|'));
}

async function fanOutState(premiereId) {
  const [[contributions, libraryRows, totalClaps, status]] = await sql(`
    SELECT (SELECT count(*) FROM contributions c WHERE c."PremiereId" = p."Id"),
           (SELECT count(*) FROM library_entries l WHERE l."PremiereId" = p."Id"),
           p."TotalClaps", p."Status"
    FROM premieres p WHERE p."Id" = '${premiereId}'`);
  return {
    contributions: Number(contributions),
    libraryRows: Number(libraryRows),
    totalClaps: Number(totalClaps),
    status,
  };
}

// ------------------------------------------------------------- RabbitMQ helpers

function rabbitHeaders() {
  const auth = Buffer.from(`${RABBIT_USER}:${RABBIT_PASS}`).toString('base64');
  return { Authorization: `Basic ${auth}`, 'Content-Type': 'application/json' };
}

/// Queue depth from `rabbitmqctl`, not the management HTTP API. The HTTP API serves *sampled* stats
/// and was observed reporting messages=0 for a queue that demonstrably held a message — which would
/// silently turn "the event was parked while the worker was down" into a false failure. rabbitmqctl
/// reads the queue directly.
async function queueDepth(name) {
  const { stdout } = await execFileAsync('docker', [
    'exec', RABBIT_CONTAINER, 'rabbitmqctl', 'list_queues', '--no-table-headers', 'name', 'messages',
  ]);
  for (const row of stdout.trim().split('\n')) {
    const [queue, messages] = row.trim().split(/\s+/);
    if (queue === name) return Number(messages);
  }
  return null; // queue does not exist yet — MassTransit creates _error queues lazily
}

/// Hand-build the MassTransit envelope. This is the wire format MassTransit's consumers expect:
/// the domain message nested under `message`, with the type URN in `messageType` so the transport
/// can dispatch it. `messageId` is what the inbox deduplicates on.
async function publishPremiereOpened(payload, messageId) {
  const envelope = {
    messageId,
    conversationId: crypto.randomUUID(),
    sourceAddress: `rabbitmq://localhost/queue-check`,
    destinationAddress: `rabbitmq://localhost/${FANOUT_EXCHANGE}`,
    messageType: [MESSAGE_URN],
    message: payload,
    sentTime: new Date().toISOString(),
  };

  const res = await fetch(
    `${RABBIT_API}/api/exchanges/%2F/${encodeURIComponent(FANOUT_EXCHANGE)}/publish`,
    {
      method: 'POST',
      headers: rabbitHeaders(),
      body: JSON.stringify({
        properties: { content_type: 'application/vnd.masstransit+json', message_id: messageId },
        routing_key: '',
        payload: JSON.stringify(envelope),
        payload_encoding: 'string',
      }),
    });
  if (!res.ok) throw new Error(`publish failed: ${res.status} ${await res.text()}`);
  const body = await res.json();
  if (!body.routed) throw new Error('published but not routed to any queue');
}

// ---------------------------------------------------------------- worker control

async function workerPids() {
  try {
    const { stdout } = await execFileAsync('powershell', ['-NoProfile', '-Command',
      "Get-Process Marquee.Worker -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id"]);
    return stdout.trim().split(/\s+/).filter(Boolean);
  } catch { return []; }
}

async function killWorker() {
  const pids = await workerPids();
  for (const pid of pids) {
    try { await execFileAsync('powershell', ['-NoProfile', '-Command', `Stop-Process -Id ${pid} -Force`]); }
    catch { /* already gone */ }
  }
  return pids.length;
}

// ------------------------------------------------------------------- scenarios

async function openAPremiere(labelPrefix) {
  const adminToken = await login(ADMIN_USER, ADMIN_PASS);
  const [status, premiere] = await post('/premieres', {}, adminToken);
  if (status !== 200 && status !== 201)
    throw new Error(`create premiere failed: ${status} ${JSON.stringify(premiere)}`);

  const users = [];
  for (let i = 0; i < premiere.threshold; i++) users.push(await registerOne(`${labelPrefix}_${i}`));

  const responses = await Promise.all(
    users.map((u) => post(`/premieres/${premiere.id}/clap`, {}, u.token)));
  const opened = responses.filter(([s, b]) => s === 200 && b && b.opened).length;

  return { premiere, users, opened };
}

/// Poll until the fan-out shows up (or give up). The queue hop is asynchronous by design, so every
/// assertion about worker output has to wait for it rather than assume it is instant.
async function waitForFanOut(premiereId, expected, timeoutMs = 20_000) {
  const deadline = Date.now() + timeoutMs;
  let state = await fanOutState(premiereId);
  while (Date.now() < deadline && state.contributions < expected) {
    await sleep(400);
    state = await fanOutState(premiereId);
  }
  return state;
}

/// Load an already-opened Premiere as the subject for checks B–D. Used when PREMIERE_ID is set,
/// which lets the script re-run without consuming another movie from the finite TMDB stub pool.
async function existingPremiere(premiereId) {
  const rows = await sql(`
    SELECT "Id", "ScopeId", "Threshold", "RegisteredClapCap", "Status"
    FROM premieres WHERE "Id" = '${premiereId}'`);
  if (rows.length === 0) throw new Error(`PREMIERE_ID ${premiereId} not found`);
  const [id, scopeId, threshold, registeredClapCap, status] = rows[0];
  if (status !== 'Opened' && status !== 'AutoOpened')
    throw new Error(`PREMIERE_ID ${premiereId} is ${status}; checks B–D need an opened Premiere`);
  return {
    id, scopeId, status,
    threshold: Number(threshold),
    registeredClapCap: Number(registeredClapCap),
  };
}

async function checkFanOut() {
  line();
  console.log('A. FAN-OUT — the worker writes the durable record off the request path');

  const { premiere, users, opened } = await openAPremiere('a');
  check('exactly one clap reported opened=true', opened === 1, `got ${opened}`);

  // Snapshot straight after the open. The status is already terminal — the request path is done —
  // while the fan-out may or may not have landed yet, since it is racing this query across the
  // queue. Informational only; check B is where the worker's absence is made deterministic.
  const immediate = await fanOutState(premiere.id);
  console.log(`     immediately after the open: status=${immediate.status}, ` +
    `contributions=${immediate.contributions}, library=${immediate.libraryRows}`);

  const state = await waitForFanOut(premiere.id, users.length);
  check('premiere is Opened', state.status === 'Opened', state.status);
  check('TotalClaps equals claps sent', state.totalClaps === users.length,
    `${state.totalClaps} vs ${users.length}`);
  check('one contribution per contributor', state.contributions === users.length,
    `${state.contributions} vs ${users.length}`);
  check('one library entry per contributor', state.libraryRows === users.length,
    `${state.libraryRows} vs ${users.length}`);

  const tiers = await sql(`
    SELECT "EmblemTier", count(*) FROM contributions
    WHERE "PremiereId" = '${premiere.id}' GROUP BY "EmblemTier" ORDER BY "EmblemTier"`);
  const noNullTiers = tiers.every(([tier]) => tier !== '');
  check('every registered contributor has an emblem tier', noNullTiers,
    tiers.map(([t, c]) => `tier ${t}: ${c}`).join(', '));

  return premiere;
}

async function waitForWorker(timeoutMs = 120_000) {
  console.log('     restart the worker now if nothing is supervising it:');
  console.log('       DOTNET_ENVIRONMENT=Development dotnet run --project src/Marquee.Worker');
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if ((await workerPids()).length > 0) return true;
    await sleep(1000);
  }
  return false;
}

/// Wait for the endpoint to drain. Polling rather than sleeping a fixed interval matters here: the
/// process appearing in the task list is not the same as the bus being connected and consuming, and
/// a worker cold start takes several seconds.
async function waitForQueueDrain(name, timeoutMs = 60_000) {
  const deadline = Date.now() + timeoutMs;
  let depth = await queueDepth(name);
  while (Date.now() < deadline && depth !== 0) {
    await sleep(500);
    depth = await queueDepth(name);
  }
  return depth;
}

/// Rebuild the PremiereOpened event for an already-opened Premiere, straight from the durable
/// record. Lets the crash and replay checks re-run against an existing Premiere instead of burning
/// a fresh one — which matters because the offline TMDB stub caps a database at 12 Premieres ever.
async function eventForPremiere(premiere) {
  const state = await fanOutState(premiere.id);
  const [[movieId]] = await sql(`SELECT "MovieId" FROM premieres WHERE "Id" = '${premiere.id}'`);
  const contributors = await sql(`
    SELECT "UserId", "ClapCount" FROM contributions
    WHERE "PremiereId" = '${premiere.id}' AND "UserId" IS NOT NULL`);

  return {
    premiereId: premiere.id,
    scopeId: premiere.scopeId,
    movieId,
    status: 'Opened',
    threshold: premiere.threshold,
    registeredClapCap: premiere.registeredClapCap,
    totalClaps: state.totalClaps,
    openedAt: new Date().toISOString(),
    contributors: contributors.map(([userId, claps]) => ({ userId, claps: Number(claps) })),
  };
}

async function checkCrashMidProcessing(premiere) {
  line();
  console.log('B. CRASH — kill the worker, restart it, expect no duplicates and no losses');

  if (process.env.SKIP_CRASH) {
    console.log('     skipped (SKIP_CRASH set)');
    return;
  }

  const payload = await eventForPremiere(premiere);
  const before = await fanOutState(premiere.id);
  console.log(`     baseline: contributions=${before.contributions}, library=${before.libraryRows}`);

  // --- B1: worker down while an event is outstanding ------------------------
  // Deterministic by construction: with no consumer running, the event provably sits in the queue.
  // This is the "no lost contributors" half — the message must survive the outage and be processed
  // on return, not be dropped.
  console.log('  B1. worker down while an event is outstanding');
  console.log(`     killed ${await killWorker()} worker process(es)`);

  // Make sure the process is really gone before publishing, otherwise a fast supervisor can restart
  // it and consume the message before the "is it parked?" assertion ever runs.
  for (let i = 0; i < 20 && (await workerPids()).length > 0; i++) await sleep(500);
  check('the worker is confirmed down', (await workerPids()).length === 0);

  await publishPremiereOpened(payload, crypto.randomUUID());
  await sleep(1500);

  const parked = await queueDepth(FANOUT_QUEUE);
  check('the event waits in the queue while the worker is down, not lost',
    parked >= 1, `${FANOUT_QUEUE} depth=${parked}`);

  const stillDown = await fanOutState(premiere.id);
  check('nothing changed while the worker was down',
    stillDown.contributions === before.contributions && stillDown.libraryRows === before.libraryRows,
    `contributions=${stillDown.contributions}, library=${stillDown.libraryRows}`);

  if (!(await waitForWorker())) {
    check('worker restarted', false, 'no Marquee.Worker process appeared in time');
    return;
  }

  const drained = await waitForQueueDrain(FANOUT_QUEUE);
  check('the parked event was consumed on restart', drained === 0, `${FANOUT_QUEUE} depth=${drained}`);

  const recovered = await fanOutState(premiere.id);
  check('after restart, no contributor was lost',
    recovered.contributions === before.contributions, `${recovered.contributions} vs ${before.contributions}`);
  check('after restart, no library entry was duplicated',
    recovered.libraryRows === before.libraryRows, `${recovered.libraryRows} vs ${before.libraryRows}`);

  // --- B2: killed with a delivery genuinely in flight ------------------------
  // Publish, then kill within milliseconds. The consumer only acks after its transaction commits, so
  // RabbitMQ redelivers whatever was in flight when the process died — the literal "killed
  // mid-processing, then restarted" case. The result must still be exactly one set of rows.
  console.log('  B2. killed with a delivery in flight, then redelivered on restart');
  await publishPremiereOpened(await eventForPremiere(premiere), crypto.randomUUID());
  await sleep(30); // just enough for the broker to hand it over
  console.log(`     killed ${await killWorker()} worker process(es) with the delivery in flight`);

  if (!(await waitForWorker())) {
    check('worker restarted after the in-flight kill', false, 'no Marquee.Worker process appeared in time');
    return;
  }

  const redelivered = await waitForQueueDrain(FANOUT_QUEUE);
  check('the redelivered message was consumed on restart', redelivered === 0,
    `${FANOUT_QUEUE} depth=${redelivered}`);

  const afterCrash = await fanOutState(premiere.id);
  check('the redelivered message added nothing',
    afterCrash.contributions === before.contributions && afterCrash.libraryRows === before.libraryRows,
    `contributions ${before.contributions}->${afterCrash.contributions}, ` +
    `library ${before.libraryRows}->${afterCrash.libraryRows}`);

  const [[dupes]] = await sql(`
    SELECT count(*) FROM (
      SELECT "UserId" FROM contributions WHERE "PremiereId" = '${premiere.id}'
      GROUP BY "UserId" HAVING count(*) > 1) d`);
  check('no duplicate contributions', Number(dupes) === 0, `${dupes} duplicated users`);

  const [[libDupes]] = await sql(`
    SELECT count(*) FROM (
      SELECT "UserId", "MovieId" FROM library_entries
      GROUP BY "UserId", "MovieId" HAVING count(*) > 1) d`);
  check('no duplicate library entries', Number(libDupes) === 0, `${libDupes} duplicated (user, movie)`);
}

async function checkReplay(premiere) {
  line();
  console.log('C. REPLAY — publishing the same event again changes nothing');

  const before = await fanOutState(premiere.id);
  const payload = await eventForPremiere(premiere);

  // (i) Same MessageId — the inbox should swallow it without running the consumer at all.
  const repeatedId = crypto.randomUUID();
  await publishPremiereOpened(payload, repeatedId);
  await sleep(2500);
  await publishPremiereOpened(payload, repeatedId);
  await sleep(3000);

  const afterSameId = await fanOutState(premiere.id);
  check('replay with the same MessageId leaves the record unchanged',
    afterSameId.contributions === before.contributions && afterSameId.libraryRows === before.libraryRows,
    `contributions ${before.contributions}->${afterSameId.contributions}, ` +
    `library ${before.libraryRows}->${afterSameId.libraryRows}`);

  // (ii) Fresh MessageId — the inbox cannot help here, so this is the consumer's own idempotency.
  await publishPremiereOpened(payload, crypto.randomUUID());
  await sleep(3500);

  const afterNewId = await fanOutState(premiere.id);
  check('replay with a NEW MessageId leaves the record unchanged',
    afterNewId.contributions === before.contributions && afterNewId.libraryRows === before.libraryRows,
    `contributions ${before.contributions}->${afterNewId.contributions}, ` +
    `library ${before.libraryRows}->${afterNewId.libraryRows}`);

  const [[dupes]] = await sql(`
    SELECT count(*) FROM (
      SELECT "UserId", "MovieId" FROM library_entries
      GROUP BY "UserId", "MovieId" HAVING count(*) > 1) d`);
  check('no user holds the same movie twice', Number(dupes) === 0, `${dupes} duplicated (user, movie)`);
}

async function checkDeadLetter(premiere) {
  line();
  console.log('D. DLQ — a poisoned message is dead-lettered instead of blocking the queue');

  const before = (await queueDepth(ERROR_QUEUE)) ?? 0;

  // Poison: a Premiere id that does not exist. The consumer raises UnknownPremiereException, which
  // is a PermanentMessageException, which the retry policy is configured to Ignore — so it skips
  // the backoff entirely and goes straight to the error queue.
  await publishPremiereOpened({
    premiereId: crypto.randomUUID(),
    scopeId: 'global',
    movieId: crypto.randomUUID(),
    status: 'Opened',
    threshold: 10,
    registeredClapCap: 2,
    totalClaps: 10,
    openedAt: new Date().toISOString(),
    contributors: [{ userId: crypto.randomUUID(), claps: 2 }],
  }, crypto.randomUUID());

  const deadline = Date.now() + 30_000;
  let after = before;
  while (Date.now() < deadline) {
    await sleep(1000);
    after = (await queueDepth(ERROR_QUEUE)) ?? 0;
    if (after > before) break;
  }
  check('poisoned message landed in the DLQ', after > before,
    `${ERROR_QUEUE}: ${before} -> ${after}`);

  // The important half: the poison must not have wedged the endpoint.
  const mainDepth = await queueDepth(FANOUT_QUEUE);
  check('the main queue is not blocked behind it', mainDepth === 0, `${FANOUT_QUEUE} depth=${mainDepth}`);

  // ...and prove it by pushing a valid message through afterwards. A replay of an already-opened
  // Premiere is a no-op for the database, but it still has to be consumed and acked — which is
  // exactly what "the endpoint is not wedged" means.
  const stateBefore = await fanOutState(premiere.id);
  await publishPremiereOpened(await eventForPremiere(premiere), crypto.randomUUID());

  const drainDeadline = Date.now() + 20_000;
  let depth = await queueDepth(FANOUT_QUEUE);
  while (Date.now() < drainDeadline && depth !== 0) {
    await sleep(500);
    depth = await queueDepth(FANOUT_QUEUE);
  }
  check('a valid message is still consumed after the poison', depth === 0,
    `${FANOUT_QUEUE} depth=${depth}`);

  const stateAfter = await fanOutState(premiere.id);
  check('and it left the record intact',
    stateAfter.contributions === stateBefore.contributions &&
    stateAfter.libraryRows === stateBefore.libraryRows,
    `contributions=${stateAfter.contributions}, library=${stateAfter.libraryRows}`);
}

// ------------------------------------------------------------------------ main

async function main() {
  console.log(`Marquee iteration 4 queue check — API ${API}, RabbitMQ ${RABBIT_API}`);

  // Checks B, C and D all replay events against the Premiere that check A opened, rather than each
  // opening its own. Deliberate: the offline TMDB stub caps a database at 12 Premieres ever (§4.6
  // forbids repeats), so a script that burned one per check would exhaust the pool in two runs.
  // PREMIERE_ID reuses an already-opened Premiere and skips check A entirely, so B–D can be re-run
  // against a database whose pool is spent.
  let premiere;
  if (process.env.PREMIERE_ID) {
    premiere = await existingPremiere(process.env.PREMIERE_ID);
    line();
    console.log(`A. FAN-OUT — skipped; reusing Premiere ${premiere.id} (${premiere.status})`);
  } else {
    premiere = await checkFanOut();
  }
  await checkReplay(premiere);
  await checkDeadLetter(premiere);
  await checkCrashMidProcessing(premiere);

  line();
  console.log(failures === 0 ? 'ALL CHECKS PASSED' : `${failures} CHECK(S) FAILED`);
  process.exit(failures === 0 ? 0 : 1);
}

main().catch((err) => { console.error('FATAL', err); process.exit(1); });
