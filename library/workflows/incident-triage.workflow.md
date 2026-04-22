# Workflow: Incident Triage

## Workflow Metadata

- Workflow ID: `incident-triage`
- Version: `1.0.0`
- Primary outcome: stabilize service quickly, preserve evidence, and prevent recurrence.

## Required Inputs

1. Incident trigger and severity.
2. Impact scope (users, regions, systems).
3. Access to logs/metrics/traces.
4. Incident owner/escalation contacts.

## Phase Gates

### Gate 1: Declare and Contain

- Required artifacts: incident declaration, impact statement, containment plan.
- Exit criteria: owner assigned, communication cadence set, blast radius bounded.
- Stop conditions: no accountable owner, high-risk action without approval.

### Gate 2: Diagnose

- Required artifacts: timeline, hypotheses, evidence set.
- Exit criteria: likely root cause identified with evidence.
- Stop conditions: evidence gaps that block safe action.

### Gate 3: Mitigate and Recover

- Required artifacts: mitigation or rollback action, recovery verification checklist.
- Exit criteria: health restored to agreed threshold.
- Stop conditions: mitigation increases impact.

### Gate 4: Post-Incident Hardening

- Required artifacts: RCA, preventive actions, owner+due dates.
- Exit criteria: follow-up actions accepted into delivery backlog.
- Stop conditions: RCA lacks evidence or ownership.

## Required Outputs

1. Incident timeline and impact summary.
2. Root cause and contributing factors.
3. Mitigation/rollback record.
4. Prevention plan and tracking IDs.
