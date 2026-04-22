using System.Collections.Concurrent;

namespace Ryan.MCP.Mcp.Services.WorkflowState;

public sealed class InMemoryWorkflowStateStore : IWorkflowStateStore
{
    private readonly ConcurrentDictionary<string, WorkflowStateEntry> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<WorkflowStateEntry> UpsertAsync(WorkflowStateUpsertRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        var normalizedId = request.WorkflowId.Trim();
        var normalizedCommand = request.Command.Trim();
        var normalizedStatus = request.Status.Trim();
        var stepId = string.IsNullOrWhiteSpace(request.StepId) ? "step-unknown" : request.StepId.Trim();
        var stepTitle = string.IsNullOrWhiteSpace(request.StepTitle) ? "Unknown step" : request.StepTitle.Trim();

        var updated = _states.AddOrUpdate(
            normalizedId,
            _ => new WorkflowStateEntry(
                normalizedId,
                normalizedCommand,
                request.Title?.Trim() ?? normalizedCommand,
                normalizedStatus,
                request.StepIndex,
                stepId,
                stepTitle,
                request.Context,
                now,
                now),
            (_, existing) => existing with
            {
                Command = normalizedCommand,
                Title = request.Title?.Trim() ?? existing.Title,
                Status = normalizedStatus,
                StepIndex = request.StepIndex,
                StepId = stepId,
                StepTitle = stepTitle,
                Context = request.Context,
                UpdatedUtc = now
            });

        return Task.FromResult(updated);
    }

    public Task<WorkflowStateEntry?> GetAsync(string workflowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return Task.FromResult<WorkflowStateEntry?>(null);
        }

        _states.TryGetValue(workflowId.Trim(), out var entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<WorkflowStateEntry>> ListAsync(
        string? status = null,
        string? command = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var safeLimit = Math.Clamp(limit, 1, 200);
        var normalizedStatus = status?.Trim();
        var normalizedCommand = command?.Trim();

        var items = _states.Values
            .Where(state => string.IsNullOrWhiteSpace(normalizedStatus)
                            || state.Status.Equals(normalizedStatus, StringComparison.OrdinalIgnoreCase))
            .Where(state => string.IsNullOrWhiteSpace(normalizedCommand)
                            || state.Command.Equals(normalizedCommand, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(state => state.UpdatedUtc)
            .Take(safeLimit)
            .ToList();

        return Task.FromResult<IReadOnlyList<WorkflowStateEntry>>(items);
    }
}
