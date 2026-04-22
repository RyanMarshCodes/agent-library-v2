# Security Guardrails v1

## Tool Risk Tiers

- Tier A (`read`): auto-allowed, no side effects.
- Tier B (`mutate`): explicit approval required.
- Tier C (`execute`): explicit approval required plus rollback intent.

## Blocked by Default

- Destructive operations without explicit approval.
- Secret retrieval/export in plaintext outputs.
- Cross-boundary access outside declared scope.

## Sensitive Data Handling

- Never output tokens, keys, credentials, or connection strings.
- Redact sensitive values in logs/traces.
- Use least-privilege credentials for external systems.

## Audit Envelope

For Tier B/C actions capture:

- actor,
- operation,
- scope,
- decision,
- result,
- timestamp.
