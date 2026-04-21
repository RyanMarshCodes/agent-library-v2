#!/usr/bin/env bash
set -euo pipefail

compose_dir="infra/mcp"
out_dir="backups/postgres/$(date -u +%Y%m%d-%H%M%SZ)"
databases=("bifrost" "ryan_mcp")

usage() {
  cat <<'EOF'
Export local compose Postgres databases to custom-format dumps.

Usage:
  ./scripts/export-postgres.sh [--compose-dir PATH] [--out-dir PATH]

Defaults:
  --compose-dir infra/mcp
  --out-dir backups/postgres/<utc-timestamp>

Notes:
  - Exports databases: bifrost, ryan_mcp
  - Reads local Postgres admin credentials from infra/mcp/.env:
    LOCAL_POSTGRES_ADMIN_USER, LOCAL_POSTGRES_ADMIN_PASSWORD
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --compose-dir)
      compose_dir="$2"
      shift 2
      ;;
    --out-dir)
      out_dir="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ ! -f "$compose_dir/docker-compose.yml" ]]; then
  echo "docker-compose.yml not found in: $compose_dir" >&2
  exit 1
fi

if [[ ! -f "$compose_dir/.env" ]]; then
  echo ".env not found in: $compose_dir" >&2
  echo "Copy .env.example to .env first." >&2
  exit 1
fi

# shellcheck source=/dev/null
set -a
source "$compose_dir/.env"
set +a

admin_user="${LOCAL_POSTGRES_ADMIN_USER:-postgres}"
admin_password="${LOCAL_POSTGRES_ADMIN_PASSWORD:-postgres}"

mkdir -p "$out_dir"

echo "Exporting databases to: $out_dir"
for db in "${databases[@]}"; do
  out_file="$out_dir/${db}.dump"
  echo " - exporting $db -> $out_file"
  docker compose -f "$compose_dir/docker-compose.yml" exec -T postgres \
    sh -lc "PGPASSWORD='$admin_password' pg_dump -U '$admin_user' -d '$db' -Fc" \
    > "$out_file"
done

echo "Export complete."
