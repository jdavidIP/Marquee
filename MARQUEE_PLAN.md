# Marquee — build plan

> Standing project context (tech stack, domain model, formulas, conventions, scope boundaries) lives in `CLAUDE.md` and is loaded automatically — this file assumes it. Implement one iteration at a time, in order. Do not start an iteration until the previous one's acceptance criteria pass.

---

### Iteration 1 — Naive vertical slice

**Goal:** the mechanic works end to end, single instance, no scale concerns. This iteration is deliberately naive; iteration 2 breaks it on purpose.

- Scaffold solution per `CLAUDE.md` §2, Docker Compose with postgres only
- EF Core entities and initial migration for `User`, `Premiere`, `Movie`, `Contribution`, `LibraryEntry`
- JWT auth: register, login, `/me`. Password hashing via ASP.NET Core Identity's hasher or Argon2
- TMDB client with the §4.6 filters; movie chosen at Premiere creation
- Admin-seeded Premiere creation endpoint (manual trigger, no scheduler yet)
- `POST /premieres/{id}/clap` — naive: read count from DB, increment, write back
- Threshold, cap, and emblem functions implemented as pure domain functions with unit tests against the §4 worked examples
- On threshold met: synchronously open the Premiere, write `LibraryEntry` rows for all contributors, assign emblems
- Angular: login/register, a single Premiere page with a clap button and a polled count, a library page

**Acceptance criteria**
- A user can register, log in, clap, and see the movie land in their library
- Domain formula unit tests pass, including the small-user-base edge case
- Admin can manually create a Premiere

---

### Iteration 2 — Break it, then fix it

**Goal:** experience the concurrency failures firsthand, then fix them properly. This is the most important iteration in the project.

**Part A — break it.** Write a k6 (or simple parallel-request) script that fires several hundred concurrent claps at one Premiere. Then document what you observe:
- Lost updates — final count is lower than claps sent, because of read-modify-write races
- The threshold-crossing event firing more than once, or not at all

Write the observed numbers down in the repo (`docs/concurrency-findings.md`). Do not skip this — the fix means nothing without seeing the failure.

**Part B — fix it.**
- Add Redis to Docker Compose
- Move clap counting to Redis `INCR` — atomic by design, no read-modify-write
- Enforce per-participant caps with a per-participant `INCR` checked against the cap
- Exactly-once opening: the `INCR` return value is the authoritative post-increment count. The single caller whose return value **equals** the threshold is the one that fires the open. Back this with a distributed lock (`SET NX` with TTL) **and** a DB-level guard (unique constraint or conditional `UPDATE ... WHERE Status = 'Active'`) so a lock expiry can't produce a double-open
- Persist final counts to Postgres at open time; Redis is the hot path, Postgres is the record

**Acceptance criteria**
- Re-run the load script: final count matches claps sent exactly
- The open event fires exactly once, verified under concurrent load
- No participant can exceed their cap, even with concurrent requests
- Findings document committed

---

### Iteration 3 — Real-time and scheduling

**Goal:** it becomes a live, shared event rather than a page you refresh.

- SignalR hub. Clients join a group per Premiere (group name derived from `scopeId` + `premiereId` — build this generically, not hardcoded to one global group)
- Broadcast clap-count updates to the group. **Throttle broadcasts** (e.g. batch every 250ms) rather than emitting on every single clap — under load, per-clap broadcast will melt
- Broadcast the reveal when the Premiere opens
- Scheduler (Quartz or Hangfire): generate the day's 4 Premieres per §4.4, activate them at their scheduled time, and enforce the 60-minute expiry with auto-open per §4.5
- Angular: the curtain + bulb marquee UI. Curtain gap and lit-bulb count must both be **derived from a single `progress` value** (`claps / threshold`), never tracked as separate state
- Live contributor count displayed

**Acceptance criteria**
- Two browsers watching the same Premiere see the count move in near-real-time
- A Premiere that hits its timer auto-opens and reveals correctly
- 4 Premieres generate daily within the constraints of §4.4, with the 2-hour minimum gap respected

---

### Iteration 4 — Queue-based unlock processing

**Goal:** get the expensive fan-out work off the request path.

- Add RabbitMQ to Docker Compose; MassTransit in API and Worker
- On open, the API publishes a `PremiereOpened` event and returns immediately. It does **not** write library entries inline
- `Marquee.Worker` consumes `PremiereOpened` and does the fan-out: resolve contributors, compute emblems, write `LibraryEntry` rows, persist final counts, then signal the reveal back through SignalR
- Consumers must be **idempotent** — reprocessing the same event must not duplicate library entries or emblems. Rely on the unique constraints and make the write path tolerate conflicts
- Configure retries with backoff and a dead-letter queue for poison messages
- Implement the **outbox pattern**: the DB state change marking the Premiere opened and the queued event must be written in one transaction, so a crash between them cannot lose the event

**Acceptance criteria**
- Kill the worker mid-processing and restart it: no duplicate library entries, no lost contributors
- Publish the same event twice manually: outcome is identical to publishing once
- A deliberately poisoned message lands in the DLQ instead of blocking the queue

---

### Iteration 5 — Security, abuse prevention, and the social layer

**Goal:** make it survivable in public, and add the user-to-user features.

**Anti-abuse**
- Rate limiting via ASP.NET Core's built-in rate limiter: per authenticated user, and per anonymous session
- Anonymous participation: issue a lightweight, short-lived session token on first page load. Not an account, never linked to a user, but throttleable and cap-enforceable. Anonymous contributions are never persisted as users
- Idempotency keys on the clap endpoint so a retried request cannot double-count
- Server-side clap debouncing — enforce a minimum interval between claps per participant

**Auth and roles**
- Policy-based authorisation, not a blanket admin boolean. Separate policies for e.g. `CanManagePremieres`, `CanBlockUsers`, so permissions can diverge later
- Admin endpoints: list/block users, view all Premieres, change a Premiere's scheduled time, regenerate a Premiere's movie (same or new filters), manually trigger a Premiere

**Social**
- Friend requests: send, accept, reject; `Friendship` per `CLAUDE.md` §3
- Profile endpoint returns a shaped DTO based on the viewer:
  - Self, admin, or accepted friend → full profile
  - Stranger, public profile → full profile
  - Stranger, private profile → **only `username` and `bio`**. Other fields are **omitted from the payload entirely**, not returned as nulls
- Private profiles remain **discoverable in search** — privacy restricts detail, not existence
- "Which of my friends contributed" — maintain `premiere:{scope}:{id}:contributors` as a Redis SET, and answer per-viewer on demand with `SINTER` against `user:{userId}:friends`. **Never** broadcast personalised friend data to all connected clients; broadcast only the public count and let each client ask about itself
- Private friends **do** appear in a viewer's friends-among-contributors list — privacy applies to strangers, not to accepted friends

**Acceptance criteria**
- A script hammering the clap endpoint is throttled without degrading service for normal users
- A stranger requesting a private profile receives only username and bio, and the private user still appears in search
- Friend intersection is computed per-request, not broadcast
- Non-admin requests to admin endpoints return 403

---

### Iteration 6 — Observability, resilience, load

**Goal:** be able to see what the system is doing and prove it holds up.

- Serilog structured logging across API and Worker, with correlation IDs that survive the queue hop
- Distributed tracing (OpenTelemetry) so one clap-to-library journey is traceable across API → Redis → queue → worker
- Health check endpoints covering Postgres, Redis, and RabbitMQ
- Polly retry and circuit breaker around the TMDB client
- Integration tests with Testcontainers against real Postgres and Redis
- k6 load test simulating a realistic Premiere: thousands of participants clapping within a 60-minute window, with a burst at the start
- An admin dashboard showing live queue depth, active connections, and clap rate

**Acceptance criteria**
- A single clap is traceable end to end across both services
- Load test runs with no lost claps and no duplicate opens
- TMDB being down does not prevent an already-scheduled Premiere from running

---

### Iteration 7 — Admin control over Premieres

**Goal:** make the admin side usable in production. The API already had most of the capability; almost none of it had a screen, and the parts that did lacked the domain rules the scheduler enforces on itself.

- Remove on-demand Premiere creation from production. How many run per day is a product invariant (§4.4), not an operator decision — the endpoint survives, gated to Development, only so tests and load scripts can put one on screen
- Editing a Scheduled Premiere: move it within its day, retune its threshold within the band the formula itself draws from (§4.1), choose its film or re-roll within a filter
- `GET /admin/premieres/{id}/edit-options` returns the allowed windows and threshold band, so the UI shows the constraints instead of letting an admin discover them one rejection at a time. **The frontend never recomputes §4.4 or §4.2** — a second copy of those formulas would drift from `Marquee.Domain`
- Movie reuse becomes a cooldown rather than a permanent ban (§4.6), with an admin override that requires an explicit acknowledgement
- Persist what a film actually is: genres and origin countries as reference tables, plus original title, language, release date and runtime — so filtering by them is a query rather than a re-fetch
- Angular admin area: routed tabs, users (list/search/block), Premieres (cards, filter, editors), and the film picker. Gated on the API's own permission claims rather than a role string

**Three defects found while building, each fixed with a test that reproduces it**
- Premieres missed while the scheduler was down all activated at once on the next start — days' worth firing together at times nobody drew. They are now marked `Missed` (§4.5)
- `AdminService.ActivateAsync` validated only status, so starting one early could give today five Premieres and another day three. It now binds to §4.4 like every other path that moves when a Premiere runs
- `GenerateDayAsync` topped up a short day by drawing a whole fresh one, overshooting past four. It now fills only the shortfall

**Acceptance criteria**
- An admin can run the day's Premieres — time, threshold, film — without touching the database or Swagger
- Every edit is refused with the domain reason when it would break §4.4 or §4.2, and the UI shows the constraints up front
- A Premiere started early announces itself over SignalR exactly as the scheduler's own activation does
- Postgres and Redis agree after every mutation that touches cached Premiere metadata
