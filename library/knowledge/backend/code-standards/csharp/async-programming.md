# C# Async Programming

Standard async/await mechanics (Task, async void is bad, use await not .Result) are assumed knowledge. This covers non-obvious patterns and project conventions.

## ConfigureAwait

```csharp
// Library code: always ConfigureAwait(false)
public async Task<Order> GetOrderAsync(int id)
{
    return await _repository.FindAsync(id).ConfigureAwait(false);
}

// Application code (ASP.NET Core): omit — no SynchronizationContext to capture
```

Rule: `ConfigureAwait(false)` in reusable libraries. Omit in ASP.NET Core application code.

## Cancellation

```csharp
public async Task<Order> GetOrderAsync(int id, CancellationToken ct = default)
{
    ct.ThrowIfCancellationRequested();
    return await _repository.FindAsync(id, ct);
}
```

### Linked Tokens for Timeouts

```csharp
using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
cts.CancelAfter(TimeSpan.FromSeconds(30));
await ProcessAsync(cts.Token);
```

Always accept `CancellationToken` as the last parameter with `default` value. Pass it through to every async call.

## ValueTask for Hot Paths

```csharp
public ValueTask<Order?> GetOrderAsync(int id)
{
    if (_cache.TryGet(id, out var order))
        return ValueTask.FromResult<Order?>(order);

    return new ValueTask<Order?>(LoadFromDbAsync(id));
}
```

Use `ValueTask<T>` only when the method frequently completes synchronously (cache hits, short-circuits). Default to `Task<T>` everywhere else.

## IAsyncEnumerable for Streaming

```csharp
public async IAsyncEnumerable<Product> GetProductsAsync(
    [EnumeratorCancellation] CancellationToken ct = default)
{
    await foreach (var product in _context.Products.AsAsyncEnumerable().WithCancellation(ct))
        yield return product;
}
```

## Parallel Async

```csharp
// Independent I/O tasks — fire all, await together
var (orders, customers) = (GetOrdersAsync(ct), GetCustomersAsync(ct));
await Task.WhenAll(orders, customers);

// Bounded parallelism for large collections
await Parallel.ForEachAsync(items,
    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
    async (item, token) => await ProcessAsync(item, token));
```

## Key Rules

- **Async all the way** — never `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` (deadlock risk)
- **Never fire-and-forget** — `_ = Task.Run(...)` loses exceptions; use a background service or `IHostedService`
- **Suffix with `Async`** — `GetOrderAsync`, not `GetOrder` for async methods
- **Don't `await` then immediately `return`** — just `return _repo.GetAsync(id)` (skip the state machine)
- **Catch cancellation separately** — `catch (Exception ex) when (ex is not OperationCanceledException)`
