# Unconfirmed-user cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A daily EventBridge Scheduler → Lambda job, defined in CDK, that deletes Cognito users who are `UNCONFIRMED` and older than 7 days, with a CloudWatch alarm on failure.

**Architecture:** A new `MarqueeCleanupStack` holds a Node.js 22 Lambda (plain `.mjs`, SDK from the runtime), its least-privilege role, a Scheduler schedule (04:00 America/Toronto, payload `{}`), an SNS topic emailing `alertEmail`, and an `Errors` alarm. Pure selection logic lives in `select.mjs` and is unit-tested with `node --test`; the handler is proven live with a console dry run.

**Tech Stack:** AWS CDK 2.271.0 (C#), Node.js 22 Lambda runtime, `@aws-sdk/client-cognito-identity-provider` (bundled in the runtime), `node:test`.

**Spec:** `docs/superpowers/specs/2026-10-14-unconfirmed-user-cleanup-design.md`

> **Amended 2026-10-14, after the first console test:** the dry-run rule was inverted. Only `{"dryRun": false}` deletes and the schedule sends it; every other event is a dry run. Task 1–3 code blocks below show the original rule; the committed code and the spec are authoritative.

## Global Constraints

- Lambda runtime **Node.js 22** (`Runtime.NODEJS_22_X`), 128 MB, 1-minute timeout; **no** `package.json`, `node_modules`, bundling or build step.
- **No VPC.**
- Lambda role: basic execution plus exactly `cognito-idp:ListUsers` and `cognito-idp:AdminDeleteUser` on the pool ARN.
- Window: `MIN_AGE_DAYS=7`, from a C# constant in the stack. No CDK context key.
- Schedule: daily 04:00 `America/Toronto`, payload `{}`; AWS default retries.
- Alarm: Lambda `Errors` sum ≥ 1 over 1 hour, missing data not breaching, action = the stack's own SNS topic → `alertEmail`.
- Log group retention: one month.
- Logs carry usernames only — never emails.
- The stack must not change `MarqueeAuthStack`, `MarqueeStack` or `MarqueeCiStack` (`cdk diff` proves it).
- `infra/` stays out of `Marquee.sln`; check `git status` for an editor-added `.csproj` entry before each commit.
- Repo workflow: every commit is shown to the user and confirmed before it is made; Conventional Commits; no AI attribution lines. AWS steps the user performs are given as **console** click-paths and marked 🟦 as stop-and-wait points; `cdk deploy` is the user's, via `!`.
- `cdk` commands need, in the same Bash command: `eval "$(aws configure export-credentials --format env)"`, and `-c alertEmail=jdavidip2022@gmail.com`.

## Review Focus

1. **A console test event with `"dryRun": "true"` (a string) or any value other than `false`** — a person expects a dry run, not real deletes. Pinned by `isDryRun` tests in Task 1.
2. **A user record with no `UserCreateDate`** (malformed or unexpected API shape) — expected: never deleted. Pinned in Task 1.
3. **A user exactly `MIN_AGE_DAYS` old** — expected: not deleted yet (strictly older only). Pinned in Task 1.
4. **More than 60 unconfirmed users** — expected: all pages are read before any delete. Not unit-testable without the SDK; Task 2 implements collect-then-delete and the reviewer checks the loop by reading it.
5. **`MIN_AGE_DAYS` missing, `0`, negative or non-numeric** (a broken deploy) — expected: the run fails loudly (alarm) and deletes nothing. Pinned by `parseMinAgeDays` tests in Task 1.

---

## File Structure

| File | Responsibility |
|---|---|
| `infra/lambda/unconfirmed-cleanup/select.mjs` (create) | Pure rules: `selectExpired`, `isDryRun`, `parseMinAgeDays`. No SDK, no I/O. |
| `infra/lambda/unconfirmed-cleanup/select.test.mjs` (create) | `node --test` tests for the three rules. Excluded from the deployed asset. |
| `infra/lambda/unconfirmed-cleanup/index.mjs` (create) | Handler: Cognito calls, collect-then-delete, summary log, error handling. |
| `infra/MarqueeCleanupStack.cs` (create) | Lambda, role grants, log group, schedule, SNS topic, alarm. |
| `infra/Program.cs` (modify) | Construct `MarqueeCleanupStack`. |
| `.github/workflows/ci.yml` (modify) | Run `node --test lambda/` in the `CDK synth` job. |
| `DEPLOYMENT.md` (modify, Task 4) | Describe the stack; record the live checks with dates. |

`isDryRun` and `parseMinAgeDays` go in `select.mjs` (not the spec's handler) so the Review Focus cases are unit-testable without the SDK, which is not installed locally.

---

### Task 1: Pure rules and their tests, run in CI

**Files:**
- Create: `infra/lambda/unconfirmed-cleanup/select.mjs`
- Test: `infra/lambda/unconfirmed-cleanup/select.test.mjs`
- Modify: `.github/workflows/ci.yml` (the `cdk` job, before `Synth`)

**Interfaces:**
- Produces:
  - `selectExpired(users: Array<{Username: string, UserStatus: string, UserCreateDate?: Date}>, now: Date, minAgeDays: number) => same-shaped array` — users that are `UNCONFIRMED` and whose `UserCreateDate` is strictly earlier than `now - minAgeDays × 24h`.
  - `isDryRun(event: unknown) => boolean` — `true` when `event` is an object with a `dryRun` key whose value is anything other than `false`.
  - `parseMinAgeDays(raw: string | undefined) => number` — the positive integer, or throws `Error` naming the bad value.

- [ ] **Step 1: Write the failing tests**

`infra/lambda/unconfirmed-cleanup/select.test.mjs`:

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { selectExpired, isDryRun, parseMinAgeDays } from './select.mjs';

const DAY = 24 * 60 * 60 * 1000;
const NOW = new Date('2026-10-21T12:00:00Z');
const user = (name, status, ageMs) => ({
  Username: name,
  UserStatus: status,
  UserCreateDate: new Date(NOW.getTime() - ageMs),
});
const names = (users) => users.map((u) => u.Username);

test('an unconfirmed user older than the window is selected', () => {
  assert.deepEqual(names(selectExpired([user('old', 'UNCONFIRMED', 8 * DAY)], NOW, 7)), ['old']);
});

test('an unconfirmed user newer than the window is not', () => {
  assert.deepEqual(selectExpired([user('new', 'UNCONFIRMED', 6 * DAY)], NOW, 7), []);
});

test('a user exactly at the window is not selected yet', () => {
  assert.deepEqual(selectExpired([user('edge', 'UNCONFIRMED', 7 * DAY)], NOW, 7), []);
  assert.deepEqual(names(selectExpired([user('edge+1', 'UNCONFIRMED', 7 * DAY + 1)], NOW, 7)), ['edge+1']);
});

test('no other status is ever selected, however old', () => {
  const ancient = 365 * DAY;
  const others = ['CONFIRMED', 'FORCE_CHANGE_PASSWORD', 'RESET_REQUIRED', 'EXTERNAL_PROVIDER', 'UNKNOWN']
    .map((s) => user(s, s, ancient));
  assert.deepEqual(selectExpired(others, NOW, 7), []);
});

test('a user with no creation date is never selected', () => {
  assert.deepEqual(selectExpired([{ Username: 'nodate', UserStatus: 'UNCONFIRMED' }], NOW, 7), []);
});

test('an empty list selects nothing', () => {
  assert.deepEqual(selectExpired([], NOW, 7), []);
});

test('only a payload naming dryRun means a dry run', () => {
  assert.equal(isDryRun({}), false);
  assert.equal(isDryRun(undefined), false);
  assert.equal(isDryRun(null), false);
  assert.equal(isDryRun({ dryRun: false }), false);
  assert.equal(isDryRun({ dryRun: true }), true);
  // Anything else a person might type still errs on the side of not deleting.
  assert.equal(isDryRun({ dryRun: 'true' }), true);
  assert.equal(isDryRun({ dryRun: 'false' }), true);
  assert.equal(isDryRun({ dryRun: 1 }), true);
});

test('MIN_AGE_DAYS must be a positive integer', () => {
  assert.equal(parseMinAgeDays('7'), 7);
  for (const bad of [undefined, '', '0', '-1', '1.5', 'seven']) {
    assert.throws(() => parseMinAgeDays(bad), /MIN_AGE_DAYS/, `accepted ${JSON.stringify(bad)}`);
  }
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `node --test infra/lambda/`
Expected: FAIL — `Cannot find module '.../select.mjs'`.

- [ ] **Step 3: Write the implementation**

`infra/lambda/unconfirmed-cleanup/select.mjs`:

```js
// The cleanup's rules, kept free of the AWS SDK so they can be tested anywhere (#30).

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * Users to delete: UNCONFIRMED and created strictly more than minAgeDays ago. ListUsers already
 * filters on status server-side; checking it again here means a mistyped filter can never widen
 * deletion to confirmed users. A record without a creation date is never selected.
 */
export function selectExpired(users, now, minAgeDays) {
  const cutoff = now.getTime() - minAgeDays * DAY_MS;
  return users.filter(
    (u) => u.UserStatus === 'UNCONFIRMED'
      && u.UserCreateDate instanceof Date
      && u.UserCreateDate.getTime() < cutoff,
  );
}

/**
 * The schedule sends {} and deletes. A console test that mentions dryRun at all gets a dry run
 * unless it says exactly false — a typo errs towards deleting nothing.
 */
export function isDryRun(event) {
  return typeof event === 'object' && event !== null && 'dryRun' in event && event.dryRun !== false;
}

/** The window from the environment; anything but a positive integer is a broken deploy. */
export function parseMinAgeDays(raw) {
  const days = Number(raw);
  if (raw === undefined || raw === '' || !Number.isInteger(days) || days < 1) {
    throw new Error(`MIN_AGE_DAYS must be a positive integer, got ${JSON.stringify(raw)}`);
  }
  return days;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `node --test infra/lambda/`
Expected: PASS — 8 tests, 0 failures.

- [ ] **Step 5: Mutation check (the boundary has teeth)**

Temporarily change `<` to `<=` in `selectExpired`, run `node --test infra/lambda/`, expect the
"exactly at the window" test to FAIL, then revert and re-run to PASS. Use the Edit tool (files are
CRLF-sensitive; no `sed -i`).

- [ ] **Step 6: Run the tests in CI**

In `.github/workflows/ci.yml`, in the `cdk` job, insert between the `actions/setup-node@v4` step and
`- name: Synth` (the job's `working-directory` is already `infra`):

```yaml
      - name: Lambda unit tests
        run: node --test lambda/
```

- [ ] **Step 7: Commit (after the user confirms)**

```bash
git add infra/lambda/unconfirmed-cleanup/select.mjs infra/lambda/unconfirmed-cleanup/select.test.mjs .github/workflows/ci.yml
git commit -m "feat: selection rules for deleting unconfirmed Cognito users (#30)"
```

---

### Task 2: The handler

**Files:**
- Create: `infra/lambda/unconfirmed-cleanup/index.mjs`

**Interfaces:**
- Consumes: `selectExpired`, `isDryRun`, `parseMinAgeDays` from `./select.mjs` (Task 1).
- Produces: `export async function handler(event)` — Lambda handler string `index.handler`. Reads env `USER_POOL_ID`, `MIN_AGE_DAYS`. Returns and logs `{ dryRun, scanned, selected: [{ username, createdAt }], deleted, failed: [{ username, error }] }`; throws after the loop if `failed` is non-empty.

- [ ] **Step 1: Write the handler**

`infra/lambda/unconfirmed-cleanup/index.mjs`:

```js
// Deletes Cognito users who never confirmed their email, once they are older than MIN_AGE_DAYS
// (#30). Invoked daily by EventBridge Scheduler with {}; invoke it from the console with
// {"dryRun": true} to see what it would delete without deleting anything.
import {
  CognitoIdentityProviderClient,
  ListUsersCommand,
  AdminDeleteUserCommand,
} from '@aws-sdk/client-cognito-identity-provider';
import { selectExpired, isDryRun, parseMinAgeDays } from './select.mjs';

const cognito = new CognitoIdentityProviderClient({});

export async function handler(event) {
  const dryRun = isDryRun(event);
  const userPoolId = process.env.USER_POOL_ID;
  if (!userPoolId) throw new Error('USER_POOL_ID is not set');
  const minAgeDays = parseMinAgeDays(process.env.MIN_AGE_DAYS);

  // Collect every page before deleting anything: deleting while paginating can shift the pages
  // underneath and silently skip users.
  const users = [];
  let paginationToken;
  do {
    const page = await cognito.send(new ListUsersCommand({
      UserPoolId: userPoolId,
      Filter: 'cognito:user_status = "UNCONFIRMED"',
      Limit: 60,
      PaginationToken: paginationToken,
    }));
    users.push(...(page.Users ?? []));
    paginationToken = page.PaginationToken;
  } while (paginationToken);

  const selected = selectExpired(users, new Date(), minAgeDays);
  const summary = {
    dryRun,
    scanned: users.length,
    // Usernames only: emails stay out of the logs.
    selected: selected.map((u) => ({ username: u.Username, createdAt: u.UserCreateDate.toISOString() })),
    deleted: 0,
    failed: [],
  };

  if (!dryRun) {
    for (const user of selected) {
      try {
        await cognito.send(new AdminDeleteUserCommand({ UserPoolId: userPoolId, Username: user.Username }));
        summary.deleted++;
      } catch (err) {
        // Confirmed or deleted since the listing: the goal is met, and a retried run stays harmless.
        if (err.name === 'UserNotFoundException') {
          summary.deleted++;
        } else {
          summary.failed.push({ username: user.Username, error: err.name });
        }
      }
    }
  }

  console.log(JSON.stringify(summary));
  // Carry on past one bad user, but still count the run as an error so the alarm fires.
  if (summary.failed.length > 0) {
    throw new Error(`${summary.failed.length} deletion(s) failed; see the summary above`);
  }
  return summary;
}
```

- [ ] **Step 2: Syntax check and re-run the rule tests**

Run: `node --check infra/lambda/unconfirmed-cleanup/index.mjs && node --test infra/lambda/`
Expected: no syntax error; 8 tests pass. (The handler is not unit-tested by design — the SDK is not
installed locally; it is proven by the console dry run and live run in Task 4.)

- [ ] **Step 3: Commit (after the user confirms)**

```bash
git add infra/lambda/unconfirmed-cleanup/index.mjs
git commit -m "feat: Lambda handler that deletes expired unconfirmed users (#30)"
```

---

### Task 3: `MarqueeCleanupStack`

**Files:**
- Create: `infra/MarqueeCleanupStack.cs`
- Modify: `infra/Program.cs:21` (add one line after `new MarqueeStack(...)`)

**Interfaces:**
- Consumes: `infra/lambda/unconfirmed-cleanup/index.mjs` exporting `handler` (Task 2); `auth.UserPool` (`UserPool`, from `MarqueeAuthStack`); `alertEmail` (string, from `Program.cs`).
- Produces: stack `MarqueeCleanupStack` with output `FunctionName`.

- [ ] **Step 1: Write the stack**

`infra/MarqueeCleanupStack.cs`:

```csharp
using Amazon.CDK;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.Cognito;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.Scheduler;
using Amazon.CDK.AWS.Scheduler.Targets;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Constructs;

namespace Marquee.Cdk;

// Deletes Cognito users who never confirmed their email, once a day (#30, DEPLOYMENT.md § Phase 2,
// decision 4). Low product value on purpose: an unconfirmed account only holds its username. This is
// the project's first Lambda and Scheduler schedule, kept out of the app's critical path.
//
// Its own stack so learning iterations never deploy MarqueeAuthStack, which holds the pool.
public class MarqueeCleanupStack : Stack
{
    // How old an unconfirmed sign-up must be before it is deleted.
    private const int UnconfirmedMaxAgeDays = 7;

    public MarqueeCleanupStack(Construct scope, string id, string alertEmail, IUserPool userPool, IStackProps props)
        : base(scope, id, props)
    {
        var cleanup = new Function(this, "UnconfirmedUserCleanup", new FunctionProps
        {
            FunctionName = "unconfirmed-user-cleanup",
            Description = "Deletes UNCONFIRMED Cognito users older than the window (#30).",
            Runtime = Runtime.NODEJS_22_X,
            Handler = "index.handler",
            // Plain .mjs: the runtime ships AWS SDK v3, so there is nothing to bundle. Paths are
            // relative to infra/, where cdk runs.
            Code = Code.FromAsset("lambda/unconfirmed-cleanup", new Amazon.CDK.AWS.S3.Assets.AssetOptions
            {
                Exclude = new[] { "*.test.mjs" },
            }),
            MemorySize = 128,
            Timeout = Duration.Minutes(1),
            Environment = new Dictionary<string, string>
            {
                ["USER_POOL_ID"] = userPool.UserPoolId,
                ["MIN_AGE_DAYS"] = UnconfirmedMaxAgeDays.ToString(),
            },
            LogGroup = new LogGroup(this, "UnconfirmedUserCleanupLogs", new LogGroupProps
            {
                Retention = RetentionDays.ONE_MONTH,
                RemovalPolicy = RemovalPolicy.DESTROY,
            }),
        });

        // Exactly the two calls the handler makes, on this pool only.
        userPool.Grant(cleanup, "cognito-idp:ListUsers", "cognito-idp:AdminDeleteUser");

        // Scheduler, not an EventBridge rule: it takes a time zone and follows daylight saving.
        // The L2 target creates Scheduler's own role, allowed only to invoke this function.
        new Schedule(this, "DailyCleanup", new ScheduleProps
        {
            Description = "Runs unconfirmed-user-cleanup daily (#30).",
            Schedule = ScheduleExpression.Cron(new CronOptionsWithTimezone
            {
                Minute = "0",
                Hour = "4",
                TimeZone = Amazon.CDK.TimeZone.AMERICA_TORONTO,
            }),
            Target = new LambdaInvoke(cleanup, new ScheduleTargetBaseProps
            {
                // {} means a real run; {"dryRun": true} is for console test invokes.
                Input = ScheduleTargetInput.FromObject(new Dictionary<string, object>()),
            }),
        });

        // Its own topic: reusing MarqueeStack's would tie this stack to the app host. The email has to be
        // confirmed within 3 days or SNS deletes the subscription (#133).
        var alerts = new Topic(this, "CleanupAlerts");
        alerts.AddSubscription(new EmailSubscription(alertEmail));

        var failures = new Alarm(this, "CleanupErrorsAlarm", new AlarmProps
        {
            AlarmDescription = "unconfirmed-user-cleanup failed; see its CloudWatch Logs.",
            Metric = cleanup.MetricErrors(new MetricOptions
            {
                Period = Duration.Hours(1),
                Statistic = "Sum",
            }),
            Threshold = 1,
            ComparisonOperator = ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
            EvaluationPeriods = 1,
            // No invocation in an hour means no data point, not a failure.
            TreatMissingData = TreatMissingData.NOT_BREACHING,
        });
        failures.AddAlarmAction(new SnsAction(alerts));

        new CfnOutput(this, "FunctionName", new CfnOutputProps { Value = cleanup.FunctionName });
    }
}
```

- [ ] **Step 2: Wire it into the app**

In `infra/Program.cs`, after line 21 (`new MarqueeStack(...)`), add:

```csharp
new MarqueeCleanupStack(app, "MarqueeCleanupStack", alertEmail, auth.UserPool, new StackProps { Env = env });
```

- [ ] **Step 3: Build and synth**

Run (from repo root): `cd infra && dotnet build -v q && npx --yes aws-cdk@2.1143.0 synth MarqueeCleanupStack --quiet -c alertEmail=ci@example.invalid`
Expected: build succeeds with no errors; synth exits 0. If `TimeZone` or `AssetOptions` fails to
resolve, fix the namespace (do not change behaviour).

- [ ] **Step 4: Inspect the synthesized template**

Run: `grep -E '"(Runtime|Handler|ScheduleExpression|ScheduleExpressionTimezone|RetentionInDays|cognito-idp:[A-Za-z]+)"' -o infra/cdk.out/MarqueeCleanupStack.template.json | sort | uniq -c`
and read the IAM policy statements in that template.
Expected: `nodejs22.x`, `index.handler`, `cron(0 4 * * ? *)`, `America/Toronto`, retention 30, and the
Lambda role's Cognito statement lists exactly `cognito-idp:ListUsers` and `cognito-idp:AdminDeleteUser`
with the pool ARN as resource. Also confirm the asset folder under `cdk.out` contains `index.mjs` and
`select.mjs` but **not** `select.test.mjs`.

- [ ] **Step 5: `cdk diff` against the live account**

Run: `cd infra && eval "$(aws configure export-credentials --format env)" && npx --yes aws-cdk@2.1143.0 diff -c alertEmail=jdavidip2022@gmail.com`
Expected: `MarqueeCleanupStack` shows only new resources; `MarqueeAuthStack`, `MarqueeStack` and
`MarqueeCiStack` show **no differences** (an output-export change on `MarqueeAuthStack` for the pool
reference is the one acceptable exception — report it to the user if it appears). If credentials have
expired, ask the user to run `! aws login` first.

- [ ] **Step 6: Commit (after the user confirms)**

`git status` first: if `Marquee.sln` changed, `git restore Marquee.sln`.

```bash
git add infra/MarqueeCleanupStack.cs infra/Program.cs
git commit -m "feat: MarqueeCleanupStack schedules the unconfirmed-user cleanup (#30)"
```

---

### Task 4: Deploy, console walkthrough, and the live record

**Files:**
- Modify: `DEPLOYMENT.md` (a short `MarqueeCleanupStack` entry where the other stacks are described, and the dated live-check record)

Each 🟦 step is the user's, in the console unless stated; stop and wait for them at each.

- [ ] **Step 1: 🟦 Deploy** — the user runs:
  `! cd infra && eval "$(aws configure export-credentials --format env)" && npx --yes aws-cdk@2.1143.0 deploy MarqueeCleanupStack -c alertEmail=jdavidip2022@gmail.com --require-approval never`

- [ ] **Step 2: 🟦 Confirm the SNS subscription** — open the "AWS Notification - Subscription Confirmation" email and click **Confirm subscription**, within 3 days. Then **SNS → Topics → `MarqueeCleanupStack-CleanupAlerts…` → Subscriptions**: status **Confirmed**.

- [ ] **Step 3: 🟦 Console tour**
  - **Lambda → Functions → `unconfirmed-user-cleanup`**: Code tab (both `.mjs` files, no test file); Configuration → General (128 MB, 1 min); Configuration → Environment variables (`USER_POOL_ID`, `MIN_AGE_DAYS=7`); the diagram shows EventBridge Scheduler as a trigger source.
  - **Configuration → Permissions → role name** (opens IAM): one policy with the two Cognito actions on the pool ARN, plus the basic execution policy for logs.
  - **EventBridge → Scheduler → Schedules → `MarqueeCleanupStack-DailyCleanup…`**: `cron(0 4 * * ? *)`, time zone America/Toronto, target the Lambda with input `{"dryRun": false}`; its execution role allows only `lambda:InvokeFunction` on this function.
  - **CloudWatch → Alarms → `MarqueeCleanupStack-CleanupErrorsAlarm…`**: Errors ≥ 1 for 1 datapoint within 1 hour, action → the topic, state OK (or Insufficient data before the first run).

- [ ] **Step 4: 🟦 Dry run** — Lambda → **Test** tab → new event named `dry-run`, JSON `{"dryRun": true}` → **Test**. Expected result: `dryRun: true`, `deleted: 0`, `failed: []`; before 2026-10-21 `selected: []`. Then **Monitor → View CloudWatch logs** → newest log stream: the same JSON summary line, no emails.

- [ ] **Step 5: 🟦 Optional alarm test** — CloudWatch → Alarms → the alarm → **Actions → Set alarm state → In alarm**: the alert email arrives; the alarm returns to OK on its next evaluation.

- [ ] **Step 6: 🟦 Live run (on or after 2026-10-21, 20:54 UTC)** — **Cognito → User pools → `marquee` → Users**: `test_throw` is Unconfirmed. Dry run (Step 4): `selected` lists `test_throw`. Then a real run: a new test event `{"dryRun": false}` → **Test** (or wait for 04:00). Expected: `deleted: 1`. Cognito Users: `test_throw` gone; `admin` and `j_david_ip` still there.

- [ ] **Step 7: Record it** — in `DEPLOYMENT.md`, add a `MarqueeCleanupStack` description next to the other stacks (what it holds, that it deploys by hand like the others, the confirm-the-email caveat) and a dated checklist of Steps 2, 4, 5 (if done) and 6 with the observed results. *Since #145 the dated checklist lives in `docs/deployment/verification-log.md` (section 2c); record Step 6 there.*

- [ ] **Step 8: Commit (after the user confirms)**

```bash
git add DEPLOYMENT.md
git commit -m "docs: record the unconfirmed-user cleanup deploy and live check (#30)"
```

- [ ] **Step 9: Review and PR** — `marquee-reviewer` on the branch; only a clean "ready" proceeds (otherwise bring notes to the user). Push and open a PR with `Closes #30` only when the user asks; wait for CI to go green before merging; closing comment on #30 after merge.
