using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Services.Policy;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class PolicyTools(
    PolicyPreflightService preflight,
    ILogger<PolicyTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "policy_preflight")]
    [Description("Resolve read/mutate/execute policy category and check whether approval is required/satisfied.")]
    public string PolicyPreflight(
        [Description("Operation/tool name to evaluate.")] string operation,
        [Description("Optional explicit category override: read|mutate|execute.")] string? category = null,
        [Description("Approval token (required for mutate/execute when policy requires it).")] string? approvalToken = null)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "PolicyTools.PolicyPreflight",
            ["Operation"] = operation,
            ["Category"] = category
        });

        if (string.IsNullOrWhiteSpace(operation))
        {
            return JsonSerializer.Serialize(new
            {
                error = "operation is required",
                hint = "Pass the planned tool/action name to evaluate policy."
            }, JsonOptions);
        }

        var decision = preflight.Evaluate(operation.Trim(), category, approvalToken);
        return JsonSerializer.Serialize(new
        {
            operation = decision.Operation,
            category = decision.Category,
            allowed = decision.Allowed,
            requiresApproval = decision.RequiresApproval,
            approvalSatisfied = decision.ApprovalSatisfied,
            reason = decision.Reason,
            hints = decision.Hints,
            evaluatedUtc = decision.EvaluatedUtc
        }, JsonOptions);
    }
}
