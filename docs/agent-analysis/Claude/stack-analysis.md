# AI Stack Assessment — Claude (Claude Code / Anthropic)

> **Tool:** Claude Code (claude-sonnet-4-6)
> **Date:** 2026-04-21
> **Scope:** Full-stack assessment of `ai-stack` repository — MCP server, agent library, workflows, guardrails, templates, and runtime integration.

---

## 1) Executive Summary

- **Dual-plane architecture is coherent and well-designed.** The split between file-based authoring (`/library/`) and MCP runtime (`/apps/mcp-server/`) is a sound pattern — policy, routing, and observability all flow correctly from config through to tooling.
- **Tool coverage is production-grade.** 50+ MCP tools across 15 classes cover the SDLC end-to-end: orchestration, routing, policy, memory, CI/build, analysis, and knowledge retrieval.
- **Guardrails have architectural intent but partial enforcement.** Tool tier policy (A/B/C), budget gating, and agent contracts exist — but audit persistence is missing, approval tokens are plain-text, and schema validation is presence-only (not content-validity).
- **Zero automated tests.** No `*Tests.cs` files exist anywhere in the repo. This is the single largest production risk.
- **CI/CD is half-built.** `guardrails-check.yml` works; `ci-mcp.yml` is empty; deploy workflows are opaque. The agentic dev loop has no automated quality gate on the server itself.
- **Workflow definitions are code-coupled, not data-driven.** `WorkflowTools.cs` hardcodes 5 workflow definitions; the authoritative markdown specs in `/library/workflows/` are ignored at runtime. Any workflow edit requires a code change.
- **Plan docs lag reality.** `agentic-platform-upgrade.plan.md` lists policy_preflight, workflow state, routing budgets, and caching as "to implement" — all are already fully implemented. Planning artifacts are now misleading.
- **Observability metrics exist but no persistent audit trail.** `PlatformMetrics` emits counters; `security-guardrails.md` mandates an audit envelope (actor, op, decision, timestamp) — no persistent table backs it.
- **Routing budget is in-memory.** `RoutingBudgetService` tracks per-workflow spend in a `ConcurrentDictionary`. A restart or second replica silently resets budgets, voiding the cost control story.
- **Maturity assessment:** Strong internal developer tooling, not yet safe for multi-tenant or production-critical use. The foundation is solid; the gaps are targeted and fixable in 60–90 days.

---

## 2) Prioritized Recommendations

| Priority | Recommendation | Why It Matters | Effort | Risk | Suggested First Step |
|----------|----------------|----------------|--------|------|----------------------|
| **P0** | Add unit + integration tests for MCP server | Zero coverage means any refactor or dependency bump is flying blind; blocks confident CI | M | Low | Create `Ryan.MCP.Mcp.Tests` xUnit project; start with `PolicyPreflightService` and `RoutingBudgetService` |
| **P0** | Complete `ci-mcp.yml` with build/test/publish | Without this, broken server code ships silently; guardrails-check only validates library files | S | Low | Add `dotnet build` → `dotnet test` → `docker build` steps; gate PRs on green |
| **P0** | Move approval token to environment secret | Plain-text `ApprovalToken` in `appsettings.json` means any repo access = full Tier B/C bypass | S | Low | Remove from config, inject via `MCP_APPROVAL_TOKEN` env var; document in `.env.example` |
| **P1** | Persist audit log for Tier B/C operations | `security-guardrails.md` mandates audit envelopes; currently only in-memory metrics, no queryable history | M | Low | Add `audit_log` table via migration 005; write from `PolicyPreflightService.RecordAudit()` |
| **P1** | Make workflow definitions data-driven | Markdown specs in `/library/workflows/` are authoritative but ignored; any workflow edit requires a server code change and redeploy | M | Med | Refactor `WorkflowTools.cs` to load definitions by parsing `*.workflow.md` frontmatter at startup (same pattern as `AgentIngestionCoordinator`) |
| **P1** | Persist routing budget to PostgreSQL | `RoutingBudgetService` uses `ConcurrentDictionary`; restart or second replica silently resets spend counters, making per-workflow budgets unreliable | M | Low | Add `routing_budget_snapshots` table; flush on budget-check + restore on startup |
| **P1** | Add `dotnet publish` + Docker build to CI | No container image is built or pushed in CI; deployment is currently manual and untested | S | Low | Extend `ci-mcp.yml` with `docker build`/`docker push` on `main` merge |
| **P2** | Validate agent contract field content (not just presence) | CI checks that 5 guardrail fields exist but not that `allowed_tools` values are real tool names or `budget_tier` is valid (`S`/`M`/`L`) | S | Low | Extend `validate-agent-contract.mjs` to check against tool registry and known tier values |
| **P2** | Add HTTP rate limiting to MCP endpoints | No rate limiting on `/mcp`, `/api/*` — a runaway agent can exhaust budget or generate noise in metrics | S | Low | Add `builder.Services.AddRateLimiter(...)` in `Program.cs` with per-IP sliding window |
| **P2** | Archive / close `agentic-platform-upgrade.plan.md` | 11/11 tasks show as complete but body text implies future work; actively misleads planning context for agents using this as context | S | Low | Mark all tasks done, add note at top: "Completed [date]; archived." |

---

## 3) Gaps and Risks

### Gap 1: No Automated Test Coverage

- **Evidence:** No `*Test.cs` or `*Tests.cs` files anywhere. No test project in the solution. `ci-mcp.yml` is a 1-line stub.
- **Consequence:** Any change to `PolicyPreflightService`, `RoutingBudgetService`, `WorkflowStateTools`, or model mapping logic ships unverified. A one-character misconfiguration in command allowlist enforcement could silently allow or block legitimate operations.
- **Mitigation:** Create `Ryan.MCP.Mcp.Tests` (xUnit + Moq). Priority test surfaces: policy evaluation logic, budget spend/enforce cycle, workflow state upsert/get roundtrip, agent contract validation, model recommendation ranking.
- **Confidence:** High

---

### Gap 2: Workflow Specs Disconnected from Runtime

- **Evidence:** `WorkflowTools.cs` contains hardcoded `WorkflowDefinition` objects for 5 workflows. `/library/workflows/feature-delivery.workflow.md`, `incident-triage.workflow.md`, etc. contain the authoritative definitions but are never parsed.
- **Consequence:** Adding a gate, renaming a step, or adding a stop condition in markdown has zero effect on MCP behavior. The two representations will diverge silently over time. Any library author editing workflow docs is under a false belief that those edits matter.
- **Mitigation:** Refactor `WorkflowTools.cs` to load definitions from markdown via a `WorkflowIngestionCoordinator` (mirror of `AgentIngestionCoordinator`). Parse frontmatter + phase structure at startup. File-watch for hot reload.
- **Confidence:** High

---

### Gap 3: Plain-Text Approval Token

- **Evidence:** `McpOptions.Policy.ApprovalToken` (and `ApprovalTokens` list) in `appsettings.json`. `PolicyPreflightService` does exact string match against this value.
- **Consequence:** Anyone with read access to the config file, container env dump, or CI logs gains Tier B/C bypass. In a multi-developer or CI/CD context this is a high-severity credential exposure.
- **Mitigation:** Remove from `appsettings.json`; bind from `MCP_APPROVAL_TOKEN` environment variable. Add to `.env.example`. Document rotation procedure. Medium-term: integrate with Azure Key Vault or similar.
- **Confidence:** High

---

### Gap 4: Missing Persistent Audit Log

- **Evidence:** `security-guardrails.md` mandates: *"Capture actor, operation, scope, decision, result, timestamp"* for Tier B/C ops. `PlatformMetrics` records allow/deny counts but no queryable audit record. No `audit_log` table in migrations 001–004.
- **Consequence:** Cannot answer "who triggered what, when, and why" in post-incident review. Compliance posture claimed in governance docs is not backed by implementation.
- **Mitigation:** Migration 005: `audit_log (id, actor, operation_category, tool_name, decision, approval_token_hash, context_json, created_utc)`. Write from `PolicyPreflightService` on every Tier B/C call.
- **Confidence:** High

---

### Gap 5: In-Memory Routing Budget (Not Crash-Safe or Multi-Replica-Safe)

- **Evidence:** `RoutingBudgetService.cs` uses `private readonly ConcurrentDictionary<string, decimal> _workflowSpend`. No persistence path. `appsettings.json` has `EnforcePerWorkflowBudget: true` with real dollar limits.
- **Consequence:** Server restart zeroes all spend counters, allowing a workflow to re-spend its full budget. Horizontal scaling (2 replicas) doubles the effective budget limit. Cost controls are cosmetic.
- **Mitigation:** Add `routing_budget_snapshots (workflow_command, period_start, spent_usd)` table. Flush on each spend event (debounced) and restore on startup. For multi-replica, use advisory locks or atomic increment.
- **Confidence:** High

---

### Gap 6: External MCP Connectors Are Untested

- **Evidence:** `appsettings.json` defines 10+ connectors (GitHub, Azure DevOps, fetch, memory, deep-wiki, etc.). All are `"enabled": false` in `appsettings.Development.json`. No integration tests or smoke tests for any connector path.
- **Consequence:** When connectors are enabled in production, there's no confidence they work. Auth failures, protocol mismatches (version `2025-06-18` vs actual endpoint), or schema drift will surface only at runtime.
- **Mitigation:** Add a `ConnectorHealthCheck` that runs at startup for each enabled connector (ping + version negotiation). Wire into `/health/ready`. Add at least one integration test per connector type (HTTP mock).
- **Confidence:** Medium (connectors may be intentionally deferred)

---

### Gap 7: No Agent-Level Authorization

- **Evidence:** All 47 agents in `/library/agents/` are returned by `list_agents` and `get_agent` to any connected client. No client identity, role, or scope check in `AgentTools.cs`. The `scope` frontmatter field exists (values: `global`, `backend`, `frontend`) but is not enforced by any gate.
- **Consequence:** In a multi-team or multi-tenant deployment, a client can discover and invoke any agent regardless of team scope.
- **Mitigation:** Add optional scope-based filtering to `list_agents` and `get_agent` keyed on client metadata (header or MCP session field). This pairs naturally with the `scope` frontmatter already present.
- **Confidence:** Medium (may be acceptable for single-team use)

---

### Gap 8: No Workflow Execution Engine (Gate Enforcement is Advisory Only)

- **Evidence:** `WorkflowTools.cs` provides `start_workflow`, `run_workflow_step`, `resolve_workflow_trigger` — these return prompts/guidance strings. There is no state machine that enforces gate progression, validates exit criteria, or blocks advancement without required artifacts.
- **Consequence:** Gate enforcement is purely agent discipline. An agent can skip Phase 2 entirely and call `run_workflow_step` for Phase 4 with no obstruction. The workflow docs' `stop_conditions` and `required_artifacts` are advisory only.
- **Mitigation:** Extend `WorkflowStateTools` to track current phase; enforce that `run_workflow_step` only advances when previous gate exit criteria are met (or are explicitly overridden with approval). This is a larger lift but is the difference between guidance and enforcement.
- **Confidence:** High (architectural limitation, not a bug)

---

## 4) Tooling Additions

### Tool 1: `spectral` — CI / Schema Validation

- **Category:** CI
- **Exact problem solved:** `agent-contract.schema.json`, `workflow-spec.schema.json`, and 5 other schemas in `/library/schemas/` are never validated against the library files. `validate-agent-contract.mjs` does field presence checks only — missing type/value/enum violations (e.g., `budget_tier: XL`, malformed `escalation_triggers`).
- **Expected productivity gain:** Catches invalid fields at PR time rather than runtime. ~15–30 min saved per PR touching agents; eliminates silent schema drift.
- **Integration approach:** Add `npx @stoplight/spectral-cli lint library/agents/**/*.agent.md --ruleset library/schemas/agent-contract.schema.json` step in `guardrails-check.yml`.
- **Prerequisites:** Node.js (already in CI). Ruleset customization for markdown frontmatter.
- **Cost/complexity:** Free (open source). Low — 1 CI step addition.
- **Why top-5:** The schema contracts already exist; this closes the enforcement gap with zero architectural change.

---

### Tool 2: `dotnet-testcontainers` (PostgreSQL integration tests) — CI / Testing

- **Category:** Testing / CI
- **Exact problem solved:** Zero test coverage of PostgreSQL-backed services (`PostgresMemoryStore`, `PostgresModelMappingStore`, `PostgresWorkflowStateStore`). These are core persistence paths with no verification.
- **Expected productivity gain:** Integration tests that spin up a real PostgreSQL container in CI catch schema drift, migration gaps, and query correctness. Prevents 1 major data corruption/regression per quarter.
- **Integration approach:** Add `Testcontainers.PostgreSql` NuGet to `Ryan.MCP.Mcp.Tests`. Write xUnit tests with `IAsyncLifetime` setup/teardown. Run against the same migration scripts as production.
- **Prerequisites:** Docker in CI (standard in GitHub Actions). `Ryan.MCP.Mcp.Tests` project (needs to be created — see P0).
- **Cost/complexity:** Free. Medium complexity to bootstrap; low per additional test.
- **Why top-5:** No other way to safely validate migrations or store correctness. Mocking PostgreSQL would hide the exact failure modes that hurt most in production.

---

### Tool 3: `Seq` or `Loki` — Observability / Log Aggregation

- **Category:** Observability
- **Exact problem solved:** Serilog structured JSON logs are configured in `Program.cs` but there's no aggregation backend. `LogForensicsTools.cs` queries logs — but without a central store, this only works against local files. Container logs vanish on restart.
- **Expected productivity gain:** Centralizes logs; makes `observability_dashboard_loop` query real data; makes `incident-triage.workflow.md` actionable via `log_forensics`. ~2–4 hours saved per incident triage.
- **Integration approach:** For local/dev: Add `Seq` to Docker Compose (free single-user). For production: Loki + Grafana (already referenced in `observability-dashboard-loop.md` PromQL templates). Add Serilog Seq/Loki sink to `Program.cs`.
- **Prerequisites:** Docker Compose update. Grafana or Seq UI.
- **Cost/complexity:** Seq free for single user. Loki open source. Low complexity for Serilog sink addition.
- **Why top-5:** The observability infrastructure is designed for this but currently has no backend. Half the observability tooling is inert without it.

---

### Tool 4: `OpenTelemetry Collector` + `Prometheus` — Observability / Metrics

- **Category:** Observability
- **Exact problem solved:** `PlatformMetrics` emits 11 counters/histograms via OTEL but there's no OTEL collector or Prometheus scrape target configured. The `observability-dashboard-loop.md` has PromQL queries written and ready — but no Prometheus instance to run them against. Metrics exist only in-process.
- **Expected productivity gain:** Makes `platform_metrics_snapshot` and `observability_dashboard_loop` tools return real data. Enables the cost/routing/caching feedback loop that justifies the entire observability design. Projected 10–20% LLM cost reduction through visible routing decisions.
- **Integration approach:** Add OTEL Collector + Prometheus to Docker Compose. Configure `UseOtlpExporter()` in `Program.cs` (OTEL already registered, just needs export endpoint). Import PromQL templates from `observability-dashboard-loop.md` into Grafana.
- **Prerequisites:** Docker Compose. Grafana (pairs with Tool 3).
- **Cost/complexity:** Open source. Medium complexity for initial wiring; low ongoing.
- **Why top-5:** The metrics code is already written and correct. This is a wiring problem, not a design problem — maximum ROI for minimal new code.

---

### Tool 5: `Renovate Bot` — CI / Dependency Hygiene

- **Category:** CI / Maintenance
- **Exact problem solved:** `apps/mcp-server/` depends on `ModelContextProtocol 1.2.0`, Npgsql, Serilog, and OpenTelemetry. Model versions, protocol versions, and SDK breaking changes move fast in the AI tooling space. No automated dependency update path exists.
- **Expected productivity gain:** Automated PRs for dependency bumps (gated by completed `ci-mcp.yml`) catch breaking changes early and keep MCP protocol compatibility current. Prevents 1 major dependency-drift incident per quarter; reduces manual maintenance ~2 hours/week.
- **Integration approach:** Add `.github/renovate.json` with rules for: `*` packages weekly, `ModelContextProtocol` on minor only, `Npgsql` grouped with DB migrations review checklist, ignore `net10.0` SDK until stable.
- **Prerequisites:** GitHub App installation (free for personal repos). Completed `ci-mcp.yml` to gate the auto-PRs.
- **Cost/complexity:** Free (Renovate hosted on Mend.io). Very low complexity. Must have CI green first or it will spam.
- **Why top-5:** The MCP protocol and Anthropic SDK are moving targets. Without automated tracking, silent protocol drift will break agent integrations at the worst moment.

---

## 5) 30/60/90 Day Improvement Plan

### 30 Days — Quick Wins (Unblock Production Safety)

| Task | Files Touched | Success Signal |
|------|--------------|----------------|
| Create `Ryan.MCP.Mcp.Tests` xUnit project; cover `PolicyPreflightService`, `RoutingBudgetService`, `CommandAllowlistService` | New test project | `dotnet test` passes on CI |
| Complete `ci-mcp.yml`: `dotnet build` + `dotnet test` + `docker build` | `.github/workflows/ci-mcp.yml` | All PRs gate on green |
| Remove `ApprovalToken` from `appsettings.json`; inject via `MCP_APPROVAL_TOKEN` env var | `appsettings.json`, `McpOptions.cs`, `Program.cs` | No secrets in any committed config file |
| Archive `agentic-platform-upgrade.plan.md` (mark complete, add date) | `agentic-platform-upgrade.plan.md` | Header reads "Completed [date]" |
| Add `Seq` to Docker Compose for structured log aggregation | `docker-compose.yml`, `Program.cs` | `docker compose up` includes Seq; logs visible in UI |

**KPIs:** `dotnet test` pass rate = 100%. CI pipeline green on `main`. Zero secrets in committed config files. Log aggregation queryable locally.

---

### 60 Days — Medium-Term Hardening

| Task | Files Touched | Success Signal |
|------|--------------|----------------|
| Migration 005: `audit_log` table; write from `PolicyPreflightService` on every Tier B/C call | New SQL migration + `PolicyPreflightService.cs` | `SELECT * FROM audit_log` returns entries after any mutate/execute op |
| Persist routing budget to PostgreSQL (migration 006) | New SQL migration + `RoutingBudgetService.cs` | Restart does not reset spend counters; verified by test |
| Wire OTEL Collector + Prometheus + Grafana to Docker Compose | `docker-compose.yml` + `Program.cs` OTEL export | PromQL queries from `observability-dashboard-loop.md` return real data |
| Add `spectral` schema validation to `guardrails-check.yml` | `.github/workflows/guardrails-check.yml` | PRs with invalid `budget_tier` or malformed `allowed_tools` fail CI |
| Add `Testcontainers.PostgreSql` integration tests for all 3 Postgres stores | `Ryan.MCP.Mcp.Tests` | Migration + CRUD roundtrip verified in CI |

**KPIs:** Audit log populated for all Tier B/C ops. Budget surviving restart validated by test. Grafana dashboard showing real routing/cache metrics. Schema validation blocking 1+ invalid PRs.

---

### 90 Days — Strategic Upgrades

| Task | Files Touched | Success Signal |
|------|--------------|----------------|
| Refactor `WorkflowTools.cs` to load definitions from markdown (data-driven) | `WorkflowTools.cs` + new `WorkflowIngestionCoordinator.cs` | Editing `feature-delivery.workflow.md` changes MCP tool behavior without code change |
| Add lightweight gate enforcement to `run_workflow_step` | `WorkflowTools.cs`, `WorkflowStateTools.cs` | Cannot advance to Phase N+1 without Phase N exit criteria acknowledged |
| Enable + smoke-test at least 3 external MCP connectors (GitHub, fetch, memory) | `appsettings.json` + `ExternalMcpClientService.cs` | `ConnectorTools.cs` returns real data for enabled connectors |
| Add scope-based authorization to `AgentTools.cs` | `AgentTools.cs`, `McpOptions.cs` | `list_agents` filtered by client scope; unauthorized agents not visible |
| Install Renovate Bot; configure package update rules | `.github/renovate.json` | Weekly Renovate PRs appear for dependencies; CI gates them |
| Add HTTP rate limiting to MCP endpoints | `Program.cs` | Load test shows rate limiter activating at threshold |

**KPIs:** 0 hardcoded workflow definitions in `WorkflowTools.cs`. At least 3 connectors smoke-tested in CI. Gate enforcement verified by a test that tries to skip a phase. Renovate generating and CI auto-closing patch-level PRs.

---

## 6) Validation Checklist

```
SECURITY & SECRETS
[ ] appsettings.json contains no ApprovalToken value
[ ] MCP_APPROVAL_TOKEN injected via environment variable in all environments
[ ] .env.example documents required env vars

TESTING
[ ] Ryan.MCP.Mcp.Tests project exists and runs on `dotnet test`
[ ] PolicyPreflightService: read/mutate/execute classification tested
[ ] RoutingBudgetService: spend tracking + enforce + reset tested
[ ] CommandAllowlistService: allow + deny tested
[ ] PostgresWorkflowStateStore: upsert/get/list roundtrip tested
[ ] PostgresMemoryStore: entity/observation/relation CRUD tested

CI/CD
[ ] ci-mcp.yml: dotnet build step passes on PR
[ ] ci-mcp.yml: dotnet test step passes on PR
[ ] ci-mcp.yml: docker build succeeds on main merge
[ ] guardrails-check.yml: spectral schema lint step added
[ ] guardrails-check.yml: invalid budget_tier value fails check

AUDIT & OBSERVABILITY
[ ] audit_log table exists in PostgreSQL (migration 005 applied)
[ ] Every Tier B/C tool call produces an audit_log row
[ ] Seq (or Loki) receiving structured JSON logs from MCP server
[ ] OTEL Collector scraping PlatformMetrics counters
[ ] Grafana dashboard shows cache hit rate, routing decisions, budget spend

DATA INTEGRITY
[ ] RoutingBudgetService reads spent amount from DB on startup
[ ] Server restart does not reset per-workflow spend to zero
[ ] Two concurrent replicas share the same budget counter

WORKFLOW DEFINITIONS
[ ] WorkflowTools.cs contains no hardcoded WorkflowDefinition objects
[ ] Editing feature-delivery.workflow.md changes list_workflows output without redeploy
[ ] WorkflowIngestionCoordinator parses and indexes *.workflow.md files

PLAN & DOCS
[ ] agentic-platform-upgrade.plan.md header reads "Completed [date]"
[ ] No open tasks remain marked "To implement" for already-shipped features

DEPENDENCY HYGIENE
[ ] .github/renovate.json exists with package update rules
[ ] At least one Renovate PR has been generated and passed CI
```

---

*Assessment produced by Claude Code (claude-sonnet-4-6) on 2026-04-21. All findings are tied to concrete file paths and implementation evidence observed during codebase exploration.*
