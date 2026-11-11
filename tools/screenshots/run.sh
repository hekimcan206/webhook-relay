#!/bin/sh
# Captures the README screenshot from a running `docker compose up` stack, inside the pinned Playwright image,
# attached to the compose network so it can reach the relay and register a deliberately broken endpoint.
# Usage (repo root, after the demo finished): sh tools/screenshots/run.sh
set -eu
export MSYS_NO_PATHCONV=1
docker run --rm --network webhook-relay_default \
  -e RELAY_URL=http://relay:8080/ \
  -v "$(pwd)/tools/screenshots:/work" -v "$(pwd)/docs/images:/out" -w /work \
  mcr.microsoft.com/playwright:v1.64.1-noble \
  sh -c "npm install --no-audit --no-fund --silent && node capture.mjs"
