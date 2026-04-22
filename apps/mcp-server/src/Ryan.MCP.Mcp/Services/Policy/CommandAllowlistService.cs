using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Services.Policy;

public sealed class CommandAllowlistService(McpOptions options, ILogger<CommandAllowlistService> logger)
{
    public CommandAllowlistDecision Evaluate(string toolName, string command)
    {
        var normalized = NormalizeCommandName(command);
        if (!options.ExecutePolicy.EnforceAllowlist)
        {
            logger.LogDebug("Command allowlist disabled; allowing {Command} for {ToolName}", normalized, toolName);
            return new CommandAllowlistDecision(true, normalized, "Allowlist enforcement disabled.");
        }

        var allowed = options.ExecutePolicy.AllowedCommands
            .Any(x => string.Equals(x?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));

        if (!allowed)
        {
            logger.LogWarning(
                "Command execution denied by allowlist: tool={ToolName}, command={Command}",
                toolName,
                normalized);

            return new CommandAllowlistDecision(
                false,
                normalized,
                $"Command '{normalized}' is not in McpOptions:ExecutePolicy:AllowedCommands.");
        }

        return new CommandAllowlistDecision(true, normalized, "Command allowed.");
    }

    public static string NormalizeCommandName(string command)
    {
        var value = (command ?? string.Empty).Trim();
        var fileName = Path.GetFileNameWithoutExtension(value);
        return fileName.ToLowerInvariant();
    }
}

public sealed record CommandAllowlistDecision(
    bool Allowed,
    string NormalizedCommand,
    string Reason);
