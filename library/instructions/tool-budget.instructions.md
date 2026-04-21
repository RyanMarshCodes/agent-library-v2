# Tool Budget Policy (Universal)

## Goal
Control execution cost while preserving quality.

## Budget Rules

1. Set a soft budget per task.
2. Batch reads, then edits, then validation.
3. Avoid repeated calls that return the same information.
4. Escalate only when targeted checks fail.

## Escalation

1. First failure: refine scope and retry targeted checks.
2. Second failure: run deeper diagnostics.
3. Third failure: stop and request clarification or approval to continue.
