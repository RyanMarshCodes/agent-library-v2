# AGENTS.md (Global Template)

Global baseline policy for all repositories.

## Baseline Guardrails

- Read-first and least privilege.
- Explicit approval for mutate and execute categories.
- Block destructive changes unless explicitly approved.
- Never expose secrets or credentials.

## Baseline Cost Discipline

- Small-first model routing.
- Escalate only when justified by risk/complexity.
- Respect budget caps and stop for confirmation on predicted overrun.
