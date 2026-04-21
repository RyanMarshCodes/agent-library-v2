# C# Security

Project-specific security patterns. Standard OWASP guidance (parameterized queries, output encoding, CSRF tokens) is assumed knowledge — only non-obvious or opinionated patterns are listed here.

## Secrets Management

```csharp
// Development: User Secrets
dotnet user-secrets set "ConnectionStrings:Default" "Server=..."

// Production: Azure Key Vault
builder.Configuration.AddAzureKeyVault(
    new Uri(Environment.GetEnvironmentVariable("KEY_VAULT_URL")!),
    new DefaultAzureCredential());
```

Never use `appsettings.json` for connection strings or API keys — even in development.

## Input Validation — FluentValidation

```csharp
public class OrderValidator : AbstractValidator<OrderDto>
{
    public OrderValidator()
    {
        RuleFor(x => x.CustomerName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Email).EmailAddress();
        RuleFor(x => x.Quantity).InclusiveBetween(1, 1000);
    }
}
```

Validate at the API boundary only (controllers/endpoints). Domain logic should receive already-validated data.

## Content Security Policy

```csharp
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    await next();
});
```

## Sensitive Data Disposal

```csharp
byte[] key = new byte[32];
try
{
    // Use key
}
finally
{
    CryptographicOperations.ZeroMemory(key);  // .NET 8+
}
```

Prefer `CryptographicOperations.ZeroMemory` over `Array.Clear` — it's not optimized away by the JIT.

## Information Disclosure

```csharp
// API: generic message to caller, detailed log internally
catch (DbUpdateException ex)
{
    _logger.LogError(ex, "Database error for order {OrderId}", order.Id);
    return Problem("An error occurred processing your request");
}
```

Never include connection strings, stack traces, or internal identifiers in API responses.

## Dependency Scanning

```bash
dotnet list package --vulnerable        # Known CVEs
dotnet list package --outdated          # Outdated packages
```

Add `--vulnerable` check to CI pipeline. See `static-analysis.md` for `Directory.Build.props` analyzer setup.

## Key Rules

- **Parameterized queries always** — EF Core does this automatically; raw SQL must use `@parameters`
- **Authorize by default** — use `[Authorize]` globally, `[AllowAnonymous]` explicitly
- **Policy-based auth over role checks** — `[Authorize(Policy = "CanDeleteOrders")]`
- **Hash passwords with BCrypt/Argon2** — never SHA256/MD5 for passwords
- **No secrets in logs** — structured logging templates help prevent accidental inclusion
