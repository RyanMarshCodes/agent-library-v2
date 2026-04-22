using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Services.Observability;
using Ryan.MCP.Mcp.Services.Policy;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class PrTools(
    ILogger<PrTools> logger,
    CommandAllowlistService commandAllowlist,
    PlatformMetrics metrics,
    PolicyPreflightService preflight)
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
            var (changedFiles, diffOutput) = await GetGitDiffAsync(gitDir, baseBranch, commandAllowlist, metrics, cancellationToken);
            var commitLog = await GetCommitLogAsync(gitDir, baseBranch, commandAllowlist, metrics, cancellationToken);

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

    [McpServerTool(Name = "pr_checks_status")]
    [Description("Get PR checks status via gh with actionable hints.")]
    public async Task<string> PrChecksStatus(
        [Description("PR number to inspect.")] int prNumber,
        [Description("Working directory with git commands (optional, defaults to current).")] string? workingDirectory = null,
        CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "PrTools.PrChecksStatus",
            ["PrNumber"] = prNumber,
            ["WorkingDirectory"] = workingDirectory
        });

        if (prNumber <= 0)
        {
            return JsonSerializer.Serialize(new { error = "prNumber must be > 0" }, JsonOptions);
        }

        var gitDir = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory;
        var result = await RunCommandAsync(gitDir, "gh", $"pr checks {prNumber} --json name,state,link,bucket", commandAllowlist, metrics, ct);
        if (!result.Success)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                prNumber,
                stderr = Truncate(result.StdErr, 2000),
                hint = BuildGhHint(result.StdErr)
            }, JsonOptions);
        }

        JsonElement checks;
        try
        {
            checks = JsonDocument.Parse(result.StdOut).RootElement.Clone();
        }
        catch
        {
            return JsonSerializer.Serialize(new
            {
                status = "ok",
                prNumber,
                raw = Truncate(result.StdOut, 4000),
                hint = "Could not parse gh JSON output. Inspect the raw field."
            }, JsonOptions);
        }

        var total = 0;
        var success = 0;
        var failed = 0;
        var pending = 0;

        if (checks.ValueKind == JsonValueKind.Array)
        {
            foreach (var check in checks.EnumerateArray())
            {
                total++;
                var state = check.TryGetProperty("state", out var value)
                    ? value.GetString() ?? ""
                    : "";

                if (state.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    success++;
                }
                else if (state.Equals("FAILURE", StringComparison.OrdinalIgnoreCase)
                         || state.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    failed++;
                }
                else
                {
                    pending++;
                }
            }
        }

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            prNumber,
            total,
            success,
            failed,
            pending,
            checks
        }, JsonOptions);
    }

    [McpServerTool(Name = "pr_create_or_update")]
    [Description("Create PR if missing, or update title/body/base if it already exists. Requires approval token.")]
    public async Task<string> PrCreateOrUpdate(
        [Description("PR title.")] string title,
        [Description("PR body markdown.")] string body,
        [Description("Base branch (default: main).")] string baseBranch = "main",
        [Description("Head branch (default: current branch).")] string? headBranch = null,
        [Description("Create as draft when creating new PR.")] bool draft = false,
        [Description("Approval token for mutate policy.")] string? approvalToken = null,
        [Description("Working directory for git/gh commands (optional).")] string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var decision = preflight.Evaluate("pr_create_or_update", "mutate", approvalToken);
        if (!decision.Allowed)
        {
            return JsonSerializer.Serialize(new
            {
                error = "policy_denied",
                decision.Category,
                decision.RequiresApproval,
                decision.Reason,
                decision.Hints
            }, JsonOptions);
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
        {
            return JsonSerializer.Serialize(new { error = "title and body are required" }, JsonOptions);
        }

        if (!IsSafeRef(baseBranch))
        {
            return JsonSerializer.Serialize(new { error = "Invalid baseBranch format" }, JsonOptions);
        }

        var gitDir = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory;
        var head = headBranch;
        if (string.IsNullOrWhiteSpace(head))
        {
            var branchResult = await RunCommandAsync(gitDir, "git", "rev-parse --abbrev-ref HEAD", commandAllowlist, metrics, ct);
            if (!branchResult.Success)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "Unable to determine current branch",
                    stderr = Truncate(branchResult.StdErr, 1200)
                }, JsonOptions);
            }

            head = branchResult.StdOut.Trim();
        }

        if (!IsSafeRef(head!))
        {
            return JsonSerializer.Serialize(new { error = "Invalid headBranch format" }, JsonOptions);
        }

        var viewResult = await RunCommandAsync(
            gitDir,
            "gh",
            $"pr view {EscapeArg(head!)} --json number,url,title,state,isDraft",
            commandAllowlist,
            metrics,
            ct);

        if (viewResult.Success)
        {
            using var json = JsonDocument.Parse(viewResult.StdOut);
            var number = json.RootElement.GetProperty("number").GetInt32();
            var url = json.RootElement.GetProperty("url").GetString();

            var editResult = await RunCommandAsync(
                gitDir,
                "gh",
                $"pr edit {number} --title {EscapeArg(title)} --body {EscapeArg(body)} --base {EscapeArg(baseBranch)}",
                commandAllowlist,
                metrics,
                ct);

            return editResult.Success
                ? JsonSerializer.Serialize(new
                {
                    status = "updated",
                    number,
                    url
                }, JsonOptions)
                : JsonSerializer.Serialize(new
                {
                    status = "error",
                    action = "update",
                    stderr = Truncate(editResult.StdErr, 2000),
                    hint = BuildGhHint(editResult.StdErr)
                }, JsonOptions);
        }

        var createArgs = $"pr create --title {EscapeArg(title)} --body {EscapeArg(body)} --base {EscapeArg(baseBranch)} --head {EscapeArg(head!)}";
        if (draft)
        {
            createArgs += " --draft";
        }

        var createResult = await RunCommandAsync(gitDir, "gh", createArgs, commandAllowlist, metrics, ct);
        return createResult.Success
            ? JsonSerializer.Serialize(new
            {
                status = "created",
                output = Truncate(createResult.StdOut.Trim(), 1000)
            }, JsonOptions)
            : JsonSerializer.Serialize(new
            {
                status = "error",
                action = "create",
                stderr = Truncate(createResult.StdErr, 2000),
                hint = BuildGhHint(createResult.StdErr)
            }, JsonOptions);
    }

    [McpServerTool(Name = "issue_sync")]
    [Description("Sync issue state/comment/labels via gh. Requires approval token.")]
    public async Task<string> IssueSync(
        [Description("Issue number.")] int issueNumber,
        [Description("Action: comment|close|reopen|label")] string action,
        [Description("Comment body when action=comment; optional close note for action=close.")] string? comment = null,
        [Description("Comma-separated labels when action=label.")] string? labelsCsv = null,
        [Description("Approval token for mutate policy.")] string? approvalToken = null,
        [Description("Working directory for git/gh commands (optional).")] string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var decision = preflight.Evaluate("issue_sync", "mutate", approvalToken);
        if (!decision.Allowed)
        {
            return JsonSerializer.Serialize(new
            {
                error = "policy_denied",
                decision.Category,
                decision.RequiresApproval,
                decision.Reason
            }, JsonOptions);
        }

        if (issueNumber <= 0)
        {
            return JsonSerializer.Serialize(new { error = "issueNumber must be > 0" }, JsonOptions);
        }

        var gitDir = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory;
        var normalizedAction = action.Trim().ToLowerInvariant();
        string commandArgs;

        switch (normalizedAction)
        {
            case "comment":
                if (string.IsNullOrWhiteSpace(comment))
                {
                    return JsonSerializer.Serialize(new { error = "comment is required when action=comment" }, JsonOptions);
                }

                commandArgs = $"issue comment {issueNumber} --body {EscapeArg(comment)}";
                break;

            case "close":
                commandArgs = string.IsNullOrWhiteSpace(comment)
                    ? $"issue close {issueNumber}"
                    : $"issue close {issueNumber} --comment {EscapeArg(comment)}";
                break;

            case "reopen":
                commandArgs = $"issue reopen {issueNumber}";
                break;

            case "label":
                if (string.IsNullOrWhiteSpace(labelsCsv))
                {
                    return JsonSerializer.Serialize(new { error = "labelsCsv is required when action=label" }, JsonOptions);
                }

                commandArgs = $"issue edit {issueNumber} --add-label {EscapeArg(labelsCsv)}";
                break;

            default:
                return JsonSerializer.Serialize(new
                {
                    error = "Invalid action",
                    allowed = new[] { "comment", "close", "reopen", "label" }
                }, JsonOptions);
        }

        var result = await RunCommandAsync(gitDir, "gh", commandArgs, commandAllowlist, metrics, ct);
        return result.Success
            ? JsonSerializer.Serialize(new
            {
                status = "ok",
                issueNumber,
                action = normalizedAction,
                output = Truncate(result.StdOut.Trim(), 1000)
            }, JsonOptions)
            : JsonSerializer.Serialize(new
            {
                status = "error",
                issueNumber,
                action = normalizedAction,
                stderr = Truncate(result.StdErr, 2000),
                hint = BuildGhHint(result.StdErr)
            }, JsonOptions);
    }

    private static async Task<(List<string> changedFiles, string diffOutput)> GetGitDiffAsync(
        string gitDir,
        string baseBranch,
        CommandAllowlistService commandAllowlist,
        PlatformMetrics metrics,
        CancellationToken ct)
    {
        var filesResult = await RunGitCommandAsync(gitDir, $"diff {baseBranch}...HEAD --name-only", commandAllowlist, metrics, ct);
        var diffResult = await RunGitCommandAsync(gitDir, $"diff {baseBranch}...HEAD", commandAllowlist, metrics, ct);

        var files = filesResult
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrEmpty(f))
            .ToList();

        return (files, diffResult);
    }

    private static async Task<string> GetCommitLogAsync(
        string gitDir,
        string baseBranch,
        CommandAllowlistService commandAllowlist,
        PlatformMetrics metrics,
        CancellationToken ct)
    {
        return await RunGitCommandAsync(gitDir, $"log {baseBranch}...HEAD --oneline", commandAllowlist, metrics, ct);
    }

    private static async Task<string> RunGitCommandAsync(
        string workingDir,
        string args,
        CommandAllowlistService commandAllowlist,
        PlatformMetrics metrics,
        CancellationToken ct)
    {
        var result = await RunCommandAsync(workingDir, "git", args, commandAllowlist, metrics, ct);
        return result.Success ? result.StdOut : $"{result.StdOut}\n{result.StdErr}";
    }

    private static async Task<CommandResult> RunCommandAsync(
        string workingDir,
        string command,
        string args,
        CommandAllowlistService commandAllowlist,
        PlatformMetrics metrics,
        CancellationToken ct)
    {
        var decision = commandAllowlist.Evaluate("pr_tools", command);
        if (!decision.Allowed)
        {
            metrics.RecordExecuteDeny();
            return new CommandResult(false, -1, string.Empty, decision.Reason);
        }

        metrics.RecordExecuteAllow();
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            return new CommandResult(false, -1, string.Empty, "Failed to start process.");
        }

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new CommandResult(process.ExitCode == 0, process.ExitCode, output, error);
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

    private static string EscapeArg(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...(truncated)";

    private static bool IsSafeRef(string value)
        => Regex.IsMatch(value, @"^[A-Za-z0-9._/\-]+$");

    private static string BuildGhHint(string stderr)
    {
        if (stderr.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
            return "Run 'gh auth login' and retry.";
        if (stderr.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "Verify the PR/issue number and repository context.";
        if (stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            return "Set workingDirectory to a valid git repository path.";
        return "Review gh stderr and retry with corrected arguments.";
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

    private sealed record CommandResult(bool Success, int ExitCode, string StdOut, string StdErr);
}