# Workflow: Eval Regression

## Workflow Metadata

- Workflow ID: `eval-regression`
- Version: `1.0.0`
- Primary outcome: detect behavior regressions before broad rollout.

## Required Inputs

1. Baseline and candidate versions.
2. Eval suite and thresholds.
3. Critical scenarios.
4. Runtime/cost budget for evaluation.

## Phase Gates

### Gate 1: Eval Readiness

- Required artifacts: eval plan, baseline snapshot, pass/fail thresholds.
- Exit criteria: reproducible suite and clear acceptance policy.
- Stop conditions: missing baseline or undefined thresholds.

### Gate 2: Execute Baseline and Candidate

- Required artifacts: structured run outputs for both versions.
- Exit criteria: runs are comparable.
- Stop conditions: invalid run parity or missing critical scenarios.

### Gate 3: Analyze Drift

- Required artifacts: delta analysis, failure clustering, likely causes.
- Exit criteria: regressions categorized by severity.
- Stop conditions: high-severity regressions with unknown cause.

### Gate 4: Release Decision

- Required artifacts: go/no-go recommendation and mitigation plan.
- Exit criteria: decision owner sign-off.
- Stop conditions: unaccepted high-severity regressions.

## Required Outputs

1. Eval result report.
2. Regression decision summary.
3. Mitigation and rerun plan.
