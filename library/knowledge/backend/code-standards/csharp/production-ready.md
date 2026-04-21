# Production-Ready .NET

> Building self-healing, resilient .NET applications that detect, isolate, and recover from faults automatically.

## Guiding Principles

1. **Observability** — Gather signals (metrics, logs, traces, health checks) to detect anomalies early.
2. **Fault isolation** — Contain failures to prevent cascade effects.
3. **Automated recovery** — Prefer automated, deterministic remedies over manual intervention.
4. **Graceful degradation** — Maintain partial functionality rather than total failure.
5. **Safe defaults** — Ensure operations can be retried without harmful side effects.

## Health Checks

Every web API must expose health check endpoints.

### Required Endpoints

```csharp
// Liveness — is the process running?
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));

// Readiness — is the service ready to accept traffic?
app.MapGet("/health/ready", async (
    IDatabaseCheck db,
    IExternalServiceClient svc,
    CancellationToken ct) =>
{
    var checks = new Dictionary<string, object>();
    var ready = true;

    var (dbOk, dbMsg) = await db.CheckAsync(ct);
    checks["database"] = new { ok = dbOk, message = dbMsg };
    ready &= dbOk;

    var (svcOk, svcMsg) = await svc.CheckAsync(ct);
    checks["external-service"] = new { ok = svcOk, message = svcMsg };
    ready &= svcOk;

    return Results.Ok(new { status = ready ? "ready" : "not_ready", checks });
});
```

### Health Check Packages

```xml
<PackageReference Include="AspNetCore.HealthChecks.Uris" />
<PackageReference Include="AspNetCore.HealthChecks.SqlServer" />
<PackageReference Include="AspNetCore.HealthChecks.Redis" />
<PackageReference Include="AspNetCore.HealthChecks.Http" />
```

### Composite Health Checks

```csharp
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>("database")
    .AddUrlGroup("https://api.external.com/health", name: "external-api")
    .AddRedis("localhost:6379", name: "cache");
```

## Resilience Patterns

Use `Microsoft.Extensions.Http.Resilience` for outbound HTTP calls.

### Required: HttpClientFactory

Never use `new HttpClient()`. Always use `HttpClientFactory`:

```csharp
builder.Services.AddHttpClient<IExternalServiceClient, ExternalServiceClient>();
```

### Retry with Exponential Backoff

```csharp
builder.Services.AddHttpClient<IExternalServiceClient, ExternalServiceClient>()
    .AddResilienceHandler("retry", strategy =>
    {
        strategy.Retry.MaxRetryAttempts = 3;
        strategy.Retry.BackoffType = DelayBackoffType.Exponential;
        strategy.Retry.UseJitter = true;
    });
```

### Circuit Breaker

```csharp
builder.Services.AddHttpClient<IExternalServiceClient, ExternalServiceClient>()
    .AddResilienceHandler("circuit-breaker", strategy =>
    {
        strategy.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        strategy.CircuitBreaker.FailureRatio = 0.5;
        strategy.CircuitBreaker.MinimumThroughput = 5;
        strategy.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
    });
```

### Combined Policy

```csharp
builder.Services.AddHttpClient<IExternalServiceClient, ExternalServiceClient>()
    .AddResilienceHandler("combined", pipeline =>
    {
        pipeline.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>()
        });
        pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>()
        });
        pipeline.AddTimeout(TimeSpan.FromSeconds(10));
    });
```

## Bulkheading

Isolate resources to prevent cascade failures.

### Separate HttpClient Instances

```csharp
// Critical service — dedicated pool
builder.Services.AddHttpClient<ICriticalServiceClient, CriticalServiceClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    MaxConnectionsPerServer = 10
});

// Non-critical service — smaller pool
builder.Services.AddHttpClient<INonCriticalServiceClient, NonCriticalServiceClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    MaxConnectionsPerServer = 5
});
```

### Thread Pool Isolation

For CPU-bound or blocking operations, consider dedicated thread pools:

```csharp
builder.Services.AddSingleton<ThreadPoolService>(sp =>
{
    return new ThreadPoolService(minThreads: 4, maxThreads: 20);
});
```

## Graceful Degradation

Maintain partial functionality when dependencies fail.

### Example: Cache-First Pattern

```csharp
public async Task<GetUserResponse> GetUserAsync(int id, CancellationToken ct)
{
    // Try cache first
    var cached = await _cache.GetAsync($"user:{id}", ct);
    if (cached != null)
        return cached;

    try
    {
        // Try database
        var user = await _db.Users.FindAsync(id, ct);
        if (user != null)
        {
            await _cache.SetAsync($"user:{id}", user, ct);
            return user;
        }
        return null;
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to fetch user from database, returning null");
        return null; // Degrade gracefully
    }
}
```

### Feature Flags for Degradation

```csharp
public async Task<GetUserResponse> GetUserAsync(int id, CancellationToken ct)
{
    // If feature flag is off, skip external enrichment
    if (!_featureFlags.IsEnabled("user-enrichment"))
        return await _db.Users.FindAsync(id, ct);

    // Enrichment enabled — try external service with fallback
    var user = await _db.Users.FindAsync(id, ct);
    if (user == null) return null;

    try
    {
        var enrichment = await _enrichmentClient.GetAsync(user.Email, ct);
        user.Enrichment = enrichment;
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Enrichment service unavailable, returning basic data");
        // Continue without enrichment — graceful degradation
    }

    return user;
}
```

## Observability

### Structured Logging

```csharp
_logger.LogInformation(
    "Request completed. {Method} {Path} responded {StatusCode} in {ElapsedMs}ms",
    context.Request.Method,
    context.Request.Path,
    response.StatusCode,
    stopwatch.ElapsedMilliseconds);
```

### Metrics with OpenTelemetry

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.ServiceName("my-api"))
    .WithMetrics(metrics =>
    {
        metrics.AddHttpClientInstrumentation();
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddMeter("MyApp");
    });
```

### Distributed Tracing

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddHttpClientInstrumentation();
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddSource("MyApp");
    });
```

## Automated Remediation

### K8s Probes (if deployed to Kubernetes)

```yaml
livenessProbe:
  httpGet:
    path: /health/live
    port: 8080
  initialDelaySeconds: 10
  periodSeconds: 10

readinessProbe:
  httpGet:
    path: /health/ready
    port: 8080
  initialDelaySeconds: 5
  periodSeconds: 5
```

### Graceful Shutdown

```csharp
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

lifetime.ApplicationStopping.Register(() =>
{
    _logger.LogInformation("Shutting down, stopping new requests");
    // Drain active requests before exiting
});
```

## Configuration Checklist

| Component | Required | Package |
|-----------|----------|---------|
| Health checks | Yes | `AspNetCore.HealthChecks.*` |
| HttpClientFactory | Yes | Built-in |
| Retry policy | Yes | `Microsoft.Extensions.Http.Resilience` |
| Circuit breaker | Recommended | `Microsoft.Extensions.Http.Resilience` |
| Structured logging | Yes | Built-in |
| OpenTelemetry | Recommended | `OpenTelemetry.*` |

## Minimal Recipe

Every .NET Web API project should include:

1. Health check endpoints (`/health/live`, `/health/ready`)
2. `HttpClientFactory` for all outbound HTTP calls
3. Retry with exponential backoff on HTTP calls
4. Circuit breaker for external services
5. Structured logging with correlation IDs

```csharp
builder.Services.AddHttpClient<IExternalClient, ExternalClient>()
    .AddResilienceHandler("default", strategy =>
    {
        strategy.Retry.MaxRetryAttempts = 3;
        strategy.Retry.BackoffType = DelayBackoffType.Exponential;
        strategy.Retry.UseJitter = true;
    })
    .AddResilienceHandler("circuit", strategy =>
    {
        strategy.CircuitBreaker.FailureRatio = 0.5;
        strategy.CircuitBreaker.MinimumThroughput = 5;
        strategy.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
    });
```

## See Also

- [Error Handling](./error-handling.md)
- [Logging](./logging.md)
- [ASP.NET Core Health Checks](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks)
- [Microsoft.Extensions.Http.Resilience](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience)
- [OpenTelemetry](https://opentelemetry.io/docs/languages/net/)
