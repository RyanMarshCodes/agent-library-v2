# C# Class and Type Design

Standard OOP concepts (SRP, encapsulation, composition over inheritance) are assumed knowledge. This covers opinionated patterns and non-obvious C# type design.

## Interface Segregation

```csharp
public interface IReadableRepository<T>
{
    Task<T?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default);
}

public interface IWritableRepository<T>
{
    Task SaveAsync(T entity, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}
```

Consumers depend only on what they need. Read-only services take `IReadableRepository<T>`.

## Records for Data Carriers

```csharp
// Immutable DTO — value equality, with-expressions, deconstruction free
public record OrderSummary(int Id, string CustomerName, decimal Total)
{
    public string Display => $"{CustomerName}: {Total:C}";
}

// Mutable variant when needed
public record class OrderDraft
{
    public required string CustomerName { get; set; }
    public List<OrderItem> Items { get; init; } = [];
}
```

Use `record` for DTOs, events, value objects. Use `record struct` for small value types that benefit from stack allocation.

## Struct Design

```csharp
public readonly record struct Money(decimal Amount, string Currency)
{
    public Money Add(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException("Currency mismatch");
        return this with { Amount = Amount + other.Amount };
    }
}
```

Use `readonly record struct` for small (<= 2 fields), immutable value types. Prefer `record struct` over plain `struct` for free equality/ToString.

## Flags Enum Pattern

```csharp
[Flags]
public enum FilePermissions
{
    None    = 0,
    Read    = 1 << 0,
    Write   = 1 << 1,
    Execute = 1 << 2,
    All     = Read | Write | Execute
}
```

Always include `None = 0`. Use bit-shift notation for clarity.

## Sealed by Default

```csharp
public sealed class OrderProcessor { }
```

Seal classes unless designed for inheritance. Enables JIT devirtualization (measurable perf gain in hot paths) and communicates intent.

## Extension Methods for Cross-Cutting

```csharp
public static class StringExtensions
{
    public static string Truncate(this string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "...");
}
```

Place in a `Extensions/` folder. Namespace should match the type being extended for discoverability.

## Key Rules

- **`required` keyword (C# 11+)** — use on properties that must be set at construction
- **`init` over `set`** — prefer immutability; use `set` only when mutation is genuinely needed
- **Max 4-5 constructor params** — more means the class is doing too much
- **No public fields** — use properties (even for `const` alternatives, prefer `static readonly` for non-primitive types)
- **Explicit interface implementation** — use when a class implements multiple interfaces with conflicting members
