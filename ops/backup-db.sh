#!/usr/bin/env bash
# Nightly backup of the whole Postgres cluster: Catalog DB, every tenant_* DB, and roles.
#
# pg_dumpall (not per-database pg_dump) so a tenant provisioned later is picked up automatically,
# with no list of databases to keep in sync here. The whole cluster is ~25 MB and compresses to well
# under 1 MB, so a full dump every night costs nothing.
#
# Written to a different physical disk than the one Postgres lives on (the DB volume is on the
# system SATA SSD; BACKUP_DIR defaults to one of the NVMe data disks), and — once RCLONE_REMOTE is
# set — copied off the server to Cloudflare R2, so losing the whole box doesn't lose the data.
#
# Restore with ops/restore-db.sh (CI runs backup → restore → row-count comparison on every push,
# so this pair is known to round-trip). Installed via systemd, see ops/systemd/ and docs/runbook.md.
set -euo pipefail

PG_CONTAINER="${PG_CONTAINER:-gatekeeper-deploy-postgres-1}"
BACKUP_DIR="${BACKUP_DIR:-/mnt/nvme0n1p1/gatekeeper-backups}"
KEEP_DAYS="${KEEP_DAYS:-30}"
# e.g. "r2:gatekeeper-backups" — empty means local-only.
RCLONE_REMOTE="${RCLONE_REMOTE:-}"
RCLONE_BIN="${RCLONE_BIN:-$HOME/bin/rclone}"
REMOTE_KEEP_DAYS="${REMOTE_KEEP_DAYS:-90}"

mkdir -p "$BACKUP_DIR"
# Leftovers from a run killed mid-dump (e.g. one of this server's hardware resets).
find "$BACKUP_DIR" -maxdepth 1 -name 'gatekeeper-*.sql.zst.partial' -delete

out="$BACKUP_DIR/gatekeeper-$(date -u +%Y%m%dT%H%M%SZ).sql.zst"
tmp="$out.partial"

# The password comes from the container's own env, so it never appears on this host's command line.
docker exec "$PG_CONTAINER" sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" pg_dumpall -h localhost -U postgres' \
  | zstd -q -19 -o "$tmp"

# pg_dumpall writes this trailer only after every database was dumped successfully — a truncated
# dump (container restarted mid-run, disk full) fails here instead of silently becoming "latest".
zstd -t -q "$tmp"
trailer="$(zstd -dc "$tmp" | tail -n 5)"
if [[ "$trailer" != *"PostgreSQL database cluster dump complete"* ]]; then
  echo "Dump is incomplete (no completion trailer): $tmp" >&2
  exit 1
fi
mv "$tmp" "$out"
echo "Backup written: $out ($(stat -c %s "$out" 2>/dev/null || stat -f %z "$out") bytes)"

find "$BACKUP_DIR" -maxdepth 1 -name 'gatekeeper-*.sql.zst' -mtime +"$KEEP_DAYS" -delete

if [[ -n "$RCLONE_REMOTE" ]]; then
  "$RCLONE_BIN" copyto "$out" "$RCLONE_REMOTE/$(basename "$out")"
  "$RCLONE_BIN" delete --min-age "${REMOTE_KEEP_DAYS}d" --include 'gatekeeper-*.sql.zst' "$RCLONE_REMOTE"
  echo "Uploaded to $RCLONE_REMOTE"
fi
