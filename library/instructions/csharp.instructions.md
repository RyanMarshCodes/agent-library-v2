# C# Overlay (Optional)

Use this overlay only when working in C#/.NET codebases. It complements the universal spec-driven instruction set.

## Design

1. Favor clear domain naming over framework-heavy patterns.
2. Keep service boundaries explicit and testable.
3. Prefer composition over inheritance unless inheritance is clearly warranted.

## Implementation

1. Enable nullable reference types and honor warnings.
2. Use async APIs end-to-end; avoid blocking calls.
3. Validate inputs at boundaries (API handlers, commands, public methods).
4. Return structured error results for recoverable failures.

## Data and Persistence

1. Keep database migrations explicit and versioned.
2. Avoid leaking persistence entities directly into external contracts.
3. Use transaction scope intentionally; keep transactions short.

## Testing

1. Add unit tests for branch logic and error paths.
2. Add integration tests for persistence or API boundaries when behavior changes.
3. Prefer deterministic tests (inject time, IDs, and external effects).

## Performance and Safety

1. Measure before optimizing.
2. Avoid unnecessary allocations in hot paths.
3. Propagate cancellation tokens through async flows.
4. Never log secrets or sensitive payloads.
