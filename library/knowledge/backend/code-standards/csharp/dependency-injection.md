# C# Dependency Injection

Standard DI concepts (constructor injection, transient/scoped/singleton) are assumed knowledge. This covers patterns, anti-patterns, and project-specific conventions.

## Primary Constructor DI (C# 12+)

```csharp
public class OrderService(
    IOrderRepository repository,
    IEmailService emailService,
    ILogger<OrderService> logger)
{
    public async Task ProcessAsync(Order order)
    {
        await repository.SaveAsync(order);
        logger.LogInformation("Order {OrderId} processed", order.Id);
    }
}
```

Primary constructors eliminate field boilerplate. Parameters are captured as-is — no `_` prefix fields needed.

## Lifetime Gotcha: Singleton + Scoped

```csharp
// BUG: Scoped service captured by singleton = stale/shared state
public class SingletonService(IScopedService scoped) { }

// FIX: Use IServiceScopeFactory
public class SingletonService(IServiceScopeFactory scopeFactory)
{
    public async Task DoWorkAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var scoped = scope.ServiceProvider.GetRequiredService<IScopedService>();
    }
}
```

This is the most common DI bug in ASP.NET Core. The runtime won't always catch it.

## Factory Pattern for Runtime Decisions

```csharp
services.AddScoped<ITenantService>(sp =>
{
    var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
    var tenantId = httpContext?.Request.Headers["X-TenantId"].FirstOrDefault();
    return new TenantService(tenantId);
});
```

## Service Collection Extensions

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        return services;
    }

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlServer(config.GetConnectionString("Default")));
        return services;
    }
}

// Program.cs
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructure(builder.Configuration);
```

Group registrations by layer (Application, Infrastructure, Presentation).

## Key Rules

- **Constructor injection only** — never `IServiceProvider.GetService<T>()` in business logic
- **Interface segregation** — `IReadableRepo<T>` + `IWritableRepo<T>` over `IRepository<T>`
- **Max 4-5 constructor params** — more suggests the class has too many responsibilities
- **`IHttpContextAccessor` is scoped** — never inject directly into singletons
- **Keyed services (.NET 8+)** — use `[FromKeyedServices("name")]` for named registrations instead of factory hacks
