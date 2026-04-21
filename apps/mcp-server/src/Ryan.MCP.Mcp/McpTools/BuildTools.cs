using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class BuildTools(ILogger<BuildTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "fix_build")]
    [Description("Runs dotnet build, iterates until successful, and explains the root cause + minimal fix. Use when build is failing.")]
    public async Task<string> FixBuild(
        [Description("Working directory (defaults to current)")] string? workingDirectory = null,
        [Description("Build arguments (e.g., '--no-restore' or '-c Release')")] string? arguments = null,
        [Description("Maximum attempts before stopping")] int maxAttempts = 5,
        CancellationToken cancellationToken = default)
    {
        var workDir = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory;
        var buildArgs = string.IsNullOrWhiteSpace(arguments) ? "build" : $"build {arguments}";

        using (logger.BeginScope(new Dictionary<string, object?> { ["ToolName"] = "BuildTools.FixBuild", ["WorkingDirectory"] = workDir }))
        using (logger.BeginScope(new Dictionary<string, object?> { ["MaxAttempts"] = maxAttempts, ["InvocationId"] = Guid.NewGuid().ToString("N")[..8] }))
        {
            logger.LogDebug("FixBuild invoked for workDir={WorkDir}", workDir);

            var attempts = new List<BuildAttempt>();
            var attempt = 1;

            while (attempt <= maxAttempts)
            {
                attempts.Add(new BuildAttempt { Number = attempt, StartTime = DateTime.UtcNow });

                var (success, output, error) = await RunDotnetCommandAsync(workDir, buildArgs, cancellationToken);
                attempts[attempt - 1].Success = success;
                attempts[attempt - 1].Output = output.Length > 5000 ? output[..5000] : output;
                attempts[attempt - 1].Error = error.Length > 5000 ? error[..5000] : error;

                if (success)
                {
                    var rootCause = AnalyzeRootCause(attempts);
                    var minimalFix = DetermineMinimalFix(attempts, rootCause);

                    return JsonSerializer.Serialize(new
                    {
                        status = "success",
                        attempts = attempts.Count,
                        rootCause,
                        minimalFix,
                        buildOutput = output.Length > 3000 ? output[..3000] : output,
                    }, JsonOptions);
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(500, cancellationToken);
                }
                attempt++;
            }

            var finalRootCause = AnalyzeRootCause(attempts);

            return JsonSerializer.Serialize(new
            {
                status = "failed",
                attempts = attempts.Count,
                finalError = attempts.Last().Error,
                rootCause = finalRootCause,
                suggestions = new[]
                {
                    "Check for missing dependencies: dotnet restore",
                    "Check for compilation errors in output above",
                    "Check for version mismatches in .csproj files",
                    "Try cleaning: dotnet clean",
                }
            }, JsonOptions);
        }
    }

    private static async Task<(bool success, string output, string error)> RunDotnetCommandAsync(
        string workingDir, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return (false, "", "Failed to start process");

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode == 0, output, error);
    }

    private static string AnalyzeRootCause(List<BuildAttempt> attempts)
    {
        var lastError = attempts.LastOrDefault()?.Error ?? "";

        if (lastError.Contains("CS0000") || lastError.Contains("error CS"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(lastError, @"error CS(\d+): (.+)");
            if (match.Success)
            {
                var errorCode = match.Groups[1].Value;
                var message = match.Groups[2].Value;

                return errorCode switch
                {
                    "0244" => $"Missing or conflicting package reference: {message}",
                    "0263" => $"Circular dependency detected: {message}",
                    "0245" => $"Top-level statements conflict with other definitions: {message}",
                    _ => $"Compilation error (CS{errorCode}): {message}"
                };
            }
        }

        if (lastError.Contains("NU") || lastError.Contains("restore"))
            return "NuGet package restore failed - check package sources and versions";

        if (lastError.Contains("Could not find") || lastError.Contains("does not exist"))
            return "Missing file or reference - check project structure";

        if (lastError.Contains("Version conflict") || lastError.Contains("more than one"))
            return "Version conflict between dependencies";

        return "Build failed - see error details above";
    }

    private static string DetermineMinimalFix(List<BuildAttempt> attempts, string rootCause)
    {
        var lastError = attempts.LastOrDefault()?.Error ?? "";

        if (rootCause.Contains("NuGet package restore"))
            return "Run 'dotnet restore' to fetch missing packages";

        if (rootCause.Contains("circular"))
            return "Refactor to remove circular dependencies between projects";

        if (rootCause.Contains("missing file"))
            return "Add missing file or restore deleted file from source control";

        if (rootCause.Contains("Version conflict"))
            return "Update package versions in .csproj to resolve conflict";

        if (rootCause.Contains("CS0244") || rootCause.Contains("NU"))
            return "Check .csproj for invalid or corrupted package references";

        return "Address the specific compilation error in the build output";
    }

    private sealed class BuildAttempt
    {
        public int Number { get; set; }
        public DateTime StartTime { get; set; }
        public bool Success { get; set; }
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
    }
}