# Unified MCP + Bifrost Runtime

This folder runs a unified runtime stack for:

1. `mcp-server` (custom .NET MCP server)
2. `bifrost` (MCP gateway)
3. Optional local `postgres` + `redis` via `--profile local`

It is designed for one `.env` file and two modes:

1. Default mode: connects to remote managed services (for example Azure Postgres/Redis)
2. Local mode: starts local Postgres/Redis containers for testing

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

2. Edit `.env` values.

## Run modes

### A) Remote services mode (default)

Use this when Postgres/Redis are managed externally (for example Azure):

```powershell
docker compose up -d --build
```

### B) Local testing mode

Use this before remote resources are ready:

1. Set these `.env` values for local hosts:
	1. `MCP_DB_HOST=postgres`
	2. `BIFROST_DB_HOST=postgres`
	3. `BIFROST_REDIS_ADDR=redis:6379`
	4. `MCP_DB_SSLMODE=Disable`
	5. `BIFROST_DB_SSLMODE=disable`

2. Start with local profile:

```powershell
docker compose --profile local up -d --build
```

## Health checks

1. MCP health: `http://localhost:${MCP_PORT}/health`
2. Bifrost health: `http://localhost:${BIFROST_PORT}/health`

## Notes

1. Aspire is still available for developer workflows; this compose stack is a runtime/deployment path.
2. Keep one canonical `.env` and switch values by mode.
3. For production, use strong secrets and TLS-required settings for managed databases.
