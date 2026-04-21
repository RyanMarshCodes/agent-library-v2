# Wolverine Framework - LLM-Friendly Guide

## Overview

Wolverine is a .NET message handling and HTTP framework built by the JasperFx team. It provides:
- Message handling with CQRS patterns
- HTTP endpoint generation
- Durable messaging with outbox pattern
- Integration with Marten (PostgreSQL document/event store)

---

## Message Handlers

### Basic Handler

```csharp
// Command/Query as record
public record CreateNomination(string ElderName, string Bio);

// Handler as static class with static method
public static class NominationHandlers
{
    public static async Task Handle(CreateNomination cmd, INominationRepository repo)
    {
        var nomination = new Nomination { ElderName = cmd.ElderName, Bio = cmd.Bio };
        await repo.SaveAsync(nomination);
    }
}
```

### Handler with Response

```csharp
public record GetNominationById(Guid Id);

public static class NominationHandlers
{
    public static Task<Nomination?> Handle(GetNomination query, INominationRepository repo)
    {
        return repo.GetByIdAsync(query.Id);
    }
}
```

### Cascading Messages (Publishing)

```csharp
public record CreateNominationCommand(string ElderName, string Bio);

public static class NominationHandlers
{
    public static IEnumerable<object> Handle(CreateNominationCommand cmd)
    {
        var nomination = new Nomination { ElderName = cmd.ElderName, Bio = cmd.Bio };
        
        // Yield additional messages to publish
        yield return new NominationCreatedEvent(nomination.Id);
    }
}
```

---

## HTTP Endpoints

### Basic Endpoint

```csharp
public static class NominationEndpoints
{
    [WolverineGet("/nominations")]
    public static async Task<IEnumerable<Nomination>> GetAll(INominationRepository repo)
    {
        return await repo.GetAllAsync();
    }

    [WolverineGet("/nominations/{id}")]
    public static async Task<Nomination> GetById(Guid id, INominationRepository repo)
    {
        return await repo.GetByIdAsync(id);
    }

    [WolverinePost("/nominations")]
    public static async Task<Guid> Create(CreateNominationRequest request, INominationRepository repo)
    {
        var nomination = new Nomination { ElderName = request.ElderName, Bio = request.Bio };
        return await repo.SaveAsync(nomination);
    }

    [WolverinePut("/nominations/{id}")]
    public static async Task<Nomination> Update(Guid id, NominationUpdateRequest request, INominationRepository repo)
    {
        var nomination = await repo.GetByIdAsync(id);
        nomination.ElderName = request.ElderName;
        nomination.Bio = request.Bio;
        return await repo.SaveAsync(nomination);
    }

    [WolverineDelete("/nominations/{id}")]
    public static async Task Delete(Guid id, INominationRepository repo)
    {
        await repo.DeleteAsync(id);
    }
}
```

---

## Wolverine + Marten Integration

### Configuration

```csharp
await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services.AddMarten(connectionString)
            .IntegrateWithWolverine();
        
        opts.Policies.AutoApplyTransactions();
        opts.Durability.Mode = DurabilityMode.Solo;
    })
    .StartAsync();
```

### Aggregate Handler (Event Sourcing)

```csharp
// Command
public record ShipOrder(Guid OrderId, int Version);

// Aggregate
public class Order
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public bool IsShipped { get; set; }

    public void Apply(OrderShipped _) => IsShipped = true;
}

// Handler with aggregate workflow
[AggregateHandler]
public static IEnumerable<object> Handle(ShipOrder command, Order order)
{
    order.IsShipped = true;
    yield return new OrderShipped();
}
```

### Read Aggregate

```csharp
// For read-only access to aggregates
public static class OrderQueries
{
    [WolverineGet("/orders/{id}")]
    public static Order? GetOrder([ReadAggregate] Order order) => order;
}
```

### Write Aggregate with Validation

```csharp
public static class OrderCommands
{
    [WolverinePost("/orders/{id}/ship")]
    public static IEnumerable<object> Ship(
        ShipOrder command,
        
        // Returns 404 if not found
        [WriteAggregate(Required = true)] Order order)
    {
        order.IsShipped = true;
        yield return new OrderShipped();
    }
}
```

---

## Outbox Pattern (Durability)

### Automatic Outbox

Wolverine automatically uses the transactional outbox when Marten is integrated:

```csharp
[AggregateHandler]
public static IEnumerable<object> Handle(CreateOrder command, Order order)
{
    yield return new OrderCreatedEvent(order.Id);
    // This message is guaranteed to be sent after the transaction commits
}
```

### Scheduled Messages

```csharp
public record SendReminderEmail(Guid NominationId);

public static class ReminderHandlers
{
    // Schedule for 7 days from now
    public static IAsyncEnumerable<object> Handle(SendReminderEmail email, IDocumentSession session)
    {
        // Schedule message for later
        yield return new ScheduledSendReminder(email.NominationId, DateTimeOffset.UtcNow.AddDays(7));
    }
}
```

---

## Middleware

### Custom Middleware

```csharp
public class LoggingMiddleware
{
    public async Task HandleAsync(MessageContext context, CancellationToken ct)
    {
        Console.WriteLine($"Processing: {context.Envelope.Message.GetType().Name}");
        await next.InvokeAsync(context, ct);
    }
}

// Register
opts.Policies.AddMiddleware<LoggingMiddleware>();
```

---

## Error Handling

### Try-Catch in Handlers

```csharp
public static class NominationHandlers
{
    public static async Task Handle(CreateNomination cmd, INominationRepository repo)
    {
        try
        {
            await repo.SaveAsync(new Nomination { ElderName = cmd.ElderName });
        }
        catch (DbUpdateException ex)
        {
            // Handle specific exceptions
            throw new InvalidOperationException("Failed to create nomination", ex);
        }
    }
}
```

### HTTP Error Responses

```csharp
[WolverinePost("/nominations")]
public static async Task<object> Create(NominationRequest request)
{
    if (string.IsNullOrEmpty(request.ElderName))
    {
        // Return 400 with problem details
        return Results.BadRequest(new { error = "ElderName is required" });
    }
    
    return new Nomination { ElderName = request.ElderName };
}
```

---

## Common Patterns

### Handler Naming Conventions

- Command: `CreateXxxCommand`, `UpdateXxxCommand`, `DeleteXxxCommand`
- Query: `GetXxxByIdQuery`, `GetAllXxxQuery`
- Handler: `XxxHandlers` or `XxxHandler` (class), `Handle` (method)

### Method Injection

```csharp
public static class NominationHandlers
{
    // Inject services as additional parameters
    public static async Task Handle(CreateNomination cmd, 
        INominationRepository repo,
        ILogger<NominationHandlers> logger)
    {
        logger.LogInformation("Creating nomination for {Name}", cmd.ElderName);
        await repo.SaveAsync(new Nomination { ElderName = cmd.ElderName });
    }
}
```

### CancellationToken Support

```csharp
public static async Task Handle(CreateNomination cmd, 
    INominationRepository repo,
    CancellationToken ct)
{
    await repo.SaveAsync(new Nomination { ElderName = cmd.ElderName }, ct);
}
```

---

## Configuration Options

```csharp
.UseWolverine(opts =>
{
    // Middleware
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDefaultForwardingHeaders();
    
    // Endpoints
    opts.Policies.RequireOutboxOnAllCommands();
    
    // Durability
    opts.Durability.Mode = DurabilityMode.Solo; // or DurabilityMode.MultiNode
    opts.Durability.MessageStorageSchemaName = "wolverine";
    
    // Error handling
    opts.Policies.OnException<InvalidOperationException>().RespondWith((ex, env) =>
    {
        return Results.BadRequest(new { error = ex.Message });
    });
})
```

---

## Best Practices

1. **Use records for commands/queries** - Immutable, easy to test
2. **Use static handler classes** - Wolverine generates code from these
3. **Return IEnumerable<object> for cascading messages** - Allows publishing additional messages
4. **Use [AggregateHandler] for event sourcing** - Automatically handles Marten integration
5. **Enable durability mode** - Use `DurabilityMode.Solo` for single-node, `MultiNode` for distributed
6. **Use try-catch in handlers** - Wolverine will retry failed messages by default

---

## Package References

```xml
<PackageReference Include="WolverineFx" Version="3.0.0" />
<PackageReference Include="WolverineFx.Http" Version="3.0.0" />
<PackageReference Include="WolverineFx.Persistence" Version="3.0.0" />
<PackageReference Include="Marten" Version="7.0.0" />
```

---

## Quick Reference

| Pattern | Syntax |
|---------|--------|
| HTTP GET | `[WolverineGet("/path")]` |
| HTTP POST | `[WolverinePost("/path")]` |
| HTTP PUT | `[WolverinePut("/path")]` |
| HTTP DELETE | `[WolverineDelete("/path")]` |
| Handler | `public static Task Handle(Command cmd, IRepo repo)` |
| Response | `return new ResponseType();` |
| Publish | `yield return new AnotherMessage();` |
| Read aggregate | `[ReadAggregate] AggregateType aggregate` |
| Write aggregate | `[WriteAggregate] AggregateType aggregate` |
