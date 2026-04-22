using System.Collections.Concurrent;
using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Services.ModelMapping;

public sealed class RoutingBudgetService(McpOptions options)
{
    private readonly ConcurrentDictionary<string, decimal> _spentByWorkflow = new(StringComparer.OrdinalIgnoreCase);

    public RoutingBudgetDecision Evaluate(
        string workflowKey,
        decimal projectedCostUsd)
    {
        var normalizedWorkflow = string.IsNullOrWhiteSpace(workflowKey) ? "default" : workflowKey.Trim();
        var budget = ResolveBudget(normalizedWorkflow);
        var spent = _spentByWorkflow.GetOrAdd(normalizedWorkflow, 0m);
        var remaining = Math.Max(0m, budget - spent);
        var projectedRemaining = remaining - projectedCostUsd;
        var allowed = !options.RoutingBudget.EnforcePerWorkflowBudget || projectedCostUsd <= remaining;

        return new RoutingBudgetDecision(
            normalizedWorkflow,
            budget,
            spent,
            remaining,
            projectedCostUsd,
            Math.Max(0m, projectedRemaining),
            allowed,
            allowed
                ? "Projected model cost is within workflow budget."
                : "Projected model cost exceeds remaining workflow budget.");
    }

    public void RecordSpend(string workflowKey, decimal amountUsd)
    {
        if (amountUsd <= 0m)
        {
            return;
        }

        var normalizedWorkflow = string.IsNullOrWhiteSpace(workflowKey) ? "default" : workflowKey.Trim();
        _spentByWorkflow.AddOrUpdate(
            normalizedWorkflow,
            amountUsd,
            (_, current) => current + amountUsd);
    }

    private decimal ResolveBudget(string workflowKey)
    {
        if (options.RoutingBudget.WorkflowBudgetUsd.TryGetValue(workflowKey, out var configured))
        {
            return configured;
        }

        return options.RoutingBudget.DefaultWorkflowBudgetUsd;
    }
}

public sealed record RoutingBudgetDecision(
    string WorkflowKey,
    decimal BudgetUsd,
    decimal SpentUsd,
    decimal RemainingUsd,
    decimal ProjectedCostUsd,
    decimal ProjectedRemainingUsd,
    bool Allowed,
    string Reason);
