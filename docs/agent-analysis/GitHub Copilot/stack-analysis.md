# AI Stack Full-Stack Assessment
**Analyst:** GitHub Copilot (Claude Sonnet 4.6)
**Date:** 2026-04-21
**Scope:** MCP server, agent/workflow/skill library, guardrails, infra, CI/CD

---

## 1) Executive Summary

- **Strongest asset:** The MCP server is a production-quality .NET control plane with ~20 tool classes covering the full dev lifecycle (workflow state, policy preflight, model routing, memory, diff analysis, PR gen, build tools, NuGet hygiene, log forensics). This is well ahead of most org-internal AI tooling.
- **Architecture coherence is high:** The dual-plane model (file library for authoring, MCP server for runtime) is clearly articulated in `AGENTS.md` and consistently applied. The `agentic-platform-upgrade.plan.md` shows all 10 upgrade tasks are marked complete.
- **Workflow contracts are solid:** Five production workflows with phase gates, stop conditions, and Postgres-backed state persistence are properly wired to `WorkflowTools.cs`.
- **Critical CI gap:** `ci-mcp.yml` and `ci-library.yml` are **empty files**. The server ships with zero automated test coverage. The only CI gate is the agent frontmatter validator.
- **Zero test projects:** No `*.Tests` projects or `*.test.cs` files exist anywhere in the solution. There is no regression safety net.
- **Security vulnerabilities present:** (a) `LogForensicsTools` reads arbitrary filesystem paths without scope validation — path traversal. (b) Default `appsettings.json` contains `Password=postgres` committed to source. (c) `IsApprovalTokenValid` uses `List.Contains` instead of a constant-time comparison, enabling timing oracle for token guessing.
- **Observability is wired but dark:** `otel-collector-config.yaml` is **empty**, meaning the OTEL pipeline terminates at the exporter but no collector is configured. In-process counters reset on restart with no persistent baseline.
- **LiteLLM is a stub:** `litellm_config.yaml` contains only a fake endpoint. There is no real model provider configured, making LiteLLM non-functional in production.
- **Workflow/doc drift risk:** The five multi-step workflows are hardcoded as C# `Dictionary<string, WorkflowDefinition>` in `WorkflowTools.cs` while canonical specs live in `library/workflows/*.workflow.md`. These are not cross-validated — description drift is invisible.
- **Overall maturity: 7/10.** The architecture is thoughtful and the platform has significant depth. Execution quality gaps (no tests, empty CI, empty observability config, LiteLLM stub) prevent it from being called production-ready today.

---

## 2) Prioritized Recommendations

| Priority | Recommendation | Why It Matters | Effort | Risk | Suggested First Step |
|---|---|---|---|---|---|
| **P0** | Add test projects and fill the empty CI workflow files | Any change to `WorkflowTools.cs`, `PolicyPreflightService`, or `MemoryTools` is unvalidated. A regression in policy gating is a security event. | M | High | Create `Ryan.MCP.Mcp.Tests` xUnit project; write 5 tests for `PolicyPreflightService.Evaluate` covering read/mutate/execute/token-missing/invalid-token paths; wire to `ci-mcp.yml` with `dotnet test` |
| **P0** | Fix path traversal in `LogForensicsTools.AnalyzeRuntimeLogs` | Tool accepts an arbitrary `path` param and calls `Path.GetFullPath` with no scope check. Any agent with `Execute` approval can exfiltrate `/etc/passwd`, connection strings in config files, etc. | S | Critical | Add a `IsPathWithinAllowedRoots(fullPath, allowedRoots)` guard before `File.ReadLines`. Allowed roots = working dir or configured log dirs only. |
| **P0** | Replace `List.Contains` token comparison with constant-time equality | Current `IsApprovalTokenValid` in `PolicyPreflightService.cs:48` uses `tokens.Contains(providedToken)` — susceptible to timing side-channel to enumerate valid tokens. | S | High | Replace with `CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(providedToken))` for each candidate. |
| **P0** | Remove plaintext credential from committed `appsettings.json` | `Host=localhost;Password=postgres` is committed to repo. Even as a "dev default", it trains developers to commit credentials and seeds leaked defaults in cloud deploys. | S | High | Replace with `Host=localhost;Password=${MCP_DB_PASSWORD}` placeholder and document in `.env.example`; add `appsettings.Development.json` to `.gitignore`. |
| **P1** | Configure `otel-collector-config.yaml` | File is **empty**. The OTEL export in `Program.cs` and Prometheus PromQL in `observability-dashboard-loop.md` are dead letters without a collector. | M | Medium | Add receivers (otlp), processors (batch), and exporters (prometheus + logging). Wire to Grafana or equivalent. |
| **P1** | Configure LiteLLM with at least one real provider | `litellm_config.yaml` has only a fake endpoint — the proxy is non-functional. The model routing budget system in `McpOptions.RoutingBudget` has no real backend to enforce costs against. | M | Medium | Add at minimum one real provider (e.g. `openai/gpt-4o`) under `model_list`, move API key to `.env`. |
| **P1** | Cross-validate workflow definitions in `WorkflowTools.cs` against `.workflow.md` specs | Workflow step names and exit criteria in C# dictionary have silently diverged from the authoritative docs (e.g., step ID `scope-spec` vs `scope-and-spec`). A drift checker at CI time would surface this. | S | Medium | Add a `validate-workflow-drift.mjs` script that parses both sources and asserts step count/ID match. Add to `guardrails-check.yml`. |
| **P1** | Replace frontmatter regex validator with JSON Schema validation | `validate-agent-contract.mjs` uses `new RegExp(\`^${field}:\`, "m")` rather than parsing YAML and validating against `agent-contract.schema.json`. Fields can pass regex but have wrong types/values. | S | Low | Use `js-yaml` + `ajv` to parse frontmatter and validate against the existing schema in `library/guardrails/agent-contract.schema.json`. |
| **P2** | Scope `tools_to_auto_execute` in Bifrost config to read-only tools only | `bifrost.config.json` auto-executes `fetch`, `analyze_diff`, `analyze_runtime_logs`, `scan_project` — these are stateful/side-effect capable. `fetch` is a SSRF vector when auto-executed without user inspection. | S | Medium | Move `fetch`, `analyze_diff`, `scan_project`, `analyze_runtime_logs` from `tools_to_auto_execute` to `tools_to_execute` (prompt before run). |
| **P2** | Persist in-process metrics to Postgres or push to OTEL on a schedule | `PlatformMetrics` counters reset on every server restart. There is no historical baseline for deny-rate or cache-hit trending. | M | Low | Add a background `IHostedService` that pushes a snapshot row to Postgres every 5 minutes; or push counters as OTEL metrics (already wired, just needs collector). |
| **P2** | Audit agent model names for accuracy | Agents reference `gpt-5.3-codex`, `gpt-5.4`, `gemini-3.1-pro`, `claude-opus-4-6` — these appear to be speculative version names. If model mapping sync reads these into the routing table, real requests will fail with 404 from providers. | S | Low | Run `sync_model_mappings` and `list_model_mappings` to verify resolved models match actual provider model IDs. Add model name validation to CI. |

---

## 3) Gaps and Risks

### Gap 1: Empty CI Pipelines
- **Evidence:** `apps/mcp-server/.github/workflows/ci-mcp.yml` (empty), `ci-library.yml` (empty)
- **Consequence:** Every push to `apps/mcp-server/**` or `library/**` deploys with no build, test, or lint gate. A broken build ships to VPS silently.
- **Mitigation:** `ci-mcp.yml`: `dotnet build`, `dotnet test`, optionally `dotnet format --verify-no-changes`. `ci-library.yml`: run `validate-agent-contract.mjs` on all agents (not just changed), validate YAML syntax of workflows.
- **Confidence:** High

### Gap 2: No Test Projects
- **Evidence:** `file_search("**/*.test.cs")` → No files found. No `*.Tests` directories exist in solution.
- **Consequence:** `PolicyPreflightService`, `WorkflowTools`, `CommandAllowlistService` are all untested. The approval token logic and the command injection defense are security-critical paths with no regression coverage.
- **Mitigation:** Bootstrap `Ryan.MCP.Mcp.Tests` using xUnit. Priority order: `PolicyPreflightService` (security), `WorkflowTools.StartWorkflow/AdvanceWorkflow` (correctness), `CommandAllowlistService.Evaluate` (security), `DiffAnalysisTools` input sanitization.
- **Confidence:** High

### Gap 3: Path Traversal in Log Forensics Tool
- **Evidence:** `apps/mcp-server/src/Ryan.MCP.Mcp/McpTools/LogForensicsTools.cs:52` calls `Path.GetFullPath(path)` on user-supplied `path` with no subsequent bounds check against a permitted root directory.
- **Consequence:** An agent or caller with the approval token can read any file accessible to the server process: `/etc/shadow`, environment-injected secrets, mounted Docker volumes, database config. In the Docker deployment, `/data/state` and `/data/library` are mounted — full exfil risk.
- **Mitigation:** Define `AllowedLogRoots` in `McpOptions` (e.g., a configurable list of log directories). In `AnalyzeRuntimeLogs`, assert `fullPath.StartsWith(allowedRoot)` after `GetFullPath` normalization before reading. Reject otherwise with `{"error": "path_not_permitted"}`.
- **Confidence:** High

### Gap 4: Static Approval Token with Timing Vulnerability
- **Evidence:** `apps/mcp-server/src/Ryan.MCP.Mcp/Services/Policy/PolicyPreflightService.cs:48` — `tokens.Contains(providedToken.Trim(), StringComparer.Ordinal)`. Standard `string.Equals` / `Contains` is not constant-time.
- **Consequence:** An external system that can observe response latency at high frequency can binary-search the token character-by-character. Low-severity in low-traffic solo usage but material in any shared/multi-tenant deployment.
- **Mitigation:** Use `CryptographicOperations.FixedTimeEquals` for each token comparison. Additionally, rotate the approval token periodically via config reload.
- **Confidence:** High

### Gap 5: OTEL Collector Not Configured
- **Evidence:** `infra/observability/otel-collector-config.yaml` is empty. `Program.cs:37-44` exports to OTLP. `library/guardrails/observability-dashboard-loop.md` contains PromQL queries that assume metrics are scraped.
- **Consequence:** All platform metrics (deny rates, cache hits, budget utilization) are visible only in-process via `platform_metrics_snapshot`. There is no historical view, no alerting, and the observability loop documented in the guardrails is effectively non-functional.
- **Mitigation:** Populate the collector config with `otlp` receiver, `batch` processor, `prometheus` exporter. Add Grafana to `infra/observability/docker-compose.yaml`. Wire the two existing dashboard loop PromQL queries as starting panels.
- **Confidence:** High

### Gap 6: LiteLLM Proxy is Non-Functional
- **Evidence:** `infra/litellm/litellm_config.yaml` model list contains only `fake-openai-endpoint` pointing to a Railway demo URL.
- **Consequence:** The model routing budget system (`McpOptions.RoutingBudget`) and the `recommend_model` tool have no actual LLM backend to route through. Cost projections are advisory only, not enforced.
- **Mitigation:** Add real provider entries under `model_list`. Use `os.environ/OPENAI_API_KEY` pattern (already in use for Redis). Document required env vars in `infra/litellm/README.md`.
- **Confidence:** High

### Gap 7: Workflow C#/Markdown Sync Drift
- **Evidence:** `apps/mcp-server/src/Ryan.MCP.Mcp/McpTools/WorkflowTools.cs` hardcodes step definitions inline (e.g., `new("scope-spec", "Scope and specification", ...)`). The authoritative spec at `library/workflows/feature-delivery.workflow.md` uses headings like `### Gate 1: Scope and Spec` with different field names/structure. No cross-validation exists.
- **Consequence:** When a workflow phase gate is updated in the `.md` file (the "authoring plane"), the runtime behavior in `WorkflowTools.cs` (the "runtime plane") stays stale. Agents following the workflow receive outdated step guidance.
- **Mitigation:** Add `validate-workflow-drift.mjs` to CI that parses both sources and asserts step counts match. Long-term: auto-generate `WorkflowDefinition` entries from parsed `.workflow.md` files at startup (similar to how agents are ingested).
- **Confidence:** High

### Gap 8: Bifrost Auto-Execute Scope Too Broad
- **Evidence:** `infra/bifrost/bifrost.config.json:tools_to_auto_execute` includes `fetch` (SSRF risk), `analyze_runtime_logs` (path traversal risk), `scan_project`, `analyze_diff` — side-effect-capable tools that bypass user inspection.
- **Consequence:** A prompt injection attack embedded in any document the model fetches (via `fetch` auto-execute) can chain to `analyze_runtime_logs` or `analyze_diff` without user confirmation, potentially exfiltrating data through the tool response.
- **Mitigation:** Move all tools that touch the filesystem or network to the `tools_to_execute` list (confirm-before-run). Only list pure read/catalog tools (`list_agents`, `get_context`, `memory_recall`, `list_workflows`, `get_workflow`, etc.) in `tools_to_auto_execute`.
- **Confidence:** High

### Gap 9: No Eval/Regression Framework Despite Workflow
- **Evidence:** `/eval-regression` workflow exists in `WorkflowTools.cs` and `library/workflows/eval-regression.workflow.md`. No eval test cases, no eval runner, no baseline metrics file, no tooling to diff prompt outputs.
- **Consequence:** Model upgrades, prompt changes, and agent modifications have no quality regression check. The eval workflow is a process document with no supporting tooling.
- **Mitigation:** Add a `library/eval/` directory with representative input/expected-output pairs for at least 5 core tools. Wire a baseline runner (shell script calling MCP endpoints with `curl` + output diffing) as a manual gate in CI.
- **Confidence:** Medium (could be by design as a future phase)

---

## 4) Tooling Additions (max 5)

### 1. PromQL-backed Grafana Dashboard
- **Category:** Observability
- **Problem solved in this repo:** The observability infrastructure exists in code (`PlatformMetrics`, OTEL export, PromQL in guardrail docs) but is dark. Deny rates, cache misses, and cost overruns are invisible. The daily "optimization loop" in `library/guardrails/observability-dashboard-loop.md` cannot be executed without a live dashboard.
- **Expected productivity gain:** Surface budget denials and cache miss spikes within minutes of occurrence instead of discovering them via `platform_metrics_snapshot` polling. Estimated 2-3 hours/week saved in manual debugging.
- **Integration approach:** Add `prometheus` + `grafana` services to `infra/observability/docker-compose.yaml`. Populate the empty `otel-collector-config.yaml` with the OTLP→Prometheus pipeline. Import the 6 panels already specified in `observability-dashboard-loop.md` as a starter dashboard JSON.
- **Prerequisites:** OTEL collector running. `McpOptions.RoutingBudget.EnforcePerWorkflowBudget = true` in prod.
- **Cost/complexity note:** Low cost (all OSS). ~4 hours of configuration work.
- **Why top-5:** The metrics are already emitted but wasted. This is configuration, not development.

### 2. PromptFoo
- **Category:** CI / Eval
- **Problem solved in this repo:** The `/eval-regression` workflow and `library/workflows/eval-regression.workflow.md` have no tooling backing. Agent prompt changes and model upgrades have no quality gate. PromptFoo provides structured test cases (input → expected output assertions) with a CLI that runs in CI.
- **Expected productivity gain:** Catch prompt regressions before they reach the VPS. Estimated prevention of 1-2 silent quality regressions per month, each of which currently requires manual investigation.
- **Integration approach:** Add `library/eval/promptfoo.yaml` with 10-15 test cases against the MCP server's HTTP endpoints. Add a `ci-eval` GitHub Actions job triggered on changes to `library/agents/**` or `apps/mcp-server/**`.
- **Prerequisites:** A functional LiteLLM or direct provider config (Gap 6 must be resolved first).
- **Cost/complexity note:** Free OSS. ~1 day to write initial test cases.
- **Why top-5:** Fills the largest workflow gap in this repo — the eval loop exists on paper but has no execution path.

### 3. Trivy (Container + Secret Scanning)
- **Category:** Security / CI
- **Problem solved in this repo:** The MCP server Dockerfile, LiteLLM image, and Bifrost image are pulled and deployed without any CVE scanning. No `dependabot.yml` exists for container images. `trivy fs --scanners secret` would immediately catch the `Password=postgres` committed in `appsettings.json`.
- **Expected productivity gain:** Automated identification of critical/high CVEs in base images and NuGet packages before they reach production. Estimated catch of 2-4 critical CVEs per quarter that would otherwise only be discovered reactively.
- **Integration approach:** Add a `trivy image` step to `ci-mcp.yml` after `docker build`. Add `trivy fs --scanners vuln,secret` step to catch hardcoded secrets. Free GitHub Actions marketplace action available.
- **Prerequisites:** Docker build step in CI (currently absent since `ci-mcp.yml` is empty).
- **Cost/complexity note:** Free OSS. ~30 minutes to add to CI once the build step exists.
- **Why top-5:** The path traversal + static approval token risks already exist. Trivy would catch `Password=postgres` on the first run, acting as a forcing function to fix that P0.

### 4. Testcontainers for .NET
- **Category:** CI / Testing
- **Problem solved in this repo:** `PostgresMemoryStore` and `PostgresWorkflowStateStore` require a live Postgres instance to test. Without Testcontainers, integration tests are impractical in CI. This is the primary reason tests likely don't exist — there's no easy harness.
- **Expected productivity gain:** Enable full round-trip integration tests of memory recall, workflow state upsert/get, and policy evaluation against real Postgres. Unlocks ~80% of the business logic that cannot be unit-tested in isolation.
- **Integration approach:** Add `Testcontainers.PostgreSql` NuGet package to `Ryan.MCP.Mcp.Tests`. Create a `PostgresFixture` that starts a container per test class. Tests for `MemoryRecall`, `WorkflowStateUpsert`, and `PolicyPreflight` immediately become feasible.
- **Prerequisites:** Docker available in CI runners (GitHub hosted runners have Docker). Requires creating the test project first (Gap 2).
- **Cost/complexity note:** Free OSS. ~1 day to scaffold fixtures.
- **Why top-5:** The single highest-leverage unlocker for addressing the no-tests gap.

### 5. Semantic Pull Requests (GitHub App)
- **Category:** Productivity / Developer Loop
- **Problem solved in this repo:** The `generate_pr_description` MCP tool generates a PR description but cannot enforce it was used. `deploy-mcp.yml` triggers on `master` push without merge quality gates. There is no conventional commit enforcement, making `changelog.agent.md` and release notes manual.
- **Expected productivity gain:** Automatic PR title validation and branch-name conventions reduce noise PRs that bypass the `/commit` → `/wrapup` workflow. The `changelog.agent.md` agent becomes significantly more effective with structured commit history.
- **Integration approach:** Add `.github/semantic.yml` defining allowed commit types (`feat`, `fix`, `chore`, `docs`, `refactor`, `test`). Enable the Semantic Pull Requests GitHub App. Cross-reference with `generate_pr_description` output format (already uses conventional commit format).
- **Prerequisites:** GitHub repo admin access.
- **Cost/complexity note:** Free for public repos, $7/month for private. ~15 minutes to configure.
- **Why top-5:** Closes the loop between AI-generated PR descriptions and actual merge quality gates. Lowest effort, immediate ROI for traceability.

---

## 5) 30/60/90 Day Improvement Plan

### 30 Days: Seal the Critical Gaps

**Actions:**
1. Fix the three P0 security issues: path traversal in `LogForensicsTools`, timing-safe token comparison in `PolicyPreflightService`, remove `Password=postgres` from `appsettings.json`
2. Fill `ci-mcp.yml` with `dotnet build` + `dotnet test` (even with zero tests, the build gate is valuable)
3. Create `Ryan.MCP.Mcp.Tests` with 10 tests covering `PolicyPreflightService`, `CommandAllowlistService`, and `WorkflowTools.ListWorkflows`
4. Populate `otel-collector-config.yaml` with a minimal working OTLP→Prometheus pipeline
5. Restrict `tools_to_auto_execute` in `bifrost.config.json` to read-only tools only
6. Upgrade `validate-agent-contract.mjs` to use `js-yaml` + `ajv` against the existing JSON schema

**Success KPIs:**
- `ci-mcp.yml` has a passing build on every PR
- `trivy fs --scanners secret` returns 0 HIGH/CRITICAL findings
- `platform_metrics_snapshot` data visible in a Grafana panel
- All changed agent files validate against JSON schema (not just regex)

---

### 60 Days: Harden Observability and Test Coverage

**Actions:**
1. Configure LiteLLM with a real provider; validate `recommend_model` end-to-end
2. Add Testcontainers to the test project; write integration tests for `PostgresWorkflowStateStore` and `PostgresMemoryStore`
3. Add `validate-workflow-drift.mjs` to CI to catch `WorkflowTools.cs` vs `.workflow.md` desync
4. Add Trivy container scanning to `ci-mcp.yml`
5. Install Semantic Pull Requests app + `.github/semantic.yml`
6. Add PromptFoo with 5-10 eval test cases for core MCP tools

**Success KPIs:**
- Test project has >40% line coverage of `McpTools/` and `Services/Policy/`
- Zero workflow steps out of sync between C# and `.md` files (CI enforced)
- LiteLLM routes at least one real provider request through the Bifrost gateway
- Grafana dashboard shows 7-day trends for all 6 PromQL panels from `observability-dashboard-loop.md`

---

### 90 Days: Platform Maturity and Self-Improvement Loop

**Actions:**
1. Generate `WorkflowDefinition` entries from parsed `.workflow.md` at startup (eliminate C#/MD drift root cause)
2. Add `AllowedLogRoots` config to `McpOptions` and enforce path scoping in all file-reading tools
3. Stand up PromptFoo eval in CI with a pass/fail threshold (≥95% test pass rate)
4. Add per-tool usage telemetry to identify which MCP tools are actually called and which are dead code
5. Author `library/eval/` baseline with 15+ representative test cases
6. Add `IMemoryStore` abstraction test to verify pgvector recall quality (recall@5 > 0.8 on a fixed test set)

**Success KPIs:**
- Zero known path traversal attack surfaces (verified by Trivy + manual review)
- PromptFoo eval suite passes in CI on every agent/server change
- Per-tool usage data shows which of the 20 tool classes are called >10 times/week
- `memory_recall` returns relevant results in >80% of test queries (eval baseline established)

---

## 6) Validation Checklist

Use this checklist to verify whether the key recommendations were implemented.

**Security**
- [ ] `LogForensicsTools.AnalyzeRuntimeLogs` rejects paths outside `McpOptions.AllowedLogRoots`
- [ ] `PolicyPreflightService.IsApprovalTokenValid` uses `CryptographicOperations.FixedTimeEquals`
- [ ] `appsettings.json` contains no plaintext credentials (no `Password=`)
- [ ] `trivy fs --scanners secret apps/mcp-server` returns 0 HIGH/CRITICAL findings
- [ ] `bifrost.config.json:tools_to_auto_execute` contains only `list_*`, `get_*`, `search_*`, `read_*`, `memory_recall`, `memory_status` — no `fetch`, `analyze_*`, `scan_*`

**CI / Testing**
- [ ] `ci-mcp.yml` runs `dotnet build` and `dotnet test` on every push to `apps/mcp-server/**`
- [ ] `ci-library.yml` runs `validate-agent-contract.mjs` on all agents (not just changed)
- [ ] `Ryan.MCP.Mcp.Tests` project exists with ≥10 tests
- [ ] `validate-workflow-drift.mjs` script runs in `guardrails-check.yml` and passes

**Observability**
- [ ] `otel-collector-config.yaml` is non-empty and container starts successfully
- [ ] Grafana shows live data for at least the execute-deny-rate and cache-hit-rate panels
- [ ] `platform_metrics_snapshot` called after 1 hour of real traffic shows non-zero counters

**Functionality**
- [ ] `litellm_config.yaml` has at least one real model entry; `recommend_model` resolves it
- [ ] `sync_model_mappings` runs without errors; all agent model names resolve to real provider model IDs
- [ ] `workflow_state_upsert` + `workflow_state_get` round-trip works with Postgres backend

**Developer Loop**
- [ ] `.github/semantic.yml` exists; PR titles without valid type prefix are blocked
- [ ] `generate_pr_description` output matches the conventional commit format enforced by semantic PR check
- [ ] PromptFoo eval suite has ≥5 test cases and runs in CI
