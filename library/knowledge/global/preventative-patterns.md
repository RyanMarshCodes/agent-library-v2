# Preventative Patterns: The Twelve Silent Killers

A prioritized list of anti-patterns that must be addressed **before** they become production problems. When scaffolding or refactoring code, always evaluate against these patterns first.

---

## Part 1: Foundation, Observability, and Async

| # | Anti-Pattern | Description |
|---|--------------|-------------|
| #1 | **Fat Controllers** | Business logic leaked into API controllers. Extract to domain services/application layers. |
| #2 | **No Input Validation** | Missing validation at API boundaries. Always validate and return RFC 7807 Problem Details. |
| #3 | **Raw Exceptions** | Swallowing or leaking raw exceptions. Wrap with domain-specific errors, never expose stack traces. |
| #4 | **Blocking Async** | Calling `.Result` or `.Wait()` on async code. Use `await` throughout the call stack. |
| #5 | **Ignoring CancellationTokens** | Not passing `CancellationToken` to long-running operations. Respect cooperative cancellation. |
| #11 | **No Observability** | Missing structured logging, metrics, and tracing. Emit distributed traces for every operation. |

---

## Part 2: Data Access and API Contracts

| # | Anti-Pattern | Description |
|---|--------------|-------------|
| #6 | **No Pagination** | Returning unbounded collections. Always paginate, using cursor-based for large datasets. |
| #7 | **Wrong HTTP Status Codes** | Using `200 OK` for errors or `404` for authorization failures. Return correct 4xx/5xx codes. |
| #8 | **Over-fetching Data** | Returning more data than the client needs. Use DTOs/projection to trim responses. |
| #9 | **Returning EF Entities** | Leaking ORM entities to API layers. Always map to DTOs before returning. |

---

## Part 3: Security, Resilience, and Idempotency

| # | Anti-Pattern | Description |
|---|--------------|-------------|
| #10 | **No Rate Limiting** | No throttling on mutating endpoints. Apply rate limits at gateway or middleware level. |
| #12 | **No Idempotency on Mutating Endpoints** | Non-idempotent POST/PATCH endpoints without idempotency keys. Client retries can cause duplicate operations. |

---

## Priority Rules

When scaffolding or reviewing code:

1. **Part 1 issues are foundational** — if these are wrong, everything built on top is fragile
2. **Part 2 issues are contract violations** — they affect API consumers and data integrity
3. **Part 3 issues are production risks** — they cause security incidents, outages, or data corruption

> Always fix higher-priority issues before lower-priority ones. A system with correct status codes but blocking async calls is still a time bomb.

---

## Integration with Standards

This document complements other global standards:
- **API Design** (`api-design.md`) — covers #6, #7, #8, #9
- **Unit Testing** (`unit-testing.md`) — use tests to verify fixes for #1, #2, #3
- **Async patterns** — apply #4, #5 consistently across all stacks
