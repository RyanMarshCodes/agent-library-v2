# C# Error Handling

## Guard Clauses — Fail Fast

```csharp
public void ProcessOrder(Order order, int priority)
{
    ArgumentNullException.ThrowIfNull(order);
    ArgumentOutOfRangeException.ThrowIfLessThan(priority, 0);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, 10);
    // Main logic after all guards
}
```

Use `ThrowIfNull`, `ThrowIfNullOrEmpty`, `ThrowIfZero` (C# 13+) — prefer static throw helpers over manual `if/throw`.

## Exception Filters

```csharp
catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
{
    _logger.LogWarning(ex, "Transient failure");
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    _logger.LogError(ex, "Unexpected error");
    throw;
}
```

## Custom Exception Template

```csharp
public class OrderProcessingException : Exception
{
    public int OrderId { get; }

    public OrderProcessingException(string message, int orderId)
        : base(message) => OrderId = orderId;

    public OrderProcessingException(string message, int orderId, Exception inner)
        : base(message, inner) => OrderId = orderId;
}
```

Keep custom exceptions minimal — skip `[Serializable]` and `SerializationInfo` unless targeting legacy .NET Framework.

## Async Exception Handling

- Never fire-and-forget (`_ = Task.Run(...)`) — exceptions are lost
- Use `catch (Exception ex) when (ex is not OperationCanceledException)` to avoid catching cancellation
- See `async-programming.md` for `ConfigureAwait` and cancellation patterns

## Key Rules

- **`throw;` not `throw ex;`** — preserves stack trace
- **Never swallow** — `catch (Exception) { }` is a bug
- **Don't use exceptions for control flow** — use `TryGetValue`, `TryParse`, pattern matching
- **Include context when logging** — `"Failed to process order {OrderId}"`, not `"Error occurred"`
- **Catch specific types** — catch `DbUpdateException`, not `Exception`, unless rethrowing
- **Don't expose internals** — generic user messages, detailed internal logs
