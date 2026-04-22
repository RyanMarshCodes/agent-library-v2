# AGENTS.md

Repository contract for agentic delivery in this project. This file defines canonical sources, workflow gates, handoff format, tool policy, and guardrails.

## Canonical Source of Truth

- Agents: `library/agents/*.agent.md`
- Skills: `library/skills/**/SKILL.md`
- Workflows: `library/workflows/*.workflow.md`
- Governance templates: `library/templates/governance/**`
- Guardrails: `library/guardrails/**`
- MCP workflow runtime: `apps/mcp-server/src/Ryan.MCP.Mcp/McpTools/WorkflowTools.cs`

## Delivery Workflow Contract

Default sequence for feature work:

`/context -> /spec -> /validate -> /architect -> /validate -> /implement -> /test -> /reflect -> /review -> /commit -> /wrapup`

Use dedicated workflow specs for operational paths:

- `feature-delivery.workflow.md`
- `incident-triage.workflow.md`
- `spike-research.workflow.md`
- `multi-agent-delivery.workflow.md`
- `eval-regression.workflow.md`

Rules:

- Do not advance when a gate fails.
- Every phase must produce artifacts.
- Production-impacting work must include rollback guidance.

## Agent Handoff Contract

Every handoff between agents must include:

- `fromRole`
- `toRole`
- `task`
- `inputs`
- `constraints`
- `successCriteria`
- `deliverables`

## MCP Tool Policy Tiers

- Tier A `read`: auto-allowed.
- Tier B `mutate`: explicit approval token required.
- Tier C `execute`: explicit approval token required, plus rollback intent.

Blocked by default:

- Destructive operations without explicit user approval.
- Secret exfiltration or credential disclosure.
- Cross-boundary actions outside declared scope.

## Security and Data Handling

- Never output plaintext secrets, tokens, or private keys.
- Redact sensitive values in logs or summaries.
- Use least privilege for tools and connectors.
- Keep production data handling constrained and explicit.

## Cost and Token Discipline

- Route small-first by default.
- Escalate model tier only on complexity/risk trigger.
- Enforce per-workflow token and spend ceilings.
- Cap retrieval breadth and context size.
- Stop-and-confirm when spend is predicted to exceed budget.

## Evidence Standard

For architecture/security/release recommendations, include:

- file or artifact references,
- validation evidence,
- assumptions,
- residual risks.

## Incident and Rollback Discipline

For production-impacting changes, include:

- impact statement,
- mitigation,
- verification checks,
- rollback plan,
- owner.
