#!/usr/bin/env bash
# Active health-check for the cloudflared tunnel container.
#
# "restart: unless-stopped" in docker-compose.yaml only catches the container process dying — it
# does nothing if cloudflared stays "Up" while its actual connection to Cloudflare's edge (or to
# the "web" upstream) has silently died, which is exactly the failure mode behind the 2026-07-07
# 502 incidents (see docs/prod_server.md). This checks the real end-to-end path — the public URL,
# same as an actual visitor — and restarts the container only when that's genuinely broken, not on
# a single transient blip.
#
# Not part of the Aspire-generated docker-compose.yaml (that gets overwritten by `aspire publish`)
# — installed once via systemd, see ops/systemd/ and docs/runbook.md.
set -euo pipefail

URL="https://gatekeeper.cerberuslab.dev/login"
TIMEOUT_SECONDS=10
RETRY_DELAY_SECONDS=5

check() {
  curl -fsS --max-time "$TIMEOUT_SECONDS" -o /dev/null "$URL"
}

if check; then
  exit 0
fi

# One retry before acting — a single edge/DNS hiccup isn't worth restarting a working tunnel.
sleep "$RETRY_DELAY_SECONDS"
if check; then
  exit 0
fi

logger -t gatekeeper-cloudflared-check "Site unreachable via tunnel after retry — restarting cloudflared container"
docker restart cloudflared
