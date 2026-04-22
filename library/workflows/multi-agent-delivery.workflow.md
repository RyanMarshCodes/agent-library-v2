# Workflow: Multi-Agent Delivery

## Workflow Metadata

- Workflow ID: `multi-agent-delivery`
- Version: `1.0.0`
- Primary outcome: coordinate parallel specialist work with clean handoffs and auditable outputs.

## Required Inputs

1. Parent objective and success metrics.
2. Subtask decomposition with dependencies.
3. Agent assignment map.
4. Shared constraints (security, cost, timeline, quality).

## Phase Gates

### Gate 1: Orchestration Plan

- Required artifacts: subtask DAG, routing table, parallelism plan.
- Exit criteria: dependency correctness verified.
- Stop conditions: ambiguous ownership, conflicting success criteria.

### Gate 2: Handoff Quality

- Required artifacts: structured handoff packet for each subtask.
- Exit criteria: handoffs are testable and unambiguous.
- Stop conditions: missing contract fields or unbounded scope.

### Gate 3: Parallel Execution

- Required artifacts: per-subtask outputs, validation evidence, blocker log.
- Exit criteria: critical-path subtasks complete or escalated.
- Stop conditions: unresolved blocker on critical path.

### Gate 4: Integration and Final Review

- Required artifacts: integrated output summary, conflict resolution notes, cross-cutting checks.
- Exit criteria: consolidated recommendation ready.
- Stop conditions: unresolved integration conflict or major evidence gaps.

## Required Outputs

1. Aggregated subtask outcomes by owner.
2. Blocker/escalation log.
3. Final delivery recommendation with residual risks.
