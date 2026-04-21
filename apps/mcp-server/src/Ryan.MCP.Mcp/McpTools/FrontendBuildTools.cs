using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class FrontendBuildTools(ILogger<FrontendBuildTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "fix_frontend_build")]
    [Description(
        "Run a frontend build (npm/pnpm/yarn/bun), iterate until success, " +
        "and explain the root cause + minimal fix. Use when a frontend build is failing.")]
    public async Task<string> FixFrontendBuild(
        [Description("Working directory (defaults to current)")] string? workingDirectory = null,
        [Description("Override build script name (defaults to auto-detect from package.json scripts: build, build:prod)")] string? buildScript = null,
        [Description("Maximum attempts before stopping")] int maxAttempts = 5,
        CancellationToken cancellationToken = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "FrontendBuildTools.FixFrontendBuild",
            ["WorkingDirectory"] = workingDirectory,
            ["BuildScript"] = buildScript,
            ["MaxAttempts"] = maxAttempts,
        });
        logger.LogDebug("FixFrontendBuild invoked");

        var workDir = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory;

        if (!File.Exists(Path.Combine(workDir, "package.json")))
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                message = "No package.json found in working directory"
            }, JsonOptions);
        }

        var pm = DetectPackageManager(workDir);
        var script = buildScript ?? DetectBuildScript(workDir);

        // Sanitize script name to prevent command injection — only allow safe npm script characters
        if (!System.Text.RegularExpressions.Regex.IsMatch(script, @"^[a-zA-Z0-9:_-]+$"))
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                message = "Invalid build script name. Only alphanumeric characters, colons, underscores, and hyphens are allowed."
            }, JsonOptions);
        }

        var attempts = new List<BuildAttempt>();
        var attempt = 1;

        while (attempt <= maxAttempts)
        {
            attempts.Add(new BuildAttempt { Number = attempt, StartTime = DateTime.UtcNow });

            var runArgs = pm.RunPrefix + script;
            var (success, output, error) = await RunCommandAsync(workDir, pm.Command, runArgs, cancellationToken);

            var combined = output + "\n" + error;
            attempts[attempt - 1].Success = success;
            attempts[attempt - 1].Output = Truncate(output, 5000);
            attempts[attempt - 1].Error = Truncate(error, 5000);

            if (success)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "success",
                    packageManager = pm.Name,
                    buildScript = script,
                    attempts = attempts.Count,
                    rootCause = AnalyzeRootCause(attempts),
                    buildOutput = Truncate(output, 3000)
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
            packageManager = pm.Name,
            buildScript = script,
            attempts = attempts.Count,
            finalError = attempts[^1].Error,
            rootCause = finalRootCause,
            suggestions = GenerateSuggestions(attempts, pm.Name)
        }, JsonOptions);
    }

    // ── Package manager detection ──────────────────────────────────────

    private static PackageManagerInfo DetectPackageManager(string workDir)
    {
        if (File.Exists(Path.Combine(workDir, "pnpm-lock.yaml")))
            return new PackageManagerInfo("pnpm", "pnpm", "run ");

        if (File.Exists(Path.Combine(workDir, "yarn.lock")))
            return new PackageManagerInfo("yarn", "yarn", "run ");

        if (File.Exists(Path.Combine(workDir, "bun.lockb")) || File.Exists(Path.Combine(workDir, "bun.lock")))
            return new PackageManagerInfo("bun", "bun", "run ");

        return new PackageManagerInfo("npm", "npm", "run ");
    }

    // ── Build script detection ─────────────────────────────────────────

    private static string DetectBuildScript(string workDir)
    {
        try
        {
            var packageJson = File.ReadAllText(Path.Combine(workDir, "package.json"));
            using var doc = JsonDocument.Parse(packageJson);

            if (doc.RootElement.TryGetProperty("scripts", out var scripts))
            {
                // Prefer "build" > "build:prod" > "build:production" > "tsc"
                string[] candidates = ["build", "build:prod", "build:production", "compile", "tsc"];
                foreach (var candidate in candidates)
                {
                    if (scripts.TryGetProperty(candidate, out _))
                        return candidate;
                }
            }
        }
        catch
        {
            // Fall through to default
        }

        return "build";
    }

    // ── Root cause analysis ────────────────────────────────────────────

    private static string AnalyzeRootCause(List<BuildAttempt> attempts)
    {
        var lastError = attempts[^1].Error + "\n" + attempts[^1].Output;

        // TypeScript errors
        if (lastError.Contains("error TS"))
        {
            var tsMatch = System.Text.RegularExpressions.Regex.Match(lastError, @"error (TS\d+): (.+)");
            if (tsMatch.Success)
                return $"TypeScript compilation error ({tsMatch.Groups[1].Value}): {tsMatch.Groups[2].Value}";
            return "TypeScript compilation errors — see output for details";
        }

        // ESLint
        if (lastError.Contains("eslint") || lastError.Contains("Lint errors"))
            return "ESLint errors are blocking the build";

        // Module not found
        if (lastError.Contains("Module not found") || lastError.Contains("Cannot find module"))
        {
            var modMatch = System.Text.RegularExpressions.Regex.Match(lastError, @"(?:Module not found|Cannot find module)\s*[':]\s*(.+?)['\""]");
            if (modMatch.Success)
                return $"Missing module: {modMatch.Groups[1].Value} — run install or check import path";
            return "Missing module — run install or check import paths";
        }

        // Out of memory
        if (lastError.Contains("FATAL ERROR") && lastError.Contains("heap"))
            return "Node.js ran out of memory — increase with NODE_OPTIONS=--max-old-space-size=8192";

        // Vite / Rollup
        if (lastError.Contains("RollupError") || lastError.Contains("[vite]"))
            return "Vite/Rollup bundling error — check import/export syntax and plugin configuration";

        // Angular
        if (lastError.Contains("ng build") || lastError.Contains("@angular"))
            return "Angular build error — check component templates, imports, and module declarations";

        // Webpack
        if (lastError.Contains("webpack") || lastError.Contains("Module build failed"))
            return "Webpack build error — check loaders and module rules";

        // Syntax error
        if (lastError.Contains("SyntaxError"))
            return "JavaScript/TypeScript syntax error — check for invalid syntax";

        // Generic ENOENT
        if (lastError.Contains("ENOENT"))
            return "File or directory not found — check file paths and ensure dependencies are installed";

        // node_modules missing
        if (lastError.Contains("node_modules") && lastError.Contains("not found"))
            return "Dependencies not installed — run install first";

        if (attempts.Count == 1 && attempts[0].Success)
            return "Build succeeded on first attempt — no issues found";

        return "Build failed — see error details in output";
    }

    // ── Suggestions ────────────────────────────────────────────────────

    private static List<string> GenerateSuggestions(List<BuildAttempt> attempts, string pm)
    {
        var lastError = attempts[^1].Error + "\n" + attempts[^1].Output;
        var suggestions = new List<string>();

        if (lastError.Contains("Module not found") || lastError.Contains("Cannot find module") ||
            lastError.Contains("ENOENT") || lastError.Contains("node_modules"))
        {
            suggestions.Add($"Run '{pm} install' to restore dependencies");
        }

        if (lastError.Contains("error TS"))
        {
            suggestions.Add("Fix TypeScript errors shown in the output");
            suggestions.Add($"Run '{pm} run tsc -- --noEmit' for a type-check-only pass");
        }

        if (lastError.Contains("eslint"))
        {
            suggestions.Add($"Run '{pm} run lint -- --fix' to auto-fix lint errors");
        }

        if (lastError.Contains("heap") || lastError.Contains("FATAL ERROR"))
        {
            suggestions.Add("Increase Node memory: set NODE_OPTIONS=--max-old-space-size=8192");
        }

        suggestions.Add("Check the build output above for specific file:line references");
        suggestions.Add($"Try cleaning: remove node_modules and {pm} install");

        return suggestions;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>On Windows, node-based CLIs (npm, pnpm, yarn, bun) are .cmd shims.</summary>
    private static string ResolveCommand(string command)
    {
        if (!OperatingSystem.IsWindows()) return command;

        if (command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return command;

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [];
        foreach (var dir in pathDirs)
        {
            var cmdPath = Path.Combine(dir, command + ".cmd");
            if (File.Exists(cmdPath)) return cmdPath;
        }

        return command + ".cmd";
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...(truncated)";

    private static async Task<(bool success, string output, string error)> RunCommandAsync(
        string workingDir, string fileName, string args, CancellationToken ct)
    {
        var resolvedFileName = ResolveCommand(fileName);
        var psi = new ProcessStartInfo
        {
            FileName = resolvedFileName,
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

    // ── Inner models ───────────────────────────────────────────────────

    private sealed record PackageManagerInfo(string Name, string Command, string RunPrefix);

    private sealed class BuildAttempt
    {
        public int Number { get; set; }
        public DateTime StartTime { get; set; }
        public bool Success { get; set; }
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
    }
}
