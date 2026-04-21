# EF Core Patterns

Naming conventions, dependency injection rules, and middleware patterns for Entity Framework Core.

## Naming Conventions

| Pattern | Example | Notes |
|---------|---------|-------|
| Services | `OrderService` | One class per aggregate root, no utility services |
| Commands | `CreateOrderCommand` | Verb first, entity second |
| Queries | `GetOrderByIdQuery` | Same pattern as commands |
| Repository interfaces | `IOrderRepository` | Interface + implementation pair |
| Repository implementations | `OrderRepository` | Implements corresponding interface |
| DbContext | `[ProjectName]DbContext` | One per bounded context |

## EF Core and DI Rules

### DbContext Lifetime

- **Lifetime is Scoped** — never Singleton, never Transient
- Lifetime is declared in `Program.cs`, invisible when reading a class file
- Inject `DbContext` via constructor injection; don't use `IDbContextFactory<T>` unless explicitly needed for multi-tenant or unit testing

### Repository Pattern

- **Never inject DbContext directly into controllers** — always go through a service
- **Never introduce generic repository interfaces over DbContext** — EF Core change tracking is tied to a specific instance; abstracting DbContext breaks the tracking context across boundaries
- Use the unit of work pattern only when multiple repositories need transactional consistency

### Change Tracking

- Use `AsNoTracking()` for read-only queries (projections, DTOs)
- Attach/detach entities explicitly when crossing service boundaries
- Never return tracked entities to API layers — map to DTOs/records first

### Migrations

- One migration per logical change
- Name migrations descriptively: `AddOrderStatusToOrder`, not `UpdateTable1`
- Keep migration files in source control; don't squash unless necessary
- Use `dotnet ef migrations script` to generate idempotent SQL for production

## Middleware and Pipeline

### Order Matters

- **UseAuthentication must precede UseAuthorization** — always, no exceptions
- Middleware order is execution order, not configuration grouping
- Never reorganise or reorder registrations in `Program.cs` for style reasons — what looks like formatting is actually pipeline sequence

### Common Middleware Order

```csharp
app.UseRouting();
app.UseAuthentication();   // Before authorization
app.UseAuthorization();    // After authentication
app.UseCors();             // Before other middleware
app.UseResponseCaching();  // After authorization
app.MapControllers();      // Endpoint routing at the end
```

### Custom Middleware Placement

- Exception handling (`UseExceptionHandler`) first
- Security headers (`UseHsts`, `UseHttpsRedirection`) early
- Performance logging last

## Query Performance

- Use projections (`Select`) to fetch only needed columns
- Avoid `Include()` unless relationships are immediately needed — use explicit loading or separate queries
- Profile generated SQL with `ToQueryString()` during development
- Use indexes on foreign key columns and frequently queried columns
- Enable sensitive data logging only in development

## Concurrency

- Use optimistic concurrency with `[ConcurrencyCheck]` or row versioning
- Handle `DbUpdateConcurrencyException` with user-friendly retry logic
- Never rely on last-write-wins for financial or inventory data