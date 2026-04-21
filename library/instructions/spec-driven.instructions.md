# Spec-Driven Execution (Universal)

Use this instruction set for any stack. The goal is predictable output quality with minimal prompt churn.

## Core Rules

1. Define scope first: goal, non-goals, acceptance criteria.
2. Work in small, verifiable increments.
3. Prefer direct implementation over speculative abstraction.
4. Keep changes local to the requested scope.
5. Run targeted validation before broad validation.

## Required Inputs

1. Problem statement in one to three sentences.
2. Affected files or subsystems.
3. Acceptance criteria.
4. Constraints (time, tooling, style, deployment assumptions).

## Execution Loop

1. Context pack: collect only relevant files and symbols.
2. Plan: create a short ordered list of implementation steps.
3. Implement: batch related edits together.
4. Validate: run focused checks for changed behavior.
5. Review: confirm acceptance criteria and edge cases.
6. Summarize: list outcome, risks, and next action.

## Tool Call Discipline

1. Batch file reads before editing.
2. Avoid repeated reads of unchanged files.
3. Prefer one coherent edit per file.
4. Escalate to broader diagnostics only on failure.

## Output Contract

1. What changed.
2. Why it changed.
3. Validation performed.
4. Remaining risks or follow-ups.

## Stop Conditions

1. Missing critical input blocks safe implementation.
2. Conflicting requirements cannot be resolved with a reasonable default.
3. External dependency unavailable and no practical fallback exists.
