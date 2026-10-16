# Runbook

How to operate the deployed stack by hand: deploy infrastructure, deploy the app without the
pipeline, roll back, and the checks to repeat after certain changes. Moved here from
`DEPLOYMENT.md` (#145), which describes *what* is deployed; this file is *how to operate it*.

## CDK: credentials, diff and deploy

Running it, signed in with `aws login` and with `aws configure set region ca-central-1` done once, from
`infra/` in Git Bash:

```
eval "$(aws configure export-credentials --format env)"   # per shell; see below
cdk bootstrap aws://<account-id>/ca-central-1              # once per account and region
cdk deploy MarqueeCiStack -c alertEmail=<address>
cdk deploy MarqueeStack -c alertEmail=<address>
```

The CDK CLI cannot yet read the `login_session` credentials `aws login` stores, so the first line hands
it short-lived credentials through environment variables for that shell. In PowerShell the equivalent
is `aws configure export-credentials --format powershell | Out-String | Invoke-Expression`. Claude
Code's `!` prompt cannot answer `cdk deploy`'s approval question: review `cdk diff` first, then pass
`--require-approval never`.

The other two stacks deploy the same way. Deploy `MarqueeAuthStack` before `MarqueeStack`, and
`MarqueeCleanupStack` from the same commit as `MarqueeAuthStack` (`DEPLOYMENT.md` §2a, §2c):

```
cdk deploy MarqueeAuthStack -c alertEmail=<address>
cdk deploy MarqueeCleanupStack -c alertEmail=<address>
```

Review first with `cdk diff <stack> -c alertEmail=<address>` (or every stack at once with no stack
name), which needs the same `eval` line in the same shell. A clean diff reports "There were no
differences" per stack.

## Confirm an alarm subscription

`MarqueeStack` and `MarqueeCleanupStack` each email `alertEmail` through their own SNS topic.

1. After any deploy that creates a subscription (a new stack, or a different `alertEmail`), open the
   "AWS Notification - Subscription Confirmation" email and click **Confirm subscription** within
   **3 days**. SNS silently deletes an unconfirmed subscription, and CloudFormation keeps reporting
   it as created, so the alarms email nobody (#133).
2. Check it: **SNS → Topics → the stack's topic → Subscriptions**, status **Confirmed**.
3. To find a subscription that was lost: run **Detect drift** on the stack in the CloudFormation
   console — the topic reports `MODIFIED` on `/Subscription`. Redeploy the stack to recreate it, then
   confirm again.

## Manual deploy (fallback)

The pipeline (`DEPLOYMENT.md` §1c) is the normal path. This is what it automates, for when GitHub Actions is
unavailable or a step needs doing by hand. From `infra/` with the CLI signed in:

```
aws cloudformation describe-stacks --stack-name MarqueeStack --query 'Stacks[0].Outputs'
```

gives `HostInstanceId`, `HostPublicDns`, `ArtifactsBucket`, `SiteUrl`, `SiteBucket` and
`DistributionId`. Then, from the repository root:

```
TAG=$(git rev-parse --short HEAD)
REGISTRY=<account-id>.dkr.ecr.ca-central-1.amazonaws.com   # aws sts get-caller-identity for the id
aws ecr get-login-password --region ca-central-1 | docker login --username AWS --password-stdin $REGISTRY
docker build --target api -t $REGISTRY/marquee-api:$TAG . && docker push $REGISTRY/marquee-api:$TAG
docker build --target worker -t $REGISTRY/marquee-worker:$TAG . && docker push $REGISTRY/marquee-worker:$TAG
aws s3 cp docker-compose.prod.yml s3://<ArtifactsBucket>/$TAG/docker-compose.prod.yml
```

Then on the host — no SSH, everything through SSM Run Command (`AWS-RunShellScript`) or, for a shell,
`aws ssm start-session --target <HostInstanceId>` (needs the Session Manager plugin locally):

```bash
TAG=<the tag from above>
REGISTRY=<the same registry host>
mkdir -p /opt/marquee && cd /opt/marquee
aws s3 cp s3://<ArtifactsBucket>/$TAG/docker-compose.prod.yml .
umask 077
aws ssm get-parameters-by-path --path /marquee/prod --with-decryption --query 'Parameters[].[Name,Value]' \
  --output text | while IFS=$'\t' read -r name value; do echo "$(basename "$name")=$value"; done > .env
echo "REGISTRY=$REGISTRY" >> .env
echo "IMAGE_TAG=$TAG" >> .env
echo "COGNITO_ISSUER=<MarqueeAuthStack Issuer>" >> .env
echo "COGNITO_CLIENT_ID=<MarqueeAuthStack UserPoolClientId>" >> .env
chmod 600 .env
aws ecr get-login-password --region ca-central-1 | docker login --username AWS --password-stdin $REGISTRY
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml up -d --wait
docker inspect --format '{{.State.Health.Status}}' marquee-prod-api-1
```

The `.env` file is written `600`, owned by root; nothing in it is ever echoed back through SSM output.
`curl http://localhost/...` on the host returns 403 for every path, `/health` included: it lacks the
origin header, and through Docker's port mapping it does not arrive from loopback inside the container.
That is the check working — read health from Docker (`docker inspect` above), which runs the
container's own in-container healthcheck.

Then the frontend, from `src/Marquee.Web` locally. Only `index.html` and `favicon.ico` are unhashed;
everything else is content-hashed and can be cached for good:

```
npx ng build --configuration production
cd dist/marquee-web/browser
aws s3 sync . s3://<SiteBucket> --delete --exclude index.html --exclude favicon.ico \
  --cache-control "public,max-age=31536000,immutable"
aws s3 cp index.html s3://<SiteBucket>/index.html --cache-control "no-cache"
aws s3 cp favicon.ico s3://<SiteBucket>/favicon.ico --cache-control "public,max-age=86400"
aws cloudfront create-invalidation --distribution-id <DistributionId> --paths "/index.html"
```

From Git Bash on Windows, prefix the invalidation with `MSYS_NO_PATHCONV=1`, or `/index.html` is
rewritten into a Windows path and rejected as invalid.

**Rotating the origin secret.** CloudFront and the host each hold a copy, updated separately, so every
`/api` and `/hubs` request gets 403 between the two steps — minutes, not seconds, because the
distribution change takes a few minutes to propagate. Do it when nobody is on:

1. `aws ssm put-parameter --name /marquee/prod/OriginVerify__Secret --type String --overwrite --value "$(openssl rand -hex 32)"`
2. `cdk deploy MarqueeStack` — CloudFront starts sending the new value.
3. Redeploy the running tag so the host rewrites `.env` from SSM: `gh workflow run deploy.yml -f
   tag=<running tag>` (the host's `/opt/marquee/last-healthy-tag`).

## Rollback

**Rollback** is the same workflow run by hand with an existing tag — backend only, no build:

```
gh workflow run deploy.yml -f tag=<last healthy tag>
```

The tag is validated against `[A-Za-z0-9._-]` before it reaches the host's shell. The frontend is left
alone: a backend failure stops before it, so it still matches the last healthy tag. Single instance,
so a failed deploy means downtime until the rollback finishes — accepted for phase 1.

A tag stays deployable only while its pieces exist: ECR keeps the **last 10 images** per repository and
the artifacts bucket expires objects after **30 days**, so roll back promptly — the last healthy tag
is normally the previous deploy, well inside both. Tags from before the pipeline (no `deploy.sh` in
the bucket) cannot be redeployed this way; use the manual deploy above.

**The phase 2 cutover (#113) cannot be rolled back this way.** Its migration (`RemovePasswordAuth`) drops
`users.PasswordHash`, which every earlier image still requires, and those images also need the old
`Jwt__Key`. A failed cutover is undone by restoring the data volume from a snapshot taken immediately
before the cutover deploy (the daily 08:00 UTC one can be a day old) and then redeploying the old tag.
Rolling back between tags built after the cutover works as above.

## After an AMI change or instance replacement

The API container reaches the instance role through IMDSv2 only while the metadata hop limit is 2
(`DEPLOYMENT.md` §2a). The pinned AMI supplies that value, not the stack, so check it after any AMI
change or instance replacement:

```
aws ec2 describe-instances --instance-ids <HostInstanceId> --query 'Reservations[0].Instances[0].MetadataOptions.HttpPutResponseHopLimit'
```

It must print `2`. At `1`, the admin seeder fails (logged in `/marquee/prod`) and the API starts
with no admin.
