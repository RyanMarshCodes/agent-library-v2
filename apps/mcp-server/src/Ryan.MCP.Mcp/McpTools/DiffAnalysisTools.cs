using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class DiffAnalysisTools(ILogger<DiffAnalysisTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "analyze_diff")]
    [Description(
        "Analyze git diff output: categorize changed files, compute stats, assess risk. " +
        "Supports staged, unstaged, or branch comparison diffs.")]
    public async Task<string> AnalyzeDiff(
        [Description("Diff mode: 'staged' (--cached), 'unstaged' (working tree), 'branch' (compare to base branch). Default: unstaged")]
        string mode = "unstaged",
        [Description("Base branch for 'branch' mode (default: main)")] string? baseBranch = null,
        [Description("Working directory (defaults to current)")] string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "DiffAnalysisTools.AnalyzeDiff",
            ["Mode"] = mode,
            ["BaseBranch"] = baseBranch,
            ["WorkingDirectory"] = workingDirectory,
        });
        logger.LogDebug("AnalyzeDiff invoked");

        var workDir = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory;
        baseBranch ??= "main";

        // Sanitize baseBranch to prevent command injection — only allow safe git ref characters
        if (!System.Text.RegularExpressions.Regex.IsMatch(baseBranch, @"^[a-zA-Z0-9_./@-]+$"))
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                message = "Invalid base branch name. Only alphanumeric characters, dots, underscores, slashes, @, and hyphens are allowed."
            }, JsonOptions);
        }

        try
        {
            var (diffArgs, statArgs) = mode.ToLowerInvariant() switch
            {
                "staged" or "cached" => ("diff --cached", "diff --cached --stat"),
                "branch" => ($"diff {baseBranch}...HEAD", $"diff {baseBranch}...HEAD --stat"),
                _ => ("diff", "diff --stat") // unstaged / working tree
            };

            var nameStatusArgs = mode.ToLowerInvariant() switch
            {
                "staged" or "cached" => "diff --cached --name-status",
                "branch" => $"diff {baseBranch}...HEAD --name-status",
                _ => "diff --name-status"
            };

            // Run all three git commands in parallel
            var diffTask = RunGitAsync(workDir, diffArgs, cancellationToken);
            var statTask = RunGitAsync(workDir, statArgs, cancellationToken);
            var nameStatusTask = RunGitAsync(workDir, nameStatusArgs, cancellationToken);

            await Task.WhenAll(diffTask, statTask, nameStatusTask);

            var diffOutput = await diffTask;
            var statOutput = await statTask;
            var nameStatusOutput = await nameStatusTask;

            var changedFiles = ParseNameStatus(nameStatusOutput);

            if (changedFiles.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "clean",
                    mode,
                    message = $"No changes found ({mode} diff is empty)"
                }, JsonOptions);
            }

            var fileStats = ParseDiffStats(statOutput);
            var categories = CategorizeFiles(changedFiles);
            var riskAssessment = AssessRisk(changedFiles, diffOutput, categories);
            var summary = BuildSummary(changedFiles, fileStats, categories);

            return JsonSerializer.Serialize(new
            {
                status = "changes_found",
                mode,
                baseBranch = mode == "branch" ? baseBranch : null,
                totalFiles = changedFiles.Count,
                totalAdditions = fileStats.Sum(f => f.Additions),
                totalDeletions = fileStats.Sum(f => f.Deletions),
                categories,
                files = fileStats,
                riskAssessment,
                summary
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message }, JsonOptions);
        }
    }

    // ── Git name-status parsing ────────────────────────────────────────

    private static List<ChangedFile> ParseNameStatus(string output)
    {
        var files = new List<ChangedFile>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 2) continue;

            var status = trimmed[0] switch
            {
                'A' => "added",
                'M' => "modified",
                'D' => "deleted",
                'R' => "renamed",
                'C' => "copied",
                _ => "unknown"
            };

            // Tab-separated: status\tfile (or status\told\tnew for renames)
            var parts = trimmed[1..].Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
            var path = parts.Length > 0 ? parts[0].Trim() : trimmed[2..].Trim();
            var newPath = parts.Length > 1 ? parts[1].Trim() : null;

            if (!string.IsNullOrEmpty(path))
            {
                files.Add(new ChangedFile
                {
                    Path = newPath ?? path,
                    OldPath = status == "renamed" ? path : null,
                    Status = status
                });
            }
        }

        return files;
    }

    // ── Diff stat parsing ──────────────────────────────────────────────

    private static List<FileStat> ParseDiffStats(string statOutput)
    {
        var stats = new List<FileStat>();
        // Format: " file | 10 ++++---" or " file | Bin 0 -> 1234 bytes"
        var lineRegex = new Regex(@"^\s*(.+?)\s+\|\s+(\d+)\s+(\+*)(-*)", RegexOptions.Compiled);
        var binaryRegex = new Regex(@"^\s*(.+?)\s+\|\s+Bin", RegexOptions.Compiled);

        foreach (var line in statOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = lineRegex.Match(line);
            if (match.Success)
            {
                stats.Add(new FileStat
                {
                    Path = match.Groups[1].Value.Trim(),
                    Additions = match.Groups[3].Value.Length,
                    Deletions = match.Groups[4].Value.Length,
                    TotalChanges = int.TryParse(match.Groups[2].Value, out var t) ? t : 0
                });
                continue;
            }

            var binMatch = binaryRegex.Match(line);
            if (binMatch.Success)
            {
                stats.Add(new FileStat
                {
                    Path = binMatch.Groups[1].Value.Trim(),
                    IsBinary = true
                });
            }
        }

        return stats;
    }

    // ── Categorization ─────────────────────────────────────────────────

    private static Dictionary<string, List<string>> CategorizeFiles(List<ChangedFile> files)
    {
        var categories = new Dictionary<string, List<string>>();

        foreach (var f in files)
        {
            var cat = ClassifyFile(f.Path);
            if (!categories.ContainsKey(cat))
                categories[cat] = [];
            categories[cat].Add(f.Path);
        }

        return categories;
    }

    private static string ClassifyFile(string path)
    {
        var lower = path.ToLowerInvariant();
        var ext = Path.GetExtension(lower);
        var name = Path.GetFileName(lower);

        // Tests
        if (lower.Contains(".test.") || lower.Contains(".spec.") || lower.Contains("/tests/") ||
            lower.Contains("/test/") || lower.Contains(".tests/") || lower.Contains("__tests__") ||
            lower.Contains("xunit") || lower.Contains("nunit") ||
            name.EndsWith(".test.ts") || name.EndsWith(".test.tsx") || name.EndsWith(".test.js") ||
            name.EndsWith(".spec.ts") || name.EndsWith(".spec.tsx") || name.EndsWith(".spec.js") ||
            name.EndsWith("tests.cs"))
            return "test";

        // Docs
        if (ext is ".md" or ".mdx" or ".txt" or ".rst" || lower.Contains("/docs/") ||
            name is "readme.md" or "changelog.md" or "license" or "license.md")
            return "docs";

        // Config / CI
        if (ext is ".json" or ".yaml" or ".yml" or ".toml" or ".ini" or ".env" or ".editorconfig" ||
            name is ".gitignore" or ".gitattributes" or ".npmrc" or ".nvmrc" or ".prettierrc" ||
            name.StartsWith(".eslint") || name.StartsWith(".prettier") ||
            lower.Contains(".github/") || lower.Contains(".azure-pipelines") ||
            lower.Contains("dockerfile") || lower.Contains("docker-compose") ||
            name is "tsconfig.json" or "vite.config.ts" or "vitest.config.ts" or
                "angular.json" or "nx.json" or "turbo.json" or "jest.config.ts" or
                "jest.config.js" or "tailwind.config.ts" or "tailwind.config.js" ||
            name.EndsWith(".csproj") || name.EndsWith(".sln") || name.EndsWith(".props") ||
            name.EndsWith(".targets"))
            return "config";

        // Infrastructure
        if (ext is ".tf" or ".bicep" or ".pulumi" || lower.Contains("infrastructure/") ||
            lower.Contains("/infra/") || lower.Contains("terraform/") ||
            name.EndsWith(".sql"))
            return "infra";

        // Styles
        if (ext is ".css" or ".scss" or ".sass" or ".less" or ".styl")
            return "styles";

        // Code (everything else that's source)
        if (ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".go" or ".rs" or
            ".java" or ".kt" or ".swift" or ".rb" or ".php" or ".html" or ".svelte" or ".vue")
            return "code";

        return "other";
    }

    // ── Risk assessment ────────────────────────────────────────────────

    private static RiskInfo AssessRisk(List<ChangedFile> files, string diff, Dictionary<string, List<string>> categories)
    {
        var risk = new RiskInfo();
        var signals = new List<string>();

        var totalFiles = files.Count;
        var deletedCount = files.Count(f => f.Status == "deleted");
        var codeFiles = categories.GetValueOrDefault("code")?.Count ?? 0;

        // High risk signals
        if (diff.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) ||
            diff.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("Database destructive operation (DROP TABLE/COLUMN)");
            risk.Level = "high";
        }

        if (diff.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("Database schema change (ALTER TABLE)");
            if (risk.Level != "high") risk.Level = "medium";
        }

        if (files.Any(f => f.Path.Contains("Program.cs", StringComparison.OrdinalIgnoreCase) ||
                           f.Path.Contains("Startup.cs", StringComparison.OrdinalIgnoreCase) ||
                           f.Path.Contains("appsettings", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("Application startup or configuration changed");
            if (risk.Level != "high") risk.Level = "medium";
        }

        if (totalFiles > 20)
        {
            signals.Add($"Large changeset ({totalFiles} files)");
            if (risk.Level != "high") risk.Level = "medium";
        }

        if (deletedCount > 3)
        {
            signals.Add($"{deletedCount} files deleted");
            if (risk.Level != "high") risk.Level = "medium";
        }

        // Check for security-sensitive patterns
        if (diff.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            diff.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            diff.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
            diff.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
            diff.Contains("connectionstring", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("Security-sensitive content detected (password/secret/key references)");
            risk.Level = "high";
        }

        if (signals.Count == 0)
        {
            signals.Add("No elevated risk signals detected");
            risk.Level = "low";
        }

        risk.Signals = signals;

        // Check for test coverage
        var hasTests = categories.ContainsKey("test");
        if (codeFiles > 0 && !hasTests)
        {
            risk.TestCoverageWarning = $"{codeFiles} code files changed but no test files modified";
        }

        return risk;
    }

    // ── Summary ────────────────────────────────────────────────────────

    private static List<string> BuildSummary(List<ChangedFile> files, List<FileStat> stats, Dictionary<string, List<string>> categories)
    {
        var bullets = new List<string>();

        // Category breakdown
        foreach (var (cat, catFiles) in categories.OrderByDescending(kv => kv.Value.Count))
        {
            bullets.Add($"{cat}: {catFiles.Count} file(s)");
        }

        // Top changed files by total changes
        var topFiles = stats
            .Where(s => !s.IsBinary)
            .OrderByDescending(s => s.TotalChanges)
            .Take(5)
            .ToList();

        if (topFiles.Count > 0)
        {
            bullets.Add("Most changed: " + string.Join(", ",
                topFiles.Select(f => $"{Path.GetFileName(f.Path)} (+{f.Additions}/-{f.Deletions})")));
        }

        // Status breakdown
        var added = files.Count(f => f.Status == "added");
        var modified = files.Count(f => f.Status == "modified");
        var deleted = files.Count(f => f.Status == "deleted");
        var renamed = files.Count(f => f.Status == "renamed");

        var statusParts = new List<string>();
        if (added > 0) statusParts.Add($"{added} added");
        if (modified > 0) statusParts.Add($"{modified} modified");
        if (deleted > 0) statusParts.Add($"{deleted} deleted");
        if (renamed > 0) statusParts.Add($"{renamed} renamed");
        bullets.Add(string.Join(", ", statusParts));

        return bullets;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static async Task<string> RunGitAsync(string workingDir, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return "";

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output;
    }

    // ── Inner models ───────────────────────────────────────────────────

    private sealed class ChangedFile
    {
        public string Path { get; set; } = "";
        public string? OldPath { get; set; }
        public string Status { get; set; } = "";
    }

    private sealed class FileStat
    {
        public string Path { get; set; } = "";
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public int TotalChanges { get; set; }
        public bool IsBinary { get; set; }
    }

    private sealed class RiskInfo
    {
        public string Level { get; set; } = "low";
        public List<string> Signals { get; set; } = [];
        public string? TestCoverageWarning { get; set; }
    }
}
