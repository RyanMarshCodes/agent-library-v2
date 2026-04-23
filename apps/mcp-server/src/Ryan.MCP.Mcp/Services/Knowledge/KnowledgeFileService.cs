using System.Text;
using System.Text.RegularExpressions;
using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Services.Knowledge;

public sealed class ReviewItem
{
    public string ReviewId { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string SubmittedAt { get; set; } = "";
    public string Reason { get; set; } = "";
    public string SuggestedPath { get; set; } = "";
    public string SuggestedDomain { get; set; } = "";
    public double Confidence { get; set; }
    public string Status { get; set; } = "pending";
    public string DraftContent { get; set; } = "";
    public string FilePath { get; set; } = "";
}

public sealed class KnowledgeFileService(McpOptions options, ILogger<KnowledgeFileService> logger)
{
    private string KnowledgePath => options.KnowledgeLibrary.KnowledgePath;
    private string RawPendingPath => options.KnowledgeLibrary.RawPendingPath;
    private string RawProcessedPath => options.KnowledgeLibrary.RawProcessedPath;
    private string MetaPath => Path.Combine(KnowledgePath, "_meta");
    private string ReviewPath => Path.Combine(KnowledgePath, "_review");

    public async Task WritePageAsync(
        string relativePath,
        string title,
        string content,
        string domain,
        string[] tags,
        string confidence,
        string? sourceFile,
        CancellationToken ct)
    {
        var fullPath = Path.Combine(KnowledgePath, NormalizePath(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: \"{EscapeYaml(title)}\"");
        sb.AppendLine($"domain: \"{domain}\"");
        sb.AppendLine($"tags: [{string.Join(", ", tags.Select(t => $"\"{t}\""))}]");
        if (sourceFile is not null)
            sb.AppendLine($"sources: [\"{sourceFile}\"]");
        sb.AppendLine($"updated: {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine($"confidence: {confidence}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(content.TrimStart());

        await File.WriteAllTextAsync(fullPath, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
        logger.LogDebug("Wrote knowledge page {Path}", relativePath);
    }

    public async Task<string?> ReadPageAsync(string relativePath, CancellationToken ct)
    {
        var fullPath = Path.Combine(KnowledgePath, NormalizePath(relativePath));
        if (!File.Exists(fullPath)) return null;
        return await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
    }

    public async Task UpdateIndexAsync(
        string relativePath,
        string title,
        string domain,
        string summary,
        CancellationToken ct)
    {
        var indexPath = Path.Combine(MetaPath, "index.md");
        var linkPath = $"../{relativePath.Replace('\\', '/')}";
        var entry = $"- [{title}]({linkPath}) — {summary}";
        var domainHeader = $"### {domain}";

        string existing = File.Exists(indexPath)
            ? await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false)
            : BuildEmptyIndex();

        existing = UpsertDomainEntry(existing, domainHeader, entry);
        existing = UpsertAzEntry(existing, title, linkPath, summary);
        existing = UpdateIndexHeader(existing);

        await File.WriteAllTextAsync(indexPath, existing, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    public async Task AppendLogAsync(string operation, string description, CancellationToken ct)
    {
        var logPath = Path.Combine(MetaPath, "log.md");
        var line = $"\n## [{DateTime.UtcNow:yyyy-MM-dd}] {operation} | {description}";
        await File.AppendAllTextAsync(logPath, line, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    public List<string> ListPending()
    {
        if (!Directory.Exists(RawPendingPath)) return [];
        return Directory.GetFiles(RawPendingPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".gitkeep"))
            .Select(f => Path.GetRelativePath(RawPendingPath, f))
            .OrderBy(f => f)
            .ToList();
    }

    public List<ReviewItem> ListReviewItems()
    {
        if (!Directory.Exists(ReviewPath)) return [];
        return Directory.GetFiles(ReviewPath, "*.md")
            .Select(ParseReviewFile)
            .Where(r => r is not null)
            .Cast<ReviewItem>()
            .OrderBy(r => r.SubmittedAt)
            .ToList();
    }

    public async Task<ReviewItem?> GetReviewItemAsync(string reviewId, CancellationToken ct)
    {
        var file = Path.Combine(ReviewPath, $"{reviewId}.md");
        if (!File.Exists(file)) return null;
        var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        return ParseReviewItem(reviewId, content, file);
    }

    public async Task WriteReviewItemAsync(ReviewItem item, CancellationToken ct)
    {
        Directory.CreateDirectory(ReviewPath);
        var filePath = Path.Combine(ReviewPath, $"{item.ReviewId}.md");

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"review_id: \"{item.ReviewId}\"");
        sb.AppendLine($"source_file: \"{item.SourceFile}\"");
        sb.AppendLine($"submitted_at: \"{item.SubmittedAt}\"");
        sb.AppendLine($"reason: \"{EscapeYaml(item.Reason)}\"");
        sb.AppendLine($"suggested_path: \"{item.SuggestedPath}\"");
        sb.AppendLine($"suggested_domain: \"{item.SuggestedDomain}\"");
        sb.AppendLine($"confidence: {item.Confidence:F2}");
        sb.AppendLine("status: pending");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(item.DraftContent);

        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
        item.FilePath = filePath;
    }

    public void DeleteReviewItem(string reviewId)
    {
        var file = Path.Combine(ReviewPath, $"{reviewId}.md");
        if (File.Exists(file)) File.Delete(file);
    }

    public void ArchiveRawFile(string fileName)
    {
        var source = Path.Combine(RawPendingPath, fileName);
        if (!File.Exists(source)) return;

        Directory.CreateDirectory(RawProcessedPath);
        var dest = Path.Combine(RawProcessedPath, fileName);
        if (File.Exists(dest))
            dest = Path.Combine(RawProcessedPath, $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(fileName)}");

        File.Move(source, dest);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static string NormalizePath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

    private static string EscapeYaml(string value) =>
        value.Replace("\"", "\\\"");

    private static string BuildEmptyIndex() =>
        "# Knowledge Index\n\n_Last updated: — 0 pages_\n\n---\n\n## By Domain\n\n---\n\n## A–Z\n\n";

    private static string UpdateIndexHeader(string content)
    {
        var pageCount = Regex.Matches(content, @"^\s*- \[", RegexOptions.Multiline).Count / 2;
        return Regex.Replace(content,
            @"_Last updated:.*_",
            $"_Last updated: {DateTime.UtcNow:yyyy-MM-dd} — {pageCount} pages_");
    }

    private static string UpsertDomainEntry(string content, string domainHeader, string entry)
    {
        var pageName = Regex.Match(entry, @"\[([^\]]+)\]").Groups[1].Value;

        if (content.Contains(domainHeader))
        {
            var headerIdx = content.IndexOf(domainHeader, StringComparison.Ordinal);
            var afterHeader = content.IndexOf('\n', headerIdx) + 1;
            var nextSection = content.IndexOf("\n###", afterHeader);
            var nextAzOrEnd = content.IndexOf("\n---", afterHeader);
            var sectionEnd = nextSection > 0 ? nextSection : nextAzOrEnd > 0 ? nextAzOrEnd : content.Length;

            var section = content[afterHeader..sectionEnd];
            var existingEntry = Regex.Match(section, $@"- \[{Regex.Escape(pageName)}\]\([^)]+\).*");
            if (existingEntry.Success)
            {
                return content[..afterHeader]
                    + section.Replace(existingEntry.Value, entry)
                    + content[sectionEnd..];
            }

            return content[..sectionEnd] + $"\n{entry}" + content[sectionEnd..];
        }

        var byDomainMarker = "## By Domain";
        var markerIdx = content.IndexOf(byDomainMarker, StringComparison.Ordinal);
        if (markerIdx < 0) return content;

        var insertAt = content.IndexOf("\n---", markerIdx);
        if (insertAt < 0) return content;

        return content[..insertAt]
            + $"\n\n{domainHeader}\n{entry}"
            + content[insertAt..];
    }

    private static string UpsertAzEntry(string content, string title, string linkPath, string summary)
    {
        var azMarker = "## A–Z";
        var markerIdx = content.IndexOf(azMarker, StringComparison.Ordinal);
        if (markerIdx < 0) return content;

        var afterMarker = content.IndexOf('\n', markerIdx) + 1;
        var endIdx = content.Length;

        var entry = $"- [{title}]({linkPath}) — {summary}";
        var section = content[afterMarker..endIdx];

        var existingEntry = Regex.Match(section, $@"- \[{Regex.Escape(title)}\]\([^)]+\).*");
        if (existingEntry.Success)
            return content[..afterMarker] + section.Replace(existingEntry.Value, entry);

        var lines = section.Split('\n').ToList();
        lines.Add(entry);
        lines.Sort(StringComparer.OrdinalIgnoreCase);
        return content[..afterMarker] + string.Join('\n', lines);
    }

    private ReviewItem? ParseReviewFile(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var reviewId = Path.GetFileNameWithoutExtension(filePath);
            return ParseReviewItem(reviewId, content, filePath);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse review file {Path}", filePath);
            return null;
        }
    }

    private static ReviewItem? ParseReviewItem(string reviewId, string content, string filePath)
    {
        var frontmatter = ExtractFrontmatter(content);
        if (frontmatter is null) return null;

        var draftMatch = Regex.Match(content, @"## Proposed Knowledge Page Preview\s*\n([\s\S]+)$");

        return new ReviewItem
        {
            ReviewId       = reviewId,
            SourceFile     = GetFrontmatterValue(frontmatter, "source_file"),
            SubmittedAt    = GetFrontmatterValue(frontmatter, "submitted_at"),
            Reason         = GetFrontmatterValue(frontmatter, "reason"),
            SuggestedPath  = GetFrontmatterValue(frontmatter, "suggested_path"),
            SuggestedDomain = GetFrontmatterValue(frontmatter, "suggested_domain"),
            Confidence     = double.TryParse(GetFrontmatterValue(frontmatter, "confidence"), out var c) ? c : 0,
            Status         = GetFrontmatterValue(frontmatter, "status"),
            DraftContent   = draftMatch.Success ? draftMatch.Groups[1].Value.Trim() : "",
            FilePath       = filePath,
        };
    }

    private static string? ExtractFrontmatter(string content)
    {
        if (!content.StartsWith("---")) return null;
        var end = content.IndexOf("---", 3, StringComparison.Ordinal);
        return end < 0 ? null : content[3..end];
    }

    private static string GetFrontmatterValue(string frontmatter, string key)
    {
        var match = Regex.Match(frontmatter, $@"^{key}:\s*""?(.+?)""?\s*$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }
}
