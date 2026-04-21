# C# Code Style

Based on Microsoft [Coding Conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions).

> **Formatting is enforced by `.editorconfig`** — see `static-analysis.md` for the template. Do not duplicate formatting rules in code reviews; the tooling handles it.

---

## Language Feature Preferences

| Feature | Preference | Notes |
|---------|-----------|-------|
| `var` | Use when type is apparent from RHS | Explicit type for complex generics or ambiguous returns |
| Expression-bodied members | Accessors, indexers, lambdas | Avoid for constructors and destructors |
| LINQ syntax | Method syntax for simple chains | Query syntax only when joins improve readability |
| String building | Interpolation for simple; `StringBuilder` for loops | Raw string literals (`"""`) for multiline |
| Collection init | C# 12 collection expressions (`[1, 2, 3]`) | Fall back to traditional for older targets |
| Delegates | `Func<>` / `Action<>` over named delegates | |
| File-scoped namespaces | Always (C# 10+) | |
| `using` placement | Outside namespace, `System.*` first | Global usings for frequently used namespaces |
| `this.` qualification | Never — use `_` prefix for fields | |

---

## Program.cs / Hosting Configuration

Keep `Program.cs` minimal — infrastructure wiring only, no business logic.

### Pattern: Extension Methods per Layer

```csharp
// Program.cs - ~15 lines max
var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddAzureKeyVault(builder.Environment);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApi(builder.Environment);

var app = builder.Build();

app.UseApi();
app.UseSwagger();
app.UseCors();

app.Run();
```

Each layer registers its own services via a `DependencyInjection` static class:

```csharp
// Application/DependencyInjection.cs
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        return services;
    }
}
```

### Rules

| Rule | Rationale |
|------|-----------|
| One `Add<Layer>()` call per project | Clear ownership |
| One `Use<Feature>()` call per feature | Easy to scan |
| No inline configuration in Program.cs | Use `IOptions` pattern |
| No business logic in Program.cs | Infrastructure wiring only |
| Group related registrations together | Easier to find and modify |
