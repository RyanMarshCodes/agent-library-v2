using Npgsql;

namespace Ryan.MCP.Mcp.Services.WorkflowState;

public sealed class PostgresWorkflowStateStore(
    NpgsqlDataSource dataSource) : IWorkflowStateStore
{
    public async Task<WorkflowStateEntry> UpsertAsync(WorkflowStateUpsertRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var normalizedId = request.WorkflowId.Trim();
        var normalizedCommand = request.Command.Trim();
        var normalizedStatus = request.Status.Trim();
        var stepId = string.IsNullOrWhiteSpace(request.StepId) ? "step-unknown" : request.StepId.Trim();
        var stepTitle = string.IsNullOrWhiteSpace(request.StepTitle) ? "Unknown step" : request.StepTitle.Trim();
        var title = string.IsNullOrWhiteSpace(request.Title) ? normalizedCommand : request.Title.Trim();

        const string sql = """
            INSERT INTO workflow_states (
                workflow_id, command, title, status, step_index, step_id, step_title, context, created_utc, updated_utc
            )
            VALUES (
                @workflow_id, @command, @title, @status, @step_index, @step_id, @step_title, @context, @now, @now
            )
            ON CONFLICT (workflow_id) DO UPDATE
            SET
                command = EXCLUDED.command,
                title = EXCLUDED.title,
                status = EXCLUDED.status,
                step_index = EXCLUDED.step_index,
                step_id = EXCLUDED.step_id,
                step_title = EXCLUDED.step_title,
                context = EXCLUDED.context,
                updated_utc = EXCLUDED.updated_utc
            RETURNING
                workflow_id,
                command,
                title,
                status,
                step_index,
                step_id,
                step_title,
                context,
                created_utc,
                updated_utc;
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("workflow_id", normalizedId);
        cmd.Parameters.AddWithValue("command", normalizedCommand);
        cmd.Parameters.AddWithValue("title", title);
        cmd.Parameters.AddWithValue("status", normalizedStatus);
        cmd.Parameters.AddWithValue("step_index", request.StepIndex);
        cmd.Parameters.AddWithValue("step_id", stepId);
        cmd.Parameters.AddWithValue("step_title", stepTitle);
        cmd.Parameters.AddWithValue("context", (object?)request.Context ?? DBNull.Value);
        cmd.Parameters.AddWithValue("now", now);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Workflow state upsert failed: no row returned.");
        }

        return MapEntry(reader);
    }

    public async Task<WorkflowStateEntry?> GetAsync(string workflowId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return null;
        }

        const string sql = """
            SELECT
                workflow_id,
                command,
                title,
                status,
                step_index,
                step_id,
                step_title,
                context,
                created_utc,
                updated_utc
            FROM workflow_states
            WHERE workflow_id = @workflow_id;
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("workflow_id", workflowId.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return MapEntry(reader);
    }

    public async Task<IReadOnlyList<WorkflowStateEntry>> ListAsync(
        string? status = null,
        string? command = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        var normalizedCommand = string.IsNullOrWhiteSpace(command) ? null : command.Trim();

        const string sql = """
            SELECT
                workflow_id,
                command,
                title,
                status,
                step_index,
                step_id,
                step_title,
                context,
                created_utc,
                updated_utc
            FROM workflow_states
            WHERE (@status IS NULL OR status = @status)
              AND (@command IS NULL OR command = @command)
            ORDER BY updated_utc DESC
            LIMIT @limit;
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("status", (object?)normalizedStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("command", (object?)normalizedCommand ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", safeLimit);

        var list = new List<WorkflowStateEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(MapEntry(reader));
        }

        return list;
    }

    private static WorkflowStateEntry MapEntry(NpgsqlDataReader reader)
        => new(
            WorkflowId: reader.GetString(0),
            Command: reader.GetString(1),
            Title: reader.GetString(2),
            Status: reader.GetString(3),
            StepIndex: reader.GetInt32(4),
            StepId: reader.GetString(5),
            StepTitle: reader.GetString(6),
            Context: reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedUtc: reader.GetDateTime(8),
            UpdatedUtc: reader.GetDateTime(9));
}
