# Knowledge Library — Plan

**Repo**: `knowledge-library` (separate from `ai-stack`)  
**Status**: Draft — decisions mostly locked, ready for Phase 0  
**Date**: 2026-04-22  
**Open questions**: See inline `> ❓` blocks.

> **Note**: This plan file lives temporarily in `ai-stack/docs/` during planning. It moves to the `knowledge-library` repo root when that repo is created.

---

## Vision

A **standalone, general-purpose, AI-maintained knowledge base** in its own repo (`knowledge-library`), separate from the `ai-stack` MCP platform. A **Librarian agent** orchestrates all ingestion, categorization, and maintenance — delegating to subagents for specialized processing tasks. The system exposes hybrid full-text + semantic search via OpenSearch and a rich MCP tool surface. Human oversight is lightweight: uncertain items land in a markdown review queue for user approval via an MCP tool.

The `knowledge-library` repo is hosted locally and is the source of truth for `raw/` and `knowledge/`. The Librarian runs against that local filesystem, then publishes/searches through MCP tools exposed by the Bifrost gateway. Runtime paths/endpoints are configuration-driven from `librarian-config.json` and validated by `librarian-config.schema.json` (no hardcoded machine-specific paths).

Human-readable wiki interface (Notion, GitHub Wiki, Obsidian, etc.) is **deferred** — the file-based knowledge store and vector search are the foundation; the viewer layer can be added later without architectural changes.

---

## Goals

1. Drop any readable file into `raw/` → Librarian categorizes, writes knowledge pages, indexes to Elasticsearch
2. Librarian delegates specialized processing (PDF extraction, image OCR, URL clipping) to subagents
3. Hybrid-RAG search (BM25 + dense vector kNN) via Elasticsearch
4. Uncertain items staged in `knowledge/_review/` for user approval before publication
5. Librarian runs on a Claude Routine/schedule and can scan configured source paths automatically
6. Full MCP tool surface: store, search, retrieve, approve, lint, status

---

## Architecture Overview

```
knowledge-library repo (local machine)           MCP endpoint (remote-first)
──────────────────────────────────────           ───────────────────────────
[User drops files — any format]                 [Bifrost MCP Gateway]
raw/pending/ (gitignored)                       https://llm.ryanmarsh.net/mcp
librarian-config.json                           (fallback: local MCP server)

        │  on-demand / scheduled run                        ▲
        ▼                                                   │ MCP tools
[Librarian Agent]  ← Claude Sonnet                          │
agents/librarian.agent.md                                   │
        │                                                   │
        ├──► [Format Subagents]                             │
        │    PDF, OCR, URL, DOCX                            │
        │    (Haiku where sufficient)                       │
        │                                                   │
        │ approved          review                          │
        ▼                   ▼                               │
[knowledge/]        [knowledge/_review/] ───────────────────┘
  local files            local files

Local repo remains canonical for file content. Remote MCP tools are the integration surface for indexing, retrieval, approvals, status, and query workflows.
```

### Repo structure (`knowledge-library`)

```
knowledge-library/
  agents/                  ← Librarian + subagent definitions
    librarian.agent.md
    subagents/
      pdf-extractor.agent.md
      image-ocr.agent.md
      url-clipper.agent.md
  raw/
    pending/               ← gitignored
    processed/             ← gitignored
    README.md
  knowledge/               ← file store (local disk; gitignored except _meta stubs)
    _meta/
      index.md
      log.md
      taxonomy.md
    _review/
    engineering/
    reference/
    ai/
    ...
  librarian-config.json
  docker-compose.yml       ← standalone local dev (OpenSearch + Elasticvue profile)
  .env.example
  PLAN.md                  ← this file (moved here from ai-stack/docs/)
```

### Endpoint Strategy (remote-first, local fallback)

The Librarian reads MCP endpoints from `librarian-config.json` and attempts them in order:
1. Remote MCP gateway (primary, e.g. `https://llm.ryanmarsh.net/mcp`)
2. Local MCP server (development/testing fallback)

This keeps knowledge authoring and file ownership local while still integrating with remote MCP capabilities when available.

### Deployment (remote services + local library workflow)

Remote services can still be deployed together on the VPS. The `ai-stack` compose uses Docker Compose v2's `include:` directive to pull in knowledge-library service definitions when running full-stack infra remotely:

```yaml
# ai-stack/docker-compose.yml
include:
  - path: ../knowledge-library/docker-compose.yml

services:
  bifrost: ...
  mcp-server: ...
  postgres: ...
  redis: ...
```

When running the local-first workflow, the configured `knowledge_path` remains the canonical file store managed by the Librarian.

---

## Components

### 1. Raw Ingestion Layer

**Drop zone**: `raw/` at the project root.

```
raw/
  pending/      ← user drops files here; Librarian reads from here
  processed/    ← archived after successful ingestion (with manifest entry)
  README.md     ← instructions (tracked in git)
```

`raw/pending/` and `raw/processed/` are gitignored. `raw/README.md` is tracked.

**Supported input types** — the Librarian handles everything; format-specific processing is delegated to subagents:

| Format | Processing |
|--------|-----------|
| Markdown / plain text | Native — Librarian reads directly |
| HTML | Converted to markdown by a cleaning subagent |
| PDF | Text extracted by a PDF subagent (PdfPig .NET library or `pdftotext` shell-out) |
| Images (PNG, JPG, etc.) | Passed to Claude vision subagent for OCR + description |
| Code files (any language) | Parsed as reference documentation with language tag |
| URLs (`.url` file or `.txt` with URL content) | Fetched and clipped to markdown by a web subagent |
| Office documents (DOCX, XLSX) | Converted to text/markdown by a document subagent |

The Librarian identifies format from extension + MIME sniffing, then routes to the appropriate subagent before making categorization decisions.

**Processing flow**:
1. File appears in `raw/pending/`
2. Librarian identifies format, dispatches to appropriate format subagent if needed
3. Subagent returns extracted text + metadata
4. Librarian analyzes content and determines canonical `knowledge/` path using taxonomy
5. If confidence ≥ auto-approve threshold → writes knowledge page(s), indexes to Elasticsearch, archives to `raw/processed/`
6. If confidence < threshold → writes to `knowledge/_review/` with structured frontmatter, does not index yet
7. Appends entry to `knowledge/_meta/log.md`

---

### 2. Librarian Agent

**Definition file**: `library/agents/librarian.agent.md`

**Model**: Claude Sonnet (orchestrator). Delegates to Claude Haiku or smaller models for:
- Bulk format extraction (PDF, HTML, DOCX)
- Confidence classification of unambiguous documents
- Cross-reference link generation on well-understood content
- Summary generation for `sources/` pages

Escalates to Sonnet (or Opus if configured) for:
- Ambiguous categorization requiring judgment
- Contradiction detection across multiple existing pages
- Taxonomy decisions (new domain creation)
- Lint analysis

**Responsibilities**:
- Identify document format and dispatch to format subagents
- Determine correct knowledge path using the taxonomy (`knowledge/_meta/taxonomy.md`)
- Write new knowledge pages; update existing pages when new content refines or contradicts them
- Maintain `knowledge/_meta/index.md` and `knowledge/_meta/log.md` on every operation
- Add cross-references between related pages (plain markdown links, not Obsidian-specific)
- Detect contradictions between new and existing pages; flag them in both pages
- Route uncertain items to `knowledge/_review/` with structured explanation
- Suggest knowledge gaps and new source queries during lint passes
- Scan configured source paths for new content to ingest

**Auto-approve rules** (configured in `librarian-config.json`):
- Source file has explicit `domain:` frontmatter → auto-approve
- Content analysis confidence ≥ configured threshold (default 0.80) → auto-approve
- Source path is in `trusted_paths` list → auto-approve
- Otherwise → route to `_review/`

**Operations**:
| Operation | Trigger | Description |
|-----------|---------|-------------|
| `ingest`  | On-demand MCP / schedule | Process all files in `raw/pending/` |
| `query`   | On-demand MCP | Answer a question from the knowledge base; optionally file the answer as a new page |
| `lint`    | Scheduled / on-demand | Find orphan pages, contradictions, missing links, stale content |
| `scan`    | Scheduled / on-demand | Check configured source paths in `librarian-config.json` for new content |

---

### 3. Human Review Queue

**Location**: `knowledge/_review/`

Each pending item is a markdown file named by review ID:

```yaml
---
review_id: "rev_abc123"
source_file: "raw/pending/some-doc.md"
submitted_at: "2026-04-22T14:30:00Z"
reason: "Ambiguous domain — could be research/ai or engineering/ml"
suggested_path: "knowledge/ai/ml-frameworks/pytorch-notes.md"
suggested_domain: "ai/ml-frameworks"
confidence: 0.61
status: pending
---

# Librarian Notes

[Librarian's analysis of the document and why it is uncertain]

## Proposed Knowledge Page Preview

[Draft content for the knowledge page, ready to publish as-is or after user edits]
```

**User workflow**:
1. Check `knowledge/_review/` — each file is a pending item
2. Read the Librarian's notes and the draft page preview
3. Optionally edit `suggested_path` or `suggested_domain` directly in the file
4. Call `knowledge_approve rev_abc123` from Claude Code MCP (optionally with `--path` override)
5. Librarian publishes the page, indexes to Elasticsearch, removes from `_review/`, updates log

**MCP tool**:
```
knowledge_approve(review_id: string, override_path?: string) → PublishResult
```

---

### 4. Knowledge File Store

**Location**: `knowledge/` — plain markdown files organized by domain, viewer-agnostic.

**Storage**: Local disk in all environments. No cloud storage abstraction needed. The `knowledge/` directory path is configurable and managed by the local Librarian process. MCP integration happens over tool calls (remote-first, local fallback), not direct file sharing.

This is the Librarian's working directory — plain markdown files organized by domain. Human-readable by opening files directly, viewer-agnostic. A wiki viewer (Obsidian, Notion import, GitHub Wiki, etc.) can be layered on top later without changing this structure.

**Directory structure**:
```
knowledge/
  _meta/
    index.md          ← Librarian-maintained catalog of all pages (LLM-readable)
    log.md            ← Append-only ingest/query/lint log
    taxonomy.md       ← Live category tree; Librarian updates as new domains emerge
  _review/            ← Pending human approval (see §3)
  engineering/
    backend/
      csharp/         ← Migrated from library/knowledge/backend/code-standards/csharp/
      efcore/
    frontend/
      angular/
      react/
    infra/
    security/
    architecture/
  reference/           ← Third-party docs, API references, tool guides
  ai/                  ← AI/ML knowledge, model notes, prompting
  research/            ← Papers, studies, reports
  product/             ← Product/UX knowledge
  sources/             ← One summary page per ingested raw source
```

The Librarian is empowered to create new top-level domains as needed. The above is a seed, not a constraint. Every new domain is recorded in `_meta/taxonomy.md`. Maximum nesting depth: **3 levels** (`domain/subdomain/topic.md`). Flat filenames are used when nesting isn't warranted.

**Page frontmatter** (standard YAML, viewer-agnostic):
```yaml
---
title: "Page Title"
domain: "engineering/backend/csharp"
tags: [async, dotnet, performance]
sources: ["sources/msdn-async-guide.md"]
updated: 2026-04-22
confidence: high          # high | medium | low | unverified
---
```

Body uses standard markdown links (`[related topic](../topic.md)`) — not Obsidian wikilinks — so the files work correctly in any viewer or plain file browser.

**`index.md`** format — two sections in one file; Librarian keeps both in sync on every ingest:

```markdown
# Knowledge Index

_Last updated: 2026-04-22 — 47 pages across 5 domains_

---

## By Domain

### engineering/backend/csharp
- [async-programming](../engineering/backend/csharp/async-programming.md) — Async/await patterns, ConfigureAwait, cancellation tokens (3 sources)
- [class-design](../engineering/backend/csharp/class-design.md) — SOLID, record types, minimal APIs (2 sources)

### engineering/frontend/react
- [component-patterns](../engineering/frontend/react/component-patterns.md) — Composition, hooks, context patterns (1 source)

### ai/prompting
- [chain-of-thought](../ai/prompting/chain-of-thought.md) — CoT techniques, few-shot examples (2 sources)

---

## A–Z

- [async-programming](../engineering/backend/csharp/async-programming.md) — Async/await patterns, ConfigureAwait, cancellation tokens
- [chain-of-thought](../ai/prompting/chain-of-thought.md) — CoT techniques, few-shot examples
- [class-design](../engineering/backend/csharp/class-design.md) — SOLID, record types, minimal APIs
- [component-patterns](../engineering/frontend/react/component-patterns.md) — Composition, hooks, context patterns
```

The Librarian reads `index.md` first on every query — the domain view helps narrow scope, the A–Z view helps when the domain is unknown.

**`log.md`** format (consistent prefix enables grep/parsing):
```markdown
## [2026-04-22] ingest | "Wolverine MCP Integration Guide" → engineering/backend/wolverine-mcp.md
## [2026-04-22] lint | 3 orphan pages, 1 contradiction flagged (async-programming vs efcore-patterns)
## [2026-04-23] approve | rev_abc123 → knowledge/ai/ml-frameworks/pytorch-notes.md
```

Log scope: ingests, lint passes, and approvals only. No query logging.

---

### 5. Search / Storage Layer

#### What embeddings are (plain language)

When you search for "database performance tips", keyword search only finds documents containing those exact words. **Embeddings** fix this: at ingest time, every document is converted to a list of ~1,500 numbers that encode its *meaning*. When you search, your query gets the same treatment, and the system finds documents whose numbers are geometrically close — so a search for "query performance" finds pages about "slow SQL" and "index tuning" even with zero keyword overlap. This is semantic search.

The model that converts text to these number lists is the embedding model. It runs once per document at ingest and once per search query.

#### Technology: OpenSearch

**OpenSearch 2.x** — Apache 2.0 licensed (fully free, no restrictions), functionally identical to Elasticsearch 8.x for this use case. Same query API, same hybrid search support, same Docker image story.

Why OpenSearch over Elasticsearch: Elasticsearch's self-hosted license (Elastic License 2.0) carries restrictions at scale. OpenSearch has none. If you ever migrate to a managed Azure service, Azure AI Search is the native option there — but that's a separate decision for later, not a blocker now.

**Why OpenSearch over pgvector**:
- First-class hybrid BM25 + kNN in a single query (RRF — Reciprocal Rank Fusion)
- Field-level boosting (`title` outweighs `content`) built in
- Rich filtering: domain, tags, date range, confidence level, source
- Scales to tens of thousands of pages without degradation
- Purpose-built for search, not a Postgres extension

**Tradeoff**: ~1-2GB RAM minimum. Cap with `OPENSEARCH_JAVA_OPTS=-Xms512m -Xmx1g` for development.

**Elasticsearch index** (`knowledge-wiki`):
```json
{
  "mappings": {
    "properties": {
      "title":       { "type": "text", "boost": 3 },
      "content":     { "type": "text" },
      "domain":      { "type": "keyword" },
      "tags":        { "type": "keyword" },
      "knowledge_path": { "type": "keyword" },
      "source_file": { "type": "keyword" },
      "updated":     { "type": "date" },
      "confidence":  { "type": "keyword" },
      "embedding":   { "type": "dense_vector", "dims": 1536, "index": true, "similarity": "cosine" }
    }
  }
}
```

**Hybrid search** (single query, RRF combined):
- BM25 match over `title` + `content`
- kNN nearest-neighbour over `embedding`
- Filterable by `domain`, `tags`, `confidence`, date range
- Returns ranked results with both relevance scores

**Embeddings provider**: **OpenAI `text-embedding-3-small`** routed through the existing Bifrost/LiteLLM gateway.

- 1536 dimensions — matches the index schema above
- ~$0.02 per million tokens — negligible cost for a knowledge base this size
- LiteLLM natively proxies the OpenAI `/embeddings` endpoint, so Bifrost handles it with no new service
- If offline or cost-free operation is ever needed: swap to Ollama `nomic-embed-text` (768 dims, requires index schema update)

Embeddings are generated at ingest time and stored in Elasticsearch. Re-embedding is only triggered if the model changes.

**`docker-compose.yml`** (local dev — all services):

```yaml
services:
  opensearch:
    image: opensearchproject/opensearch:2.13.0
    environment:
      - discovery.type=single-node
      - OPENSEARCH_JAVA_OPTS=-Xms512m -Xmx1g
      - plugins.security.disabled=true
    volumes:
      - opensearch_data:/usr/share/opensearch/data
    ports:
      - "9200:9200"
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:9200/_cluster/health"]
      interval: 30s
      timeout: 10s
      retries: 5

  azurite:
    image: mcr.microsoft.com/azure-storage/azurite:latest
    ports:
      - "10000:10000"   # Blob service
    volumes:
      - azurite_data:/data
    command: azurite-blob --blobHost 0.0.0.0

  elasticvue:
    image: cars10/elasticvue:latest
    profiles: [dashboards]
    ports:
      - "8090:8080"
    environment:
      - ELASTICVUE_CLUSTERS=[{"name":"knowledge","uri":"http://opensearch:9200"}]

volumes:
  opensearch_data:
  azurite_data:
```

**`docker-compose.prod.yml`** (Azure VM — no Azurite, storage via env var):

```yaml
services:
  opensearch:
    image: opensearchproject/opensearch:2.13.0
    environment:
      - discovery.type=single-node
      - OPENSEARCH_JAVA_OPTS=-Xms512m -Xmx1g
      - plugins.security.disabled=true
    volumes:
      - opensearch_data:/usr/share/opensearch/data
    restart: unless-stopped

  elasticvue:
    image: cars10/elasticvue:latest
    profiles: [dashboards]
    ports:
      - "8090:8080"
    environment:
      - ELASTICVUE_CLUSTERS=[{"name":"knowledge","uri":"http://opensearch:9200"}]

volumes:
  opensearch_data:
```

Elasticvue (~50MB) gives a browser UI at `http://localhost:8090` to browse indexes and run queries. No OpenSearch Dashboards needed (~1GB).

---

### 6. MCP Tool Surface

New tool class: **KnowledgeLibrarianTools**

| Tool | Description |
|------|-------------|
| `knowledge_ingest` | Trigger Librarian to process `raw/pending/`; returns count of items processed and items queued for review |
| `knowledge_search` | Hybrid BM25 + semantic search over Elasticsearch; filters: domain, tags, date, confidence |
| `knowledge_retrieve` | Fetch a specific knowledge page by path or title |
| `knowledge_store` | Directly store a knowledge item to file store + Elasticsearch (for agent-generated content, bypasses raw flow) |
| `knowledge_approve` | Approve a `_review/` item; publishes to knowledge store + indexes to Elasticsearch |
| `knowledge_lint` | Run library health-check: orphan pages, contradictions, gaps, stale content |
| `librarian_run` | Invoke Librarian for a named operation: `ingest`, `lint`, `scan`, `query` |
| `librarian_status` | Show: pending review count, recent ingests, Elasticsearch health, last lint timestamp, scan path status |
| `librarian_scan` | Scan configured source paths in `librarian-config.json` for new content and queue/ingest it |

**Existing tools to retire/update**:
- `search_documents` → deprecated; replaced by `knowledge_search`
- `knowledge_retrieve` → upgraded to query Elasticsearch instead of file scan
- `list_standards` → absorbed into `knowledge_search` with `domain: engineering` filter
- `read_document` → kept as-is for direct knowledge page access by path

---

### 7. Configuration

**File**: `librarian-config.json` at project root

```json
{
  "paths": {
    "knowledge_path": "./knowledge",
    "raw_pending_path": "./raw/pending",
    "raw_processed_path": "./raw/processed"
  },
  "mcp": {
    "endpoints": [
      "https://llm.ryanmarsh.net/mcp",
      "http://localhost:8081/mcp"
    ],
    "timeout_ms": 8000
  },
  "scan_paths": [
    {
      "path": "/absolute/path/to/some/codebase/docs",
      "domain": "engineering",
      "auto_approve": true,
      "description": "Primary project documentation"
    },
    {
      "path": "/absolute/path/to/notes",
      "domain": null,
      "auto_approve": false,
      "description": "Personal notes — always queue for review"
    }
  ],
  "auto_approve_confidence_threshold": 0.80,
  "trusted_paths": [],
  "librarian_model": "claude-sonnet-4-6",
  "subagent_model": "claude-haiku-4-5-20251001",
  "embeddings": {
    "provider": "openai",
    "model": "text-embedding-3-small",
    "dimensions": 1536,
    "via_bifrost": true
  },
  "opensearch": {
    "url": "http://localhost:9200",
    "index": "knowledge-wiki"
  },
  "review_notify": true,
  "lint_schedule": "0 8 * * 1"
}
```

---

### 8. Librarian Routine / Schedule

**Mechanism**: Claude Routine via the `/schedule` skill, running on a configurable cron.

**Default schedule**: Daily (exact time and frequency configurable).

**Routine tasks** (in order):
1. Check `raw/pending/` for new files → run ingest (with subagent delegation for format processing)
2. Run `librarian_scan` against all configured `scan_paths`
3. Process all discovered items (auto-approve or route to `_review/`)
4. On Mondays → run `knowledge_lint`
5. Append summary to `knowledge/_meta/log.md`
6. Surface a brief status report (items ingested, items pending review, any contradictions found)

The Librarian can also be invoked on-demand at any time via `librarian_run` or `knowledge_ingest` MCP tools.

---

## Deployment

### Local dev
`docker compose up` in the `knowledge-library` repo — runs OpenSearch always-on, Elasticvue behind `--profile=dashboards`. Files are written to the local `knowledge/` directory and managed by the local Librarian.

### Remote infra (VPS — Vultr, scaled to 8GB/4vCPU, ~$48/month)

Both repos can be cloned side by side on the VPS. `ai-stack/docker-compose.yml` uses `include:` to pull in knowledge-library services. One `docker compose up` starts the full combined stack when you want remote-hosted infra.

```
/srv/
  ai-stack/
    docker-compose.yml      ← includes ../knowledge-library/docker-compose.yml
    .env
  knowledge-library/
    docker-compose.yml      ← standalone services (OpenSearch, Elasticvue)
    .env
    knowledge/              ← file store on VPS disk
    raw/pending/
```

nginx + certbot already configured on the VPS. `llm.ryanmarsh.net` already points at the VPS and is the preferred MCP endpoint whenever reachable.

---

## Library Reorganization

**Current state**: `library/knowledge/` contains dev-only standards (backend, frontend, global).

**Proposed split** (two separate concerns):
- `library/` → stays as the **agent/skill/workflow/prompt** platform config store (no change to structure)
- `knowledge/` → new top-level directory for **all knowledge content** (replaces `library/knowledge/`)

**Migration**: One-time ingest of existing `library/knowledge/` files through the Librarian so they receive proper knowledge pages, cross-references, Elasticsearch indexing, and frontmatter. After migration is confirmed complete, `library/knowledge/` is removed.

**Taxonomy seed** (Librarian grows this dynamically; not a hard constraint):
```
knowledge/
  engineering/      ← from library/knowledge/backend/ + frontend/ + global/
  reference/        ← third-party tool docs, API references
  ai/               ← AI/ML, prompting, model notes
  research/         ← papers, studies
  product/          ← product/UX
```

---

## Implementation Phases

### Phase 0 — New repo + Foundation
- [ ] Create `knowledge-library` repo
- [ ] Move this plan to `knowledge-library/PLAN.md`
- [ ] Set up `docker-compose.yml` (OpenSearch always-on + Elasticvue behind `--profile=dashboards`)
- [ ] Create `raw/` directory with README and `.gitignore` rules
- [ ] Create `knowledge/_meta/` scaffold (index.md, log.md, taxonomy.md stubs)
- [ ] Create `librarian-config.json` with schema
- [ ] Add config/schema fields for local filesystem paths (`knowledge_path`, `raw_pending_path`, `raw_processed_path`)
- [ ] Add config/schema fields for ordered MCP endpoints (remote-first + local fallback)
- [ ] Design and create OpenSearch index (`knowledge-wiki`) with mapping
- [ ] Confirm OpenAI embeddings route via Bifrost (test `/embeddings` endpoint in ai-stack config)
- [ ] Wire `OPENSEARCH_URL` env var into ai-stack MCP server `.env`
- [ ] Configure MCP endpoint resolution from `librarian-config.json` (remote-first with local fallback)
- [ ] Add `include:` for knowledge-library services in ai-stack `docker-compose.yml`

### Phase 1 — Librarian Agent + Review Queue
- [ ] Create `library/agents/librarian.agent.md` (orchestrator definition)
- [ ] Create subagent definitions: PDF extractor, image OCR, URL clipper, HTML cleaner
- [ ] Implement `knowledge/_review/` format and `knowledge_approve` MCP tool
- [ ] Implement `librarian_status` and `librarian_run` MCP tools
- [ ] Implement `knowledge_ingest` — Librarian reads `raw/pending/`, dispatches to subagents, routes items

### Phase 2 — Knowledge File Store
- [ ] Create `knowledge/` directory structure with `_meta/` scaffold
- [ ] Implement Librarian file writer (index.md, log.md, taxonomy.md maintenance)
- [ ] Define and validate page frontmatter schema
- [ ] Implement `knowledge_store` for direct agent-generated content

### Phase 3 — Search Layer
- [ ] Implement OpenSearch indexing service (.NET — `OpenSearchKnowledgeIndexer`)
- [ ] Implement embeddings pipeline (OpenAI `text-embedding-3-small` via Bifrost)
- [ ] Implement `knowledge_search` with hybrid BM25 + kNN (RRF)
- [ ] Wire `knowledge_retrieve` to OpenSearch
- [ ] Deprecate `search_documents`; update `list_standards`

### Phase 4 — Migration + Schedule
- [ ] Migrate `library/knowledge/` into `knowledge/engineering/` via one-time Librarian ingest
- [ ] Remove `library/knowledge/` after migration confirmed
- [ ] Set up Librarian Claude Routine with configured cron schedule
- [ ] Implement `librarian_scan` for configured source paths
- [ ] End-to-end test: drop PDF → subagent extracts → Librarian categorizes → knowledge page written → Elasticsearch indexed → `knowledge_search` returns it

### Phase 5 — Polish
- [ ] `knowledge_lint` implementation
- [ ] Human-readable wiki viewer layer (Obsidian, Notion import, GitHub Wiki — TBD)
- [ ] Tune auto-approve confidence threshold from real usage data
- [ ] Scale Vultr VPS to 8GB/4vCPU; verify both stacks run comfortably

---

## Open Questions / Decisions

| # | Question | Decision | Status |
|---|----------|----------|--------|
| 1 | Elasticsearch vs OpenSearch | **OpenSearch** — Apache 2.0, free, same API | ✅ Decided |
| 2 | Human-readable wiki viewer | Deferred to Phase 5 | ⏸ Deferred |
| 3 | Embeddings via Bifrost | OpenAI `text-embedding-3-small` via Bifrost; verify `/embeddings` endpoint in Phase 0 (technical task) | ✅ Decided |
| 4 | `raw/` in git | Gitignore `pending/` and `processed/`; track only `README.md` | ✅ Decided |
| 5 | `personal/` domain | Removed — not needed | ✅ Closed |
| 6 | Separate repo vs same repo | **Separate repo** (`knowledge-library`) — independent codebase, single merged Docker Compose for deployment | ✅ Decided |
| 7 | Hosting model | **Local-first knowledge-library**; all filesystem and endpoint paths are config-driven via `librarian-config.json` + schema | ✅ Decided |
| 8 | MCP target | Ordered endpoints from config; prefer remote gateway, fallback to local MCP for testing | ✅ Decided |
| 9 | File storage | Local disk in `knowledge-library/knowledge` managed by Librarian (no cloud storage) | ✅ Decided |
