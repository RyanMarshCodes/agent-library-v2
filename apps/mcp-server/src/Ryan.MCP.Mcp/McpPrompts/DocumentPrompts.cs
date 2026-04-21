using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Services;

namespace Ryan.MCP.Mcp.McpPrompts;

[McpServerPromptType]
public sealed class DocumentPrompts(DocumentIngestionCoordinator documents)
{
    [McpServerPrompt(Name = "use_standard")]
    [Description("Load a standards document as active system-level instructions for the current conversation. Use this to apply coding standards, style guides, or best practices before writing or reviewing code.")]
    public async Task<ChatMessage> UseStandard(
        [Description("Document tier: 'official', 'organization', or 'project'")] string tier,
        [Description("Relative path as returned by list_standards, e.g. 'csharp/async-programming.md'")] string path,
        CancellationToken cancellationToken)
    {
        var entry = documents.Snapshot.Documents.FirstOrDefault(d =>
            d.Tier.Equals(tier, StringComparison.OrdinalIgnoreCase) &&
            d.RelativePath.Equals(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            return new ChatMessage(ChatRole.User,
                $"No standard found at '{path}' in tier '{tier}'. " +
                "Use list_standards() or read documents://list to browse available standards.");
        }

        try
        {
            var content = await File.ReadAllTextAsync(entry.AbsolutePath, cancellationToken).ConfigureAwait(false);
            return new ChatMessage(ChatRole.System,
                $"# Active Standard: {entry.RelativePath} (tier: {entry.Tier})\n\n{content}");
        }
        catch (Exception ex)
        {
            return new ChatMessage(ChatRole.User,
                $"Failed to load standard '{path}': {ex.Message}");
        }
    }
}
