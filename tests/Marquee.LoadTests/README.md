# Marquee load tests

Scripts that exercise the API the way real traffic would — no test hooks, no back doors, same public
endpoints a browser hits.

Iteration 2's before/after numbers live in
[`../../docs/concurrency-findings.md`](../../docs/concurrency-findings.md).

## Scripts

| File | Runner | Use |
|---|---|---|
| `clap-storm.mjs` | Node 18+ (no deps) | **Iteration 2.** Four labelled scenarios (lost updates, double open, contended open, cap enforcement) with a full tally. This is what produced the numbers in the findings doc. |
| `clap-storm.js` | [k6](https://k6.io) | The canonical load test per CLAUDE.md §2. Single contended burst; emits `claps_opened_true`, `claps_accepted`, `claps_5xx` metrics. |
| `realtime-check.mjs` | Node 22+ (no deps) | **Iteration 3.** Asserts the acceptance criteria that need a running system: two watchers see the count move, broadcasts are throttled, the reveal arrives exactly once, and a Premiere auto-opens on its timer. Exits non-zero on failure. |
| `signalr-client.mjs` | – | A ~90-line SignalR JSON-protocol client over the native `WebSocket`, so the realtime check stays dependency-free like the rest of this folder. |
| `queue-check.mjs` | Node 22+ (no deps) | **Iteration 4.** Asserts the queue acceptance criteria: the worker fans out off the request path, a killed-and-restarted worker loses and duplicates nothing, a replayed event changes nothing, and a poisoned message is dead-lettered without blocking the queue. Exits non-zero on failure. |

## Prerequisites

- Postgres, Redis and RabbitMQ up (`docker compose up -d`) and the API running on
  `http://localhost:5080`.
- The seeded dev admin (`admin` / `admin12345`) exists — the API seeds it on startup.
- For `queue-check.mjs`: `Marquee.Worker` running, plus `docker` on `PATH` (it reads the
  authoritative numbers straight out of the Postgres container).

### The stub TMDB pool is a hard ceiling

With no `Tmdb__ApiKey` configured, movies come from `StubTmdbClient`'s fixed pool of **12** films, and
§4.6 forbids a movie ever repeating. That caps a given database at 12 Premieres in total — which these
scripts burn through quickly, after which premiere creation fails with
`TMDB returned no fresh movie for a new Premiere`. Either set a real key, or clear the tables before a
long session:

```bash
docker exec -e PGPASSWORD=marquee marquee-postgres psql -U marquee -d marquee \
  -c 'DELETE FROM library_entries; DELETE FROM contributions; DELETE FROM premieres; DELETE FROM movies;'
```

## Running

```bash
cd tests/Marquee.LoadTests

# Iteration 2 — concurrency (zero install)
node clap-storm.mjs
k6 run clap-storm.js        # or the canonical k6 version

# Iteration 3 — real-time and scheduling
node realtime-check.mjs
SKIP_AUTOOPEN=1 node realtime-check.mjs   # skip the ~1 minute timer wait

# Iteration 4 — queue, outbox, idempotency, DLQ
node queue-check.mjs
SKIP_CRASH=1 node queue-check.mjs         # skip the check that kills the worker
```

The crash check deliberately kills `Marquee.Worker` and then waits (up to 120s) for a process to
reappear, so run it with something supervising the worker — a `dotnet watch`, a restart loop, or just
restart it by hand when the script prompts.

Environment overrides: `API_BASE`, `USERS`, `ADMIN_USER`, `ADMIN_PASS` (all scripts), plus `HUB_URL`
and `SCOPE_ID` for the realtime check, and `RABBIT_API` / `RABBIT_USER` / `RABBIT_PASS` /
`PG_CONTAINER` for the queue check.

```bash
USERS=500 API_BASE=http://localhost:5080/api node clap-storm.mjs
```

The scheduler activating or auto-opening one of the day's Premieres mid-run is harmless — every
script creates and targets its own Premiere — but it makes the output noisier. To take it out of the
picture entirely, start the API with `Scheduler__Enabled=false`.

## Verifying the authoritative result

The scripts report what each clap *response* said; the source of truth is Postgres. After a run,
check the premiere row the script prints:

```bash
docker exec -e PGPASSWORD=marquee marquee-postgres \
  psql -U marquee -d marquee \
  -c 'SELECT id, "Status", "TotalClaps", "Threshold" FROM "Premieres" ORDER BY "CreatedAt" DESC LIMIT 2;'
```

- **Lost-update scenario (below threshold):** `TotalClaps` must equal the accepted-clap count.
  Before the fix it is lower (lost updates); after the fix it matches exactly.
- **Double-open scenario:** exactly one clap response carries `opened=true`, and there is exactly
  one set of `LibraryEntries` per contributor. Before the fix, multiple `opened=true` responses
  and/or `5xx` from the contended library fan-out.
