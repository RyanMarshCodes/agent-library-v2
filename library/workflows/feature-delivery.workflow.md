# Workflow: Feature Delivery

## Workflow Metadata

- Workflow ID: `feature-delivery`
- Version: `1.0.0`
- Primary outcome: ship a feature with spec clarity, verified behavior, and safe release posture.

## Required Inputs

1. Feature statement and objective.
2. Acceptance criteria and non-goals.
3. Constraints (time, risk, dependencies, compliance).
4. Affected systems/components.

## Phase Gates

### Gate 1: Scope and Spec

- Entry: feature request is accepted.
- Required artifacts: product spec, acceptance criteria, assumptions/non-goals.
- Exit criteria: acceptance criteria are testable, key ambiguity removed.
- Stop conditions: conflicting requirements, missing decision owner.

### Gate 2: Architecture and Plan

- Entry: spec is approved.
- Required artifacts: engineering design, task DAG, risk register, rollback intent.
- Exit criteria: dependencies are sequenced, constraints captured.
- Stop conditions: unsupported assumptions, unbounded migration scope.

### Gate 3: Implementation and Verification

- Entry: design is approved.
- Required artifacts: scoped code changes, tests for changed behavior, validation evidence.
- Exit criteria: acceptance criteria satisfied, no unresolved high severity defects.
- Stop conditions: mandatory checks fail, unmitigated regression.

### Gate 4: Review and Release Readiness

- Entry: implementation validation passes.
- Required artifacts: prioritized findings, release notes, rollback checklist.
- Exit criteria: blockers resolved or explicitly waived, recommendation recorded.
- Stop conditions: unresolved security blocker, missing rollback path.

## Required Outputs

1. Change summary mapped to acceptance criteria.
2. Validation evidence and test scope.
3. Risk, rollback, and follow-up actions.
4. Merge/release recommendation.
