using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Services.Policy;

public sealed class PolicyPreflightService(McpOptions options)
{
    public PolicyDecision Evaluate(string operation, string? requestedCategory, string? approvalToken)
    {
        var category = ResolveCategory(operation, requestedCategory);
        var requiresApproval = category switch
        {
            ToolCategory.Mutate => options.Policy.RequireApprovalTokenForMutate,
            ToolCategory.Execute => options.Policy.RequireApprovalTokenForExecute,
            _ => false
        };

        var approvalSatisfied = !requiresApproval || IsApprovalTokenValid(approvalToken);
        var allowed = !requiresApproval || approvalSatisfied;

        return new PolicyDecision(
            operation,
            category.ToString().ToLowerInvariant(),
            allowed,
            requiresApproval,
            approvalSatisfied,
            allowed
                ? (requiresApproval ? "Approved with valid token." : "Read operation auto-allowed.")
                : "Approval token required for this operation category.",
            BuildHints(operation, category, requiresApproval, approvalSatisfied),
            DateTime.UtcNow);
    }

    private bool IsApprovalTokenValid(string? providedToken)
    {
        if (string.IsNullOrWhiteSpace(providedToken))
        {
            return false;
        }

        var tokens = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Policy.ApprovalToken))
        {
            tokens.Add(options.Policy.ApprovalToken.Trim());
        }

        tokens.AddRange(
            options.Policy.ApprovalTokens
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim()));

        if (tokens.Count == 0)
        {
            return false;
        }

        return tokens.Contains(providedToken.Trim(), StringComparer.Ordinal);
    }

    private ToolCategory ResolveCategory(string operation, string? requestedCategory)
    {
        if (Enum.TryParse<ToolCategory>(requestedCategory, true, out var parsed))
        {
            return parsed;
        }

        if (StartsWithAny(operation, options.Policy.ReadToolPrefixes))
        {
            return ToolCategory.Read;
        }

        if (StartsWithAny(operation, options.Policy.MutateToolPrefixes))
        {
            return ToolCategory.Mutate;
        }

        if (StartsWithAny(operation, options.Policy.ExecuteToolPrefixes))
        {
            return ToolCategory.Execute;
        }

        return ToolCategory.Execute;
    }

    private static bool StartsWithAny(string value, IEnumerable<string> prefixes) =>
        prefixes.Any(prefix => !string.IsNullOrWhiteSpace(prefix)
                               && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> BuildHints(
        string operation,
        ToolCategory category,
        bool requiresApproval,
        bool approvalSatisfied)
    {
        if (!requiresApproval || approvalSatisfied)
        {
            return [];
        }

        return
        [
            "Pass approvalToken for mutate/execute operations.",
            "Call policy_preflight before mutating or side-effecting tools.",
            $"Operation '{operation}' resolved to '{category.ToString().ToLowerInvariant()}'."
        ];
    }
}

public enum ToolCategory
{
    Read,
    Mutate,
    Execute
}

public sealed record PolicyDecision(
    string Operation,
    string Category,
    bool Allowed,
    bool RequiresApproval,
    bool ApprovalSatisfied,
    string Reason,
    IReadOnlyList<string> Hints,
    DateTime EvaluatedUtc);
