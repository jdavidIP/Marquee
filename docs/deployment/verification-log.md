# Verification log

Dated records of every live check against the deployed stack, moved here from `DEPLOYMENT.md`
(#145) so that file can describe what is deployed. **Append-only**: add new entries at the end of
the section they belong to, and start a new section for a new piece of infrastructure.

Entries are dated in Toronto time (the audience's day, and the scheduler's); clock times inside an
entry say which zone they are in. Entries marked *operator* were done by hand and reported back.

## 1d. Phase 1 verification

- [x] **2026-09-28** — `docker compose -f docker-compose.prod.yml up -d --wait` on the host: all five
  containers reach `healthy`/running, `/health/ready` returns 200, and the worker's MassTransit
  consumers register against RabbitMQ with no errors.
- [x] **2026-09-28** — Reboot survival: wrote a marker row into Postgres, `aws ec2 reboot-instances`,
  confirmed via `uptime -s` that the host actually rebooted (not just SSM reconnecting), then confirmed
  the data volume and swap remounted from `/etc/fstab`, Docker and all five containers restarted on
  their own (`restart: unless-stopped` survives a daemon restart), `/health/ready` returned 200 again,
  and the marker row was still there with its original timestamp.
- [x] **2026-09-28** — Nothing reachable inbound except from CloudFront: direct TCP connection attempts
  to the host's public DNS name on ports 80, 22, 5432, 6379 and 15672, run from outside AWS, all got no
  response.
- [x] **2026-09-29** — Snapshot restore: created a scratch gp3 volume from the first DLM snapshot of
  the data volume, attached it to the host and mounted it read-only (`ro,nouuid,norecovery`, since the
  XFS clone shares the live volume's UUID). All three named volumes were present, and `pg_controldata`
  run on the restored Postgres directory read it cleanly and returned the same database system
  identifier as the live cluster. Its cluster state was `in production`, which is expected for a
  crash-consistent snapshot, and Postgres replays WAL for that on start. Unmounted, detached and
  deleted the scratch volume afterwards.
- [x] **2026-09-29** — CloudFront (#76), at `SiteUrl` with the host on the origin-checking image:
  - The app loads, and refreshing deep links serves the SPA: `/`, `/library`, `/u/someone/friends` and
    `/u/john.doe` (a username with a dot) all return `index.html` with a 200.
  - An unknown `/api/...` path returns the API's own 404, not `index.html`.
  - `index.html` is served `Cache-Control: no-cache`; hashed bundles `max-age=31536000, immutable`.
  - SignalR over **WebSockets** through CloudFront: in a browser on the site, negotiate offered
    WebSockets, a `wss://` connection opened with the connection token, and the server answered the
    JSON protocol handshake with `{}`. The app's own hub client made no long-polling or SSE requests.
  - The host is unreachable directly: a direct request to port 80 times out (security group). On the
    host itself, requests without the origin header get 403 — `/health/ready` included, since through
    Docker's port mapping it does not arrive from loopback — while the in-container healthcheck gets
    200 and Docker reports the API `healthy`. Through CloudFront the same `/api` request gets through,
    including when the viewer sends its own `X-Origin-Verify` (overwritten by CloudFront). `/health`
    is not routed to the API at all: through CloudFront it is just another SPA path.
  - `/u/bob.js` and `/u/x.json` — usernames ending in a static-file extension — also serve the SPA.
- [x] **2026-09-29** — `deploy.sh` (#77) run for real on the host through SSM, before merge, with the
  tag already running (`8df159c`): artifacts fetched, `.env` rewritten (still 600/root, no `.env.new`
  left), images pulled, `up -d --wait` healthy, `last-healthy-tag` written. A no-op as intended — no
  container was recreated.
- [x] **2026-09-29** — Pipeline (#77): merging PR #92 (`b14c689`) deployed backend and frontend with no
  manual step. CI passed on `main`, Deploy started from it on its own, both jobs passed on the first
  run, and the host came up on `b14c689` with `last-healthy-tag` recorded.
- [x] **2026-09-29** — Pipeline (#77): a deliberately unhealthy image fails the workflow at the health
  check, and redeploying the last healthy tag restores service. The worker image, pushed as
  `marquee-api:unhealthy-test`, never answers HTTP. `gh workflow run deploy.yml -f tag=unhealthy-test`
  failed at the SSM step (`Error dependency api failed to start`), printed the last healthy tag and
  the rollback command, listed the API as `unhealthy`, printed no log lines, and skipped the frontend.
  `/api` through CloudFront returned 504 meanwhile. `-f tag=b14c689` then passed and the site and API
  returned 200. The test images and artifacts were deleted afterwards.
- [x] **2026-09-29** — Pipeline (#77): the repository holds no AWS credentials. No access-key-shaped
  string in any commit's tree, no secret-key assignments, no GitHub repository secrets; the only
  setting is the role ARN variable.
- [x] **2026-09-29** — Phase 1 gate (#78): a restored backup **starts**. The data-volume snapshot
  restored into a scratch volume, mounted read-write on the host, and a throwaway `postgres:16`
  (no network) started on it: it reported the unclean shutdown, replayed WAL in milliseconds and
  accepted connections. Its contents matched the live database row for row (10 Premieres, 1 user,
  10 movies, same last scheduled Premiere). Container, mount and volume removed afterwards.
- [x] **2026-09-29** — Phase 1 gate (#78): the rate limiter sees distinct client IPs, checked by
  behaviour rather than by logging IPs (an IP is personal data, and nothing else needs it logged).
  One IP exhausted `SessionIssue` (10 per 5 minutes: ten 200s, then 429s at 23:55:30 UTC); a phone on
  mobile data — another IP, same CloudFront — was issued a session at 23:57:25; the first IP was still
  refused at 23:58:04. **Read the result from the request log** (`/marquee/prod`, filter
  `sessions/anonymous`), not from the page: the SPA deliberately hides a refused session and renders
  normally without one, and it skips the call entirely when it already holds a session.
- [x] **2026-09-29** — Phase 1 gate (#78): the schedule runs in Toronto time. The day's four
  Premieres fell at 09:25, 14:56, 18:00 and 20:51 EDT — inside 07:00–23:00, gaps 5h31/3h04/2h51, all
  over the 2-hour minimum — and the 20:51 slot (`00:51Z` the next UTC day) is listed under the local
  day.
- [x] **2026-09-29** — Phase 1 gate (#78): k6 against the CloudFront URL (`capacity.js`), inside a
  deliberate window with `RateLimiting__Enabled=false` set in SSM and redeployed — every k6 request
  comes from one IP, so with limits on it would have measured the limiter. The parameter was deleted
  and redeployed afterwards, and 429s confirmed back (ten 200s, then 429). Results replace the estimate
  in `DEPLOYMENT.md`'s "Measured capacity".
- [x] **2026-09-29** — Phase 1 gate (#78): budget. Free plan credits **$159.58** remaining, plan ends
  **2027-03-24** (`aws freetier get-account-plan-state`). Cost Explorer cannot forecast yet (too little
  history), so the answer is a range: the credits outlast the plan (~5.8 months away) only at
  **≤ ~$27/month**. At the table's ~$25 they last ~6.4 months; at its ~$30, ~5.3 months — running
  out around early March. The first days' actual rate sits at the low end, but that is not yet
  proof: **recheck once Cost Explorer has a month of history** (it can then forecast).
- [x] **2026-09-29** — Phase 1 gate (#78): a real Premiere (20:51 EDT, threshold 48) clapped from
  several browsers through CloudFront — an admin browser, a scripted anonymous browser and a tester's
  phone. Counts moved live between them (21 on every screen without a refresh), over WebSockets: the
  scripted browser's only hub HTTP request was the negotiate, no long-polling or SSE.
- [x] **2026-09-29** — Phase 1 gate (#78): reboot mid-Premiere. **Reboot 1**, while Active at 21 of
  48: `uptime -s` confirmed a real reboot (00:53:39 UTC), the site was back in ~40 s, every container
  restarted on its own, and the Premiere came back Active with the same 21 claps, 3 contributors,
  `ExpiresAt` and day schedule (Redis AOF + Postgres). **Reboot 2**, with a fan-out message waiting:
  the worker was stopped, the Premiere clapped open (48 claps; `marquee-premiere-fanout` then held 1
  message, 0 consumers; 0 library entries), and the host rebooted (00:56:53 UTC). The message was
  still there afterwards — the worker, stopped by hand, stays stopped across a reboot, which is what
  made this checkable — and starting the worker delivered it: queue at 0, 1 library entry, the film in
  the admin's library.
  - Found along the way, filed as follow-ups rather than fixed here: a page reconnecting at the moment
    the reveal is broadcast never learns the Premiere opened (#95) — the reveal waits for the
    worker's fan-out (`PremiereRevealReady`), so it can arrive well after the open; the clap pips
    overflow the card at a large cap (#96 — one confirmed user makes the cap the whole threshold,
    48 here); and the Premiere card has no phone layout (#97).
  - After a reboot the API starts before RabbitMQ is ready (compose `depends_on` applies to `up`, not
    to Docker restarting containers at boot) and logs `BrokerUnreachable` for a few seconds until
    MassTransit's retry connects. Harmless — the outbox holds anything published meanwhile — but it
    looks alarming in the log.

## Phase 2: verification against the real pool

`cognito-local` stands in for Cognito everywhere but prod, so these are checked live and recorded here
with dates, as §1d was: sign-up → code email arrives → confirm → auto sign-in creates the row; wrong and
expired code; resend; sign-in while unconfirmed routes to code entry; password reset end to end; a
9-character and a digit-less password are refused, and an all-lowercase password with a digit is
accepted; an ID token presented to the API is refused; the seeded admin can sign in and is Admin; a role
change and a block take effect on the next request.

Recorded after the cutover deploy (#113, `06cac2e`), against pool `ca-central-1_4mv04X3ap` and the live
site. The checks marked *operator* were done by hand in the browser and reported back; the rest were
run from the command line.

- [x] **2026-10-07** — Deploy: CI and the Deploy workflow green on the merge commit; the new route
  `/api/auth/password-rules` answers 200 and the removed `/api/auth/login` answers 404 through
  CloudFront; `Seeded admin 'admin' as cc8d1508-70a1-7086-6506-ad593f2e499b` in `/marquee/prod`.
- [x] **2026-10-07** — The seeded admin signs in on the site. *(operator)*
- [x] **2026-10-07** — Sign-up with a real address → code email arrives → confirm → signed in
  automatically; a wrong code is refused; "Send a new code" works; signing in before confirming routes
  to code entry; password reset end to end. *(operator)*
- [x] **2026-10-07** — A 9-character and a digit-less password are refused; a lowercase-only password of
  at least 10 characters with a digit is accepted. *(operator)*
- [x] **2026-10-07** — A role change and a block take effect on the user's next request. *(operator)*
- [x] **2026-10-07** — Access token versus ID token, sent to `GET /api/auth/me` on the live site: the
  pool's access token (`token_use` `access`, `client_id` our app client) answers 200 with the account;
  the same sign-in's ID token (`token_use` `id`) answers 401; no token answers 401.
- [x] **2026-10-07** — `/marquee/prod/Jwt__Key` deleted from SSM once the above held; nothing reads it
  any more; the six parameters left under `/marquee/prod` are the ones listed under *SSM Parameter Store*.
- [x] **2026-10-15** — An **expired** confirmation code is refused: a sign-up code left unused for
  about 26 hours was entered on the live site and refused with "That code has expired. Send a new one."
  (the pool's `ExpiredCodeException`, not `CodeMismatchException`). *(operator)*

## 2a. User pool

Checked live 2026-10-04: `SignUp` with a 9-character password → `InvalidPasswordException` (not long
enough); with no digit → `InvalidPasswordException` (numeric characters); an all-lowercase password
with a digit accepted (via `AdminCreateUser` + `AdminSetUserPassword --permanent`, so no email was sent
— status `CONFIRMED`); `InitiateAuth` with `USER_PASSWORD_AUTH` signed in using the username in upper
case (case-insensitive) and returned a 24h token; test user deleted, pool left empty. Host role policy
read back from IAM with exactly the three actions on the pool ARN.

## 2c. Unconfirmed-user cleanup (#30)

- [x] **2026-10-14** — Deployed; the SNS subscription confirmed (status **Confirmed** in the console).
  *(operator)*
- [x] **2026-10-14** — Console tour matches the stack: the function's memory, timeout and environment;
  the role's two Cognito actions on the pool ARN; the schedule's cron, time zone and
  `{"dryRun": false}` input; the alarm's threshold and SNS action. *(operator)*
- [x] **2026-10-14** — Dry runs with `{"dryRun": true}` and with `{}`: both `dryRun: true`, one user
  scanned, nothing selected (the only unconfirmed user was under 7 days old). *(operator)*
- [x] **2026-10-14** — Alarm forced to `ALARM` (19:33 Toronto): the SNS action succeeded and the email
  arrived. *(operator)*
- [x] **2026-10-22** — Live run, by the **scheduled 04:00 run** (no manual run first): the
  unconfirmed `test_throw` (created 2026-10-14 20:54 UTC) was deleted. Its CloudWatch log line at
  08:00:30 UTC reads `{"dryRun":false,"scanned":1,"selected":[{"username":"test_throw",…}],"deleted":1,"failed":[]}`.
  Read back on 2026-10-25: the pool lists only `admin` and `j_david_ip`, both `CONFIRMED`, and
  the runs on 10-23, 10-24 and 10-25 each logged `scanned: 0, deleted: 0`.

## 2d. Metadata hop limit in the stack (#128)

Before deploying (2026-10-25) the live host already read hop limit `2`, `HttpTokens` `required`, from
the AMI's default, so the deploy changes no value; it makes the stack the source of it.

- [x] **2026-10-25** — `cdk deploy MarqueeStack` updated `Host` in place (`UPDATE_COMPLETE` in 4s; the
  Elastic IP association `HostIp` was re-applied in place too). The instance id is still
  `i-025489e6f2ed90173` and its launch time unchanged (no restart); `describe-instances` reports
  `HttpPutResponseHopLimit` `2` and `HttpTokens` `required`; the site's API answered `200` through
  CloudFront afterwards. *(operator)*

## 2e. Profile photos (#63)

The `Avatars` bucket, its CloudFront behaviour at `/avatars/*` and the host role's two S3 actions are in
`MarqueeStack`; `cdk diff` (2026-10-25, change set) showed only additions plus in-place updates to the
host role's policy and the distribution, no replacement. Deploy the stack **before** merging the code:
the workflow fails on a missing `AvatarsBucketName` output rather than deploy without it.

- [ ] `cdk deploy MarqueeStack`: the `Avatars` bucket exists with all public access blocked, and the
  stack has an `AvatarsBucketName` output. *(operator)*
- [ ] Upload through the site: the badge shows the photo, served from `<SiteUrl>/avatars/...` with
  `Cache-Control: public, max-age=86400`, and the object is a 640×640 WebP.
- [ ] Replace: the previous object is gone from the bucket (`aws s3 ls s3://<AvatarsBucketName>/avatars/<user id>/`).
- [ ] Remove: the monogram is back and the object is gone from the bucket.
- [ ] The bucket refuses a direct request: `https://<AvatarsBucketName>.s3.ca-central-1.amazonaws.com/avatars/...` returns 403.
