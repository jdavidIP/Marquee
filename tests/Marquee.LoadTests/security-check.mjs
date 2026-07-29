// Marquee — iteration 5 acceptance check (zero-dependency, Node 22+).
//
// Verifies the four acceptance criteria from MARQUEE_PLAN.md iteration 5, plus the anti-abuse
// machinery that makes them hold:
//
//   A. THROTTLING     — a script hammering the clap endpoint is throttled, and a normal user
//                       clapping at a human pace at the same time is completely unaffected.
//   B. PRIVACY        — a stranger asking for a private profile gets ONLY username and bio, with
//                       the other fields absent from the payload rather than null; the private user
//                       is still discoverable in search; and an accepted friend sees everything.
//   C. FRIENDS        — "which of my friends contributed" is answered per request and per viewer:
//                       two viewers of the same Premiere get different answers, non-friends never
//                       appear, and nothing personalised is ever broadcast to the group.
//   D. AUTHORISATION  — every admin endpoint returns 403 to a normal user and 401 to an anonymous
//                       one, and a blocked user is refused with a token that is still valid.
//   E. GUARDS         — anonymous sessions can clap under their own cap, an Idempotency-Key replays
//                       instead of double-counting, and the debounce rejects a too-fast second clap.
//   F. ANON FAN-OUT   — (OPEN_PREMIERE=1) drive the Premiere open and confirm anonymous claps are
//                       persisted, counted in TotalClaps, and earn no emblem and no library entry.
//
// Everything is asserted against the API's observable behaviour and, where it matters, against
// Postgres — never against what a response merely claimed.
//
// Usage:  node security-check.mjs
// Env:    API_BASE (http://localhost:5080/api), ADMIN_USER (admin), ADMIN_PASS (admin12345),
//         HUB_URL (http://localhost:5080/hubs/premieres), PG_CONTAINER (marquee-postgres),
//         PREMIERE_ID (unset — reuse an existing Active Premiere instead of creating one, so the
//                      script can run against a database whose TMDB stub pool is spent)

import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { SignalRClient } from './signalr-client.mjs';

const execFileAsync = promisify(execFile);

const API = process.env.API_BASE ?? 'http://localhost:5080/api';
const HUB_URL = process.env.HUB_URL ?? 'http://localhost:5080/hubs/premieres';
const ADMIN_USER = process.env.ADMIN_USER ?? 'admin';
const ADMIN_PASS = process.env.ADMIN_PASS ?? 'admin12345';
const PG_CONTAINER = process.env.PG_CONTAINER ?? 'marquee-postgres';
const PASSWORD = 'seccheck123';
const RUN = Date.now().toString(36);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const line = () => console.log('-'.repeat(74));

let failures = 0;
function check(label, ok, detail) {
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? ` — ${detail}` : ''}`);
  if (!ok) failures++;
}

// ---------------------------------------------------------------- HTTP helpers

function authHeaders({ token, anonToken, idempotencyKey } = {}) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) headers['Authorization'] = `Bearer ${token}`;
  if (anonToken) headers['X-Anon-Session'] = anonToken;
  if (idempotencyKey) headers['Idempotency-Key'] = idempotencyKey;
  return headers;
}

async function request(method, path, { body, ...auth } = {}) {
  const res = await fetch(`${API}${path}`, {
    method,
    headers: authHeaders(auth),
    body: method === 'GET' || method === 'DELETE' ? undefined : JSON.stringify(body ?? {}),
  });
  const text = await res.text();
  let parsed = text;
  try {
    parsed = text ? JSON.parse(text) : null;
  } catch {
    /* leave as text */
  }
  return { status: res.status, body: parsed };
}

const get = (path, auth) => request('GET', path, auth);
const post = (path, auth) => request('POST', path, auth);
const patch = (path, auth) => request('PATCH', path, auth);
const del = (path, auth) => request('DELETE', path, auth);

async function login(usernameOrEmail, password) {
  const { status, body } = await post('/auth/login', { body: { usernameOrEmail, password } });
  if (status !== 200) throw new Error(`login ${usernameOrEmail} failed: ${status} ${JSON.stringify(body)}`);
  return body.token;
}

async function register(label) {
  const username = `s_${RUN}_${label}`;

  // Registration is IP-rate-limited too (credential-stuffing brake), and this script creates a lot
  // of accounts. Back off and retry rather than failing a security check for the wrong reason.
  for (let attempt = 0; ; attempt++) {
    const { status, body } = await post('/auth/register', {
      body: { username, email: `${username}@marquee.test`, password: PASSWORD },
    });

    if (status === 200 || status === 201) return { username, token: body.token, id: body.user.id };
    if (status === 409) {
      const token = await login(username, PASSWORD);
      const me = await get('/auth/me', { token });
      return { username, token, id: me.body.id };
    }
    if (status === 429 && attempt < 6) {
      await sleep(5000);
      continue;
    }
    throw new Error(`register ${username} failed: ${status} ${JSON.stringify(body)}`);
  }
}

async function anonSession() {
  const { status, body } = await post('/sessions/anonymous');
  if (status !== 200) throw new Error(`anonymous session failed: ${status} ${JSON.stringify(body)}`);
  return body;
}

// ------------------------------------------------------------ Postgres helpers

async function sql(query) {
  const { stdout } = await execFileAsync('docker', [
    'exec', '-e', 'PGPASSWORD=marquee', PG_CONTAINER,
    'psql', '-U', 'marquee', '-d', 'marquee', '-t', '-A', '-F', '|', '-c', query,
  ]);
  return stdout.trim();
}

const scalar = async (query) => Number(await sql(query));

/** A body that should be a list. Anything else (an error object, a 500 page) becomes []. */
const asArray = (body) => (Array.isArray(body) ? body : []);

/**
 * These checks need the Premiere to stay Active for their duration, and a Premiere opens the moment
 * its threshold is crossed. The script is deliberately frugal with claps for that reason, but a
 * small dev threshold plus a Premiere that already had traffic can still tip it over — in which
 * case every later check would fail for an unrelated reason. Stopping with a clear message beats
 * reporting a cascade of misleading failures.
 */
async function assertStillActive(premiereId, phase) {
  const { body } = await get(`/premieres/${premiereId}`);
  if (body?.status === 'Active') return true;
  console.log(
    `\n  STOP  the Premiere opened (${body?.status}) before "${phase}" could run — ` +
      `${body?.totalClaps}/${body?.threshold} claps.`,
  );
  console.log('        Re-run against a fresh Premiere, or raise its threshold, to complete these checks.');
  failures++;
  return false;
}

// ------------------------------------------------------------------ setup

async function activePremiere(adminToken) {
  if (process.env.PREMIERE_ID) {
    const { status, body } = await get(`/premieres/${process.env.PREMIERE_ID}`);
    if (status !== 200) throw new Error(`PREMIERE_ID ${process.env.PREMIERE_ID} not found`);
    return body;
  }

  const existing = await get('/premieres/active');
  if (existing.status === 200) return existing.body;

  const created = await post('/premieres', { token: adminToken, body: {} });
  if (created.status !== 201 && created.status !== 200) {
    throw new Error(
      `could not create a Premiere: ${created.status} ${JSON.stringify(created.body)}\n` +
        'The stub TMDB pool is 12 movies; clear the tables or set PREMIERE_ID (see README).',
    );
  }
  return created.body;
}

// =========================================================== A. THROTTLING

async function checkThrottling(premiereId) {
  console.log('\nA. THROTTLING — an abusive script is limited; a normal user beside it is not');

  const attacker = await register('attacker');
  const bystander = await register('bystander');

  // The attacker and a normal user run at the same time, on purpose: the criterion is not just
  // "the abuser is limited" but "the person beside them is not". A global (unpartitioned) limiter
  // would satisfy the first and fail the second.
  const burst = Promise.all(
    Array.from({ length: 150 }, () => post(`/premieres/${premiereId}/clap`, { token: attacker.token })),
  );

  const bystanderResults = [];
  for (let i = 0; i < 4; i++) {
    bystanderResults.push(await post(`/premieres/${premiereId}/clap`, { token: bystander.token }));
    await sleep(400);
  }

  const attackerResults = await burst;
  const attackerThrottled = attackerResults.filter((r) => r.status === 429).length;
  const attackerAccepted = attackerResults.filter((r) => r.status === 200).length;
  const attackerErrors = attackerResults.filter((r) => r.status >= 500).length;

  check(
    'the hammering script is throttled',
    attackerThrottled > 0,
    `${attackerThrottled}/${attackerResults.length} rejected with 429, ${attackerAccepted} accepted`,
  );

  // The guards must also *contain* the abuse, not merely label it: 150 requests fired at once should
  // yield a couple of claps, not 150. That is the debounce doing its job behind the rate limiter.
  check(
    'and lands only a handful of claps despite 150 attempts',
    attackerAccepted <= 5,
    `${attackerAccepted} clap(s) counted`,
  );

  check('throttling never degenerates into a server error', attackerErrors === 0, `${attackerErrors} 5xx`);

  const bystanderThrottled = bystanderResults.filter((r) => r.status === 429).length;
  const bystanderOk = bystanderResults.filter((r) => r.status === 200).length;
  check(
    'the normal user beside them is not degraded',
    bystanderThrottled === 0 && bystanderOk === bystanderResults.length,
    `${bystanderOk}/${bystanderResults.length} accepted, ${bystanderThrottled} throttled`,
  );

  // A throttled caller must be told when to come back, or a well-behaved client has no basis for a
  // backoff and just retries into the wall.
  const sample = attackerResults.find((r) => r.status === 429);
  check('a 429 explains itself', Boolean(sample?.body?.error), JSON.stringify(sample?.body ?? null));

  return { attacker, bystander };
}

// ============================================================= B. PRIVACY

async function checkPrivacy() {
  console.log('\nB. PRIVACY — a private profile shows a stranger only username and bio');

  const priv = await register('private');
  const stranger = await register('stranger');
  const friend = await register('privfriend');

  await patch('/users/me', {
    token: priv.token,
    body: { bio: 'Quietly here for the movies.', isPrivate: true },
  });

  // --- stranger's view ---
  const asStranger = await get(`/users/${priv.username}`, { token: stranger.token });
  check('a private profile still resolves for a stranger', asStranger.status === 200, `HTTP ${asStranger.status}`);

  const keys = Object.keys(asStranger.body ?? {}).sort();
  check(
    'the stranger sees exactly username and bio',
    JSON.stringify(keys) === JSON.stringify(['bio', 'username']),
    `keys: [${keys.join(', ')}]`,
  );

  // The plan is explicit that the other fields are omitted, not nulled. A null still tells the
  // reader the field exists and leaks the shape of the record.
  const raw = JSON.stringify(asStranger.body ?? {});
  check(
    'the withheld fields are absent, not null',
    !raw.includes('null') && !('id' in (asStranger.body ?? {})) && !('friendCount' in (asStranger.body ?? {})),
    raw,
  );

  // --- discoverability ---
  const search = await get(`/users?query=${encodeURIComponent(priv.username)}`, { token: stranger.token });
  const found = asArray(search.body).some((u) => u.username === priv.username);
  check('the private user is still discoverable in search', found, `${search.body?.length ?? 0} result(s)`);

  // --- anonymous viewer ---
  const asAnon = await get(`/users/${priv.username}`);
  const anonKeys = Object.keys(asAnon.body ?? {}).sort();
  check(
    'an unauthenticated viewer is also restricted',
    JSON.stringify(anonKeys) === JSON.stringify(['bio', 'username']),
    `keys: [${anonKeys.join(', ')}]`,
  );

  // --- accepted friend sees the whole thing ---
  await post('/friends/requests', { token: friend.token, body: { username: priv.username } });
  const inbox = await get('/friends/requests', { token: priv.token });
  const pending = asArray(inbox.body).find((r) => r.username === friend.username && !r.outgoing);
  check('the friend request reached the private user', Boolean(pending));
  if (pending) {
    await post(`/friends/requests/${pending.id}/accept`, { token: priv.token });
  }

  const asFriend = await get(`/users/${priv.username}`, { token: friend.token });
  check(
    'an accepted friend sees the full profile despite the privacy flag',
    asFriend.status === 200 && 'friendCount' in (asFriend.body ?? {}) && 'moviesCollected' in (asFriend.body ?? {}),
    `keys: [${Object.keys(asFriend.body ?? {}).sort().join(', ')}]`,
  );

  // --- a public profile is unrestricted ---
  const asSelf = await get(`/users/${stranger.username}`, { token: priv.token });
  check(
    'a public profile is fully visible to anyone',
    asSelf.status === 200 && 'friendCount' in (asSelf.body ?? {}),
    `HTTP ${asSelf.status}`,
  );

  return { priv, stranger, friend };
}

// ============================================================= C. FRIENDS

async function checkFriendIntersection(premiereId, scopeId) {
  console.log('\nC. FRIENDS — the intersection is per viewer, per request, and never broadcast');

  const viewer = await register('viewer');
  const pal = await register('pal');
  const outsider = await register('outsider');

  // viewer <-> pal are friends; outsider is nobody's friend.
  await post('/friends/requests', { token: viewer.token, body: { username: pal.username } });
  const palInbox = await get('/friends/requests', { token: pal.token });
  const req = asArray(palInbox.body).find((r) => r.username === viewer.username && !r.outgoing);
  check('the friend request arrived', Boolean(req));
  if (req) await post(`/friends/requests/${req.id}/accept`, { token: pal.token });

  const friends = await get('/friends', { token: viewer.token });
  check(
    'the friendship is listed for both sides',
    asArray(friends.body).some((f) => f.username === pal.username),
    `${asArray(friends.body).length} friend(s)`,
  );

  // Everyone claps. Spaced, so the debounce does not eat them.
  for (const who of [pal, outsider, viewer]) {
    await post(`/premieres/${premiereId}/clap`, { token: who.token });
    await sleep(350);
  }

  const asViewer = await get(`/premieres/${premiereId}/friends`, { token: viewer.token });
  const names = asArray(asViewer.body?.friends).map((f) => f.username);
  check('the viewer sees their friend among the contributors', names.includes(pal.username), `[${names.join(', ')}]`);
  check('a non-friend contributor does not appear', !names.includes(outsider.username), `[${names.join(', ')}]`);
  check('the viewer does not appear in their own friend list', !names.includes(viewer.username));

  // The same Premiere, a different viewer, a different answer. This is what "per viewer" means and
  // is impossible to satisfy with a single broadcast payload.
  const asOutsider = await get(`/premieres/${premiereId}/friends`, { token: outsider.token });
  const outsiderNames = asArray(asOutsider.body?.friends).map((f) => f.username);
  check(
    'a different viewer of the same Premiere gets a different answer',
    outsiderNames.length === 0,
    `outsider sees [${outsiderNames.join(', ')}]`,
  );

  // And nothing personalised goes over the hub: the broadcast payload must carry public aggregates
  // only. If a friend list ever leaked into it, every viewer would receive every other viewer's.
  const hub = new SignalRClient(HUB_URL);
  let broadcast = null;
  try {
    await hub.start();
    hub.on('clapUpdate', (payload) => {
      broadcast ??= payload;
    });
    await hub.invoke('JoinPremiere', scopeId, premiereId);
    await post(`/premieres/${premiereId}/clap`, { token: pal.token });
    for (let i = 0; i < 40 && !broadcast; i++) await sleep(100);
  } finally {
    hub.stop();
  }

  if (broadcast) {
    const fields = Object.keys(broadcast).sort();
    check(
      'the hub broadcast carries public aggregates only',
      !fields.some((f) => /friend/i.test(f)) && !JSON.stringify(broadcast).includes(pal.username),
      `fields: [${fields.join(', ')}]`,
    );
  } else {
    check('the hub broadcast carries public aggregates only', false, 'no clapUpdate observed');
  }

  return { viewer, pal, outsider };
}

// ======================================================= D. AUTHORISATION

async function checkAuthorisation(adminToken, premiereId) {
  console.log('\nD. AUTHORISATION — admin endpoints reject non-admins; a block bites immediately');

  const normal = await register('normal');

  const adminRoutes = [
    ['GET', '/admin/users'],
    ['GET', '/admin/premieres'],
    ['POST', `/admin/premieres/${premiereId}/activate`],
    ['POST', `/admin/premieres/${premiereId}/movie`],
    ['PATCH', `/admin/premieres/${premiereId}/schedule`],
    ['POST', `/admin/users/${normal.id}/block`],
    ['POST', `/admin/users/${normal.id}/unblock`],
  ];

  const forbidden = [];
  const unauthorised = [];
  for (const [method, path] of adminRoutes) {
    const asUser = await request(method, path, { token: normal.token, body: { scheduledForUtc: new Date().toISOString() } });
    const asAnon = await request(method, path, { body: { scheduledForUtc: new Date().toISOString() } });
    forbidden.push(`${method} ${path} -> ${asUser.status}`);
    unauthorised.push(asAnon.status);
    if (asUser.status !== 403) failures++;
  }

  check(
    'every admin endpoint returns 403 to a normal user',
    forbidden.every((f) => f.endsWith('403')),
    forbidden.join('; '),
  );
  check(
    'and 401 to an anonymous caller',
    unauthorised.every((s) => s === 401),
    `statuses: ${unauthorised.join(', ')}`,
  );

  // The admin's own surface still works — a 403-for-everyone result would pass the check above for
  // the wrong reason.
  const asAdmin = await get('/admin/users?pageSize=1', { token: adminToken });
  check('an admin can still reach it', asAdmin.status === 200, `HTTP ${asAdmin.status}`);

  // A block has to bite on the token the user already holds. Refusing them only at login would
  // leave a blocked account working until its JWT expired.
  const beforeBlock = await get('/auth/me', { token: normal.token });
  await post(`/admin/users/${normal.id}/block`, { token: adminToken, body: { reason: 'security-check' } });
  const afterBlock = await get('/auth/me', { token: normal.token });

  check('the user worked before the block', beforeBlock.status === 200, `HTTP ${beforeBlock.status}`);
  check(
    'a blocked user is refused with their existing token',
    afterBlock.status === 403,
    `HTTP ${afterBlock.status}`,
  );

  const blockedClap = await post(`/premieres/${premiereId}/clap`, { token: normal.token });
  check('and cannot clap', blockedClap.status === 403, `HTTP ${blockedClap.status}`);

  await post(`/admin/users/${normal.id}/unblock`, { token: adminToken });
  const afterUnblock = await get('/auth/me', { token: normal.token });
  check('unblocking restores access', afterUnblock.status === 200, `HTTP ${afterUnblock.status}`);
}

// ============================================================== E. GUARDS

async function checkGuards(premiereId) {
  console.log('\nE. GUARDS — anonymous sessions, idempotency keys, and debouncing');

  // --- anonymous participation ---
  const session = await anonSession();
  check('an anonymous session is issued', Boolean(session.token && session.sessionId));

  const anonClap = await post(`/premieres/${premiereId}/clap`, { anonToken: session.token });
  check('an anonymous visitor can clap', anonClap.status === 200, `HTTP ${anonClap.status}`);

  const noSession = await post(`/premieres/${premiereId}/clap`);
  check('a visitor with no session cannot', noSession.status === 401, `HTTP ${noSession.status}`);

  const forged = await post(`/premieres/${premiereId}/clap`, {
    anonToken: 'forged-session.9999999999.not-a-signature',
  });
  check('a forged session token is rejected', forged.status === 401, `HTTP ${forged.status}`);

  // §4.2 gives anonymous participants their own, smaller cap. The assertion that matters is that
  // the *anonymous* cap is the one being applied — exhausting it would just burn claps against the
  // threshold and prove less.
  const premiere = await get(`/premieres/${premiereId}`);
  const anonCap = premiere.body.anonymousClapCap;
  const registeredCap = premiere.body.registeredClapCap;

  check(
    'the anonymous cap is lower than the registered one',
    anonCap < registeredCap,
    `anonymous ${anonCap} vs registered ${registeredCap}`,
  );
  check(
    'an anonymous clap is measured against the anonymous cap',
    anonClap.body?.myCap === anonCap,
    `response reported myCap ${anonClap.body?.myCap}, premiere says ${anonCap}`,
  );
  check(
    'and stays within it',
    (anonClap.body?.myClaps ?? 0) <= anonCap,
    `${anonClap.body?.myClaps} clap(s) against a cap of ${anonCap}`,
  );

  // --- idempotency ---
  const idemUser = await register('idem');
  const key = `idem-${RUN}`;

  const first = await post(`/premieres/${premiereId}/clap`, { token: idemUser.token, idempotencyKey: key });
  check('the first clap with an Idempotency-Key is counted', first.status === 200, `HTTP ${first.status}`);

  const replay = await post(`/premieres/${premiereId}/clap`, { token: idemUser.token, idempotencyKey: key });
  check(
    'replaying the same key returns the original response, not a second clap',
    replay.status === 200 && replay.body?.myClaps === first.body?.myClaps,
    `myClaps ${first.body?.myClaps} -> ${replay.body?.myClaps}`,
  );

  // A replay must not be treated as "too fast" either: it is one clap the client is unsure landed.
  check('a replay is not throttled as a duplicate tap', replay.status !== 429, `HTTP ${replay.status}`);

  // A *different* key from the same user is a genuinely new clap.
  await sleep(320);
  const second = await post(`/premieres/${premiereId}/clap`, {
    token: idemUser.token,
    idempotencyKey: `${key}-b`,
  });
  check(
    'a different key is a new clap',
    second.status === 200 && second.body?.myClaps > first.body?.myClaps,
    `myClaps ${first.body?.myClaps} -> ${second.body?.myClaps}`,
  );

  // --- debounce ---
  const fastUser = await register('fast');
  await post(`/premieres/${premiereId}/clap`, { token: fastUser.token });
  const tooSoon = await post(`/premieres/${premiereId}/clap`, { token: fastUser.token });
  check('a second clap inside the minimum interval is rejected', tooSoon.status === 429, `HTTP ${tooSoon.status}`);

  await sleep(400);
  const afterWaiting = await post(`/premieres/${premiereId}/clap`, { token: fastUser.token });
  check('and accepted once the interval has passed', afterWaiting.status === 200, `HTTP ${afterWaiting.status}`);

  // --- anonymous contributions are persisted, but earn nothing (§4.3) ---
  const anonRows = await scalar(
    `SELECT COUNT(*) FROM contributions WHERE "PremiereId" = '${premiereId}' AND "AnonymousSessionId" IS NOT NULL`,
  );
  console.log(`  note  anonymous contribution rows so far: ${anonRows} (written at open time, not now)`);
}

// ============================== F. ANONYMOUS FAN-OUT (destructive: opens the Premiere)

/**
 * Drives the Premiere over its threshold and checks what the open-time fan-out did with the
 * anonymous participants — the half of Iteration 5 that only becomes observable once a Premiere
 * opens.
 *
 * §4.3 is precise about the asymmetry: anonymous participants contribute to the threshold and are
 * persisted as Contribution rows, but earn nothing — no emblem, no library entry. And because their
 * claps counted towards the open, TotalClaps has to include them, or the durable record would
 * disagree with the number the room watched cross the line.
 *
 * Opt-in (OPEN_PREMIERE=1) because it consumes a Premiere, and the offline TMDB stub only has
 * twelve movies to give a database.
 */
async function checkAnonymousFanOut(premiereId) {
  console.log('\nF. ANONYMOUS FAN-OUT — anonymous claps count, but earn nothing (§4.3)');

  const anon = await anonSession();
  let anonClaps = 0;
  for (let i = 0; i < 3; i++) {
    const r = await post(`/premieres/${premiereId}/clap`, { anonToken: anon.token });
    if (r.status === 200) anonClaps = r.body.myClaps;
    await sleep(320);
  }
  check('the anonymous visitor clapped before the open', anonClaps > 0, `${anonClaps} clap(s)`);

  // Push it over the line. The debounce is per participant, so distinct users can all clap at once.
  let premiere = (await get(`/premieres/${premiereId}`)).body;
  let guard = 0;
  while (premiere.status === 'Active' && guard++ < 8) {
    const needed = premiere.threshold - premiere.totalClaps;
    if (needed <= 0) break;

    const users = await Promise.all(
      Array.from({ length: Math.min(needed + 2, 20) }, (_, i) => register(`push${guard}_${i}`)),
    );
    await Promise.all(users.map((u) => post(`/premieres/${premiereId}/clap`, { token: u.token })));
    await sleep(500);
    premiere = (await get(`/premieres/${premiereId}`)).body;
  }

  check('the Premiere opened', premiere.status === 'Opened' || premiere.status === 'AutoOpened',
    `status ${premiere.status}, ${premiere.totalClaps}/${premiere.threshold}`);
  if (premiere.status === 'Active') return;

  // Give the queue hop (API -> outbox -> worker -> fan-out) time to land.
  let anonRows = 0;
  for (let i = 0; i < 40; i++) {
    anonRows = await scalar(
      `SELECT COUNT(*) FROM contributions WHERE "PremiereId" = '${premiereId}' AND "AnonymousSessionId" IS NOT NULL`,
    );
    if (anonRows > 0) break;
    await sleep(500);
  }

  check('anonymous contributions were persisted by the worker', anonRows > 0, `${anonRows} row(s)`);

  const anonWithEmblem = await scalar(
    `SELECT COUNT(*) FROM contributions WHERE "PremiereId" = '${premiereId}'
       AND "AnonymousSessionId" IS NOT NULL AND "EmblemTier" IS NOT NULL`,
  );
  check('and earned no emblem', anonWithEmblem === 0, `${anonWithEmblem} anonymous row(s) with a tier`);

  const anonWithUser = await scalar(
    `SELECT COUNT(*) FROM contributions WHERE "PremiereId" = '${premiereId}'
       AND "AnonymousSessionId" IS NOT NULL AND "UserId" IS NOT NULL`,
  );
  check('and was never linked to a user', anonWithUser === 0, `${anonWithUser} linked row(s)`);

  const registeredRows = await scalar(
    `SELECT COUNT(*) FROM contributions WHERE "PremiereId" = '${premiereId}' AND "UserId" IS NOT NULL`,
  );
  const registeredWithoutEmblem = await scalar(
    `SELECT COUNT(*) FROM contributions WHERE "PremiereId" = '${premiereId}'
       AND "UserId" IS NOT NULL AND "EmblemTier" IS NULL`,
  );
  check('registered contributors all received an emblem', registeredWithoutEmblem === 0,
    `${registeredRows} registered row(s), ${registeredWithoutEmblem} without a tier`);

  const libraryRows = await scalar(
    `SELECT COUNT(*) FROM library_entries WHERE "PremiereId" = '${premiereId}'`,
  );
  check('library entries were written for registered contributors only', libraryRows === registeredRows,
    `${libraryRows} library row(s) vs ${registeredRows} registered contributor(s)`);

  // The assertion that anonymous claps are actually part of the durable total.
  const totalClaps = await scalar(`SELECT "TotalClaps" FROM premieres WHERE "Id" = '${premiereId}'`);
  const summed = await scalar(
    `SELECT COALESCE(SUM("ClapCount"), 0) FROM contributions WHERE "PremiereId" = '${premiereId}'`,
  );
  check('TotalClaps counts anonymous claps as well as registered ones', totalClaps === summed,
    `TotalClaps ${totalClaps} vs summed contributions ${summed}`);

  const anonClapSum = await scalar(
    `SELECT COALESCE(SUM("ClapCount"), 0) FROM contributions
       WHERE "PremiereId" = '${premiereId}' AND "AnonymousSessionId" IS NOT NULL`,
  );
  check('and the anonymous share is non-zero, so the check has teeth', anonClapSum > 0,
    `${anonClapSum} anonymous clap(s) inside a total of ${totalClaps}`);
}

// ================================================================== main

async function main() {
  line();
  console.log('Marquee — iteration 5 acceptance check (security, anti-abuse, social)');
  line();

  const adminToken = await login(ADMIN_USER, ADMIN_PASS);
  const premiere = await activePremiere(adminToken);
  console.log(`Premiere ${premiere.id} — status ${premiere.status}, threshold ${premiere.threshold}, ` +
    `registered cap ${premiere.registeredClapCap}, anonymous cap ${premiere.anonymousClapCap}`);

  if (premiere.status !== 'Active') {
    console.log('\nThe target Premiere is not Active, so the clap-path checks cannot run.');
    console.log('Create one (admin POST /api/premieres) or set PREMIERE_ID to an active Premiere.');
    process.exit(1);
  }

  await checkThrottling(premiere.id);
  await checkPrivacy();
  if (await assertStillActive(premiere.id, 'friend intersection')) {
    await checkFriendIntersection(premiere.id, premiere.scopeId);
  }
  await checkAuthorisation(adminToken, premiere.id);
  if (await assertStillActive(premiere.id, 'clap guards')) {
    await checkGuards(premiere.id);
  }

  if (process.env.OPEN_PREMIERE === '1') {
    await checkAnonymousFanOut(premiere.id);
  } else {
    console.log('\nF. ANONYMOUS FAN-OUT — skipped (set OPEN_PREMIERE=1; it consumes the Premiere)');
  }

  const final = await get(`/premieres/${premiere.id}`);
  console.log(`\nPremiere ended the run at ${final.body?.totalClaps}/${final.body?.threshold} claps ` +
    `(status ${final.body?.status}).`);

  line();
  if (failures === 0) {
    console.log('ALL CHECKS PASSED');
  } else {
    console.log(`${failures} CHECK(S) FAILED`);
  }
  line();
  process.exit(failures === 0 ? 0 : 1);
}

main().catch((err) => {
  console.error('\nsecurity-check failed to run:', err);
  process.exit(1);
});
