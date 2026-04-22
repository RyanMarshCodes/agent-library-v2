# AI Platform Full-Stack Assessment

**Analyzed by:** opencode
**Date:** 2026-04-21
**Repository:** D:\Projects\ai-stack

---

## 1) Executive Summary

**Key Strengths:**
- MCP server architecture is well-factored (50+ tools/services with clear separation of concerns)
- Multi-tier policy system (read/mutate/execute) with approval tokens already implemented
- Comprehensive workflow definitions with budget controls per workflow
- 45+ specialized agents covering major tech domains with routing table
- Semantic + prefix caching for cost optimization
- OpenTelemetry-native with structured metrics
- Workflow state persistence (postgres or in-memory)

**Top Weaknesses:**
- CI pipelines are empty (`ci-mcp.yml`, `ci-library.yml` contain zero lines)
- No unit tests anywhere in the codebase
- Guardrails exist only as markdown docs—no operational enforcement in code
- `agent-contract.schema.json` defined but never validated or used
- `audit-agents` skill is skeletal (7 lines) with broken file references
- Contract drift: AGENTS.md references `library/instructions/` that doesn't exist
- Empty README at root

**Maturity Assessment:**
- **Tier 2 (Functional but incomplete)** — Core infrastructure works, but critical governance loops (CI, tests, validation) are missing. Production-readiness gated by missing automation.

---

## 2) Prioritized Recommendations

| Priority | Recommendation | Why it matters | Effort | Risk | Suggested first step |
|----------|-------------|---------------|---------|------|--------------------|
| **P0** | Add unit tests for MCP tools and services | Zero test coverage = hidden regressions, especially in policy/workflow logic | M | Low | Create `tests/` folder, add xUnit project, test `PolicyPreflightService` and `RoutingBudgetService` first |
| **P0** | Populate CI pipelines | Empty CI = no automated validation of PRs | S | Low | Fill `ci-library.yml` to run agent contract validation on PRs; populate `ci-mcp.yml` with dotnet build/test |
| **P1** | Implement agent contract validation in CI | Schema exists but unused—agents can have invalid frontmatter | M | Low | Wire `scripts/validate-agent-contract.mjs` into `guardrails-check.yml` for all agent changes |
| **P1** | Operationalize cost guardrails | Document exists but no enforcement in code—budgets can be bypassed | M | Med | Add middleware in Program.cs to enforce per-request token caps from cost-guardrails.md |
| **P1** | Operationalize security guardrails | Doc exists but no audit logging for Tier B/C actions | M | Low | Add audit envelope capture in PolicyPreflightService for mutate/execute operations |
| **P2** | Implement `audit-agents` skill fully | Skill references missing files (`library/instructions/`, `memory-bridge-instructions.md`) | M | Low | Create missing instruction files or remove broken references |
| **P2** | Add health-check dashboard | No visibility into MCP health without direct inspection | S | Low | Expose `/health/ready` metrics to Prometheus endpoint |
| **P2** | Document runtime config for guardrails in appsettings | Users must discover config knobs via code reading | S | Low | Add appsettings.json comments explaining each Policy/RoutingBudget/Retrieval setting |

---

## 3) Gaps and Risks

| Gap title | Evidence | Consequence | Mitigation | Confidence |
|----------|----------|-------------|------------|-----------|
| **No test infrastructure** | No `*.test.cs` files found; empty ci-mcp.yml | Hidden regressions in policy/budget logic; no confidence for refactoring | Add xUnit project, prioritize PolicyPreflightService + RoutingBudgetService tests | High |
| **Empty CI pipelines** | `ci-mcp.yml` and `ci-library.yml` are 0 bytes | No automated validation; PRs bypass quality gates | Populate workflows with build, test, and agent validation steps | High |
| **Guardrails not operational** | `cost-guardrails.md` and `security-guardrails.md` are doc-only; no enforcement in code | Budgets can be exceeded; security actions not audited | Implement middleware for cost enforcement; add audit logging | Med |
| **Agent contract schema unused** | `agent-contract.schema.json` exists but no validation code | Malformed agents silently accepted | Add schema validation in `AgentIngestionCoordinator` or CI | Med |
| **Broken skill references** | `audit-agents` SKILL.md references `library/instructions/` (doesn't exist); orchestrator references missing `memory-bridge-instructions.md` | Skills fail at runtime when referenced paths missing | Create missing directories/files or fix references | Med |
| **Contract drift** | `AGENTS.md` line 12 says runtime is `WorkflowTools.cs` but actual path is `McpTools/WorkflowTools.cs` | Confusion for contributors; potential doc outdatedness | Fix path in AGENTS.md or verify actual usage | Low |
| **No observability dashboard** | Metrics exist in code but no /metrics endpoint for Prometheus | Can't monitor MCP health/usage without direct log inspection | Add /metrics endpoint using OpenTelemetry.Exporter.Prometheus | Med |

---

## 4) Tooling Additions (max 5)

### 1. Agent Contract Validator MCP Tool
- **Category:** MCP / CI
- **Problem:** No way to validate agent definitions before they're ingested; malformed agents cause runtime errors
- **Productivity gain:** Catches frontmatter errors before deployment (~15 min saved per incident × 10+ agents/year)
- **Integration:** Add to `AgentIngestionCoordinator` or expose as `validate_agent_contract` tool
- **Prerequisites:** `agent-contract.schema.json` already exists
- **Cost/Complexity:** Low — JSON schema validation is built into .NET
- **Why top-5:** Schema exists but unused—this makes it operational

### 2. Workflow Budget Enforcer Middleware
- **Category:** MCP / Policy
- **Problem:** Cost guardrails defined in markdown but not enforced—budget limits can be exceeded
- **Productivity gain:** Prevents runaway costs (~saved spend × likelihood of runaway)
- **Integration:** Add to Program.cs as early middleware checking request context
- **Prerequisites:** `RoutingBudgetService` already exists
- **Cost/Complexity:** Low — reuse existing service
- **Why top-5:** Budget enforcement is the single largest cost control

### 3. Test Coverage Gate in CI
- **Category:** CI
- **Problem:** Zero tests = zero coverage; can't trust changes to policy/workflow logic
- **Productivity gain:** Catches bugs before production (~1hr saved per bug × estimated 5 bugs/yr)
- **Integration:** Add to `ci-mcp.yml` after populating it
- **Prerequisites:** Need tests first (see P0)
- **Cost/Complexity:** Low — dotnet test is built in
- **Why top-5:** Without tests, all other recommendations have high regression risk

### 4. Security Audit Logger MCP Tool
- **Category:** Observability / Security
- **Problem:** Security guardrails say to capture audit envelope but nothing does it
- **Productivity gain:** Compliance evidence for mutate/execute actions (~audit prep time × actions)
- **Integration:** Add new `AuditLoggerService` + expose as `log_audit_event` tool
- **Prerequisites:** `security-guardrails.md` already documents envelope fields
- **Cost/Complexity:** Med — requires storage (table or log sink)
- **Why top-5:** Only security guardrail that has documented requirements but zero implementation

### 5. Prometheus Metrics Endpoint
- **Category:** Observability
- **Problem:** Metrics collected but not exposed—can't build dashboards or alert on thresholds
- **Productivity gain:** Proactive alerting vs. reactive investigation (~2hrs × incidents)
- **Integration:** Add endpoint to Program.cs using OpenTelemetry.Exporter.Prometheus
- **Prerequisites:** `PlatformMetrics` already exists
- **Cost/Complexity:** Low — NuGet package exists
- **Why top-5:** Platform is observability-native but endpoint is missing

---

## 5) 30/60/90 Day Improvement Plan

### 30 days — Quick Wins
- [ ] Populate `ci-mcp.yml` with `dotnet build && dotnet test`
- [ ] Populate `ci-library.yml` with agent validation step
- [ ] Create `tests/` folder with xUnit project
- [ ] Add first 3 tests: `PolicyPreflightServiceTests`, `RoutingBudgetServiceTests`, `WorkflowToolsTests`
- [ ] Expose `/health/ready` via /metrics endpoint

**KPI:** CI runs on every PR (even if build only); 3+ passing tests

### 60 days — Medium-Term Hardening
- [ ] Wire agent contract schema validation into CI (`scripts/validate-agent-contract.mjs`)
- [ ] Add budget enforcement middleware to Program.cs
- [ ] Add audit envelope logging for Tier B/C operations
- [ ] Create missing skill files referenced in `orchestrator.agent.md`
- [ ] Fix broken file references in `audit-agents` skill
- [ ] Add test coverage gate (e.g., 40% minimum)

**KPI:** Budget enforcement active; audit logs stored; 40%+ coverage

### 90 days — Strategic Upgrades
- [ ] Implement full `audit-agents` skill with gap/overlap detection
- [ ] Add runtime agent validation on ingestion
- [ ] Create observability dashboard with Grafana (consume /metrics)
- [ ] Add model routing optimization based on budget analysis
- [ ] Document all config knobs in appsettings.json comments

**KPI:** Full audit-agents capability; observability dashboard running; documented config

---

## 6) Validation Checklist

- [ ] **CI pipelines non-empty:** `ci-mcp.yml` and `ci-library.yml` contain actual workflow steps (not blank)
- [ ] **Tests exist:** `tests/` folder with at least one test project and 3+ passing tests
- [ ] **Agent validation in CI:** `guardrails-check.yml` runs on agent file changes
- [ ] **Budget enforced:** Add request-level budget check middleware; verify it blocks when budget exceeded
- [ ] **Audit logging:** Tier B/C operations produce audit log entries (file, table, or log sink)
- [ ] **Metrics endpoint:** `/metrics` or similar returns Prometheus-format metrics
- [ ] **Skill file references fixed:** All skills with broken `library/instructions/` or similar paths resolved
- [ ] **Contract schema used:** Either CI validates or ingestion validates agents against schema
- [ ] **Readme populated:** Root README.md contains project overview

---

**Summary:** The codebase has excellent foundation—MCP server design, workflow logic, and agent library are production-worthy. The critical gap is **governance automation** (CI, tests, validation), which blocks confidence in long-term maintenance. Prioritize tests + CI first.