# Context Packaging Policy (Universal)

## Goal
Provide high-signal context to reduce tool churn and model confusion.

## Include

1. Task objective and acceptance criteria.
2. Relevant file paths and symbols.
3. Constraints and non-goals.
4. Known edge cases or previous failures.

## Exclude

1. Unrelated files and historical noise.
2. Duplicated excerpts.
3. Secrets or private credentials.

## Packaging Rules

1. Prefer summaries with exact file references.
2. Include only the minimum code needed to act.
3. Keep context current after each major edit.
