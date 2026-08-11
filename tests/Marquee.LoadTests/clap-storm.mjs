// Marquee — clap storm load driver (zero-dependency, Node 18+).
//
// A "simple parallel-request script" (MARQUEE_PLAN.md, Iteration 2 Part A). It fires many
// concurrent claps at a single Premiere to expose the naive read-modify-write counter, then is
// re-run after the Redis fix to prove the counter is now exact and the open fires exactly once.
//
// It runs two scenarios against two freshly created Premieres:
//   1. LOST UPDATES  — sends (threshold - margin) single claps from distinct users. No open should
//                      happen, so the authoritative DB TotalClaps must equal the number of accepted
//                      claps. The naive counter loses some; the Redis counter loses none.
//   2. DOUBLE OPEN   — sends one clap from every user, overshooting the threshold, so the
//                      threshold-crossing open is contended. Exactly one response must carry
//                      opened=true. The naive path fires it more than once (and/or 500s on the
//                      duplicate library fan-out); the Redis path fires it exactly once.
//
// Usage:  node clap-storm.mjs
// Env:    API_BASE (default http://localhost:5080/api), USERS (default 300),
//         ADMIN_USER (admin), ADMIN_PASS (seed-me-locally-1)
//
// After the run, verify the authoritative numbers in Postgres with the queries the script prints
// (see docs/concurrency-findings.md for the recorded results).

const API = process.env.API_BASE ?? 'http://localhost:5080/api';
const USERS = parseInt(process.env.USERS ?? '300', 10);
const ADMIN_USER = process.env.ADMIN_USER ?? 'admin';
const ADMIN_PASS = process.env.ADMIN_PASS ?? 'seed-me-locally-1';
const PASSWORD = 'clapstorm-load-1';
const RUN = Date.now().toString(36);

async function parse(res) {
  const text = await res.text();
  try { return [res.status, JSON.parse(text)]; } catch { return [res.status, text]; }
}

async function post(path, body, token) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) headers['Authorization'] = `Bearer ${token}`;
  const res = await fetch(`${API}${path}`, { method: 'POST', headers, body: JSON.stringify(body ?? {}) });
  return parse(res);
}

async function login(usernameOrEmail, password) {
  const [status, body] = await post('/auth/login', { usernameOrEmail, password });
  if (status !== 200) throw new Error(`login ${usernameOrEmail} failed: ${status} ${JSON.stringify(body)}`);
  return body.token;
}

async function registerOne(i) {
  const username = `storm_${RUN}_${i}`;
  const [status, body] = await post('/auth/register', {
    username, email: `${username}@marquee.load`, password: PASSWORD, confirmPassword: PASSWORD,
  });
  if (status === 200 || status === 201) return body.token;
  if (status === 409) return login(username, PASSWORD); // idempotent re-run
  throw new Error(`register ${username} failed: ${status} ${JSON.stringify(body)}`);
}

// Fire many concurrent claps from a SINGLE participant — stresses the per-participant cap under
// contention (the atomic Lua cap check must never let one participant exceed their cap).
async function stormOneUser(premiereId, token, count) {
  const settled = await Promise.allSettled(
    Array.from({ length: count }, () => post(`/premieres/${premiereId}/clap`, {}, token)));
  const tally = { ok: 0, capReached: 0, server5xx: 0, errors: 0 };
  let maxMine = 0;
  for (const s of settled) {
    if (s.status === 'rejected') { tally.errors++; continue; }
    const [code, body] = s.value;
    if (code === 200) {
      tally.ok++;
      if (body && typeof body === 'object') {
        if (body.capReached) tally.capReached++;
        if (typeof body.myClaps === 'number') maxMine = Math.max(maxMine, body.myClaps);
      }
    } else if (code >= 500) tally.server5xx++;
  }
  return { tally, maxMine };
}

// Clap sequentially (one at a time) so the counter is accurate — used to walk a Premiere right up
// to threshold-1 before a contended burst across the boundary.
async function rampClaps(premiereId, tokens) {
  let last = null;
  for (const t of tokens) last = await post(`/premieres/${premiereId}/clap`, {}, t);
  return last;
}

// Run tasks with a bounded worker pool so we don't open thousands of sockets at registration time.
async function pooled(items, size, fn) {
  const results = new Array(items.length);
  let next = 0;
  async function worker() {
    while (next < items.length) {
      const i = next++;
      results[i] = await fn(items[i], i);
    }
  }
  await Promise.all(Array.from({ length: Math.min(size, items.length) }, worker));
  return results;
}

async function createPremiere(adminToken) {
  const [status, body] = await post('/premieres', {}, adminToken);
  if (status !== 200 && status !== 201) throw new Error(`create premiere failed: ${status} ${JSON.stringify(body)}`);
  return body;
}

// The whole point: fire all claps at once (true concurrency), await them all, tally the outcomes.
async function stormClaps(premiereId, tokens) {
  const started = Date.now();
  const settled = await Promise.allSettled(tokens.map(t => post(`/premieres/${premiereId}/clap`, {}, t)));
  const elapsedMs = Date.now() - started;

  const tally = { ok: 0, opened: 0, capReached: 0, notActive409: 0, server5xx: 0, other: 0, errors: 0 };
  let maxTotalSeen = 0;
  for (const s of settled) {
    if (s.status === 'rejected') { tally.errors++; continue; }
    const [code, body] = s.value;
    if (code === 200) {
      tally.ok++;
      if (body && typeof body === 'object') {
        if (body.opened) tally.opened++;
        if (body.capReached) tally.capReached++;
        if (typeof body.totalClaps === 'number') maxTotalSeen = Math.max(maxTotalSeen, body.totalClaps);
      }
    } else if (code === 409) tally.notActive409++;
    else if (code >= 500) tally.server5xx++;
    else tally.other++;
  }
  return { tally, maxTotalSeen, elapsedMs };
}

function line() { console.log('-'.repeat(72)); }

async function main() {
  console.log(`Marquee clap storm — API ${API}, users ${USERS}, run id ${RUN}`);
  line();

  const adminToken = await login(ADMIN_USER, ADMIN_PASS);
  process.stdout.write(`Registering ${USERS} users... `);
  const tokens = await pooled([...Array(USERS).keys()], 50, i => registerOne(i));
  console.log('done.');

  // --- Scenario 1: lost updates (stay below threshold so nothing opens) ---
  line();
  const premA = await createPremiere(adminToken);
  const margin = 10;
  const s1Count = Math.max(1, Math.min(tokens.length, premA.threshold - margin));
  console.log(`Scenario 1 — LOST UPDATES`);
  console.log(`  premiere ${premA.id}  threshold ${premA.threshold}  registeredCap ${premA.registeredClapCap}`);
  console.log(`  sending ${s1Count} concurrent single claps (below threshold, no open expected)`);
  const r1 = await stormClaps(premA.id, tokens.slice(0, s1Count));
  console.log(`  accepted(200): ${r1.tally.ok}   opened: ${r1.tally.opened}   409: ${r1.tally.notActive409}   5xx: ${r1.tally.server5xx}   errors: ${r1.tally.errors}   (${r1.elapsedMs} ms)`);
  console.log(`  >> claps that should count: ${r1.tally.ok}   max TotalClaps seen in a response: ${r1.maxTotalSeen}`);

  // --- Scenario 2: contended open (overshoot threshold) ---
  line();
  const premB = await createPremiere(adminToken);
  console.log(`Scenario 2 — DOUBLE OPEN`);
  console.log(`  premiere ${premB.id}  threshold ${premB.threshold}  registeredCap ${premB.registeredClapCap}`);
  console.log(`  sending ${tokens.length} concurrent single claps (overshoots threshold)`);
  const r2 = await stormClaps(premB.id, tokens);
  console.log(`  accepted(200): ${r2.tally.ok}   opened=true responses: ${r2.tally.opened}   409(not active): ${r2.tally.notActive409}   5xx: ${r2.tally.server5xx}   errors: ${r2.tally.errors}   (${r2.elapsedMs} ms)`);
  console.log(`  >> EXPECT exactly one opened=true. Observed: ${r2.tally.opened}`);

  // --- Scenario 3: contended open at the exact boundary (probes exactly-once) ---
  line();
  const premC = await createPremiere(adminToken);
  const burst = 25;
  const rampTo = premC.threshold - 1;
  console.log(`Scenario 3 — CONTENDED OPEN (exactly-once probe)`);
  console.log(`  premiere ${premC.id}  threshold ${premC.threshold}  registeredCap ${premC.registeredClapCap}`);
  if (rampTo > tokens.length - burst) {
    console.log(`  (skipped: need ${rampTo + burst} users, have ${tokens.length})`);
  } else {
    console.log(`  ramping sequentially to threshold-1 (${rampTo} claps), then bursting ${burst} concurrent claps across the boundary`);
    await rampClaps(premC.id, tokens.slice(0, rampTo));
    const r3 = await stormClaps(premC.id, tokens.slice(rampTo, rampTo + burst));
    console.log(`  burst accepted(200): ${r3.tally.ok}   opened=true responses: ${r3.tally.opened}   409(not active): ${r3.tally.notActive409}   5xx: ${r3.tally.server5xx}   errors: ${r3.tally.errors}`);
    console.log(`  >> EXPECT exactly one opened=true and no 5xx. Observed opened=${r3.tally.opened}, 5xx=${r3.tally.server5xx}`);
  }

  // --- Scenario 4: per-participant cap under concurrency ---
  line();
  const premD = await createPremiere(adminToken);
  const cap = premD.registeredClapCap;
  const attempts = cap * 5;
  console.log(`Scenario 4 — CAP ENFORCEMENT`);
  console.log(`  premiere ${premD.id}  registeredCap ${cap}`);
  console.log(`  one user firing ${attempts} concurrent claps (5x the cap)`);
  const r4 = await stormOneUser(premD.id, tokens[0], attempts);
  console.log(`  accepted(200): ${r4.tally.ok}   capReached responses: ${r4.tally.capReached}   5xx: ${r4.tally.server5xx}   errors: ${r4.tally.errors}`);
  console.log(`  >> EXPECT the user's clap count to never exceed ${cap}. Observed max myClaps: ${r4.maxMine}`);

  // --- Authoritative verification queries (run against Postgres) ---
  line();
  console.log('Verify the authoritative counts in Postgres:');
  console.log('  docker exec -e PGPASSWORD=marquee marquee-postgres psql -U marquee -d marquee -c "\\');
  console.log(`    SELECT id, \\"Status\\", \\"TotalClaps\\", \\"Threshold\\" FROM \\"Premieres\\" WHERE id IN ('${premA.id}','${premB.id}');"`);
  console.log('Scenario 1 (no open) success test:  TotalClaps of premiere A must equal accepted(200) =', r1.tally.ok);
  console.log(`  premiere A (lost updates) = ${premA.id}`);
  console.log(`  premiere B (double open)  = ${premB.id}`);
  console.log(`  premiere C (contended)    = ${premC.id}`);
  console.log(`  premiere D (cap)          = ${premD.id}`);
}

main().catch(err => { console.error('FATAL', err); process.exit(1); });
