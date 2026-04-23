# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

An AI agentic platform built around the Model Context Protocol (MCP). The stack provides 20+ tool suites to AI agents, routes requests through a governance gateway (Bifrost), persists agent memory in PostgreSQL, and manages a file-based library of agent definitions, workflows, and knowledge documents.

Primary consumers: Claude Code, GitHub Copilot, OpenCode — all connecting via MCP.

## Build & Run

### MCP Server (.NET 10)

```bash
cd apps/mcp-server
dotnet build -nologo -v:minimal
```

### Local Dev (Aspire)

```bash
aspire stop
aspire start
# Dashboard auto-opens; MCP endpoint: http://localhost:8081
```

### Docker Compose (prod-like)

```bash
# Copy and edit .env first
docker compose up -d --build
```

### Validate Agent Library

```bash
node scripts/validate-agent-contract.mjs
```

### Database Backup / Restore

```bash
./scripts/export-postgres.sh
./scripts/import-postgres.sh --backup-dir <dir> --yes
```

## Architecture

```
External clients (Claude Code, Copilot, OpenCode)
        │  MCP
        ▼
  Bifrost Gateway  (:8080) — governance, virtual keys, tool allowlist
        │
        ▼
  MCP Server (:8081, .NET 10) — 20 tool suites, prompts, resources
        │
  ┌─────┴──────┐
  ▼            ▼
PostgreSQL   Redis
(memory,     (vector store,
 mappings,    Bifrost cache)
 workflow
 state)
```

**MCP Server** (`apps/mcp-server/src/Ryan.MCP.Mcp`) is the core service. On startup it:
1. Runs SQL migrations (`MemoryMigrationRunner`)
2. Ingests all files under `library/` into the knowledge store (`DocumentIngestionCoordinator`, `AgentIngestionCoordinator`)
3. Syncs agent → LLM model mappings from frontmatter (`ModelMappingSyncService`)

**Aspire AppHost** (`apps/mcp-server/src/Ryan.MCP.AppHost/AppHost.cs`) composes the MCP server, optional sidecar containers (Sequential Thinking, Azure MCP, Docker MCP, etc.), and mounts `library/` as a read-only volume for hot-reload ingestion.

**Library** (`library/`) is the version-controlled knowledge base — not generated, not a build artifact. Changes here are auto-ingested on next startup (or via `WatchForChanges`). Structure:
- `agents/*.agent.md` — 50+ agent definitions with YAML frontmatter (`model`, `temperature`, `tags`)
- `workflows/*.workflow.md` — Delivery phase sequences with gate rules
- `skills/**/SKILL.md` — Reusable agent skill templates
- `guardrails/` — Security and governance policies
- `knowledge/` — Documentation for global, backend, and frontend domains

## Key Files

| File | Purpose |
|------|---------|
| `AGENTS.md` | Agentic delivery contract — read this for workflow gates, tool policy, handoff schema |
| `apps/mcp-server/src/Ryan.MCP.Mcp/Program.cs` | DI registration, OpenTelemetry, MCP protocol setup |
| `apps/mcp-server/src/Ryan.MCP.AppHost/AppHost.cs` | Service composition (ports, volumes, sidecars) |
| `infra/bifrost/bifrost.config.json` | Bifrost routing, virtual keys, governance rules |
| `infra/mcp/docker-compose.yml` | Primary compose for the runtime stack |
| `.env` | Single config file — ports, DB creds, feature toggles, project slug |
| `infra/mcp/initdb/01-create-databases.sql` | DB init (creates `ryan_mcp` and `bifrost` databases) |

## MCP Tool Tiers (from AGENTS.md)

- **Tier A (read)** — auto-allowed
- **Tier B (mutate)** — requires explicit approval token
- **Tier C (execute)** — requires approval token + rollback intent declared

Destructive ops, secret exfiltration, and cross-boundary actions are blocked by default.

## Feature Delivery Workflow

```
/context → /spec → /validate → /architect → /validate
→ /implement → /test → /reflect → /review → /commit → /wrapup
```

Do not advance when a gate fails. Every phase must produce artifacts. Production-impacting changes require a rollback plan.

## Configuration

All runtime config flows from the root `.env`. Key toggles:
- `McpOptions__Ingestion__AutoIngestOnStartup` — re-ingest library on startup
- `McpOptions__Ingestion__WatchForChanges` — hot-reload library changes
- `PROJECT_SLUG` — namespaces all containers and DBs (default: `ryan-mcp`)
- `LOG_STYLE` — `json` or `console`

Aspire-specific overrides live in `apps/mcp-server/src/Ryan.MCP.AppHost/appsettings.json`.

## CI/CD

Six GitHub Actions workflows:
- `ci-mcp.yml` — .NET build + tests on MCP server changes
- `ci-library.yml` — Agent/workflow contract validation on library changes
- `guardrails-check.yml` — Security policy validation
- `deploy-mcp.yml`, `deploy-stack.yml`, `deploy-litellm.yml` — VPS deployment

## Evidence Standard (for architecture/security decisions)

Include: file or artifact references, validation evidence, assumptions, residual risks.
