#!/usr/bin/env bash
# Restores a dump made by ops/backup-db.sh into a Postgres container.
#
# Usage: restore-db.sh <gatekeeper-*.sql.zst> [container]
#
# The target must be an EMPTY cluster (fresh container / fresh data volume): pg_dumpall output
# CREATEs every database and fails on ones that already exist. The only error that is expected and
# ignored is the "postgres" role already existing — every fresh cluster has it. Anything else fails
# the restore, so a half-restored database is never mistaken for a good one.
set -euo pipefail

dump="${1:?usage: restore-db.sh <dump.sql.zst> [container]}"
PG_CONTAINER="${2:-${PG_CONTAINER:-gatekeeper-deploy-postgres-1}}"

log="$(mktemp)"
trap 'rm -f "$log"' EXIT

zstd -dc "$dump" \
  | docker exec -i "$PG_CONTAINER" sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -h localhost -U postgres -d postgres -q' \
  > /dev/null 2> "$log"

unexpected="$(grep 'ERROR:' "$log" | grep -v 'role "postgres" already exists' || true)"
if [[ -n "$unexpected" ]]; then
  echo "Restore finished with errors:" >&2
  echo "$unexpected" >&2
  exit 1
fi
echo "Restored $dump into $PG_CONTAINER"
