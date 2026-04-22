namespace Ryan.MCP.Mcp.Services.WorkflowState;

public interface IWorkflowStateStore
{
    Task<WorkflowStateEntry> UpsertAsync(WorkflowStateUpsertRequest request, CancellationToken ct = default);
    Task<WorkflowStateEntry?> GetAsync(string workflowId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowStateEntry>> ListAsync(
        string? status = null,
        string? command = null,
        int limit = 50,
        CancellationToken ct = default);
}
