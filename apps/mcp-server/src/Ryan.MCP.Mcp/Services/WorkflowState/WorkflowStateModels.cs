namespace Ryan.MCP.Mcp.Services.WorkflowState;

public sealed record WorkflowStateEntry(
    string WorkflowId,
    string Command,
    string Title,
    string Status,
    int StepIndex,
    string StepId,
    string StepTitle,
    string? Context,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record WorkflowStateUpsertRequest(
    string WorkflowId,
    string Command,
    string? Title,
    string Status,
    int StepIndex,
    string? StepId,
    string? StepTitle,
    string? Context);
