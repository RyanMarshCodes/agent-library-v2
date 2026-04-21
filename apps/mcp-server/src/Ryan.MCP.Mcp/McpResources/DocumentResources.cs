using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Services;

namespace Ryan.MCP.Mcp.McpResources;

[McpServerResourceType]
public sealed class DocumentResources(DocumentIngestionCoordinator documents)
{
    [McpServerResource(UriTemplate = "documents://list", Name = "documents_list", MimeType = "text/markdown")]
    [Description("Browse all indexed standards documents organized by tier and language. Use read_document(tier, path) to fetch the content of any listed document.")]
    public string GetDocumentsList()
    {
        var snapshot = documents.Snapshot;
        if (snapshot.TotalDocuments == 0)
        {
            return "# Standards Library\n\nNo documents are currently indexed. Run `ingest_documents()` to index your standards.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Standards Library");
        sb.AppendLine();
        sb.AppendLine($"**{snapshot.TotalDocuments} documents indexed** | Last updated: {snapshot.UpdatedUtc:u}");
        sb.AppendLine();

        foreach (var tierGroup in snapshot.Documents
            .GroupBy(d => d.Tier, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"## {tierGroup.Key}");
            sb.AppendLine();

            foreach (var langGroup in tierGroup
                .GroupBy(d => Path.GetDirectoryName(d.RelativePath)?.Replace('\\', '/') ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(langGroup.Key))
                {
                    sb.AppendLine($"### {langGroup.Key}");
                    sb.AppendLine();
                }

                foreach (var doc in langGroup.OrderBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"- `{doc.RelativePath}`");
                    sb.AppendLine($"  → `read_document(\"{doc.Tier}\", \"{doc.RelativePath}\")`");
                    sb.AppendLine($"  → `use_standard(\"{doc.Tier}\", \"{doc.RelativePath}\")`");
                }

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
