#!/usr/bin/env bash
# Runs ON THE HOST, through SSM Run Command, from .github/workflows/deploy.yml (DEPLOYMENT.md §1c).
#
#   deploy.sh <tag> <registry> <artifacts-bucket> <cognito-issuer> <cognito-client-id>
#
# Points the stack at images <registry>/marquee-{api,worker}:<tag> and waits for them to go healthy.
# Exits non-zero if they do not, so the workflow fails at the health check. The last tag that did go
# healthy is kept in last-healthy-tag: rolling back is re-running the workflow with that tag.
set -euo pipefail

TAG=$1 REGISTRY=$2 BUCKET=$3 COGNITO_ISSUER=$4 COGNITO_CLIENT_ID=$5
REGION=ca-central-1
COMPOSE="docker compose -f docker-compose.prod.yml"

cd /opt/marquee
PREVIOUS=$(cat last-healthy-tag 2>/dev/null || echo none)
echo "deploying $TAG (last healthy: $PREVIOUS)"

aws s3 cp "s3://$BUCKET/$TAG/docker-compose.prod.yml" docker-compose.prod.yml --only-show-errors

# Secrets come from SSM every time, so a rotated value needs only a redeploy. Written to a temp file
# and moved, so a failure halfway never leaves a truncated .env behind.
(
  umask 077
  aws ssm get-parameters-by-path --region "$REGION" --path /marquee/prod --with-decryption \
    --query 'Parameters[].[Name,Value]' --output text \
    | while IFS=$'\t' read -r name value; do echo "$(basename "$name")=$value"; done > .env.new
  echo "REGISTRY=$REGISTRY" >> .env.new
  echo "IMAGE_TAG=$TAG" >> .env.new
  echo "COGNITO_ISSUER=$COGNITO_ISSUER" >> .env.new
  echo "COGNITO_CLIENT_ID=$COGNITO_CLIENT_ID" >> .env.new
)
mv .env.new .env

aws ecr get-login-password --region "$REGION" | docker login --username AWS --password-stdin "$REGISTRY" >/dev/null 2>&1

# Only the app images: pulling the infrastructure ones could move Postgres to a new minor version, and
# `up` would then recreate its container mid-deploy. `up` still fetches them on a brand-new host.
$COMPOSE pull -q api worker

# --wait returns non-zero if a container exits or its healthcheck fails; the API's start_period
# covers its EF migrations, and --wait-timeout bounds a container that never answers at all.
if ! $COMPOSE up -d --wait --wait-timeout 240; then
  echo "::: $TAG did not go healthy. Last healthy tag: $PREVIOUS" >&2
  echo "::: Roll back by re-running the Deploy workflow with tag=$PREVIOUS" >&2
  # States only, never log lines: this output lands in the public Actions log. The containers' logs
  # are in CloudWatch, log group /marquee/prod.
  $COMPOSE ps --format '{{.Service}} {{.Status}}' >&2 || true
  echo "::: container logs: CloudWatch log group /marquee/prod" >&2
  exit 1
fi

echo "$TAG" > last-healthy-tag
# Unused images older than a week: the running ones and anything recent are untouched, and a rollback
# pulls what it needs from ECR. Housekeeping only — it must never turn a healthy deploy into a failed one.
docker image prune -af --filter "until=168h" >/dev/null || true
echo "healthy: $TAG"
