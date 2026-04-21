# C# Naming Conventions

Follow [Microsoft Framework Design Guidelines](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/naming-guidelines). Standard PascalCase/camelCase rules apply — only non-obvious conventions are listed here.

## Field Prefixes

```csharp
private int _orderCount;                          // _ prefix for private instance fields
private static HttpClient s_httpClient;           // s_ prefix for private static fields
private static readonly Settings s_defaults;      // s_ for static readonly too
private const int MaxRetries = 3;                 // PascalCase for constants (no prefix)
```

## Enums

- Non-flags: singular nouns (`OrderStatus`)
- Flags: plural nouns (`[Flags] enum FileModes`)
- Never prefix values with the type name (`Active`, not `OrderStatusActive`)

## Type Parameters

Use descriptive `T`-prefixed names when multiple; single `T` only when self-explanatory:

```csharp
public class Dictionary<TKey, TValue> { }
public interface IRepository<T> where T : class { }
```

## Namespace & Assembly

```
Contoso.Commerce.OrderProcessing   // reverse domain, functional grouping
Contoso.Commerce.dll               // assembly matches primary namespace
```

## Key Avoidances

- No Hungarian notation (`strName`, `iCount`)
- No abbreviations unless universally understood (`Id`, `Url`, `Http` are fine)
- No language keywords as names (use `@class` only if unavoidable)
- Prefer `CanScrollHorizontally` over `ScrollableX` — clarity over brevity
