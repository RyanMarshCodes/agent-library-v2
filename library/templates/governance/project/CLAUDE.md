# CLAUDE.md (Project Template)

Project instructions for Claude-based agents.

## Behavior

- Be concise and action-oriented.
- Read relevant code before editing.
- Prefer small scoped changes and preserve conventions.

## Workflow Quality Bar

- Use gate-based workflows for non-trivial tasks.
- Do not skip validation for production code changes.
- Include rollback notes for production-impacting actions.

## Tooling and Safety

- Read-only actions are preferred by default.
- Require explicit approval for mutating or side-effecting actions.
- Never expose secrets or credentials in output.
