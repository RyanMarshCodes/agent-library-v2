# Unified MCP + Bifrost Runtime

This folder runs a unified runtime stack for:

1. `mcp-server` (custom .NET MCP server)
2. `bifrost` (MCP gateway)
3. Optional local `postgres` + `redis` via `--profile local`

It is designed for one `.env` file and two modes:

1. Default mode: local-first (dockerized Postgres + Redis)
2. Remote mode: override env values for managed services and optionally start only app services

## Files

1. `docker-compose.yml`: unified runtime stack
2. `.env.example`: single environment contract
3. `bifrost.config.json`: Bifrost config using `env.*` references
4. `initdb/01-create-databases.sql`: local profile bootstrap SQL

## Quick start

1. Copy env template:

```powershell
Copy-Item .env.example .env
```

1. Edit `.env` values.

## Run modes

### A) Local-first mode (default)

Start everything (MCP, Bifrost, Postgres, Redis):

```powershell
docker compose up -d --build
```

### B) Remote managed services mode

Use this when Postgres/Redis are managed externally (for example Azure):

1. Override these `.env` values for managed hosts/credentials:
1. `MCP_DB_HOST`, `MCP_DB_USER`, `MCP_DB_PASSWORD`, `MCP_DB_SSLMODE`
1. `BIFROST_DB_HOST`, `BIFROST_DB_USER`, `BIFROST_DB_PASSWORD`, `BIFROST_DB_SSLMODE`
1. `BIFROST_REDIS_ADDR`

1. Start app services only (skip local Postgres/Redis containers):

```powershell
docker compose up -d --build mcp-server bifrost
```

## Health checks

1. MCP health: `http://localhost:${MCP_PORT}/health`
2. Bifrost health: `http://localhost:${BIFROST_PORT}/health`

## Notes

1. Aspire is still available for developer workflows; this compose stack is a runtime/deployment path.
2. Keep one canonical `.env` and switch values by mode.
3. For production, use strong secrets and TLS-required settings for managed databases.
4. For Postgres migration/copy between environments, prefer `pg_dump`/`pg_restore` over copying DB files.

## Postgres export and import scripts (Ubuntu/Linux)

From repo root:

```bash
chmod +x scripts/export-postgres.sh scripts/import-postgres.sh
```

Export local compose Postgres DBs (`bifrost`, `ryan_mcp`):

```bash
./scripts/export-postgres.sh
```

Import from a backup folder (destructive restore):

```bash
./scripts/import-postgres.sh --backup-dir backups/postgres/<timestamp> --yes
```
