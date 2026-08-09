# Marquee — project context

This file is standing context, loaded every session. It does not change as the project progresses through iterations. For the iteration-by-iteration build sequence, see `MARQUEE_PLAN.md`.

Re-read §3 (domain rules) before writing any logic that touches thresholds, caps, or emblems — those formulas are exact and must not be improvised. If a rule here is ambiguous or contradicts something discovered while building, stop and ask rather than guessing.

---

## 1. What this is

**Marquee** is a social movie-discovery web app built around a synchronous, collaborative unlock mechanic.

Four times a day, at randomised times, a **Premiere** appears. A Premiere contains a hidden movie. It can only be opened when enough users collectively **clap** for it before its 60-minute timer expires. When it opens, the movie is revealed and added to the library of everyone who clapped. Users earn **emblems** based on how much they contributed.

The product goal is movie recommendation through a shared, time-bound event — a BeReal-style "everyone shows up at once" hook.

**The engineering goal is the real point of this project.** CRUD is plumbing here. The substance is:

- High-contention counting (thousands of concurrent claps against one counter)
- Exactly-once event firing under concurrency
- Caching and cache invalidation (Redis)
- Asynchronous processing via a message queue and background worker
- Real-time fan-out (WebSockets)
- Security: authentication, authorisation, rate limiting, anti-bot / anti-cheat
- Observability and load testing

Build decisions should favour exercising these concepts correctly over shipping features fast.

### Glossary

| Term | Meaning |
|---|---|
| **Marquee** | The app itself. |
| **Premiere** | A single unlock event. Contains one hidden movie, has a threshold and a 60-minute window. |
| **Clap** | The unit of contribution. One tap = one clap. |
| **Threshold** | Number of claps needed to open a Premiere. Dynamic — see §3.1. |
| **Cap** | Max claps a single participant may contribute to one Premiere. Dynamic — see §3.2. |
| **Emblem** | A tier awarded per Premiere based on contribution as a percentage of that user's cap. |
| **Library** | A user's collection of movies obtained from Premieres they clapped for. |
| **Scope** | Which audience a Premiere belongs to. v1 is global-only, but the model must support scoping later (see §6). |
| **Anonymous participant** | A visitor who claps without an account. Contributes to the threshold, earns nothing, is not persisted as a user. |

---

## 2. Tech stack

- **Frontend:** Angular (latest stable), standalone components, signals for state, Angular CDK where useful
- **Backend:** ASP.NET Core Web API (controllers, not minimal APIs)
- **ORM:** EF Core, code-first migrations
- **Database:** PostgreSQL
- **Cache / counters:** Redis
- **Queue:** RabbitMQ, accessed via MassTransit
- **Real-time:** SignalR
- **Scheduling:** Quartz.NET or Hangfire (pick one, document the choice)
- **Movie data:** TMDB API
- **Local orchestration:** Docker Compose (api, worker, postgres, redis, rabbitmq)
- **Testing:** xUnit, Testcontainers for integration tests, k6 for load tests

### Solution layout

```
marquee/
  src/
    Marquee.Api/            ASP.NET Core Web API + SignalR hubs
    Marquee.Worker/         Background service, consumes the queue
    Marquee.Domain/         Entities, value objects, domain rules (pure, no I/O)
    Marquee.Infrastructure/ EF Core, Redis, RabbitMQ, TMDB client
    Marquee.Web/            Angular app
  tests/
    Marquee.UnitTests/
    Marquee.IntegrationTests/
    Marquee.LoadTests/      k6 scripts
  docker-compose.yml
  CLAUDE.md
  MARQUEE_PLAN.md
```

The threshold, cap, and emblem formulas live in `Marquee.Domain` as pure functions with no dependencies. They must be unit-testable without a database, Redis, or HTTP.

---

## 3. Domain model

Entities and their essential fields. Add audit fields (`CreatedAt`, `UpdatedAt`) everywhere.

**User**
`Id`, `Username` (unique), `Email` (unique), `PasswordHash`, `Bio`, `IsPrivate` (bool, default false), `IsBlocked` (bool), `Role` (enum: `User` | `Admin`), `CreatedAt`

**Premiere**
`Id`, `ScopeId` (string, `"global"` in v1 — see §6), `ScheduledFor` (UTC), `OpensAt` (when it became active), `ExpiresAt` (= `OpensAt` + 60 min), `Threshold` (int, computed at creation), `RegisteredClapCap` (int, computed at creation), `AnonymousClapCap` (int, computed at creation), `Status` (enum: `Scheduled` | `Active` | `Opened` | `AutoOpened` | `Missed` — see §4.5), `MovieId` (FK), `TotalClaps` (int, authoritative final count, written at open time), `OpenedAt`

**Movie**
`Id`, `TmdbId` (unique), `Title`, `PosterPath`, `ReleaseYear`, `Overview`, `VoteAverage`, `VoteCount`, `CachedAt`

**Contribution**
`Id`, `PremiereId`, `UserId` (nullable — null for anonymous), `AnonymousSessionId` (nullable), `ClapCount`, `EmblemTier` (nullable, assigned at open time)
Unique constraint on (`PremiereId`, `UserId`) and on (`PremiereId`, `AnonymousSessionId`).

**LibraryEntry**
`Id`, `UserId`, `MovieId`, `PremiereId`, `AcquiredAt`
Unique constraint on (`UserId`, `MovieId`).

**Friendship**
`Id`, `RequesterId`, `AddresseeId`, `Status` (enum: `Pending` | `Accepted` | `Rejected`), `CreatedAt`
Unique constraint on (`RequesterId`, `AddresseeId`).

### Redis keys

Namespace every key with the scope from day one, even though v1 only ever uses `global`:

```
premiere:{scopeId}:{premiereId}:claps          counter (INCR)
premiere:{scopeId}:{premiereId}:user:{userId}  per-user clap count (INCR, enforces cap)
premiere:{scopeId}:{premiereId}:anon:{sessionId}
premiere:{scopeId}:{premiereId}:contributors   SET of userIds (for friend intersection)
premiere:{scopeId}:{premiereId}:lock           distributed lock for exactly-once open
user:{userId}:friends                          SET of accepted friend userIds
movie:tmdb:{tmdbId}                            cached TMDB metadata
```

---

## 4. Domain rules

These are exact. Implement them as pure functions in `Marquee.Domain` and unit-test them against the worked examples given.

### 4.1 Threshold

Computed once, at Premiere creation, from the total count of registered users.

```
peak hours       = ScheduledFor local time is >= 10:00 and <= 20:00
percentageRange  = peak     -> random between 45% and 55%
                 = off-peak -> random between 32% and 45%

rawThreshold     = round(percentageRange * totalRegisteredUsers)
threshold        = clamp(rawThreshold, min: random(30, 50), max: none)
```

The floor is itself randomised in the 30–50 range, so a tiny user base still gets variance. If `rawThreshold` lands below that floor, the floor is used.

Worked example: 1,000 registered users, peak hours, roll of 50% → threshold = 500.
Worked example: 40 registered users, off-peak, roll of 35% → raw = 14 → below floor → threshold = random(30, 50).

**An admin may retune a Scheduled Premiere's threshold**, but only within the band the formula itself could have produced:

```
adminBand.min = FloorMin                                        (30)
adminBand.max = max(round(PeakMaxPct * totalRegisteredUsers), FloorMax)
```

The `max(...)` guard matters at small user counts: with 40 users `0.55 × 40 = 22`, which is *below* the 30–50 floor range, and without it the band would come out inverted. An admin can therefore re-roll the dice but never leave the table. The caps are always re-derived from the chosen threshold via §4.2 rather than set by hand, so the participation guarantee holds by construction. Once a Premiere is Active the threshold is fixed: it is the target people are already clapping towards, and the caps are limits some of them have already spent.

### 4.2 Per-participant clap cap

Also computed once at creation. The intent: **even if every participant maxes out their cap, at least 8% of the registered user base must still be needed to reach the threshold.**

```
minParticipants  = ceil(0.08 * totalRegisteredUsers)
registeredCap    = max(1, floor(threshold / minParticipants))
anonymousCap     = max(2, round(0.25 * registeredCap))
```

Worked example: 1,000 users, threshold 500 → minParticipants = 80 → registeredCap = 6 → anonymousCap = max(2, 1.5→2) = 2.

> **Known limitation — document this in a code comment, do not engineer around it in v1.** At very small user counts the 8% guarantee becomes weak (20 users → minParticipants = 2 → two people could open a Premiere alone). This is an accepted tradeoff for v1.

### 4.3 Emblem tiers

Five tiers, assigned per Premiere per contributor, based on `claps / cap` for that participant. Anonymous participants earn nothing.

| Tier | Condition |
|---|---|
| 1 | < 25% of cap |
| 2 | 25% – 50% of cap |
| 3 | 51% – 75% of cap |
| 4 | > 75% of cap, but below cap |
| 5 | reached cap exactly |

Tier names are cosmetic and can be decided later; store the tier number.

### 4.4 Scheduling

- 4 Premieres per day
- All scheduled between 07:00 and 23:00 local time
- Minimum 2 hours between consecutive Premieres
- Times randomised daily
- Each Premiere is active for **60 minutes** from the moment it opens

**These bind an admin starting a Premiere early, not only the generator.** Activation changes when a Premiere runs but leaves `ScheduledFor` alone, so an unchecked "activate now" breaks the day count in both directions — today's audience gets a fifth Premiere, the borrowed-from day is left with three, and the generator (which counts by `ScheduledFor`) sees both days as full and tops up neither. So activation requires all of:

1. the Premiere belongs to **today** — it cannot be pulled forward from another day, or run late from a past one;
2. the current local time is inside the day window;
3. the minimum gap is clear of everything else that ran today.

Spacing is measured against each Premiere's **effective** time, `OpensAt ?? ScheduledFor`: one already started early occupies the slot it actually ran in, not the one it was drawn for.

`Scheduler:EnforceActivationRules` (default true) gates this. It is deliberately not relaxed in Development — the Development-only `POST /api/premieres` already exists for putting a Premiere on screen on demand.

### 4.5 Expiry

If the threshold is not met within 60 minutes, the Premiere **auto-opens anyway** with status `AutoOpened`. Everyone who clapped still receives the movie and their emblem, calculated the same way. There is no failure state for a Premiere that ran.

**A Premiere that never ran is a separate case.** If the scheduler was not running when a Premiere came due, it is only started if it is less than `ActivationGraceMinutes` (default 30) late; past that it is marked `Missed` and abandoned. Without that bound, every Premiere missed during downtime activates the moment the scheduler returns — days' worth firing at once, at times nobody drew, which is exactly what §4.4 exists to prevent.

`Missed` is not a failure state in the §4.5 sense — nobody was let down, because nobody ever saw it. It reveals no movie, queues no fan-out, broadcasts nothing, and **releases its film back to the pool**: §4.6 counts a film as spent only once a Premiere has actually opened.

### 4.6 Movie selection

Query TMDB `/discover/movie` with:
- `vote_count.gte=500`
- `vote_average.gte=5.0`
- Must have a poster

Pick randomly from the filtered pool. The chosen movie is resolved and cached **at Premiere creation time**, never during the clap flow.

**Reuse is a cooldown, not a ban.** A film is unavailable to the random pick while:

1. it is attached to a Premiere that has not run yet (`Scheduled` or `Active`), or
2. it was revealed within `MovieCooldownDays` (default 90) of now.

The clock runs from `Premiere.OpenedAt` — when the film was actually *seen*. Being scheduled is not being seen, and a film swapped out of a Premiere before it ran was never shown at all, so neither starts the timer. The exclusion set is therefore derived from Premieres, **not** from every `Movie` row ever cached: a discarded pick must not shrink the pool for something nobody watched.

Because a film may premiere more than once, `Movie` rows are reused rather than re-created (`TmdbId` is unique), and the open-time fan-out already skips a `LibraryEntry` for anyone who owns the film from an earlier Premiere.

An admin choosing a film explicitly may override the cooldown, but only with an explicit acknowledgement — they are shown when it last premiered and when it comes free. Rule 1 has no override: the same film in two pending Premieres is a scheduling mistake, not a judgement about freshness.

**A Premiere's film can only be changed while it is `Scheduled`.** Not merely because the film is public afterwards — a *running* Premiere can cross its threshold at any moment, and the open path takes its `MovieId` from the Redis `PremiereMeta` snapshot the crossing clap already read. A swap committing between that read and the open's own guarded update would leave `Premiere.MovieId` naming a film that was never revealed and never reached a single library.

---

## 5. Design for later, build for now

v1 is **global scope only**. But two things must be built generically from the start, because retrofitting them is a rewrite rather than an addition:

1. **`ScopeId` exists on `Premiere` from day one**, always `"global"` in v1. Every Redis key is namespaced with it. Every SignalR group name is derived from it.
2. **SignalR group joining is generic** — clients join a group computed from scope, not a single hardcoded global broadcast.

This costs nothing now and means "friend-group Premieres with their own filters" later becomes a `ScopeSettings` table plus populating a different `ScopeId`, not a migration of your counters, locks, and real-time layer.

---

## 6. Explicitly out of scope for v1

Do not build these. If they seem necessary, ask first.

- Scoped / friend-group Premieres (design for it per §5, do not build it)
- Native mobile apps
- Web push or platform push notifications. Design an `INotificationDispatcher` abstraction with a single in-app implementation so a real channel is additive later
- ML-based or personalised recommendation — movie selection is filtered-random, nothing more
- Comments, reactions, or messaging
- Multi-instance deployment and SignalR Redis backplane

---

## 7. Conventions

- Domain rules live in `Marquee.Domain` as pure, dependency-free, unit-tested functions
- Never expose EF entities from controllers — DTOs at every boundary
- All configuration values that are tunable (percentage ranges, floors, the 8% participation target, emblem tier boundaries) go in strongly-typed options bound from configuration, **not** as magic numbers in code
- Every write endpoint that could be retried must be idempotent
- Redis is the hot path for counting; Postgres is the durable record. Never count claps by querying Postgres during an active Premiere
- Sentence case in UI copy. The action is a "clap"; the event is a "Premiere"; the app is "Marquee"
- Commit the concurrency findings document from Iteration 2 (see `MARQUEE_PLAN.md`) — it is part of the deliverable
