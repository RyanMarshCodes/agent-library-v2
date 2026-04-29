# naf-knowledge: Company AI Knowledge Layer

**Status**: Draft  
**Date**: 2026-04-27  
**Stack**: .NET 10, PostgreSQL + pgvector, Docker Compose  
**Starting point**: Greenfield, work laptop, weekend project

---

## What Problem This Solves

Every AI agent (Claude Code, Cursor, GitHub Copilot, OpenCode) starts each session knowing nothing about the company: no org structure, no product map, no codebase context, no standards. Agents hallucinate org facts, give advice inconsistent with company patterns, and need constant re-orientation.

This project builds a central knowledge layer that all agents query — so they start sessions already knowing the company, and can retrieve deep context on demand.

---

## Design Philosophy — What This Is and Isn't

This system draws from three different ideas that are worth distinguishing, because each informs a different part of the design.

### Karpathy's LLM Wiki (content authoring standard)

Andrej Karpathy's concept: write a wiki *for LLMs*, not for humans. Traditional documentation is written with the assumption that the reader can infer context, read between lines, and tolerate prose. LLMs work better with dense factual assertions, explicit relationships, and predictable structure — information formatted to be *used*, not read.

**What this project takes from it:** The page format standard (§6). Every knowledge page in this system is authored using Karpathy's principles — dense assertions, no narrative, explicit `depends_on`/`consumed_by` relationships, gotchas stated directly. This is the authoring philosophy that governs *what goes in each page*.

**Where this project diverges:** Karpathy's concept is human-authored — people writing structured facts for LLMs to consume. This system adds an AI Librarian that can also generate and maintain pages. The underlying philosophy still applies; the author is just sometimes the machine.

### Traditional RAG (retrieval infrastructure)

Standard Retrieval-Augmented Generation: chunk documents, embed them, store vectors, retrieve semantically at query time. The dominant pattern for enterprise knowledge systems today.

**What this project takes from it:** The retrieval layer (§5, §12). pgvector provides the vector index; `knowledge_search` embeds queries and returns the most relevant chunks. This is how agents access deep knowledge they can't hold in context.

**Where this project diverges:** Traditional RAG treats source documents as fixed artifacts — PDFs, Confluence pages, Git commits. This system treats the *wiki pages themselves* as the primary corpus, written specifically for retrieval quality. The wiki and the RAG index are the same thing, not separate systems. Traditional RAG also doesn't govern content quality — this system does, via the fact-checking pipeline, confidence scoring, and lifecycle states.

### This system (enterprise knowledge platform)

The combination of the above, plus the infrastructure needed to make it practical at team scale:

- **Content layer**: LLM-wiki-formatted markdown pages (Karpathy-inspired authoring standard)
- **Retrieval layer**: pgvector semantic search over those pages (RAG pattern)
- **Ingestion layer**: Librarian agent that converts raw inputs (docs, code, images) into wiki-format pages
- **Governance layer**: fact-checking against codebase, confidence scoring, review queue, lifecycle states, hallucination controls
- **Access layer**: MCP server exposing knowledge tools to all AI clients uniformly
- **Distribution layer**: AI Gateway + client config files connecting every developer's AI tool to the same knowledge base

**The short version:** Karpathy's authoring philosophy applied to an enterprise RAG system, with a Librarian agent for maintenance and an MCP server for access.

---

## The Three Context Mechanisms

These serve distinct purposes. All three are needed and they are additive, not competing:

| Mechanism | What it is | When used | Where stored |
|-----------|-----------|-----------|-------------|
| **Always-on context** | Small curated core (~500–2K tokens), always in every session | Automatically, from session start | Markdown file → `CLAUDE.md` / `.cursorrules` |
| **Retrieved knowledge (RAG)** | Full knowledge base, searched at query time via MCP | When agent calls `knowledge_search` | Markdown (canonical) + pgvector (index) |
| **Agent memory** | What persists across sessions for a specific agent | Ongoing per-agent writes | PostgreSQL |

The wiki IS the RAG corpus. You write knowledge pages in markdown → they get embedded → the vector store is an index derived from the markdown. They are never managed separately.

---

## The Enterprise Stack (Three Governance Layers)

These are commonly conflated. They solve different problems and should be built in this order:

| Layer | Controls | Build order |
|-------|---------|------------|
| **AI Gateway** | How teams access LLMs (auth, cost, routing, caching) | First |
| **Agent Platform** | What agents know and how they behave (definitions, skills, workflows) | Second |
| **MCP Gateway** | What tools agents can invoke (allowlist, approval, audit) | Third |

---

## 1. AI Gateway

Sits between every AI client and the underlying LLM APIs. Required before team rollout — without it you have no cost visibility, no audit trail, and no way to prevent one runaway agent from burning the budget.

**Capabilities:**

- **Virtual keys** — one key per dev or team; individually revocable and limitable
- **Per-team spend limits and token quotas** — hard stops when budget is exceeded
- **Model routing policy** — Haiku for simple completions, Sonnet for agents, Opus only when explicitly requested
- **Semantic caching** — near-identical queries return cached responses; 30–60% cost reduction on knowledge-heavy teams
- **Audit log** — every prompt, response, and tool call; required for compliance
- **Failover** — if one LLM API is degraded, route to a backup automatically

**Options for a .NET/Azure shop:**

| Option | Fit | Notes |
|--------|-----|-------|
| **LiteLLM** (self-hosted) | Best for now | One Docker service, proxies all major LLM APIs, virtual keys, spend limits, Prometheus metrics. Fastest to stand up. |
| **Azure APIM AI Gateway** | Best long-term | Native AAD auth, token-rate policies, semantic caching, Azure Monitor integration. Steeper initial setup. |
| **Portkey** | If no self-hosting | Managed SaaS equivalent of LiteLLM. |

**Recommendation**: Run LiteLLM now (add to `docker-compose.yml`). Plan Azure APIM migration when you need AAD integration and compliance reporting.

---

## 2. MCP Gateway

Governs tool access — which agents can call which MCP tools, with what approval requirements. Without this, any connected agent can invoke any tool including destructive ones.

**Capabilities:**

- **Tool allowlists per team/role** — backend team gets DB tools; frontend team does not
- **Tier-based approval flows:**
  - Tier A (read) — auto-allowed, no prompt
  - Tier B (mutate) — requires explicit approval token
  - Tier C (execute/destructive) — requires approval token + declared rollback intent
- **Per-tool rate limiting** — prevent loops from spamming expensive operations
- **Audit log** — every tool call with full arguments and response

**Options:**

- **Azure APIM policy** — can proxy MCP HTTP endpoints and apply per-route policies. No native MCP awareness but policy-based control works well.
- **Custom .NET reverse proxy** — thin middleware that intercepts MCP requests, enforces allowlists, writes audit events to Postgres. Simple to build for basic governance.
- **Bifrost** (open source) — MCP-aware gateway with virtual keys and allowlists. Good reference implementation if you want something pre-built.

**Key pattern: tool tiers declared in agent definitions.** Every agent definition declares its maximum tool tier. The gateway enforces it. Tier C tools always trigger a human confirmation prompt — the agent cannot bypass it.

---

## 3. Agent Platform (`naf-agents` repo)

A separate git repository that defines how agents behave, what they know, and what workflows they follow — distributed consistently to every AI client.

**The portability problem:** Claude Code, Cursor, GitHub Copilot, and OpenCode all have different native formats:

| Client | Agent definition | Skills/commands | Context injection |
|--------|-----------------|----------------|-------------------|
| Claude Code | `.agent.md` | `.claude/skills/*.md` | `CLAUDE.md` |
| Cursor | `.cursor/rules/*.mdc` | Contextual rule activation | `.cursorrules` |
| GitHub Copilot | N/A | N/A | `.github/copilot-instructions.md` |
| OpenCode | `opencode.json` | N/A | System prompt |

**Solution: canonical format + compilation.** Write everything in `.agent.md` format (the most expressive), then run a build script that compiles the portable subset to each client's native format. ~80% parity across clients; the remaining 20% is advanced Claude Code features with no equivalent elsewhere.

### Repo structure

```
naf-agents/
  agents/
    backend-dev.agent.md        ← identity, model, temp, tool tier, context
    frontend-dev.agent.md
    code-reviewer.agent.md
    spec-writer.agent.md
    architect.agent.md
    librarian.agent.md          ← knowledge base custodian
    subagents/
      code-verifier.agent.md    ← fact-checks code claims
      pdf-extractor.agent.md
      url-clipper.agent.md
      code-summarizer.agent.md  ← generates codebase overview pages
  workflows/
    feature-delivery.workflow.md    ← gate-based spec→implement→test→review→commit
    bug-triage.workflow.md
    security-review.workflow.md
    knowledge-submission.workflow.md
  skills/
    spec.md          ← /spec skill: generate spec from feature request
    implement.md     ← /implement: implement from approved spec
    review.md        ← /review: structured code review
    test.md          ← /test: generate and run tests
    commit.md        ← /commit: structured commit with context
    wrapup.md        ← /wrapup: session summary and handoff notes
    context.md       ← /context: load project context before starting
  rules/
    global.md        ← applies to all agents (tone, format, tool policy)
    backend.md       ← .NET/Azure specific rules
    frontend.md      ← Angular/TypeScript specific rules
    security.md      ← security rules (no secrets in output, OWASP awareness)
  scripts/
    compile.js       ← reads canonical format, writes per-client dist files
    validate.js      ← validates agent contracts (model, tier, required fields)
  dist/              ← compiled output, committed to repo
    .cursorrules
    CLAUDE.md
    copilot-instructions.md
  .github/
    workflows/
      validate.yml   ← CI: validate agent contracts on every PR
      compile.yml    ← CI: recompile dist/ on changes to agents/ or skills/
```

### Agent definition format (`.agent.md`)

```markdown
---
name: backend-dev
description: Senior .NET backend developer for NAF services
model: claude-sonnet-4-6
temperature: 0.3
tool_tier: B          # A (read) | B (mutate) | C (execute)
tags: [backend, dotnet, azure]
---

# Identity
You are a senior .NET backend developer at NAF...

# Context
- Primary stack: .NET 10, Azure SQL, Service Bus, Azure Functions
- Follow NAF API v2 standards (see knowledge_search "NAF API standards")
- Prefer async/await throughout; never block on Task.Result

# Tool policy
- Always call `knowledge_search` before making claims about NAF architecture
- Never commit without calling `get_standards` for the relevant domain first
```

### Delivery workflow format (`.workflow.md`)

```markdown
---
name: feature-delivery
phases: [context, spec, validate, architect, validate, implement, test, reflect, review, commit, wrapup]
---

## Phase: spec
Artifact: `spec.md` in project root
Gate criteria:
- [ ] Problem statement is one sentence
- [ ] Acceptance criteria are testable
- [ ] Out-of-scope explicitly listed
- [ ] No implementation details (those go in architect phase)
Advance when: human approves spec.md

## Phase: implement
Artifact: working code + passing tests
Gate criteria:
- [ ] All acceptance criteria from spec are addressed
- [ ] No new failing tests
- [ ] knowledge_search called for any NAF pattern referenced
Advance when: /test passes and human approves
```

### Distribution to team repos

Each company repo includes a one-time setup:

```bash
# Pull latest compiled agent configs from naf-agents
curl -fsSL https://raw.githubusercontent.com/naf/naf-agents/main/dist/CLAUDE.md -o CLAUDE.md
curl -fsSL https://raw.githubusercontent.com/naf/naf-agents/main/dist/.cursorrules -o .cursorrules
```

Or: add `naf-agents` as a git submodule and symlink the dist files. CI in each repo runs `validate.js` to confirm the configs are current.

### Skills distribution

Claude Code skills live in `.claude/skills/` (project-scoped) or `~/.claude/skills/` (global). For team distribution, commit `.claude/skills/` to each company repo, sourced from `naf-agents/skills/`. Developers get the same `/spec`, `/review`, `/commit` commands in every repo automatically.

---

## 4. Spec-Driven Delivery Workflow

A gate-based sequence every feature goes through. The value: consistent process regardless of developer or AI tool.

```
/context → /spec → /validate → /architect → /validate
→ /implement → /test → /reflect → /review → /commit → /wrapup
```

**Gate discipline:** Each gate produces a specific artifact. Gates must pass before advancing. The workflow definition defines pass/fail criteria explicitly — agents do not decide whether a gate passes; they produce the artifact and the criteria make it clear.

**Multi-agent orchestrator pattern:**

```
Feature request
    │
    ▼
Orchestrator (manages handoffs, checks gates)
    │
    ├──► Spec Writer Agent → spec.md
    │         ↓ (human approves)
    ├──► Architect Agent  → architecture.md
    │         ↓ (human approves)
    ├──► Implementer Agent ──┐
    │                        ├──► (parallel)
    ├──► Test Agent ─────────┘
    │         ↓
    └──► Reviewer Agent → review comments
              ↓ (human decides to merge)
```

Humans only touch gate transitions. Agents do the work inside each gate. This keeps human effort roughly constant regardless of feature complexity.

---

## 5. Technical Decisions

Concrete library and tool choices a new agent needs before writing any code.

### MCP Server (Gap 1)

**SDK**: `ModelContextProtocol` NuGet package — Microsoft's official .NET MCP SDK.

- Supports HTTP (Streamable HTTP) and SSE transports
- Integrates with ASP.NET Core minimal API or controller style
- Handles tool registration, request routing, and protocol framing

**`Program.cs` wiring (minimal):**

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<KnowledgeSearchTool>()
    .WithTools<KnowledgeRetrieveTool>()
    .WithTools<KnowledgeStoreTool>()
    .WithTools<GetProductMapTool>()
    .WithTools<GetOrgContextTool>()
    .WithTools<GetCodebaseContextTool>()
    .WithTools<LibrarianRunTool>()
    .WithTools<LibrarianStatusTool>()
    .WithTools<KnowledgeApproveTool>()
    .WithTools<KnowledgeRejectTool>();

builder.Services.AddSingleton<DocumentIndexer>();
builder.Services.AddSingleton<LibrarianOrchestrator>();
builder.Services.AddSingleton<OpenSearchKnowledgeStore>(); // see §10

var app = builder.Build();
app.MapMcp("/mcp");
app.MapGet("/submit", SubmissionFormHandler.Serve);  // static HTML page (see §6)
app.MapPost("/api/knowledge/submit", KnowledgeSubmitHandler.Handle);
app.Run();
```

**NuGet packages** (add to `NAF.Knowledge.Mcp.csproj`):

```xml
<PackageReference Include="ModelContextProtocol" Version="0.*" />
<PackageReference Include="ModelContextProtocol.AspNetCore" Version="0.*" />
<PackageReference Include="Anthropic.SDK" Version="*" />           <!-- Librarian API calls -->
<PackageReference Include="Pgvector.EntityFrameworkCore" Version="*" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="*" />
```

---

### File Extraction Libraries (Gap 5)

The ingestion pipeline must extract plain text from multiple input formats before the Librarian can process them. Use these .NET libraries — no external services required except for images.

| Format | Library | NuGet |
|--------|---------|-------|
| PDF | UglyToad.PdfPig | `UglyToad.PdfPig` |
| DOCX / XLSX | Open XML SDK | `DocumentFormat.OpenXml` |
| HTML | HtmlAgilityPack | `HtmlAgilityPack` |
| Images (PNG, JPG, etc.) | Claude Vision API (no local OCR) | via `Anthropic.SDK` |
| Markdown | No extraction needed — read as-is | — |
| Code files | No extraction needed — read as-is | — |
| DrawIO | Export to PNG first, then vision | — |

**Image handling:** Pass the raw bytes directly to the Anthropic API as a vision message. Claude returns a structured description. No local OCR library needed.

```csharp
// FileExtractorService.cs
public async Task<string> ExtractTextAsync(string filePath)
{
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    return ext switch
    {
        ".pdf"  => ExtractPdf(filePath),
        ".docx" => ExtractDocx(filePath),
        ".html" or ".htm" => ExtractHtml(filePath),
        ".png" or ".jpg" or ".jpeg" or ".gif" => await ExtractImageAsync(filePath),
        _       => await File.ReadAllTextAsync(filePath)  // .md, .cs, .ts, etc.
    };
}

private string ExtractPdf(string path)
{
    using var doc = PdfDocument.Open(path);
    return string.Join("\n", doc.GetPages().Select(p => p.Text));
}

private string ExtractDocx(string path)
{
    using var doc = WordprocessingDocument.Open(path, false);
    return doc.MainDocumentPart!.Document.Body!.InnerText;
}

private string ExtractHtml(string path)
{
    var html = new HtmlDocument();
    html.Load(path);
    return html.DocumentNode.InnerText;
}

private async Task<string> ExtractImageAsync(string path)
{
    var bytes = await File.ReadAllBytesAsync(path);
    var ext   = Path.GetExtension(path).TrimStart('.');
    // Send to Claude Vision — returns a structured description for the wiki
    return await _anthropicClient.DescribeImageAsync(bytes, ext);
}
```

**Add to `NAF.Knowledge.Mcp.csproj`:**

```xml
<PackageReference Include="UglyToad.PdfPig" Version="*" />
<PackageReference Include="DocumentFormat.OpenXml" Version="*" />
<PackageReference Include="HtmlAgilityPack" Version="*" />
```

---

## 6. Knowledge Architecture

### The Karpathy LLM Wiki Principle

Every knowledge page is written for LLM consumption first, human readability second:

- Dense factual assertions — not narrative prose
- Explicit relationships — never implied
- Predictable structure — LLMs parse sections, not free text
- No fluff — every word has information content

**Example page:**

```markdown
---
title: Inventory Management Service
type: product
team: backend-squad
stack: [C#, .NET 8, Azure SQL, Service Bus]
depends_on: [auth-service, purchasing-service]
consumed_by: [order-service, admin-portal]
sources: ["raw/processed/inventory-design-doc-2024.pdf"]
confidence: high
updated: 2026-04-27
---

**Purpose**: Manages stock levels, replenishment orders, and warehouse locations.
**Repos**: `naf/inventory-api`, `naf/inventory-worker`
**Key flow**: Order placed → Service Bus → Inventory Worker → adjusts stock → emits `StockUpdated`
**Gotchas**: Eventually consistent (up to 30s lag). Avoid queries during batch import window (2–4am UTC).
**Standards**: NAF API v2, shared auth middleware, async-first event patterns.
```

### Repo structure (`naf-knowledge`)

```
naf-knowledge/
  src/
    NAF.Knowledge.Mcp/           ← .NET 10 MCP server + web API
      Tools/                     ← MCP tool handlers
      Indexer/                   ← file watcher, chunker, embedder
      Librarian/                 ← agent orchestration
      WebUI/                     ← serves wiki viewer + submission form
      Program.cs
  knowledge/
    _meta/
      index.md                   ← Librarian-maintained catalog
      changelog.md               ← append-only log of all changes
      taxonomy.md                ← live domain tree
    _review/                     ← pending human approval
    org/
      _core.md                   ← always-on context (Tier 1)
      teams.md
      ownership-map.md
      glossary.md
    products/
      product-catalog.md
      {product-name}/
        overview.md
        data-flows.md
        api-surface.md
    architecture/
    codebase/
      {repo-name}/
        overview.md
        key-patterns.md
    standards/
  raw/
    pending/                     ← drop zone (gitignored)
    processed/                   ← archived after ingestion (gitignored)
    README.md
  mkdocs.yml                     ← wiki viewer config
  docker-compose.yml
  docker-compose.prod.yml
  .env.example
```

---

## 7. Web UI

Two distinct interfaces served by the same stack:

| Interface | Purpose | Technology |
|-----------|---------|-----------|
| **Wiki viewer** | Read-only browsing of knowledge pages | MkDocs Material (static site) |
| **Submission form** | Human writes and submits new knowledge | Embedded in .NET web API |

### Wiki viewer (MkDocs Material)

MkDocs reads the `knowledge/` directory directly and renders a static site. No schema coupling — the markdown files remain the single source of truth.

**`mkdocs.yml`**:

```yaml
site_name: NAF Knowledge Base
docs_dir: knowledge
theme:
  name: material
  features:
    - search.highlight
    - navigation.sections
    - navigation.expand
plugins:
  - search
  - tags
nav:
  - Home: _meta/index.md
  - Org: org/
  - Products: products/
  - Architecture: architecture/
  - Codebase: codebase/
  - Standards: standards/
```

Served on port `:8090` in docker-compose. The `mkdocs-serve` container watches `knowledge/` and hot-reloads when pages change — so publishing a new page (via Librarian or human approval) is immediately visible in the browser.

**docker-compose service:**

```yaml
mkdocs:
  image: squidfunk/mkdocs-material:latest
  volumes:
    - ./knowledge:/docs/knowledge:ro
    - ./mkdocs.yml:/docs/mkdocs.yml:ro
  ports:
    - "8090:8000"
  command: serve --dev-addr=0.0.0.0:8000
```

### Submission form and UI integration (Gap 2)

**How the two services connect:** MkDocs (`:8090`) and the .NET server (`:8081`) are separate containers. The wiki viewer is read-only — it does not host the submission form. Instead:

- The .NET server serves a **static HTML page** at `http://localhost:8081/submit`
- MkDocs renders a link in the nav/footer: `[Submit Knowledge](http://localhost:8081/submit)`
- The form POSTs directly to `http://localhost:8081/api/knowledge/submit` — no cross-origin issues because the page and its target are on the same origin (`:8081`)
- For production: an nginx reverse proxy routes `/` → MkDocs and `/submit` + `/api/` → .NET server under a single domain. No proxy needed locally.

**`mkdocs.yml` footer link** (add to `extra` section):

```yaml
extra:
  social:
    - icon: fontawesome/solid/pen-to-square
      link: http://localhost:8081/submit
      name: Submit Knowledge
```

**nginx (production `docker-compose.prod.yml` only):**

```nginx
server {
    listen 80;
    location /submit { proxy_pass http://mcp-server:8080; }
    location /api/   { proxy_pass http://mcp-server:8080; }
    location /       { proxy_pass http://mkdocs:8000; }
}
```

The submission form is a **single static HTML file** (`WebUI/submit.html`) embedded as a resource in the .NET project and served by `SubmissionFormHandler`. It uses **Toast UI Editor** loaded from CDN (no build step required):

```html
<!DOCTYPE html>
<html>
<head>
  <link rel="stylesheet"
    href="https://uicdn.toast.com/editor/latest/toastui-editor.min.css" />
</head>
<body>
  <form id="submit-form">
    <input name="title" placeholder="Title" required />
    <select name="domain">
      <option>org</option><option>products</option>
      <option>architecture</option><option>codebase</option><option>standards</option>
    </select>
    <input name="tags" placeholder="comma-separated tags" />
    <select name="status">
      <option value="current">Current state</option>
      <option value="planned">Planned feature</option>
      <option value="in-progress">In progress</option>
      <option value="deprecated">Deprecating</option>
    </select>
    <input name="submitted_by" placeholder="Your name / team" />
    <div id="editor"></div>
    <button type="submit">Submit for Review</button>
  </form>
  <script src="https://uicdn.toast.com/editor/latest/toastui-editor-all.min.js"></script>
  <script>
    const editor = new toastui.Editor({ el: document.querySelector('#editor'), height: '500px' });
    document.getElementById('submit-form').addEventListener('submit', async (e) => {
      e.preventDefault();
      const data = Object.fromEntries(new FormData(e.target));
      data.content = editor.getMarkdown();
      const res = await fetch('/api/knowledge/submit', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(data)
      });
      alert(res.ok ? 'Submitted — check back once reviewed.' : 'Error submitting.');
    });
  </script>
</body>
</html>
```

**Submission flow:**

```
Human opens /submit in browser
    │
    ├── Fills in: title, domain (dropdown), tags
    └── Writes content in WYSIWYG editor
              ↓
        Clicks Submit
              ↓
    POST /api/knowledge/submit
    {
      title, domain, tags, content,
      submitted_by: "name / team"
    }
              ↓
    Saved to knowledge/_review/ as draft
    Librarian fact-check pipeline triggered (async)
              ↓
    Browser shows: "Submitted — under review. You'll see it at /knowledge once approved."
```

The submission endpoint validates the frontmatter fields (title, domain required), saves the draft, and enqueues a Librarian fact-check job. The submitter does not need to write YAML frontmatter — the form fields generate it.

---

## 8. Human Submission + Librarian Fact-Checking

This is the full pipeline from human submission to published knowledge page.

### The problem with unverified human submissions

Humans know things agents don't — institutional knowledge, design intent, tribal context. But humans also misremember API paths, confuse service names, or document how things *should* work rather than how they *actually* work. Agents that trust human-submitted knowledge uncritically will inherit those errors and repeat them confidently.

**Solution**: The Librarian fact-checks every human submission against the actual codebase before publishing. Claims that can be verified in code are verified. Claims that can't be verified go to the review queue flagged as unverifiable — a human reviewer sees exactly which claims need a second look.

### Fact-checking pipeline (Gap 4 — Orchestration)

**Implementation model:** This is a **single Claude API call with tool use** — not a multi-process system, not a message queue, not separate containers. The Librarian is a call to Claude Sonnet that has a `verify_claim` tool registered. Sonnet reads the submission, identifies claims, and calls `verify_claim` for each one. The .NET process implements `verify_claim` as a codebase grep. Results flow back to Sonnet, which synthesizes the final report. Everything runs inside `LibrarianOrchestrator.cs` in the same .NET process.

**`LibrarianOrchestrator.cs` (simplified):**

```csharp
public async Task<VerificationReport> FactCheckAsync(string markdownContent)
{
    var tools = new[]
    {
        new Tool("verify_claim",
            "Search the codebase for evidence of a factual claim.",
            new {
                type = "object",
                properties = new {
                    claim       = new { type = "string", description = "The claim to verify" },
                    repo_path   = new { type = "string", description = "Repo path to search (from configured list)" },
                    search_term = new { type = "string", description = "Symbol, path, or keyword to grep for" }
                },
                required = new[] { "claim", "repo_path", "search_term" }
            })
    };

    var messages = new List<Message>
    {
        new(Role.User, $"""
            Fact-check the following knowledge page submission.
            For each verifiable factual claim (endpoint paths, class names, event names,
            data flows, schema fields), call verify_claim to check it against the codebase.
            Classify each claim as VERIFIED, CONTRADICTED, or UNVERIFIABLE.

            Submission:
            {markdownContent}
            """)
    };

    // Agentic loop: Sonnet calls verify_claim until it has checked all claims
    while (true)
    {
        var response = await _anthropic.Messages.CreateAsync(new()
        {
            Model    = "claude-sonnet-4-6",
            MaxTokens = 4096,
            Tools    = tools,
            Messages = messages
        });

        if (response.StopReason == "end_turn") break;

        var toolResults = new List<ToolResultContent>();
        foreach (var toolUse in response.Content.OfType<ToolUseContent>())
        {
            var result = toolUse.Name == "verify_claim"
                ? await ExecuteVerifyClaimAsync(toolUse.Input)
                : "Unknown tool";
            toolResults.Add(new(toolUse.Id, result));
        }

        messages.Add(new(Role.Assistant, response.Content));
        messages.Add(new(Role.User, toolResults));
    }

    return ParseReport(messages.Last());
}

private async Task<string> ExecuteVerifyClaimAsync(JsonElement input)
{
    var repoPath   = input.GetProperty("repo_path").GetString()!;
    var searchTerm = input.GetProperty("search_term").GetString()!;
    var matches    = await GrepRepoAsync(repoPath, searchTerm);  // ripgrep or Directory.EnumerateFiles
    return matches.Any()
        ? $"FOUND: {string.Join("; ", matches.Take(3))}"   // "InventoryController.cs:47: ..."
        : "NOT_FOUND";
}
```

**Codebase access:** Repos are local clones on the same machine/container. Configure paths in `.env`:

```
INDEXED_REPOS=C:\code\naf\inventory-api,C:\code\naf\order-service
```

Update via `git pull` on a schedule (cron sidecar in docker-compose, or run manually).

**Conceptual flow:**

```
LibrarianOrchestrator.FactCheckAsync(content)
    │
    ▼  [single Anthropic API session — tool use loop]
Claude Sonnet reads submission
    │
    ├── calls verify_claim("POST /api/v2/stock/adjust", repo, "stock/adjust")
    │       ← .NET greps repo → "FOUND: InventoryController.cs:47"
    │
    ├── calls verify_claim("StockUpdated event", repo, "StockUpdated")
    │       ← .NET greps repo → "FOUND: StockEventEmitter.cs:23 (InventoryAdjusted)"
    │
    └── end_turn → synthesizes VerificationReport with VERIFIED / CONTRADICTED / UNVERIFIABLE
```

---

### Scaffolding note — Cursor vs. Claude Code

**For writing the .NET code:** Cursor works well. Point Cursor's agent at this plan, give it the `ModelContextProtocol` SDK docs, and let it generate the project structure, `Program.cs`, tool classes, and `LibrarianOrchestrator.cs`. The schemas in §11 and the code sketch above are sufficient context.

**For the runtime Librarian:** The implementation runs headlessly inside Docker as a .NET service — completely independent of whichever IDE was used to write it. Do not use Cursor's agent mode as the production Librarian; it is an IDE tool, not a scheduled background service.

### Review queue item format

```markdown
---
review_id: rev_abc123
submitted_by: "Sarah / Backend Squad"
submitted_at: 2026-04-27T14:30:00Z
source: human-submission
draft_path: knowledge/_review/rev_abc123.md
status: pending
verification_summary:
  verified: 3
  contradicted: 1
  unverifiable: 1
---

# Librarian Verification Report

## ✅ Verified Claims
- `POST /api/v2/stock/adjust` — found at `InventoryController.cs:47`
- `StockUpdated event` — found at `StockEventEmitter.cs:23`
- `naf/inventory-api` repo name — confirmed in codebase index

## ❌ Contradicted Claims
- **Submitted**: "emits `StockUpdated` after adjustment"
- **Actual**: Event class is named `InventoryAdjusted` (`StockEventEmitter.cs:23`)
- **Action needed**: Correct the event name in the draft before publishing

## ⚠️ Unverifiable Claims
- "reorder_point column on inventory table" — not found in indexed migrations
- **Action needed**: Verify manually or confirm the column exists in prod

---

# Draft Page (edit as needed, then approve)

[full draft content here]
```

### Reviewer workflow

1. Get notified (email, Teams message, or just check `librarian_status`) that items are in `_review/`
2. Open the review item in the wiki viewer or directly in the file
3. Read the verification report — only look at flagged items, not the whole page
4. Edit the draft if needed (fix the contradicted claim, verify the unverifiable one)
5. Call `knowledge_approve rev_abc123` via MCP tool or the web UI approve button
6. Page publishes, indexes, appears in wiki viewer

---

## 9. Knowledge Lifecycle (Current vs. Planned vs. In-Progress)

One of the most important design decisions: the knowledge base must represent both the current state of the system AND what's coming down the pipeline. These are both valid and valuable — but agents must never confuse them.

### The problem

A Product Owner adds a page describing a planned feature — new endpoints, new data flows, new service dependencies. All valid knowledge. But:

- The fact-checker will fail to verify any claim (the code doesn't exist yet)
- An agent asked "does this feature exist?" must not say yes
- The page should not block the PO's submission just because nothing is built yet

### Solution: lifecycle status on every page

Every knowledge page has a `status` field:

| Status | Meaning | Fact-check behavior | Agent behavior |
|--------|---------|---------------------|---------------|
| `current` | Reflects actual system state | Verify aggressively — flag contradictions | Treat as ground truth |
| `planned` | Approved feature, not yet built | Skip code verification — check architectural consistency instead | Treat as future intent — do not tell users it exists |
| `in-progress` | Currently being built | Partial verification — note what exists and what doesn't | Treat as partial — clarify what is and isn't live |
| `deprecated` | Being phased out or replaced | Verify removal progress, flag if still referenced by live code | Warn agents not to recommend this path |

### Fact-checking by status

**`current` submissions** → full code verification (existing behavior)

**`planned` submissions** → architectural consistency check:

- Are referenced services real and currently deployed?
- Does the proposed data flow contradict existing documented flows?
- Are proposed patterns consistent with current standards?
- Does nothing in the existing codebase already do this (duplication risk)?

Output: publishes with `status: planned`, `confidence: medium`, note that code verification was intentionally skipped.

**`in-progress` submissions** → partial verification:

- Verify what exists, note what doesn't
- Flag the gap explicitly in the review report
- Confidence: `low` until the feature ships and status updates to `current`

### Agent consumption rules (in system prompts)

```
When knowledge_search returns a result:
- status: current   → use as ground truth
- status: planned   → treat as intended future behavior; do NOT say this feature exists today
- status: in-progress → clarify which parts are live; note the rest is in development
- status: deprecated → warn that this approach is being retired; suggest the replacement
```

### Lifecycle transitions

```
planned ──► in-progress ──► current ──► deprecated ──► [archived]
                │                              │
                └── (cancelled) ──► rejected   └── (fully removed)
```

**Automated transitions:**

- CI/CD webhook fires when a PR merges referencing a `planned` page → Librarian flags it for status update to `in-progress` or `current`
- Lint pass: `planned` pages older than 90 days get flagged (may be cancelled, forgotten, or silently shelved)
- `deprecated` pages with no linked replacement get flagged in lint

**Manual transitions:**

- PO or developer calls `knowledge_store` with updated `status` field when a feature ships
- The wiki viewer surfaces a "Mark as Current" button on `in-progress` pages visible to the submitter

### Submission form additions

The submission form gains a **Status** field (required, dropdown):

- Current state — reflects existing code
- Planned feature — approved but not yet built
- In progress — being built now
- Deprecating — this approach is being retired

And an optional **Links** field: ADO work item, PR, or design doc URL. Used as the `sources` reference when no code exists yet.

---

## 10. Knowledge Governance (Preventing Hallucination Drift)

The threat: the Librarian or a submitter writes a wrong fact, it gets embedded, and every future agent confidently repeats it. It compounds — hallucinated facts get cited and reinforced.

**Defense in depth:**

### Sources required

Every knowledge page must cite a `sources` field pointing to the actual artifact it was derived from (file, URL, commit, PR, meeting note). The Librarian cannot write facts it did not read from an input. Claims without a source are marked `unverified`.

### Review queue is the firewall

Low-confidence content never auto-publishes. The queue is non-bypassable — even the Librarian cannot force-publish a page that fails the confidence check.

**Auto-approve rules:**

- Source file has explicit `domain:` frontmatter AND confidence ≥ 0.80 AND no contradictions → auto-publish
- Contradicts an existing page → always review
- Human submission with any contradicted claim → always review

### Contradiction detection at ingest

Before publishing, the Librarian checks for conflicts with existing pages. Contradictions surface both pages in `_review/` for resolution.

### Confidence decays over time

During lint passes, pages not updated in N months automatically downgrade: `high → medium → low`. Stale pages surface in `librarian_status`. Old content cannot appear authoritative indefinitely.

### Search results carry confidence metadata

`knowledge_search` returns `confidence` and `updated` fields with every result. Agent system prompts should instruct: "If confidence is `low` or `unverified`, say so explicitly and recommend verification."

### Git is the audit trail

Every page change is a commit. If bad data is found: `git log` shows when it appeared and from what source. `git revert` removes it cleanly. The vector index rebuilds from the corrected files on next startup.

---

## 11. Data Model (PostgreSQL + pgvector)

```sql
-- One row per knowledge page
CREATE TABLE knowledge_documents (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    file_path   TEXT UNIQUE NOT NULL,
    title       TEXT NOT NULL,
    domain      TEXT,
    tags        TEXT[],
    content     TEXT NOT NULL,
    hash        TEXT NOT NULL,          -- SHA256; incremental indexing
    confidence  TEXT NOT NULL DEFAULT 'unverified',
    status      TEXT NOT NULL DEFAULT 'current',  -- current | planned | in-progress | deprecated
    sources     TEXT[],
    submitted_by TEXT,                  -- populated for human submissions
    updated_at  TIMESTAMPTZ DEFAULT now()
);

-- One row per chunk (~512 tokens each)
CREATE TABLE knowledge_chunks (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id UUID REFERENCES knowledge_documents(id) ON DELETE CASCADE,
    chunk_index INT NOT NULL,
    content     TEXT NOT NULL,
    embedding   vector(1536)
);

CREATE INDEX ON knowledge_chunks USING ivfflat (embedding vector_cosine_ops);

-- Human submissions and Librarian-uncertain items pending review
CREATE TABLE knowledge_review_queue (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    draft_path      TEXT NOT NULL,
    submitted_by    TEXT,
    reason          TEXT NOT NULL,
    confidence      FLOAT,
    verification    JSONB,             -- full verification report from fact-checker
    submitted_at    TIMESTAMPTZ DEFAULT now(),
    status          TEXT NOT NULL DEFAULT 'pending'
);
```

---

## 12. MCP Tools (Gap 3 — Schemas)

Each tool is a C# class decorated with `[McpServerTool]` from the `ModelContextProtocol` SDK. All inputs are strongly typed; all outputs are JSON-serializable records.

```csharp
// ── knowledge_search ─────────────────────────────────────────────────────────
[McpServerTool, Description("Semantic search over the knowledge wiki")]
public record KnowledgeSearchInput(
    [property: Description("Natural language query")] string Query,
    [property: Description("Filter by domain (org|products|architecture|codebase|standards)")] string? Domain = null,
    [property: Description("Filter by tag")] string? Tag = null,
    [property: Description("Filter by status (current|planned|in-progress|deprecated)")] string? Status = null,
    [property: Description("Max results to return (default 5)")] int Limit = 5
);

public record KnowledgeSearchResult(
    string FilePath,      // relative path in knowledge/
    string Title,
    string Snippet,       // best matching chunk text
    string Domain,
    string Confidence,    // high | medium | low | unverified
    string Status,        // current | planned | in-progress | deprecated
    DateTimeOffset Updated,
    float Score
);

// Returns: List<KnowledgeSearchResult>

// ── knowledge_retrieve ────────────────────────────────────────────────────────
[McpServerTool, Description("Fetch a full knowledge page by path or title")]
public record KnowledgeRetrieveInput(
    [property: Description("Relative file path (e.g. products/inventory/overview.md) OR exact title")] string PathOrTitle
);
// Returns: KnowledgeDocument (full content + frontmatter fields)

// ── knowledge_store ───────────────────────────────────────────────────────────
[McpServerTool, Description("Write or update a knowledge page. Creates file and indexes it.")]
public record KnowledgeStoreInput(
    [property: Description("Relative path under knowledge/ to write to")] string FilePath,
    [property: Description("Full markdown content including YAML frontmatter")] string Content,
    [property: Description("Reason for this write (logged in changelog)")] string Reason
);
// Returns: { FilePath, IndexedChunks: int, Action: "created"|"updated" }

// ── get_product_map ───────────────────────────────────────────────────────────
[McpServerTool, Description("Returns the full product catalog with team ownership and dependencies")]
// No input parameters
// Returns: List<ProductEntry>
public record ProductEntry(
    string Name, string Purpose, string Team, string[] Repos,
    string[] DependsOn, string[] ConsumedBy, string Status
);

// ── get_org_context ───────────────────────────────────────────────────────────
[McpServerTool, Description("Returns org structure, team list, and NAF glossary")]
// No input parameters
// Returns: OrgContext
public record OrgContext(
    string CoreSummary,         // full text of org/_core.md
    List<TeamEntry> Teams,
    List<GlossaryEntry> Glossary
);

// ── get_codebase_context ──────────────────────────────────────────────────────
[McpServerTool, Description("Returns the knowledge summary for a named repository")]
public record GetCodebaseContextInput(
    [property: Description("Repository name (e.g. naf/inventory-api)")] string RepoName
);
// Returns: KnowledgeDocument for knowledge/codebase/{repo}/overview.md, or null if not indexed

// ── librarian_run ─────────────────────────────────────────────────────────────
[McpServerTool, Description("Trigger a Librarian operation")]
public record LibrarianRunInput(
    [property: Description("Operation: ingest | lint | summarize_repo | fact_check")] string Operation,
    [property: Description("For summarize_repo: absolute path to local repo clone. For fact_check: review_id.")] string? Argument = null
);
// Returns: LibrarianRunResult
public record LibrarianRunResult(
    string Operation, bool Success,
    int ItemsProcessed, int ItemsQueued,
    string[] Errors, string LogEntry
);

// ── librarian_status ──────────────────────────────────────────────────────────
[McpServerTool, Description("Returns knowledge base health and Librarian status")]
// No input parameters
// Returns: LibrarianStatus
public record LibrarianStatus(
    int TotalDocuments, int TotalChunks,
    int PendingReviewCount,
    DateTimeOffset? LastIngest, DateTimeOffset? LastLint,
    string[] RecentChangelog  // last 10 entries from _meta/changelog.md
);

// ── knowledge_approve ─────────────────────────────────────────────────────────
[McpServerTool, Description("Approve a pending review item — publishes to knowledge/ and indexes")]
public record KnowledgeApproveInput(
    [property: Description("review_id from the _review/ item frontmatter")] string ReviewId,
    [property: Description("Optional: override the suggested file path")] string? OverridePath = null
);
// Returns: { PublishedPath, IndexedChunks: int }

// ── knowledge_reject ──────────────────────────────────────────────────────────
[McpServerTool, Description("Reject a pending review item")]
public record KnowledgeRejectInput(
    [property: Description("review_id to reject")] string ReviewId,
    [property: Description("Reason for rejection (stored in changelog)")] string Reason
);
// Returns: { ReviewId, Status: "rejected" }
```

**Summary table:**

| Tool | Input | Returns |
|------|-------|---------|
| `knowledge_search` | `KnowledgeSearchInput` | `List<KnowledgeSearchResult>` |
| `knowledge_retrieve` | `KnowledgeRetrieveInput` | `KnowledgeDocument` |
| `knowledge_store` | `KnowledgeStoreInput` | `{ FilePath, IndexedChunks, Action }` |
| `get_product_map` | *(none)* | `List<ProductEntry>` |
| `get_org_context` | *(none)* | `OrgContext` |
| `get_codebase_context` | `GetCodebaseContextInput` | `KnowledgeDocument?` |
| `librarian_run` | `LibrarianRunInput` | `LibrarianRunResult` |
| `librarian_status` | *(none)* | `LibrarianStatus` |
| `knowledge_approve` | `KnowledgeApproveInput` | `{ PublishedPath, IndexedChunks }` |
| `knowledge_reject` | `KnowledgeRejectInput` | `{ ReviewId, Status }` |

---

## 13. Always-On Context (Tier 1)

A single curated file injected into every AI session. Not retrieved — always present. Agents know this without calling a tool.

**`knowledge/org/_core.md`** (~100 lines, maintained manually):

```markdown
# NAF Company Context

**What we do**: [one sentence]
**Primary stack**: .NET / Azure / Windows. Angular frontend. Azure SQL.

## Products (quick reference)
- **Product A**: [purpose]. Team: X. Repo: `naf/product-a`.
- **Product B**: [purpose]. Team: Y. Repo: `naf/product-b`.

## Teams
- **Backend Squad**: owns inventory, orders, auth
- **Frontend Squad**: owns all Angular UIs

## Key terminology
- **[NAF term]**: what it means in this context
```

Embedded in: `CLAUDE.md`, `.cursorrules`, `.vscode/mcp.json` system prompt, Copilot instructions.

---

## 14. Client Setup

Commit to each company repo:

**`.cursor/mcp.json`** / **`.vscode/mcp.json`** / **`.mcp.json`** (Claude Code):

```json
{
  "servers": {
    "naf-knowledge": {
      "type": "http",
      "url": "http://localhost:8081/mcp",
      "headers": { "Authorization": "Bearer ${NAF_KNOWLEDGE_KEY}" }
    }
  }
}
```

Once deployed: swap `localhost:8081` for the team URL. Each dev sets `NAF_KNOWLEDGE_KEY` in their shell profile.

**GitHub Copilot (VS Code agent mode)**: Uses `.vscode/mcp.json` — VS Code agent mode has supported MCP since early 2025.

**GitHub Copilot (other IDEs / web)**: No MCP support. Inject Tier 1 context via `.github/copilot-instructions.md`.

---

## 15. Docker Compose (Full Stack)

```yaml
services:
  postgres:
    image: postgres:16
    environment:
      POSTGRES_DB: naf_knowledge
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - postgres_data:/var/lib/postgresql/data
    ports:
      - "5432:5432"
    healthcheck:
      test: ["CMD", "pg_isready", "-U", "${POSTGRES_USER}"]

  mcp-server:
    build: ./src/NAF.Knowledge.Mcp
    environment:
      ConnectionStrings__Default: "Host=postgres;Database=naf_knowledge;..."
      AzureOpenAI__ApiKey: ${AZURE_OPENAI_KEY}
      AzureOpenAI__Endpoint: ${AZURE_OPENAI_ENDPOINT}
      Anthropic__ApiKey: ${ANTHROPIC_API_KEY}
      MCP__ApiKey: ${NAF_KNOWLEDGE_KEY}
    ports:
      - "8081:8080"    # MCP + web API
    depends_on:
      postgres: { condition: service_healthy }

  mkdocs:
    image: squidfunk/mkdocs-material:latest
    volumes:
      - ./knowledge:/docs/knowledge:ro
      - ./mkdocs.yml:/docs/mkdocs.yml:ro
    ports:
      - "8090:8000"
    command: serve --dev-addr=0.0.0.0:8000

  litellm:
    image: ghcr.io/berriai/litellm:main-latest
    volumes:
      - ./litellm-config.yaml:/app/config.yaml:ro
    ports:
      - "4000:4000"    # LLM gateway
    environment:
      LITELLM_MASTER_KEY: ${LITELLM_MASTER_KEY}
    command: --config /app/config.yaml

volumes:
  postgres_data:
```

---

## 16. Initial Knowledge Seed

The goal of Phase 1 is to get enough knowledge into the system that agents immediately become more useful — not to capture everything. Prioritize by **frequency of agent error**: what do agents get wrong most often today?

### Ingestion path by file type

| Input type | Format | How to ingest |
|-----------|--------|---------------|
| Architecture docs | `.md`, `.docx`, `.pdf` | Drop in `raw/pending/` → Librarian extracts → wiki page |
| Code standards | `.md`, `.docx` | Drop in `raw/pending/` or write directly in `knowledge/standards/` |
| Diagrams (raster) | `.jpg`, `.png` | Drop in `raw/pending/` → Vision subagent describes → wiki page with embedded description |
| Diagrams (Mermaid) | `.md` with ` ```mermaid ` block | Write directly or drop → renders natively in MkDocs |
| Diagrams (DrawIO) | `.drawio` | Export to `.png` first, then drop in `raw/pending/` |
| OpenAPI / Swagger specs | `.yaml`, `.json` | Drop in `raw/pending/` → Librarian extracts endpoints → `api-surface.md` |
| Repo READMEs | `.md` | Drop in `raw/pending/` → generates `codebase/{repo}/overview.md` |
| Azure DevOps wiki export | `.md` files | Batch drop in `raw/pending/` (even stale content is a starting point) |
| Meeting notes / design docs | `.docx`, `.pdf`, `.md` | Drop in `raw/pending/` → status: `planned` or `current` as appropriate |
| ADRs (Architecture Decision Records) | `.md` | Drop directly into `knowledge/architecture/decisions/` |

**Diagrams specifically:** Images are handled by a vision subagent that reads the diagram and generates a structured LLM-wiki description. The original image is stored in `knowledge/_assets/` and linked from the page. Mermaid diagrams embedded in markdown are the best format — they render visually in MkDocs and remain readable as plain text for LLMs.

---

### Priority order (what to gather first)

#### Tier 1 — Gather before any team rollout

These are the pages agents need to stop hallucinating org facts. Without them, every agent session starts from zero.

| What | Where it goes | Why first |
|------|--------------|-----------|
| **Org chart + team list** | `knowledge/org/teams.md` | Agents constantly guess team names and ownership wrong |
| **Product → team ownership map** | `knowledge/org/ownership-map.md` | "Who owns X?" is the most common question with no good answer today |
| **Company glossary** | `knowledge/org/glossary.md` | NAF-specific terms, acronyms, product names that LLMs don't know |
| **Product catalog** | `knowledge/products/product-catalog.md` | One-liner per product — gives agents a map of the whole system |
| **System architecture overview** | `knowledge/architecture/system-overview.md` | Even a rough diagram + description stops wrong architectural advice |
| **Always-on core** | `knowledge/org/_core.md` | Distills Tier 1 into the small file that goes into every session |

**Effort**: 1–2 days. Most of this lives in someone's head — just write it down in LLM wiki format. Stale Azure DevOps content is a starting point; mark it `confidence: low` and clean up over time.

---

#### Tier 2 — Gather during team rollout

These make agents useful for actual development work, not just org questions.

| What | Where it goes | Format hint |
|------|--------------|-------------|
| **Product detail pages** (top 5–10 services) | `knowledge/products/{name}/overview.md` | Purpose, repos, stack, data flows, gotchas, standards that apply |
| **Core data flow diagrams** | `knowledge/architecture/data-flows/` | Mermaid preferred; PNG/JPG accepted — vision subagent will describe them |
| **Coding standards** | `knowledge/standards/coding/` | One page per language/framework: .NET, Angular, SQL, etc. |
| **API standards** | `knowledge/standards/api/` | Versioning, error format, auth pattern, pagination conventions |
| **Database schema overview** | `knowledge/architecture/data-model.md` | Key tables, relationships, naming conventions — not the full DDL |
| **Internal API surfaces** | `knowledge/products/{name}/api-surface.md` | Key endpoints, request/response shape, auth — from Swagger or README |
| **Security standards** | `knowledge/standards/security/` | Auth patterns, secrets policy, OWASP requirements in use |
| **Development workflow** | `knowledge/standards/process/dev-workflow.md` | PR process, code review expectations, branch strategy |

**Effort**: 3–5 days spread over the rollout period. Automate where possible: OpenAPI specs → api-surface.md, repo READMEs → codebase overviews.

---

#### Tier 3 — Ongoing ingestion

Not a one-time task — set up the Librarian schedule and let it accumulate over time.

| What | Source | Ingestion method |
|------|--------|-----------------|
| **All Azure DevOps wiki content** | Wiki export | Batch drop in `raw/pending/`; mark `confidence: low` initially |
| **Repo READMEs** | Each repo | `librarian_run scan {repo_path}` or CI webhook |
| **Architecture Decision Records (ADRs)** | `docs/decisions/` in each repo | Drop or scan; goes to `knowledge/architecture/decisions/` |
| **Meeting notes / design discussions** | Teams export, email, Notion | Drop in `raw/pending/`; Librarian summarizes and categorizes |
| **Post-mortems / incident reports** | Any format | Drop in `raw/pending/`; goes to `knowledge/architecture/incidents/` — high value for gotcha knowledge |
| **Onboarding docs** | Existing HR/IT docs | Drop in `raw/pending/`; useful for new dev context |
| **Third-party tool / library notes** | Any format | Goes to `knowledge/reference/` |

---

### What makes a good seed page

Good seed content answers questions agents get wrong today. To identify what to write first, ask:

- What do new developers always ask about in their first week?
- What wrong assumptions do code reviewers correct most often?
- What does every PR comment about that could just be a standard?
- What context do you repeat to an AI agent every time you start a session?

Those answers are your Tier 1 content.

**Format checklist for every seed page:**

- [ ] `title`, `type`, `team`, `status`, `confidence`, `sources` in frontmatter
- [ ] **Purpose** — one sentence, what it does
- [ ] **Connects to** — explicit `depends_on` / `consumed_by` in frontmatter OR a "Connects to" section
- [ ] **Gotchas** — at least one non-obvious fact; if you can't think of one, ask the team lead
- [ ] No narrative prose — assertions only
- [ ] Links to related pages where they exist

---

## 17. Implementation Phases

### Phase 0 — Scaffold (Weekend 1, Day 1)

- [ ] Create `naf-knowledge` git repo on work laptop
- [ ] `docker-compose.yml`: postgres + mcp-server placeholder + mkdocs + litellm
- [ ] `.env.example` with all required vars
- [ ] .NET 10 solution: `NAF.Knowledge.Mcp`, startup, DI, health check
- [ ] SQL migrations: `knowledge_documents`, `knowledge_chunks`, `knowledge_review_queue`
- [ ] Stub MCP server: `knowledge_search` returning hardcoded test result
- [ ] MkDocs serving `knowledge/` on `:8090`
- [ ] Verify: `docker compose up` → Cursor connects → calls `knowledge_search` → gets response → wiki visible in browser

### Phase 1 — Seed Content (Weekend 1, Day 2)

See §15 for full details on what to gather, ingestion paths by file type, and priority order.

**Tier 1 checklist** (must complete before moving to Phase 2):

- [ ] `knowledge/org/teams.md` — team names, members, responsibilities
- [ ] `knowledge/org/ownership-map.md` — team → product ownership
- [ ] `knowledge/org/glossary.md` — NAF-specific terms and acronyms
- [ ] `knowledge/products/product-catalog.md` — all products, one-liner each
- [ ] `knowledge/architecture/system-overview.md` — rough system map + any existing diagrams dropped in `raw/pending/`
- [ ] `knowledge/org/_core.md` — distilled always-on context (Tier 1 injection file)
- [ ] Add `_core.md` content to `CLAUDE.md` and `.cursorrules` in one test repo — verify agent picks it up

### Phase 2 — Indexer + Real Search (Weekend 2)

- [ ] `DocumentIndexer.cs`: scan `knowledge/`, hash files, chunk, embed, upsert pgvector
- [ ] Incremental startup indexing (only changed files)
- [ ] Wire Azure OpenAI `text-embedding-3-small`
- [ ] Real `knowledge_search` with pgvector kNN
- [ ] `knowledge_retrieve` by path or title
- [ ] End-to-end: write page → restart → search → returned correctly

### Phase 3 — Drop Zone + Librarian Ingestion (Weekend 3)

- [ ] `raw/pending/` drop zone + `.gitignore` entries
- [ ] Librarian agent: raw file → Claude Sonnet → wiki-format markdown → `knowledge/`
- [ ] `_review/` queue + `knowledge_approve` / `knowledge_reject` MCP tools
- [ ] `librarian_run ingest` MCP tool
- [ ] `librarian_run summarize_repo {path}` → `knowledge/codebase/{repo}/overview.md`
- [ ] `_meta/index.md` and `_meta/changelog.md` auto-maintenance

### Phase 4 — Web Submission + Fact-Checking (Weekend 4)

- [ ] Submission form at `/submit` (Toast UI Editor WYSIWYG + domain dropdown + tags)
- [ ] `POST /api/knowledge/submit` endpoint → saves draft to `_review/`
- [ ] `CodeVerifierAgent`: given a claim, searches codebase for evidence, returns VERIFIED / CONTRADICTED / UNVERIFIABLE
- [ ] Librarian fact-check pipeline: parse submission → spawn verifier subagents → generate report → attach to review item
- [ ] Approve button in wiki viewer (calls `knowledge_approve` via API)
- [ ] Notification on new review items (Teams webhook or email — simple HTTP POST)

### Phase 5 — Lint + Governance Tooling (Weekend 4–5)

- [ ] `librarian_run lint`: orphan pages, missing `sources`, confidence-decay by age, contradiction detection
- [ ] `librarian_status`: index health, review queue count, last lint, changelog tail
- [ ] `knowledge_search` response includes `confidence` and `updated` fields
- [ ] Agent system prompt template: instructs agents to surface low-confidence results explicitly

### Phase 6 — Agent Platform (`naf-agents` repo)

- [ ] Create `naf-agents` git repo
- [ ] Write canonical agent definitions for backend-dev, frontend-dev, code-reviewer, spec-writer
- [ ] Write skill definitions: `/spec`, `/implement`, `/review`, `/test`, `/commit`, `/wrapup`
- [ ] Write `feature-delivery.workflow.md` gate sequence
- [ ] `compile.js`: generates `.cursorrules`, `CLAUDE.md`, `copilot-instructions.md` from canonical format
- [ ] `validate.js`: checks required fields, valid model names, valid tool tiers
- [ ] CI: validate + compile on every PR
- [ ] Commit `.claude/skills/` from `naf-agents/skills/` to each main company repo

### Phase 7 — Team Rollout

- [ ] Deploy to Azure VM (`docker-compose.prod.yml`)
- [ ] Per-dev API key (Azure Key Vault or env file)
- [ ] Commit MCP config files to each main company repo
- [ ] Add `_core.md` content to `CLAUDE.md` / `.cursorrules` / `copilot-instructions.md`
- [ ] Team onboarding doc: set env var → restart IDE → test query → test submission form
- [ ] Run `librarian_run summarize_repo` against top 5 codebases

### Phase 8 — Azure APIM AI Gateway

- [ ] Azure APIM with AI Gateway policy as the public MCP endpoint
- [ ] AAD auth — team gets token via `az login`, no manual API keys
- [ ] Per-team rate limiting, usage analytics, audit logs
- [ ] Azure Pipelines webhook: push to main → `librarian_run summarize_repo` keeps codebase context fresh
- [ ] Migrate LiteLLM gateway → APIM for LLM routing

---

## Open Questions

| # | Question | Recommendation |
|---|----------|----------------|
| 1 | Azure OpenAI vs. OpenAI direct for embeddings | Azure OpenAI — consistent with company infra, same APIM path |
| 2 | Hybrid search (BM25 + vector)? | Start vector-only; add `pg_trgm` keyword boosting if recall is weak in practice |
| 3 | Which codebases get indexed for fact-checking? | Start with the 3–5 most-referenced; configure as a list in `.env` |
| 4 | Submission notifications | Teams webhook is simplest for a Windows/.NET shop |
| 5 | `naf-agents` repo: private GitHub vs. Azure DevOps | Wherever company repos live; match the existing VCS |
| 6 | MkDocs vs. custom Angular wiki viewer | MkDocs for now; custom viewer only if you need login-gated access or inline editing |
| 7 | Fact-checker: which repos does it have access to? | Local clones on the server; configure paths in `.env`; update via `git pull` on a schedule |
