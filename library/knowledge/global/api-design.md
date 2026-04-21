# API Design Standards

Global standards for REST API design — versioning, naming, pagination, error handling, and status codes.

## URL structure

- Use lowercase kebab-case for path segments: `/user-profiles`, not `/UserProfiles` or `/user_profiles`
- Use nouns, not verbs: `GET /orders`, not `GET /getOrders`
- Nest resources only when the child cannot exist without the parent, and max two levels deep:
  - ✓ `/orders/{orderId}/items`
  - ✗ `/users/{userId}/orders/{orderId}/items/{itemId}/notes`
- Use plural nouns for collections: `/products`, `/invoices`
- Resource identifiers go in the path, not the query string: `/orders/{id}`, not `/orders?id=123`

## HTTP methods

| Method | Use | Idempotent | Body |
|---|---|---|---|
| `GET` | Retrieve resource(s) | Yes | No |
| `POST` | Create a resource or trigger an action | No | Yes |
| `PUT` | Replace a resource entirely | Yes | Yes |
| `PATCH` | Partial update | No (by default) | Yes |
| `DELETE` | Remove a resource | Yes | Optional |

- `GET` and `DELETE` must not have side effects beyond the intended action
- `PUT` must be idempotent — the same body sent twice produces the same result
- Use `POST` for non-CRUD actions: `/payments/{id}/refund`, `/documents/{id}/publish`

## Versioning

Use URL path versioning as the default:
- `/v1/orders`, `/v2/orders`
- Version the API when a breaking change is unavoidable
- Breaking changes: removing a field, changing a field type, changing status code semantics, removing an endpoint
- Non-breaking (additive): adding optional fields to responses, adding new optional query parameters, adding new endpoints

Version sunset policy:
- Announce deprecation at least 6 months before removing a version
- Add `Deprecation: <date>` and `Sunset: <date>` response headers to deprecated endpoints
- Keep v(N-1) alive while v(N) is current unless usage is zero

## Request design

- Accept `Content-Type: application/json` by default
- For file uploads: `multipart/form-data`
- For bulk operations: accept an array in the request body; return per-item results
- Validate all inputs at the API boundary and return 400 with field-level errors (see Error Responses below)
- Document all query parameters; mark optional vs required

## Response design

### Success responses

| Scenario | Status | Body |
|---|---|---|
| Resource returned | `200 OK` | The resource |
| Resource created | `201 Created` | The created resource + `Location` header |
| Async operation accepted | `202 Accepted` | Job/status reference |
| No content to return | `204 No Content` | Empty |
| Partial success (batch) | `207 Multi-Status` | Per-item results |

Always return the created/updated resource in the response body. Never return a bare `200 OK` with an empty body for a create or update — the client needs the server-assigned ID and timestamps.

### Collection responses

Wrap collections in an envelope — never return a bare array as the top-level response:

```json
{
  "data": [...],
  "pagination": {
    "cursor": "eyJpZCI6MTAwfQ==",
    "hasMore": true,
    "totalCount": 4821
  }
}
```

If adding metadata later (pagination, totals) to a bare-array response is a breaking change, that's exactly why to use an envelope from day one.

## Pagination

Choose one strategy and apply it consistently across the entire API.

**Cursor-based** (preferred for large or live datasets):
- Request: `GET /events?cursor=<opaque>&limit=50`
- Response: `{ "data": [...], "nextCursor": "<opaque>", "hasMore": true }`
- Cursor is opaque to the client — do not expose the underlying offset or ID

**Offset-based** (acceptable for small, stable datasets):
- Request: `GET /products?page=2&pageSize=20`
- Response: `{ "data": [...], "page": 2, "pageSize": 20, "totalCount": 847 }`
- Cap `pageSize` at a reasonable maximum (100–500 depending on payload size)

## Error responses

Use RFC 7807 Problem Details for all error responses:

```json
{
  "type": "https://example.com/errors/validation-failed",
  "title": "Validation Failed",
  "status": 400,
  "detail": "One or more fields failed validation.",
  "instance": "/orders/42",
  "errors": {
    "quantity": ["Must be greater than 0"],
    "productId": ["Product not found"]
  },
  "traceId": "00-abc123-def456-00"
}
```

- `type`: a stable URI identifying the error class (not a random URL — it can be a `urn:`)
- `title`: human-readable, never changes for the same `type`
- `detail`: instance-specific explanation
- `errors`: field-level validation errors (for 400 responses)
- `traceId`: always include for debuggability

### Status codes

| Code | Meaning | Notes |
|---|---|---|
| `400` | Bad Request | Validation failure, malformed JSON, business rule violation |
| `401` | Unauthorized | Missing or invalid credentials — not "you don't have permission" |
| `403` | Forbidden | Authenticated but not authorized for this resource |
| `404` | Not Found | Resource does not exist (or is hidden from this caller) |
| `409` | Conflict | Optimistic concurrency failure, duplicate creation |
| `422` | Unprocessable Entity | Request is valid JSON but semantically invalid |
| `429` | Too Many Requests | Always include `Retry-After` header |
| `500` | Internal Server Error | Never leak stack traces or internal details |
| `503` | Service Unavailable | Include `Retry-After` if known |

Do not use `200 OK` with an error payload. Use the correct 4xx/5xx status.

## Headers

Always return:
- `Content-Type: application/json; charset=utf-8` on JSON responses
- `X-Request-Id` or `X-Trace-Id`: the trace ID for correlation
- `Cache-Control` on `GET` responses: `no-store` for private/dynamic data, appropriate `max-age` for static resources

For paginated collections:
- `X-Total-Count: 4821` (optional but useful)

## Security

- Require HTTPS. Redirect HTTP to HTTPS — never serve API content over plain HTTP in production.
- Use `Authorization: Bearer <token>` for token-based auth; never pass tokens in query strings (they appear in server logs)
- Apply rate limiting at the gateway level; return `429` with `Retry-After`
- Return `404` (not `403`) when hiding the existence of a resource from an unauthorized caller
- Validate and sanitize all inputs — treat every request body as untrusted

## Documentation

Every API must have:
- An OpenAPI 3.x spec (machine-readable, kept in sync with code)
- Human-readable documentation for non-obvious flows (auth handshakes, webhooks, async operations)
- A changelog for breaking changes
- Example requests and responses for every endpoint

### API Documentation Tools

**Preferred tool**: [Scalar](https://scalar.com/) — modern, fast, beautiful OpenAPI explorer with curl generation, request history, and dark mode. Supports .NET, Java, Python, Go, Node.js, React, Rust, and more.

**For .NET projects**:
- Use Scalar (not Swagger/NSwag) when scaffolding new APIs
- Install via `Scalar.AspNetCore` NuGet package
- Configure in `Program.cs` with `app.MapScalarUI()`

**Alternative** (if Scalar unavailable): Swashbuckle/NSwag for older projects, but prefer Scalar for new work.

**For other stacks**: Use Scalar if a native integration exists, otherwise the community-standard tool for the framework.
