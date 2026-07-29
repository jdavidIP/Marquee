# Security, abuse prevention, and the social layer — Iteration 5

What stands between a working demo and something that could face the open internet, and what the
social features look like when privacy is a first-class constraint rather than a filter applied on
the way out.

Companion to [`concurrency-findings.md`](./concurrency-findings.md) (Iteration 2, counting) and
[`queue-and-outbox.md`](./queue-and-outbox.md) (Iteration 4, fan-out).

---

## 1. Four guards, and why one is not enough

The clap endpoint is the most contended and most abusable surface in the product. Four independent
mechanisms sit in front of it, and each answers a question the others cannot.

| Guard | Question it answers | What it does not cover |
|---|---|---|
| **Per-participant cap** (§4.2, Iteration 2) | "Has this participant already contributed as much as they are allowed to *in total*?" | Says nothing about *rate*. A script can spend the whole cap in 50ms. |
| **Debounce** | "Did this clap arrive too soon after that participant's last one?" | Cannot tell a retry of one clap from a genuine second clap. |
| **Idempotency key** | "Is this the same logical clap I already counted?" | Not a limit — a caller with fresh keys each time is unconstrained by it. |
| **Rate limiter** | "Has this caller made too many *requests* in this window?" | Operates on requests, not on claps, and knows nothing about the domain. |

Removing any one leaves a real hole. Without the debounce, the cap is spendable instantly. Without
idempotency, a flaky network turns one clap into several. Without the rate limiter, a caller who is
capped out can still hammer the endpoint for free and burn server capacity on rejections.

### Guard order is load-bearing

```
rate limiter (middleware)
  └─ blocked-user check
       └─ idempotency claim        ← before the debounce, deliberately
            └─ debounce
                 └─ atomic Redis count (cap enforced inside the Lua script)
```

Idempotency is checked **before** the debounce. A retry carrying the same `Idempotency-Key` is one
clap the client is unsure landed — not a second clap arriving too fast. Debouncing it would answer a
dropped response with `429 Too Many Requests` for work that had in fact already been counted, which
is precisely the wrong thing to tell a client that is trying to recover. Checking idempotency first
means a retry replays the original response and never reaches the rate guard at all.

The reverse order would be a subtle, intermittent bug: correct under a clean network, wrong exactly
when a retry actually mattered.

### The debounce has no timestamps in it

```csharp
await _db.StringSetAsync(RedisKeys.ClapDebounce(...), "1", minInterval, When.NotExists);
```

The key's TTL *is* the minimum interval. Setting it succeeds only if the previous one has expired,
which is exactly "at least `minInterval` has passed". No stored timestamps, no clock comparison, no
read-modify-write, and nothing to go wrong when two requests race — `SET NX` decides.

### Idempotency has three states, not two

A naive implementation checks "have I seen this key?" and stores the result afterwards. Two
concurrent requests with the same key both miss the check and both count.

The reservation is therefore atomic, and the lookup has three outcomes:

- **Reserved** — this caller won `SET NX`; it owns the work.
- **Replay** — the key holds a stored response; return it verbatim.
- **InFlight** — the key holds the in-flight marker; the original is still running. The caller is
  told to retry rather than being silently counted a second time.

A reservation whose work then fails — throttled, Premiere closed, an exception — is released, so the
caller's retry is allowed to do the work instead of being told "in flight" until the TTL runs out.

## 2. Rate limiting partitions per caller

The acceptance criterion is *"a script hammering the clap endpoint is throttled **without degrading
service for normal users**"*. That second clause rules out a global counter: one abuser would deny
service to everyone, which is a denial-of-service amplifier wearing a safety vest.

Every rule partitions on the caller — signed-in user, else valid anonymous session, else IP:

```csharp
var participant = resolver?.Resolve(context);
var key = participant?.KeyPart ?? IpKey(context);   // "u:{guid}" | "a:{sessionId}" | "ip:{addr}"
```

The `u:` / `a:` prefixes are why `Participant.KeyPart` exists rather than callers formatting ids
themselves: a session id that happened to look like a user id must never land in the same bucket.

Windows are **sliding**, not fixed. A fixed window lets a caller spend a full budget at the end of
one window and again at the start of the next — a burst of twice the intended limit at the boundary.

Two endpoints partition by IP instead, because the caller has no identity yet and these are the
endpoints an attacker would use to *acquire* one: anonymous-session issuance, and login/register.

## 3. Anonymous participation

A visitor can clap without an account. They contribute to the threshold, are held to their own
smaller cap (§4.2), and are persisted as a `Contribution` row — but they earn nothing: no emblem, no
library entry (§4.3), and no row in `users`, ever.

### The session is a signed token, not a database row

```
{sessionId}.{expiryUnix}.{HMAC-SHA256(key, "{sessionId}.{expiryUnix}")}
```

Nothing is stored server-side. That matters because the entire point of anonymous participation is
that it costs almost nothing — a table of anonymous sessions would mean a write on every first page
load, which is the kind of work this design exists to avoid.

What the signature buys is that a session id cannot be **invented**. A visitor cannot mint a thousand
ids in a loop and spread their claps across them to defeat the per-participant cap; each one has to
be requested from the issuing endpoint, which is IP-rate-limited.

**The honest limit of this:** it raises the cost of Sybil behaviour, it does not remove it. A
determined bot can still collect tokens one at a time, within the IP limit. Making that genuinely
hard — proof of work, device attestation, a CAPTCHA — is out of scope for v1, and pretending
otherwise would be worse than naming it.

The signing key is **derived** from `Jwt:Key` rather than being it:

```csharp
HMACSHA256.HashData(jwtKeyBytes, "marquee-anonymous-session-v1")
```

Domain separation. Signing two different kinds of credential with one secret means a weakness in
either can forge the other. Deriving a subkey costs nothing and keeps them independent, while still
requiring no new secret to configure.

Signature comparison is fixed-time (`CryptographicOperations.FixedTimeEquals`) — a byte-by-byte
early exit would leak how much of a forged signature was correct, which is enough to reconstruct one
guess at a time.

### Anonymous claps reach the durable record

The Redis keys are `premiere:{scope}:{id}:anon:{sessionId}` for the counter and a separate
`:anon-contributors` set. Separate from the registered set on purpose — that one is the SINTER
operand for friend intersection, and an anonymous session can never be anyone's friend.

At open time both sets are snapshotted into `PremiereOpened`, and `TotalClaps` is the sum of both.
Leaving anonymous claps out would make the durable record disagree with the number the room watched
cross the line.

## 4. Authorisation: permissions, not a role check

Iteration 1 gated the admin surface on `RequireRole(Admin)`. Iteration 5 replaced that with a
permission claim per capability:

```csharp
options.AddPolicy(CanBlockUsers, p => p.RequireClaim(
    MarqueePermissions.ClaimType, MarqueePermissions.BlockUsers));
```

Permissions are stamped into the JWT at login from a single `RolePermissions` map. The call sites
never changed — they already referenced `AuthPolicies.CanManagePremieres` — which is exactly the
property the split was for. Adding a Moderator who can block users but not touch Premieres is now one
line in that map; with role checks scattered across controllers it would be an edit at every call
site, and the ones that were missed would fail *open*.

### Blocking has to bite immediately, so it is not a claim

A JWT is a bearer token with no server-side session behind it. Refusing a blocked user at login is
not enough: the token they already hold keeps working until it expires — up to `Jwt:ExpiryHours`.

So blocking is checked on **every** authenticated request, which means it has to be cheap: a Redis
`GET`, falling back to Postgres only on a miss, with both the positive and the negative answer cached
for a short TTL. Caching the negative matters as much as the positive, or every request from every
normal user becomes a database round trip.

`Redis:BlockStatusTtlSeconds` (30s by default) is the visible lag between an admin blocking someone
and every instance refusing them, which is why the admin endpoint *invalidates* the key rather than
waiting for it to expire.

This is the deliberate asymmetry in the design: **capabilities** change rarely and ride in the token;
**blocking** must take effect now and is checked per request.

## 5. Privacy is a different payload, not a nulled-out one

MARQUEE_PLAN.md is explicit: a stranger viewing a private profile gets username and bio, and the
other fields are *"omitted from the payload entirely, not returned as nulls"*.

That is implemented as two genuinely different types — `FullProfileDto` and `LimitedProfileDto` —
selected at the service boundary, rather than one type with nulls. A null field still tells the
reader that the field exists and leaks the shape of the record; an absent one says nothing.

The entitlement rule:

```
self  OR  admin  OR  accepted friend  OR  the profile is public   →  full
otherwise                                                          →  username + bio only
```

Two consequences worth stating plainly:

- **An accepted friend sees everything, private or not.** Privacy governs what *strangers* can see.
- **Private profiles stay discoverable in search.** Hiding them would make a private account
  unfindable rather than merely private — and would leak, by omission, exactly which accounts are
  private.

## 6. Friend intersection: per viewer, never broadcast

"Which of my friends contributed" is a different answer for every connected client. Broadcasting it
would mean either a personalised message per client, or — far worse — sending everyone the full
contributor list and letting the browser filter it, which hands every viewer the identity of every
participant.

So the hub carries the public aggregate (count, threshold, contributor count) and each client asks
about itself:

```
GET /api/premieres/{id}/friends   →  SINTER user:{viewerId}:friends  premiere:{scope}:{id}:contributors
```

One server-side `SINTER`. Fetching both sets and intersecting in the API would move the contributors
set over the wire — the very thing that grows with a Premiere's popularity.

### The cold-cache trap

A Redis SET with no members is indistinguishable from a missing key. So "this user has no friends"
and "this cache is cold after a restart" look identical, and answering the second as the first
returns a silently wrong intersection — no error, no log line, just an empty list.

Hence a companion marker key, `user:{id}:friends:loaded`, and a rebuild from Postgres when it is
absent. For the same reason, `LinkAsync` skips a set that was never loaded: adding one member to a
cold set would produce a *partial* set that then looks complete, which is worse than a cold one.

### After the reveal

Once a Premiere opens, the opener deletes its hot keys, so the SINTER has nothing to intersect. The
same question is then answered from Postgres. Falling back matters: after the reveal is exactly when
someone wants to see who they shared it with, and a Redis-only implementation would answer "nobody"
for every Premiere in history.

## 7. What was verified

`tests/Marquee.LoadTests/security-check.mjs`, run against the full stack. It asserts against the
API's observable behaviour and, where the durable record is the point, against Postgres.

```
A. THROTTLING — an abusive script is limited; a normal user beside it is not
  PASS  the hammering script is throttled — 149/150 rejected with 429, 1 accepted
  PASS  and lands only a handful of claps despite 150 attempts — 1 clap(s) counted
  PASS  throttling never degenerates into a server error — 0 5xx
  PASS  the normal user beside them is not degraded — 4/4 accepted, 0 throttled
  PASS  a 429 explains itself

B. PRIVACY — a private profile shows a stranger only username and bio
  PASS  a private profile still resolves for a stranger — HTTP 200
  PASS  the stranger sees exactly username and bio — keys: [bio, username]
  PASS  the withheld fields are absent, not null
  PASS  the private user is still discoverable in search — 1 result(s)
  PASS  an unauthenticated viewer is also restricted — keys: [bio, username]
  PASS  an accepted friend sees the full profile despite the privacy flag
  PASS  a public profile is fully visible to anyone — HTTP 200

C. FRIENDS — the intersection is per viewer, per request, and never broadcast
  PASS  the friendship is listed for both sides — 1 friend(s)
  PASS  the viewer sees their friend among the contributors
  PASS  a non-friend contributor does not appear
  PASS  a different viewer of the same Premiere gets a different answer — outsider sees []
  PASS  the hub broadcast carries public aggregates only — fields: [contributors, premiereId, threshold, totalClaps]

D. AUTHORISATION — admin endpoints reject non-admins; a block bites immediately
  PASS  every admin endpoint returns 403 to a normal user   (7 routes)
  PASS  and 401 to an anonymous caller — statuses: 401 x7
  PASS  an admin can still reach it — HTTP 200
  PASS  a blocked user is refused with their existing token — HTTP 403
  PASS  and cannot clap — HTTP 403
  PASS  unblocking restores access — HTTP 200

E. GUARDS — anonymous sessions, idempotency keys, and debouncing
  PASS  an anonymous visitor can clap — HTTP 200
  PASS  a visitor with no session cannot — HTTP 401
  PASS  a forged session token is rejected — HTTP 401
  PASS  the anonymous cap is lower than the registered one — anonymous 11 vs registered 45
  PASS  an anonymous clap is measured against the anonymous cap — myCap 11
  PASS  replaying the same key returns the original response, not a second clap — myClaps 1 -> 1
  PASS  a replay is not throttled as a duplicate tap — HTTP 200
  PASS  a different key is a new clap — myClaps 1 -> 2
  PASS  a second clap inside the minimum interval is rejected — HTTP 429
  PASS  and accepted once the interval has passed — HTTP 200

F. ANONYMOUS FAN-OUT — anonymous claps count, but earn nothing (§4.3)
  PASS  the Premiere opened — status Opened, 45/45
  PASS  anonymous contributions were persisted by the worker — 3 row(s)
  PASS  and earned no emblem — 0 anonymous row(s) with a tier
  PASS  and was never linked to a user — 0 linked row(s)
  PASS  registered contributors all received an emblem — 21 registered row(s), 0 without a tier
  PASS  library entries were written for registered contributors only — 21 vs 21
  PASS  TotalClaps counts anonymous claps as well as registered ones — 45 vs 45
  PASS  and the anonymous share is non-zero, so the check has teeth — 4 anonymous claps in 45
```

The throttling check runs the attacker and the bystander **concurrently** on purpose. Running them
in sequence would pass even with a global limiter, because the bystander would arrive after the
attacker's burst had drained. Overlapping them is what makes the second assertion mean anything.

Iteration 4's `queue-check.mjs` was re-run as a regression and passes. That is a more useful result
than it looks: the script hand-builds `PremiereOpened` envelopes that carry **no**
`anonymousContributors` field, so a green run is a direct test of the nullable-on-the-wire tolerance
described in §3.

## 8. Known limitations

- **Anonymous sessions are Sybil-resistant only up to the IP limit.** Named above, repeated here
  because it is the weakest link in the anti-abuse story. A distributed bot with many source
  addresses can still acquire many sessions.
- **`X-Forwarded-For` is deliberately ignored.** v1 does not run behind a trusted proxy, and
  honouring a client-supplied forwarding header without one would let any caller choose their own
  rate-limit bucket. Deploying behind a load balancer means adding `UseForwardedHeaders` with an
  explicit list of trusted proxies — not doing it by default.
- **Rate-limiter state is per process.** ASP.NET Core's limiter is in-memory, so running two API
  instances doubles every effective limit. Multi-instance deployment is out of scope for v1
  (CLAUDE.md §6); a distributed limiter would move these counters into Redis alongside the claps.
- **Permission changes take effect on next login.** They ride in the JWT. Blocking is the case where
  that lag is unacceptable, and it is handled separately (§4).
- **`Friendship` uniqueness is directional.** The index is on `(RequesterId, AddresseeId)` per
  CLAUDE.md §3, so it does not by itself prevent A and B from opening requests to each other.
  `FriendshipService` resolves that by treating an incoming request from someone you already asked as
  a mutual accept, rather than by adding a second constraint.
- **Search is a prefix match with no ranking.** `ILIKE 'term%'` on an unindexed expression. Fine at
  this scale; a trigram index is the answer if it ever isn't.
