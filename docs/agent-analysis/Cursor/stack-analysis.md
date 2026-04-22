# Stack Analysis (Cursor)

## 1) Executive Summary

- The repository has a strong control-plane foundation centered on the MCP server (`apps/mcp-server/src/Ryan.MCP.Mcp/Program.cs`) with coherent service composition for policy, routing, workflow state, observability, and MCP tool discovery.
- Workflow maturity is high: structured multi-step workflows and resumable state are implemented in `McpTools/WorkflowTools.cs` and `Services/WorkflowState/*`.
- Cost/safety optimization is materially implemented: routing escalation/fallback, per-workflow budget checks, semantic retrieval cache, and prompt-prefix cache are present in `McpTools/ModelMappingTools.cs` and `Services/Knowledge/*`.
- Guardrail intent is clear but runtime enforcement is inconsistent; `policy_preflight` exists, but not all execute/mutate paths are required to pass through it.
- Security posture needs hardening: Bifrost currently allows `"tools_to_execute": ["*"]` in `infra/bifrost/bifrost.config.json`, and app-layer auth middleware is not evident in the MCP server.
- CI validation is too narrow for current complexity: only guardrail field checks run, and those checks are changed-file + regex-based (`scripts/validate-agent-contract.mjs`).
- Contract drift risk exists due to duplicated workflow sources (runtime hardcoded workflows + multiple markdown workflow files, including legacy flow docs).
- Overall maturity: strong architecture and momentum, but P0 hardening is needed for policy enforcement consistency, perimeter security, and verification automation.

## 2) Prioritized Recommendations

| Priority (P0/P1/P2) | Recommendation | Why it matters (impact) | Effort (S/M/L) | Risk | Suggested first step (concrete) |
|---|---|---|---|---|---|
| P0 | Enforce centralized policy preflight for all mutate/execute tools | Eliminates policy bypass paths and aligns guardrails with runtime behavior | M | Medium | Add a shared policy-gate helper/interceptor and apply it to `BuildTools`, `FrontendBuildTools`, `NpmHygieneTools`, `DiffAnalysisTools`, and `ConnectorTools` |
| P0 | Remove wildcard execution in Bifrost | Reduces blast radius from prompt/tool misuse and enforces least privilege | S | Low | Replace `"tools_to_execute": ["*"]` with an explicit allowlist in `infra/bifrost/bifrost.config.json` |
| P0 | Add authN/authZ boundary for MCP/API endpoints | Prevents unauthorized tool/API usage if service is reachable | M | Medium | Add `AddAuthentication`/`UseAuthentication`/`UseAuthorization` in `Program.cs` and enforce on `/mcp` + management endpoints |
| P1 | Add mandatory CI quality gate (build + tests + static checks) | Prevents regressions and supports safe agent-driven iteration | M | Low | Add `.github/workflows/mcp-quality.yml` for `dotnet build`, tests, analyzers/lint, and guardrail validation |
| P1 | Upgrade guardrail validation to full schema validation | Current regex checks can pass invalid contracts and miss latent drift | M | Low | Update `scripts/validate-agent-contract.mjs` to parse frontmatter as YAML and validate with `library/guardrails/agent-contract.schema.json` |
| P1 | Harden readiness checks | Current readiness can report healthy while dependencies are degraded | S | Low | Extend `/health/ready` in `Program.cs` to include workflow-state store, connector reachability, and ingestion baseline checks |
| P2 | Consolidate workflow source of truth | Reduces drift between runtime and docs and improves operator clarity | M | Medium | Decide canonical source (markdown or runtime config) and add a drift-check CI script |
| P2 | Persist routing budget spend in durable storage | In-memory spend resets on restart and is not multi-instance safe | M | Medium | Add a Postgres-backed budget ledger and wire `RoutingBudgetService` to read/write it |

## 3) Gaps and Risks

### Gap title
Policy gate enforcement is partial for execute/mutate tools.

- Evidence (file paths/symbols/config references):
  - `apps/mcp-server/src/Ryan.MCP.Mcp/McpTools/PolicyTools.cs` (`policy_preflight` exists)
  - `apps/mcp-server/src/Ryan.MCP.Mcp/McpTools/BuildTools.cs` (command allowlist only)
  - `apps/mcp-server/src/Ryan.MCP.Mcp/McpTools/ConnectorTools.cs` (`call_external_mcp_tool` direct execution path)
- Consequence if unaddressed: policy remains advisory; high-risk operations can run without consistent approval semantics.
- Mitigation plan: enforce preflight centrally for all mutate/execute handlers; fail closed on missing token where required.
- Confidence (High/Med/Low): High

### Gap title
Overbroad tool execution in Bifrost.

- Evidence:
  - `infra/bifrost/bifrost.config.json` -> `"tools_to_execute": ["*"]`
- Consequence if unaddressed: elevated risk from unsafe tool invocation scope.
- Mitigation plan: explicit execute allowlist + CI lint to block wildcard reintroduction.
- Confidence: High

### Gap title
App-layer authentication/authorization is not visible in MCP server.

- Evidence:
  - No `AddAuthentication`, `UseAuthentication`, or `UseAuthorization` usage found in `apps/mcp-server/src/Ryan.MCP.Mcp`.
- Consequence if unaddressed: unauthorized access risk on exposed service interfaces.
- Mitigation plan: add auth middleware and endpoint-level authorization policy.
- Confidence: High

### Gap title
Readiness endpoint lacks critical dependency checks.

- Evidence:
  - `apps/mcp-server/src/Ryan.MCP.Mcp/Program.cs` readiness logic includes document check `TotalDocuments >= 0` (always true), without connector/workflow-state/telemetry probes.
- Consequence if unaddressed: false-green readiness and delayed incident detection.
- Mitigation plan: probe workflow-state persistence, external connector reachability, and telemetry export path in readiness response.
- Confidence: High

### Gap title
Workflow contract drift risk due to duplicate sources.

- Evidence:
  - Runtime hardcoded workflows in `apps/mcp-server/src/Ryan.MCP.Mcp/McpTools/WorkflowTools.cs`
  - Multiple docs in `library/workflows/*.workflow.md` including legacy files (`feature-flow.workflow.md`, `bugfix-flow.workflow.md`, etc.) alongside new canonical workflows.
- Consequence if unaddressed: operators/agents may follow stale or conflicting flow definitions.
- Mitigation plan: canonicalize one source and enforce parity via CI drift checks.
- Confidence: High

### Gap title
Guardrail schema is not fully enforced.

- Evidence:
  - Schema: `library/guardrails/agent-contract.schema.json`
  - Validator: `scripts/validate-agent-contract.mjs` (changed files only + regex presence checks)
- Consequence if unaddressed: malformed/partial contracts pass CI and degrade reliability of orchestration policy.
- Mitigation plan: full-library schema validation on schedule + PR changed-file schema validation.
- Confidence: High

### Gap title
Insufficient automated testing for critical MCP logic.

- Evidence:
  - No `*Tests*.csproj` found under `apps/mcp-server`.
  - Workflows in `.github/workflows` currently focus on deploy + guardrail check.
- Consequence if unaddressed: regressions in policy/routing/cache/workflow-state are likely under rapid change.
- Mitigation plan: add focused tests first for policy classification, routing fallback behavior, cache invalidation, and workflow-state stores.
- Confidence: Medium

## 4) Tooling Additions (max 5)

### 1) Name
`policy_coverage_audit`

- Category (MCP/CI/Security/Observability/Productivity/etc.): MCP + Security/Governance
- Exact problem solved in THIS repo: identifies tools that bypass policy preflight or approval semantics.
- Expected productivity gain: high; cuts manual policy-code audits by ~30-40%.
- Integration approach (where/how to add): new tool in `McpTools/PolicyTools.cs` that reflects tool catalog and reports enforcement coverage per tool.
- Prerequisites/dependencies: MCP tool metadata/reflection access.
- Cost/complexity note: Medium.
- Why this made top-5 (vs alternatives): directly addresses highest-risk inconsistency with immediate actionable output.

### 2) Name
`workflow_contract_drift_check`

- Category: CI + Productivity/Reliability
- Exact problem solved: catches mismatch between runtime workflows and markdown workflow library.
- Expected productivity gain: medium-high; fewer orchestration errors and faster onboarding (~20-30%).
- Integration approach: add script/tool comparing `WorkflowTools` definitions with `library/workflows/*.workflow.md`; run in PR CI.
- Prerequisites/dependencies: markdown/frontmatter parser.
- Cost/complexity note: Medium.
- Why top-5: high maintainability ROI with low operational risk.

### 3) Name
`agent_contract_fullscan`

- Category: CI/Governance
- Exact problem solved: current validator checks only changed files and key presence; misses full schema conformance.
- Expected productivity gain: medium; reduces latent config defects and rework (~15-25%).
- Integration approach: upgrade `scripts/validate-agent-contract.mjs` to YAML + JSON Schema validation (AJV) with changed-file and full-scan modes.
- Prerequisites/dependencies: `ajv`, `yaml` parser.
- Cost/complexity note: Small-Medium.
- Why top-5: low-cost, immediate reliability boost across agent library.

### 4) Name
`runtime_dependency_probe`

- Category: Observability/Operations
- Exact problem solved: readiness currently does not prove critical dependencies are healthy.
- Expected productivity gain: medium-high for incident response (~30% faster triage).
- Integration approach: add tool + readiness integration in `Program.cs` to probe workflow-state store, memory store, external connectors, and telemetry export path.
- Prerequisites/dependencies: lightweight probe interfaces in relevant services.
- Cost/complexity note: Medium.
- Why top-5: directly improves production safety and MTTR.

### 5) Name
`bifrost_policy_linter`

- Category: Security/CI
- Exact problem solved: prevents insecure Bifrost MCP config patterns (wildcard execute, unsafe auto-exec set).
- Expected productivity gain: medium; avoids risky config regressions before deploy (~20%).
- Integration approach: add node script under `scripts/` to lint `infra/bifrost/bifrost.config.json`; run in PR CI.
- Prerequisites/dependencies: none beyond Node.
- Cost/complexity note: Small.
- Why top-5: fastest path to reduce external execution risk.

## 5) 30/60/90 Day Improvement Plan

### 30 days: quick wins

- Remove wildcard execute scope from Bifrost config.
- Add CI quality workflow for MCP server (build + tests scaffold + analyzers + guardrail checks).
- Upgrade contract validator to schema-based validation.
- Improve readiness checks to include critical dependencies.

Success metrics/KPIs:

- 0 wildcard execute policies in default branch.
- CI quality workflow pass rate >95% for active PRs.
- 100% changed agent/skill files schema-validated.
- Readiness includes per-dependency status and no known false-green incidents.

### 60 days: medium-term hardening

- Implement centralized policy enforcement across all mutate/execute tools.
- Add `policy_coverage_audit` and `workflow_contract_drift_check`.
- Add focused unit/integration tests for policy/routing/cache/workflow-state critical paths.

Success metrics/KPIs:

- 100% mutate/execute tools report policy-enforced in coverage audit.
- Drift check catches mismatches pre-merge.
- Critical module test coverage >70%.
- 0 confirmed policy bypass incidents.

### 90 days: strategic upgrades

- Persist routing budget spend in Postgres for restart/multi-instance continuity.
- Add observability SLO guards (deny-rate spikes, cache hit floors, budget-deny trend thresholds).
- Standardize workflow source-of-truth and deprecate legacy flow docs.

Success metrics/KPIs:

- Budget continuity across restarts: 100%.
- SLO alert quality: <5% false positives.
- Workflow drift incidents: 0.
- PR cycle time improvement target: 15-25%.

## 6) Validation Checklist

- [ ] `infra/bifrost/bifrost.config.json` has explicit execute allowlist (no wildcard).
- [ ] All mutate/execute MCP tools enforce policy preflight + approval semantics where configured.
- [ ] MCP server uses authentication and authorization middleware on sensitive surfaces.
- [ ] `/health/ready` verifies workflow-state, connector, and ingestion health (not optimistic placeholders).
- [ ] CI runs build + tests + static analysis for MCP changes.
- [ ] Agent/skill contracts are validated against `agent-contract.schema.json` (not regex-only).
- [ ] Workflow runtime definitions and markdown workflow specs pass drift checks.
- [ ] Routing budget accounting persists across restarts/instances.
- [ ] Dashboards include policy deny rates, routing budget trend, retrieval cache hit ratio, and prompt cache hit ratio.
- [ ] Regression tests exist for policy gate behavior, routing fallback, and cache invalidation.

## Assumptions

- Branch naming is currently mixed (`main` in guardrail validation script vs `master` in deploy workflow), which may require normalization.
- Repository contains active in-flight changes; recommendations are based on visible code/config snapshots.
