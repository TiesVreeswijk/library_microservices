#!/usr/bin/env bash
# Deploys ONE service on the EduCloud VM. The other services keep running untouched.
# Called by .github/workflows/build-and-deploy.yml on the self-hosted runner, from the repo root.
#
#   deploy/deploy-service.sh <compose service> <tag variable> <image tag>
#   deploy/deploy-service.sh book-service BOOK_SERVICE_TAG 3f2a91c...
set -euo pipefail

service="$1"
tag_var="$2"
tag="$3"
deploy_dir="${DEPLOY_DIR:-/opt/library}"

if [ ! -f "$deploy_dir/.env" ]; then
  echo "No $deploy_dir/.env found. Create it first (see docs/03-deployment.md)." >&2
  exit 1
fi

# Copy the latest compose file and Postgres init script. The .env with the secrets stays on the VM.
cp docker-compose.prod.yml "$deploy_dir/"
mkdir -p "$deploy_dir/db"
cp -r db/init "$deploy_dir/db/"
cd "$deploy_dir"

# Write down which version of this service runs now, so the next deploy of
# another service doesn't fall back to an older version of this one.
if grep -q "^${tag_var}=" .env; then
  sed -i "s/^${tag_var}=.*/${tag_var}=${tag}/" .env
else
  echo "${tag_var}=${tag}" >> .env
fi

compose="docker compose -f docker-compose.prod.yml"
# Start Postgres if it isn't running yet (does nothing when it already runs),
# and wait until it is healthy so the service can apply its migrations.
$compose up -d --wait postgres
# Pull and restart only this service. --no-deps: don't touch any other container.
$compose pull "$service"
$compose up -d --no-deps "$service"
$compose ps
