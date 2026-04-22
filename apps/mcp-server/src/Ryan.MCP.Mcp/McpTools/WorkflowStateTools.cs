using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Services.Observability;
using Ryan.MCP.Mcp.Services.WorkflowState;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class WorkflowStateTools(
    IWorkflowStateStore store,
    PlatformMetrics metrics,
    ILogger<WorkflowStateTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "workflow_state_upsert")]
    [Description("Create or update workflow state for resumability and auditability.")]
    public async Task<string> WorkflowStateUpsert(
        [Description("Stable workflow id for the run.")] string workflowId,
        [Description("Workflow command (e.g. '/feature-delivery').")] string command,
        [Description("State status: planned|in_progress|blocked|completed|cancelled")] string status,
        [Description("1-based step index.")] int stepIndex,
        [Description("Optional title for this workflow run.")] string? title = null,
        [Description("Optional current step id.")] string? stepId = null,
        [Description("Optional current step title.")] string? stepTitle = null,
        [Description("Optional context payload.")] string? context = null,
        CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "WorkflowStateTools.WorkflowStateUpsert",
            ["WorkflowId"] = workflowId,
            ["Command"] = command,
            ["Status"] = status,
            ["StepIndex"] = stepIndex
        });

        if (string.IsNullOrWhiteSpace(workflowId)
            || string.IsNullOrWhiteSpace(command)
            || string.IsNullOrWhiteSpace(status))
        {
            return JsonSerializer.Serialize(new
            {
                error = "workflowId, command, and status are required"
            }, JsonOptions);
        }

        if (stepIndex < 1)
        {
            return JsonSerializer.Serialize(new
            {
                error = "stepIndex must be >= 1"
            }, JsonOptions);
        }

        var entry = await store.UpsertAsync(
            new WorkflowStateUpsertRequest(
                workflowId.Trim(),
                command.Trim(),
                title,
                status.Trim(),
                stepIndex,
                stepId,
                stepTitle,
                context),
            ct).ConfigureAwait(false);
        metrics.RecordWorkflowStateUpsert();

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            entry
        }, JsonOptions);
    }

    [McpServerTool(Name = "workflow_state_get")]
    [Description("Fetch workflow state by workflow id.")]
    public async Task<string> WorkflowStateGet(
        [Description("Workflow id.")] string workflowId,
        CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "WorkflowStateTools.WorkflowStateGet",
            ["WorkflowId"] = workflowId
        });

        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return JsonSerializer.Serialize(new
            {
                error = "workflowId is required"
            }, JsonOptions);
        }

        var entry = await store.GetAsync(workflowId.Trim(), ct).ConfigureAwait(false);
        metrics.RecordWorkflowStateRead();
        if (entry is null)
        {
            return JsonSerializer.Serialize(new
            {
                status = "not_found",
                workflowId
            }, JsonOptions);
        }

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            entry
        }, JsonOptions);
    }

    [McpServerTool(Name = "workflow_state_list")]
    [Description("List workflow states, optionally filtered by status and command.")]
    public async Task<string> WorkflowStateList(
        [Description("Optional status filter.")] string? status = null,
        [Description("Optional command filter.")] string? command = null,
        [Description("Maximum items to return (1-200, default 50).")] int limit = 50,
        CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "WorkflowStateTools.WorkflowStateList",
            ["Status"] = status,
            ["Command"] = command,
            ["Limit"] = limit
        });

        var items = await store.ListAsync(status, command, limit, ct).ConfigureAwait(false);
        metrics.RecordWorkflowStateRead();
        return JsonSerializer.Serialize(new
        {
            count = items.Count,
            states = items
        }, JsonOptions);
    }
}
