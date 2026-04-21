# C# Logging Standards

Standard log levels and `ILogger` usage are assumed knowledge. This covers project-specific patterns and non-obvious rules.

## Message Templates (Not Interpolation)

```csharp
// Structured — properties are indexed and searchable
_logger.LogInformation("Processing order {OrderId} for customer {CustomerId}",
    order.Id, order.CustomerId);

// WRONG — string interpolation defeats structured logging
_logger.LogInformation($"Processing order {order.Id}");
```

Use **PascalCase** for template placeholders: `{OrderId}` not `{orderId}`.

## What NOT to Log

- Passwords, tokens, API keys, connection strings
- Credit card numbers, SSNs, PHI
- Full request/response bodies containing PII
- Individual items in hot-path loops

```csharp
// Bad — logging per iteration in a hot path
foreach (var item in thousandsOfItems)
    _logger.LogDebug("Processing {ItemId}", item.Id);

// Good — summary after completion, guarded
if (_logger.IsEnabled(LogLevel.Debug))
    _logger.LogDebug("Processed {Count} items", items.Count);
```

## Scoped Logging with Correlation

```csharp
using (_logger.BeginScope("OrderId={OrderId}", order.Id))
{
    // All log entries within this block include OrderId
    _logger.LogInformation("Validating");
    _logger.LogInformation("Persisting");
}
```

## Performance: Timing Long Operations

```csharp
var sw = Stopwatch.StartNew();
await ProcessLargeFileAsync(filePath);
_logger.LogInformation("Processed {FileName} in {ElapsedMs}ms",
    fileName, sw.ElapsedMilliseconds);
```

## Service Collection Extension for Serilog

```csharp
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "OrderService"));
```

## Key Rules

- **Always pass exceptions as first arg** — `LogError(ex, "msg {Id}", id)` not `LogError("msg: " + ex.Message)`
- **Don't log and rethrow differently** — log once, `throw;` (not `throw ex;`)
- **Use `ILogger<T>`** via constructor injection — never static loggers in production code
- **Log security events at Warning+** — failed logins, permission denials, unusual patterns
