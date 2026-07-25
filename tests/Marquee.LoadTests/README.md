# Marquee load tests

Load scripts for Iteration 2 (MARQUEE_PLAN.md) — used to break the naive clap counter under
concurrency, then to prove the Redis-backed counter is exact and the open fires exactly once.

The recorded before/after numbers live in [`../../docs/concurrency-findings.md`](../../docs/concurrency-findings.md).

## Scripts

| File | Runner | Use |
|---|---|---|
| `clap-storm.mjs` | Node 18+ (no deps) | The runnable driver. Two labelled scenarios (lost updates, double open) with a full tally. This is what produced the numbers in the findings doc. |
| `clap-storm.js` | [k6](https://k6.io) | The canonical load test per CLAUDE.md §2. Single contended burst; emits `claps_opened_true`, `claps_accepted`, `claps_5xx` metrics. |

Both hit the same public API and the same clap endpoint — no test hooks, no back doors.

## Prerequisites

- Postgres up (`docker compose up -d`) and the API running on `http://localhost:5080`.
- The seeded dev admin (`admin` / `admin12345`) exists — the API seeds it on startup.

## Running

```bash
# Node driver (recommended here — zero install)
cd tests/Marquee.LoadTests
node clap-storm.mjs

# or k6
k6 run clap-storm.js
```

Environment overrides (both scripts): `API_BASE`, `USERS`, `ADMIN_USER`, `ADMIN_PASS`.

```bash
USERS=500 API_BASE=http://localhost:5080/api node clap-storm.mjs
```

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
