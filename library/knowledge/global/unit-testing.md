# Unit Testing Standards

Language-agnostic testing guidelines. Standard patterns (AAA, one assertion per test, don't test privates) are assumed knowledge — only opinionated rules and useful patterns are listed here.

## Coverage

- **>90% for new/changed code** — measured per-PR, not globally
- Prioritize: business logic > error paths > edge cases > data transformations
- Skip coverage for: DTOs, generated code, thin wrappers around external libraries

## Naming Convention

```
[MethodName]_[Scenario]_[ExpectedResult]
```

```csharp
ProcessPayment_WithInsufficientFunds_ThrowsInsufficientFundsException
ParseDate_WithEmptyString_ReturnsNull
```

## Test Data Builders

```csharp
public static class TestData
{
    public static Order CreateValidOrder(
        int id = 1,
        OrderStatus status = OrderStatus.Pending) => new()
    {
        Id = id,
        CustomerId = 100,
        Items = [new OrderItem("Widget", 10.00m, 2)],
        Status = status
    };
}
```

Use optional parameters for flexible setup without builder ceremony.

## Shared Fixtures (xUnit)

```csharp
public class DatabaseFixture : IAsyncLifetime
{
    public TestDbContext Context { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Context = await TestDbContextFactory.CreateAsync();
    }

    public async Task DisposeAsync() => await Context.DisposeAsync();
}

[CollectionDefinition("Database")]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture> { }

[Collection("Database")]
public class OrderRepositoryTests(DatabaseFixture db) { }
```

Use `IAsyncLifetime` over `IDisposable` when setup/teardown is async.

## Determinism Rules

- **No `DateTime.Now`** — inject `TimeProvider` (abstract, .NET 8+) or `IClock`
- **No random without seed** — use `new Random(42)` in tests
- **No network calls** — mock HTTP with `MockHttpMessageHandler` or similar
- **No `Thread.Sleep`/`Task.Delay`** — use `ManualResetEventSlim` or time-travel abstractions

## Test Organization

```
/Tests
  /Unit/Services/OrderServiceTests.cs
  /Integration/Api/OrdersControllerTests.cs
```

Mirror the source project structure. One test class per production class.

## CI Integration

- Run all tests on every PR — fail the build on any failure
- Target < 5 minutes for full unit test suite
- Generate coverage reports; set coverage thresholds as PR gates
- Keep integration tests in a separate project/step (they're slower)

## Key Rules

- **Test behavior, not implementation** — assert on public API outputs, not internal state
- **Don't overmock** — if you're mocking more than 3 dependencies, the class has too many responsibilities
- **Flaky = disabled** — flaky tests erode trust; fix or remove immediately
- **Each test is independent** — no shared mutable state, no test ordering dependencies
