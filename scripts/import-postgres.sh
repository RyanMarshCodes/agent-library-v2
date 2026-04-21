#!/usr/bin/env bash
set -euo pipefail

compose_dir="infra/mcp"
backup_dir=""
databases=("bifrost" "ryan_mcp")

usage() {
  cat <<'EOF'
Import custom-format dumps into local compose Postgres databases.

Usage:
  ./scripts/import-postgres.sh --backup-dir PATH [--compose-dir PATH] [--yes]

Required:
  --backup-dir PATH   Folder containing bifrost.dump and ryan_mcp.dump

Defaults:
  --compose-dir infra/mcp

Safety:
  - This drops and recreates target databases before restore.
  - Pass --yes to confirm destructive behavior.
EOF
}

confirm="false"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --compose-dir)
      compose_dir="$2"
      shift 2
      ;;
    --backup-dir)
      backup_dir="$2"
      shift 2
      ;;
    --yes)
      confirm="true"
      shift
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

if [[ -z "$backup_dir" ]]; then
  echo "--backup-dir is required" >&2
  usage
  exit 1
fi

if [[ "$confirm" != "true" ]]; then
  echo "Refusing to run without --yes (this operation drops databases)." >&2
  exit 1
fi

if [[ ! -f "$compose_dir/docker-compose.yml" ]]; then
  echo "docker-compose.yml not found in: $compose_dir" >&2
  exit 1
fi

if [[ ! -f "$compose_dir/.env" ]]; then
  echo ".env not found in: $compose_dir" >&2
  echo "Copy .env.example to .env first." >&2
  exit 1
fi

if [[ ! -d "$backup_dir" ]]; then
  echo "Backup directory not found: $backup_dir" >&2
  exit 1
fi

for db in "${databases[@]}"; do
  if [[ ! -f "$backup_dir/${db}.dump" ]]; then
    echo "Missing dump file: $backup_dir/${db}.dump" >&2
    exit 1
  fi
done

# shellcheck source=/dev/null
set -a
source "$compose_dir/.env"
set +a

admin_user="${LOCAL_POSTGRES_ADMIN_USER:-postgres}"
admin_password="${LOCAL_POSTGRES_ADMIN_PASSWORD:-postgres}"

echo "Importing dumps from: $backup_dir"
for db in "${databases[@]}"; do
  dump_file="$backup_dir/${db}.dump"
  echo " - restoring $db from $dump_file"

  docker compose -f "$compose_dir/docker-compose.yml" exec -T postgres sh -lc \
    "PGPASSWORD='$admin_password' psql -U '$admin_user' -d postgres -v ON_ERROR_STOP=1 -c \"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$db' AND pid <> pg_backend_pid();\" -c \"DROP DATABASE IF EXISTS \"\"$db\"\";\" -c \"CREATE DATABASE \"\"$db\"\";\""

  cat "$dump_file" | docker compose -f "$compose_dir/docker-compose.yml" exec -T postgres sh -lc \
    "PGPASSWORD='$admin_password' pg_restore -U '$admin_user' -d '$db' --no-owner --no-privileges --clean --if-exists"
done

echo "Import complete."
