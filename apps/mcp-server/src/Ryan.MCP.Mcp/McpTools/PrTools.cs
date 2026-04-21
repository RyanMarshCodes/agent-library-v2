using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class PrTools(ILogger<PrTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "generate_pr_description")]
    [Description("Generates a structured GitHub PR description: max 5 bullet summary, risks + rollback plan, paste-ready description, and what reviewers should focus on. Use before opening any PR.")]
    public async Task<string> GeneratePrDescription(
        [Description("Base branch to diff against (default: main)")] string? baseBranch = null,
        [Description("Linked issue or ticket number (optional)")] string? issueNumber = null,
        [Description("Working directory for git commands (optional, defaults to current)")] string? workingDirectory = null,
        [Description("If true, returns just the summary bullets (for quick context)")] bool summaryOnly = false,
        CancellationToken cancellationToken = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "PrTools.GeneratePrDescription",
            ["BaseBranch"] = baseBranch,
            ["IssueNumber"] = issueNumber,
            ["WorkingDirectory"] = workingDirectory,
            ["SummaryOnly"] = summaryOnly,
        });
        logger.LogDebug("GeneratePrDescription invoked");

        baseBranch ??= "main";
        var gitDir = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory;

        try
        {
            var (changedFiles, diffOutput) = await GetGitDiffAsync(gitDir, baseBranch, cancellationToken);
            var commitLog = await GetCommitLogAsync(gitDir, baseBranch, cancellationToken);

            var analysis = AnalyzeChanges(changedFiles, diffOutput, commitLog);

            if (summaryOnly)
            {
                return JsonSerializer.Serialize(new
                {
                    summary = analysis.Summary,
                    riskLevel = analysis.RiskLevel,
                    breakingChanges = analysis.BreakingChanges,
                }, JsonOptions);
            }

            var description = BuildPrDescription(analysis, issueNumber);
            return JsonSerializer.Serialize(new
            {
                summary = analysis.Summary,
                riskLevel = analysis.RiskLevel,
                breakingChanges = analysis.BreakingChanges,
                rollbackPlan = analysis.RollbackPlan,
                reviewerFocus = analysis.ReviewerFocus,
                fullDescription = description,
                changedFiles = changedFiles,
                commitCount = analysis.CommitCount,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static async Task<(List<string> changedFiles, string diffOutput)> GetGitDiffAsync(
        string gitDir, string baseBranch, CancellationToken ct)
    {
        var filesResult = await RunGitCommandAsync(gitDir, $"diff {baseBranch}...HEAD --name-only", ct);
        var diffResult = await RunGitCommandAsync(gitDir, $"diff {baseBranch}...HEAD", ct);

        var files = filesResult
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrEmpty(f))
            .ToList();

        return (files, diffResult);
    }

    private static async Task<string> GetCommitLogAsync(string gitDir, string baseBranch, CancellationToken ct)
    {
        return await RunGitCommandAsync(gitDir, $"log {baseBranch}...HEAD --oneline", ct);
    }

    private static async Task<string> RunGitCommandAsync(string workingDir, string args, CancellationToken ct)
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

    private static PrAnalysis AnalyzeChanges(List<string> changedFiles, string diffOutput, string commitLog)
    {
        var analysis = new PrAnalysis
        {
            ChangedFiles = changedFiles,
            CommitCount = commitLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
        };

        foreach (var file in changedFiles)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var isTest = file.Contains(".test.", StringComparison.OrdinalIgnoreCase) ||
                         file.EndsWith(".tests.cs", StringComparison.OrdinalIgnoreCase) ||
                         file.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase);

            var category = ext switch
            {
                ".cs" when file.Contains("Tests") || file.Contains(".Test.") => "Tests",
                ".cs" => "Code",
                ".md" => "Docs",
                ".json" or ".yaml" or ".yml" => "Config",
                ".sql" => "Database",
                _ => "Other"
            };

            analysis.CategoryCounts[category] = analysis.CategoryCounts.GetValueOrDefault(category) + 1;

            if (file.Contains("Controller", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("Service", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("Handler", StringComparison.OrdinalIgnoreCase))
            {
                analysis.HasBusinessLogic = true;
            }

            if (file.Contains("Program.cs", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("Startup.cs", StringComparison.OrdinalIgnoreCase))
            {
                analysis.HasConfigChange = true;
            }

            if (ext is ".sql" or ".cs" && diffOutput.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
            {
                analysis.HasSchemaChange = true;
            }
        }

        analysis.RiskLevel = DetermineRiskLevel(analysis);
        analysis.Summary = BuildSummary(analysis, commitLog);
        analysis.BreakingChanges = FindBreakingChanges(changedFiles, diffOutput);
        analysis.RollbackPlan = DetermineRollbackPlan(analysis);
        analysis.ReviewerFocus = DetermineReviewerFocus(analysis);

        return analysis;
    }

    private static string DetermineRiskLevel(PrAnalysis a)
    {
        if (a.HasSchemaChange) return "High";
        if (a.CategoryCounts.TryGetValue("Code", out var codeCount) && codeCount > 10) return "Medium";
        if (a.HasConfigChange) return "Medium";
        return "Low";
    }

    private static List<string> FindBreakingChanges(List<string> files, string diff)
    {
        var breaking = new List<string>();

        foreach (var f in files)
        {
            if (f.Contains("Controller") && diff.Contains("[HttpDelete]", StringComparison.OrdinalIgnoreCase))
                breaking.Add($"Removed endpoint in {f}");
            if (f.EndsWith(".cs") && diff.Contains("public virtual", StringComparison.OrdinalIgnoreCase))
                breaking.Add($"Removed virtual method in {f}");
        }

        if (diff.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase) && diff.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase))
            breaking.Add("Database: dropped column");

        return breaking;
    }

    private static string DetermineRollbackPlan(PrAnalysis a)
    {
        if (a.HasSchemaChange)
            return "Requires schema migration rollback + data migration";
        return "Revert commit is sufficient";
    }

    private static List<string> DetermineReviewerFocus(PrAnalysis a)
    {
        var focus = new List<string>();

        if (a.HasSchemaChange) focus.Add("Database migrations and data integrity");
        if (a.HasBusinessLogic) focus.Add("Business logic correctness");
        if (a.CategoryCounts.ContainsKey("Tests")) focus.Add("Test coverage for new/changed code");

        return focus.Count > 0 ? focus : new List<string> { "General code review" };
    }

    private static List<string> BuildSummary(PrAnalysis a, string commits)
    {
        var bullets = new List<string>();
        var commitLines = commits.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var c in commitLines.Take(5))
        {
            var msg = c.Length > 80 ? c[..77] + "..." : c;
            bullets.Add(msg);
        }

        return bullets;
    }

    private static string BuildPrDescription(PrAnalysis a, string? issueNumber)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        foreach (var bullet in a.Summary.Take(5))
        {
            sb.AppendLine($"- {bullet}");
        }
        sb.AppendLine();

        sb.AppendLine("## Risks & Rollback");
        sb.AppendLine();
        sb.AppendLine($"- **Risk Level**: {a.RiskLevel}");
        sb.AppendLine($"- **Breaking Changes**: {(a.BreakingChanges.Count == 0 ? "None" : string.Join(", ", a.BreakingChanges))}");
        sb.AppendLine($"- **Rollback Plan**: {a.RollbackPlan}");
        sb.AppendLine();

        sb.AppendLine("## What Reviewers Should Focus On");
        sb.AppendLine();
        foreach (var focus in a.ReviewerFocus)
        {
            sb.AppendLine($"- {focus}");
        }
        sb.AppendLine();

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Full PR Description");
        sb.AppendLine();
        sb.AppendLine("## Changes");
        sb.AppendLine();

        var cats = a.CategoryCounts.OrderByDescending(x => x.Value).Select(x => x.Key);
        foreach (var cat in cats)
        {
            sb.AppendLine($"### {cat}");
            var files = a.ChangedFiles.Where(f =>
                cat == "Tests" && (f.Contains("test", StringComparison.OrdinalIgnoreCase) || f.Contains(".Test.")) ||
                cat == "Code" && !f.Contains("test", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".md") ||
                cat == "Docs" && f.EndsWith(".md") ||
                cat == "Config" && (f.EndsWith(".json") || f.EndsWith(".yaml") || f.EndsWith(".yml")) ||
                cat == "Database" && f.EndsWith(".sql")).ToList();

            foreach (var file in files.Take(10))
            {
                sb.AppendLine($"- {file}");
            }
            if (files.Count > 10)
            {
                sb.AppendLine($"- ... and {files.Count - 10} more");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Test Plan");
        sb.AppendLine();
        sb.AppendLine("- [ ] Automated tests pass");
        sb.AppendLine("- [ ] Manual verification of changed behavior");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(issueNumber))
        {
            sb.AppendLine($"Closes #{issueNumber}");
        }

        return sb.ToString();
    }

    private sealed class PrAnalysis
    {
        public List<string> ChangedFiles { get; set; } = [];
        public int CommitCount { get; set; }
        public bool HasBusinessLogic { get; set; }
        public bool HasConfigChange { get; set; }
        public bool HasSchemaChange { get; set; }
        public Dictionary<string, int> CategoryCounts { get; set; } = [];
        public string RiskLevel { get; set; } = "Low";
        public List<string> Summary { get; set; } = [];
        public List<string> BreakingChanges { get; set; } = [];
        public string RollbackPlan { get; set; } = "";
        public List<string> ReviewerFocus { get; set; } = [];
    }
}