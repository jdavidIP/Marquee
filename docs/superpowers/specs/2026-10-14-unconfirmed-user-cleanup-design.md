# Unconfirmed-user cleanup — design

Issue: #30 · Date: 2026-10-14 · Status: approved in conversation, awaiting spec review

## Goal

Once a day, delete Cognito users who are `UNCONFIRMED` and older than 7 days. Nothing else is ever
touched.

**Why it exists:** an unconfirmed account cannot sign in and has no Postgres row (DEPLOYMENT.md § Phase
2, decisions 1 and 2); all it holds is its username, which Cognito never frees on its own. Product
value is low and that is accepted. This is deliberately an **AWS learning task**: the project's first
Lambda, an EventBridge Scheduler schedule, a Lambda's own IAM role, Cognito's admin APIs, and a
CloudWatch alarm, with nothing in the app's critical path depending on it.

## Decisions

| # | Decision | Rejected, and why |
|---|---|---|
| 1 | **Node.js**, a plain `.mjs` Lambda. The runtime ships AWS SDK v3, so no dependencies, no bundling, no build step; `cdk synth` in CI is unchanged. | C#: matches the backend but adds a project, `dotnet publish` before synth (or Docker bundling) and `Amazon.Lambda.*` packages. Python: a new language and test toolchain for ~40 lines. |
| 2 | **CDK creates every resource.** The console is for everything that is not CDK code: inspecting, test invokes, logs, test data. | Building it by hand first, then in CDK: double work and a drift risk. Console only: contradicts the issue and every other resource. |
| 3 | **Its own stack, `MarqueeCleanupStack`.** | Inside `MarqueeAuthStack`: every learning iteration would deploy the stack holding the irreplaceable pool. |
| 4 | **A CloudWatch alarm** on the Lambda's `Errors`, emailing `alertEmail` through the stack's own SNS topic. | Logs only: nobody looks. Reusing `MarqueeStack`'s topic: couples this stack to the app host. |
| 5 | **Dry run chosen per invocation**, by `{"dryRun": true}` in the event. The schedule sends `{}`. | An environment variable: redeploy (or console drift) to switch, and easy to leave on. No dry run: no safe first look. |

## Architecture

```
Scheduler (04:00 Toronto) ──invoke {}──▶ Lambda ──ListUsers / AdminDeleteUser──▶ Cognito pool
                                            │
                                            └─ Errors metric ──▶ Alarm ──▶ SNS ──▶ alertEmail
```

`infra/MarqueeCleanupStack.cs`, constructed in `Program.cs` with `auth.UserPool` and `alertEmail`, the
same way `MarqueeStack` is. It contains:

1. **Lambda `unconfirmed-user-cleanup`** — Node.js 22, 128 MB, 1-minute timeout. Code from
   `infra/lambda/unconfirmed-cleanup/` via `Code.FromAsset`, excluding `*.test.mjs`. Environment:
   `USER_POOL_ID` (from the pool) and `MIN_AGE_DAYS=7` (from a C# constant in the stack; changing the
   window is a code change and a deploy). Its log group has **one-month retention**.
2. **The Lambda's role** — basic execution (its own logs) plus exactly `cognito-idp:ListUsers` and
   `cognito-idp:AdminDeleteUser` on the pool ARN, granted with `userPool.Grant(...)`.
3. **EventBridge Scheduler schedule** — daily at 04:00 `America/Toronto` (Scheduler handles the time zone
   and daylight saving). Target: the Lambda, payload `{}`. CDK generates Scheduler's own role, allowed
   only `lambda:InvokeFunction` on this function. Retry settings stay at AWS defaults.
4. **SNS topic** with an email subscription to `alertEmail`.
5. **CloudWatch alarm** — Lambda `Errors` sum ≥ 1 over one hour → the topic. Missing data is treated as
   not breaching (no invocations means no data points).

Two roles on purpose: Scheduler's can only invoke the function; the function's can only list and delete
users in this pool. Neither can do the other's job.

The schedule uses the Scheduler L2 constructs (`Amazon.CDK.AWS.Scheduler.Schedule` with the
`Scheduler.Targets.LambdaInvoke` target), both present in the pinned CDK version (2.271.0).

## Lambda

Files in `infra/lambda/unconfirmed-cleanup/`:

- **`select.mjs`** — `selectExpired(users, now, minAgeDays)`: pure, no SDK, no I/O.
- **`index.mjs`** — the handler: Cognito calls, the dry-run switch, logging.
- **`select.test.mjs`** — `node --test` tests for `selectExpired`.

**Selection rule.** A user is selected only if `UserStatus === "UNCONFIRMED"` **and** `UserCreateDate`
is strictly more than `minAgeDays × 24h` before `now`. `ListUsers` already filters with
`cognito:user_status = "UNCONFIRMED"`; the function checks the status again so a mistyped filter cannot
widen deletion to every user. IAM scope, server filter and client check are three layers; the client
check is the one the unit test pins down.

**Handler flow.**

1. `dryRun` = `event?.dryRun === true`. Read `USER_POOL_ID` and `MIN_AGE_DAYS`; if either is missing
   or `MIN_AGE_DAYS` is not a positive integer, throw (a misconfigured deploy raises the alarm).
2. **Collect first, delete second:** page through `ListUsers` (limit 60, following `PaginationToken`)
   and gather every candidate before deleting anything — deleting mid-pagination can shift pages and
   skip users.
3. `selectExpired(collected, new Date(), minAgeDays)`.
4. Dry run: log and return the selection. Otherwise `AdminDeleteUser` for each, sequentially.
5. Log and return one JSON summary:
   `{ dryRun, scanned, selected: [{ username, createdAt }], deleted, failed }`. Usernames only — no
   emails in logs.

**Errors.**

- `UserNotFoundException` on delete counts as success (confirmed or deleted in between); this also makes
  a retried invocation harmless.
- Any other delete failure: log it, continue with the rest, and **throw at the end** if anything failed,
  so the run counts as an error and the alarm fires.
- A `ListUsers` failure throws immediately: nothing has been deleted.
- Scheduler invokes asynchronously, so Lambda retries a failed run up to twice by default. Every step is
  safe to repeat, and the next day's run is the backstop.

## Testing

`select.test.mjs`:

- `UNCONFIRMED` and older than the window → selected.
- `UNCONFIRMED` and newer → not selected; plus the exact boundary (exactly `minAgeDays` old → not
  selected), so a `<` / `<=` slip fails.
- `CONFIRMED`, `FORCE_CHANGE_PASSWORD`, `RESET_REQUIRED`, `EXTERNAL_PROVIDER`, however old → never
  selected.
- Empty list → empty result.

The handler is not unit-tested: mocking the SDK client would mostly test the mock. It is proven by the
dry run and the live run against the real pool, recorded in DEPLOYMENT.md.

**CI:** the existing `CDK synth` job (Node already set up) runs `node --test lambda/` before synth. CI
uses Node 24, the function runs on Node 22; nothing used differs between them.

## Rollout

🟦 = the user, in the AWS console unless stated. Each is a stop-and-wait point.

1. Build on `feat/30-unconfirmed-user-cleanup`, one confirmed commit at a time; `cdk diff` shows only
   `MarqueeCleanupStack` resources and no changes to the other stacks.
2. 🟦 `cdk deploy MarqueeCleanupStack` (CLI, via `!`).
3. 🟦 Confirm the SNS subscription email **within 3 days** — SNS deletes unconfirmed subscriptions, which
   is exactly what left `MarqueeStack`'s alerts topic empty (#133).
4. 🟦 Console tour: the Lambda (code, configuration, environment, trigger); IAM (both roles and their
   policies); EventBridge Scheduler (expression, time zone, target); CloudWatch Alarms (threshold,
   action); SNS (subscription confirmed).
5. 🟦 Dry run from the Lambda **Test** tab with `{"dryRun": true}` — before 2026-10-21 it selects nobody.
   Read the same run in **CloudWatch Logs**.
6. 🟦 Optional: **Set alarm state** → `ALARM` in CloudWatch and check the email arrives.
7. 🟦 On or after 2026-10-21: `test_throw` (created 2026-10-14 20:54 UTC, kept `UNCONFIRMED`) shows in
   **Cognito → Users**; a dry run lists it; a real invoke (`{}`, or the 04:00 run) deletes it; `admin`
   and `j_david_ip` are untouched.
8. Record steps 5–7 in DEPLOYMENT.md with dates; reviewer; PR (`Closes #30`); closing comment.

The expired-code check (DEPLOYMENT.md, phase 2 record) also uses `test_throw`, on 2026-10-15, without
resending a code; it does not conflict with step 7.

## Cost

Scheduler (14M invocations/month free), Lambda (always-free 1M requests), one more CloudWatch alarm
(10 free) and SNS email (1,000/month free) are all inside free allowances; the log group is a few KB a
month. Nothing here can move the account off the free plan.

## Out of scope

- Deleting anything from Postgres (unconfirmed users have no row).
- A "disable first" stage (an unconfirmed user already cannot sign in).
- Fixing `MarqueeStack`'s empty alerts topic — tracked in #133.
