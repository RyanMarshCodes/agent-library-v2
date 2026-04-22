Here is the full-stack assessment of the repository's agentic platform, focusing on the MCP server, agent library, workflows, and guardrails.

## 1) Executive Summary
- **Key Strength**: World-class conceptual architecture. The separation of declarative files (authoring plane) and the MCP server (runtime plane) is highly scalable and well-designed.
- **Key Strength**: Exceptional operational guardrails. The token/cost budgeting, model routing (small-first), and `policy_preflight` gates represent state-of-the-art AI cost control.
- **Key Weakness**: Operational contract drift. The `library/workflows` markdown files and the hardcoded dictionary in C# `WorkflowTools.cs` are fundamentally disconnected and have already drifted.
- **Key Weakness**: Runtime tool blindspots. Highly capable dev-loop tools like `PrTools.cs` are completely broken in production because the base Docker image lacks required CLI dependencies (`git`, `gh`).
- **Maturity Assessment**: Intermediate/Advanced. The underlying infrastructure and policy controls are incredibly robust, but manual syncing and container dependency oversights prevent it from being a fully autonomous, reliable SDLC engine today.

## 2) Prioritized Recommendations

| Priority | Recommendation | Why it matters (impact) | Effort | Risk | Suggested first step |
| --- | --- | --- | --- | --- | --- |
| **P0** | **Fix MCP Container Tool Dependencies** | `PrTools.cs` shells out to `git` and `gh`, but neither are installed in the `aspnet:10.0` Docker container. The `/review` and `/commit` workflows currently fail in production. | S | Low | Add `RUN apt-get update && apt-get install -y git gh` to `src/Ryan.MCP.Mcp/Dockerfile`. |
| **P0** | **Reconcile Workflow File Drift** | `library/workflows` contains legacy duplicates (`feature-flow.md` vs `feature-delivery.md`) that confuse the orchestrator and degrade UX. | S | Low | Delete `feature-flow.workflow.md`, `bugfix-flow.workflow.md`, `release-flow.workflow.md` and `review-flow.workflow.md`. |
| **P1** | **Update Bifrost Auto-Execute Config** | `bifrost.config.json` is missing new read-only tools (`policy_preflight`, `workflow_state_get`), forcing unnecessary manual approval prompts that slow down automation. | S | Low | Add `policy_preflight`, `workflow_state_get`, `workflow_state_list`, `platform_metrics_snapshot` to `tools_to_auto_execute`. |
| **P1** | **Dynamic Workflow Parsing** | `WorkflowTools.cs` hardcodes the 5 workflows. Updating a markdown workflow requires a C# deployment to take effect, defeating the purpose of the file-based library. | M | Med | Refactor `WorkflowTools.cs` to ingest and parse `library/workflows/*.md` dynamically on startup. |
| **P2** | **Update Orchestrator Routing Contract** | `orchestrator.agent.md` tells sub-agents to look for `WORKFLOW_COMMANDS.md`, which no longer exists. | S | Low | Update the canonical workflows section in `orchestrator.agent.md` to reference `library/workflows/`. |

## 3) Gaps and Risks

**Gap 1: MCP Runtime Dependency Blindspot**
- **Evidence**: `apps/mcp-server/src/Ryan.MCP.Mcp/Dockerfile` uses the `mcr.microsoft.com/dotnet/aspnet:10.0` base image but does not install `git` or `gh`. `PrTools.cs` uses `RunCommandAsync` to execute both.
- **Consequence if unaddressed**: The `/review` and `/commit` workflows will fail outright when attempting to use PR tools, breaking the Dev Loop closure plan.
- **Mitigation plan**: Add installation steps for `git` and the GitHub CLI (`gh`) to the `final` stage of the Dockerfile.
- **Confidence**: High

**Gap 2: Workflow Definition Drift**
- **Evidence**: `WorkflowTools.cs` exposes `/feature-delivery`, `/incident-triage`, but `library/workflows/` contains legacy duplicates (`feature-flow.workflow.md`, `bugfix-flow.workflow.md`). `orchestrator.agent.md` references a missing `WORKFLOW_COMMANDS.md`.
- **Consequence if unaddressed**: Orchestrator agent will hallucinate workflow paths, developers will use the wrong templates, and required phase gates will be bypassed.
- **Mitigation plan**: Purge legacy workflow files. Update `orchestrator.agent.md` routing table to point directly to `library/workflows/`.
- **Confidence**: High

**Gap 3: Incomplete Auto-Execute Allowlist**
- **Evidence**: `infra/bifrost/bifrost.config.json` lacks new platform tools (`policy_preflight`, `workflow_state_get`, `workflow_state_list`, `platform_metrics_snapshot`).
- **Consequence if unaddressed**: Developers and agents will hit approval prompts for safe, read-only diagnostic operations, breaking autonomous delivery chains.
- **Mitigation plan**: Append the missing tool names to the `tools_to_auto_execute` array in `bifrost.config.json`.
- **Confidence**: High

## 4) Tooling Additions (max 5)

**1. AST/Codebase Map MCP Tool (`grep-ast` or Roslyn C# Parser)**
- **Category**: MCP / Productivity
- **Exact problem solved in THIS repo**: `WorkflowTools.cs` and `PrTools.cs` are massive. Agents burn significant token budgets reading entire files just to understand structural dependencies.
- **Expected productivity gain**: High; massive token reduction and faster context gathering.
- **Integration approach**: Add an MCP tool `get_file_skeleton` that uses Roslyn (or a packaged `grep-ast` binary in Docker) to return only class/method signatures.
- **Prerequisites/dependencies**: C# Roslyn packages or `grep-ast`.
- **Cost/complexity note**: Medium complexity to implement parsing, very low runtime cost.
- **Why this made top-5**: Token efficiency is a stated primary goal in the `agentic-platform-upgrade.plan.md`.

**2. Automated PR Agent Reviewer (GitHub Action)**
- **Category**: CI / Productivity
- **Exact problem solved in THIS repo**: PR creation works via MCP, but the `/review` workflow is only run manually by the developer. Multi-agent review should be asynchronous and continuous.
- **Expected productivity gain**: Very High; catches bugs, coverage gaps, and policy violations before human reviewers even see the PR.
- **Integration approach**: Add `.github/workflows/agent-pr-review.yml` that triggers the MCP server's `/review` workflow via Bifrost against the PR diff when opened.
- **Prerequisites/dependencies**: Exposed Bifrost endpoint or local agent CLI runner in CI.
- **Cost/complexity note**: Low complexity.
- **Why this made top-5**: Perfectly closes the "Dev Loop integration" mentioned in the upgrade plan.

**3. Schema Validation Pre-Commit Hook**
- **Category**: Security / Productivity
- **Exact problem solved in THIS repo**: `agent-contract.schema.json` is validated in CI, but developers can author invalid agents locally, wasting cycles before pushing.
- **Expected productivity gain**: Medium; faster feedback loop for agent authors.
- **Integration approach**: Add a Husky or Lefthook pre-commit hook running `scripts/validate-agent-contract.mjs`.
- **Prerequisites/dependencies**: Node.js.
- **Cost/complexity note**: Very Low.
- **Why this made top-5**: Pushes governance (a core focus of the repo) to the earliest possible point in the SDLC.

**4. Dynamic Markdown Parser Service**
- **Category**: Observability / Productivity
- **Exact problem solved in THIS repo**: Hardcoded workflows in C# require full deployments to change SDLC phases. 
- **Expected productivity gain**: High; creates a single source of truth for SDLC workflows (the markdown files).
- **Integration approach**: Create a C# service in `Ryan.MCP.Mcp` that reads `library/workflows/*.md` on startup, parses the headers/steps using `Markdig`, and populates `WorkflowTools.cs`.
- **Prerequisites/dependencies**: Markdig NuGet package.
- **Cost/complexity note**: Medium.
- **Why this made top-5**: Resolves the most glaring architectural contract drift in the codebase.

**5. Token/Cost Budget Pre-flight Enforcer**
- **Category**: Security / Cost
- **Exact problem solved in THIS repo**: MCP checks budgets at runtime, but agents can propose massive edits that predictably blow the budget before execution even starts.
- **Expected productivity gain**: Medium; catches budget overruns before they execute and fail halfway through a mutate operation.
- **Integration approach**: Enhance `policy_preflight` to accept a `predicted_tokens` or `context_size` arg, failing fast if the workflow budget is statically exceeded.
- **Prerequisites/dependencies**: None.
- **Cost/complexity note**: Low.
- **Why this made top-5**: Directly supports the "Token/Cost Optimization" phase of the platform upgrade plan.

## 5) 30/60/90 Day Improvement Plan

**30 Days: Quick Wins (Drift Elimination & Config Fixes)**
- Add `git` and `gh` to the `apps/mcp-server/src/Ryan.MCP.Mcp/Dockerfile`.
- Delete legacy duplicated workflows (`feature-flow`, `bugfix-flow`).
- Update `bifrost.config.json` with new read-only tools.
- Update `orchestrator.agent.md` to point to the correct workflow files.
- **Success Metrics/KPIs**: 0 duplicate workflows, 100% of read tools auto-execute without prompts, `/commit` tool runs without binary missing errors.

**60 Days: Medium-term Hardening (CI Automation)**
- Implement GitHub Action for automated PR reviews using the `/review` workflow.
- Implement AST/Codebase Map MCP tool for token reduction.
- Add local pre-commit hook for `agent-contract.schema.json`.
- **Success Metrics/KPIs**: 20% reduction in context-gathering token spend, 100% of PRs receive an AI review comment within 5 minutes.

**90 Days: Strategic Upgrades (Dynamic State & Tooling)**
- Refactor `WorkflowTools.cs` to dynamically parse `library/workflows/*.md`.
- Enhance `policy_preflight` with predictive token budget enforcement.
- **Success Metrics/KPIs**: 0 hardcoded workflows in C#, workflow step updates require 0 MCP server deployments.

## 6) Validation Checklist
- [ ] Is `apt-get install -y git gh` (or alpine equivalent) present in the `final` stage of `apps/mcp-server/src/Ryan.MCP.Mcp/Dockerfile`?
- [ ] Are `feature-flow.workflow.md`, `bugfix-flow.workflow.md`, `release-flow.workflow.md`, and `review-flow.workflow.md` deleted from `library/workflows`?
- [ ] Does `orchestrator.agent.md` reference `library/workflows/*.md` directly instead of `WORKFLOW_COMMANDS.md`?
- [ ] Does `infra/bifrost/bifrost.config.json` include `policy_preflight` and `workflow_state_get` in the `tools_to_auto_execute` array?
- [ ] Has `WorkflowTools.cs` been refactored to read workflow markdown dynamically instead of hardcoding a `Dictionary`?
