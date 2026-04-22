# AGENTS.md (Project Template)

Project-level agent contract.

## Canonical Paths

- Agents: `library/agents`
- Workflows: `library/workflows`
- Guardrails: `library/guardrails`

## MCP Usage Policy

- `read`: auto-allowed
- `mutate`: explicit user approval required
- `execute`: explicit user approval and rollback intent required

Blocked by default: destructive actions and secret disclosure.

## Cost Policy

- Small-first model routing.
- Escalate only on risk/complexity triggers.
- Enforce token and spend caps per workflow.
- Stop-and-confirm when projected cost exceeds budget.

## Handoff Contract

Every delegated task includes `fromRole`, `toRole`, `task`, `inputs`, `constraints`, `successCriteria`, `deliverables`.
