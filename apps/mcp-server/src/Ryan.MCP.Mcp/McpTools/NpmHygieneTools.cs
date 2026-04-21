using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class NpmHygieneTools(ILogger<NpmHygieneTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "npm_hygiene")]
    [Description(
        "Check for vulnerable and outdated npm/pnpm/yarn/bun packages. " +
        "Auto-detects package manager from lock files. Use to keep frontend dependencies healthy.")]
    public async Task<string> NpmHygiene(
        [Description("Working directory (defaults to current)")] string? workingDirectory = null,
        [Description("Check for vulnerabilities only (skip outdated check)")] bool vulnerabilitiesOnly = false,
        CancellationToken cancellationToken = default)
    {
        var workDir = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory;
        var result = new HygieneResult();

        using (logger.BeginScope(new Dictionary<string, object?> { ["ToolName"] = "NpmHygieneTools.NpmHygiene", ["WorkingDirectory"] = workDir }))
        {
            logger.LogDebug("NpmHygiene invoked for workDir={WorkDir}", workDir);

            try
            {
                var pm = DetectPackageManager(workDir);
                result.PackageManager = pm.Name;

                if (!File.Exists(Path.Combine(workDir, "package.json")))
                {
                    result.Error = "No package.json found in working directory";
                    return JsonSerializer.Serialize(result, JsonOptions);
                }

                var (auditSuccess, auditOutput, auditError) =
                    await RunCommandAsync(workDir, pm.Command, pm.AuditArgs, cancellationToken);
                result.Vulnerabilities = ParseAuditOutput(auditOutput + "\n" + auditError, pm.Name);
                result.HasVulnerabilities = result.Vulnerabilities.Count > 0 || !auditSuccess;

                if (!auditSuccess && result.Vulnerabilities.Count == 0)
                {
                    result.AuditSummary = Truncate(auditOutput + "\n" + auditError, 3000);
                }

                if (!vulnerabilitiesOnly)
                {
                    var (_, outdatedOutput, outdatedError) =
                        await RunCommandAsync(workDir, pm.Command, pm.OutdatedArgs, cancellationToken);
                    result.OutdatedPackages = ParseOutdatedOutput(outdatedOutput, pm.Name);
                    result.HasOutdated = result.OutdatedPackages.Count > 0;
                }

                result.Recommendations = GenerateRecommendations(result);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return JsonSerializer.Serialize(result, JsonOptions);
        }
    }

    // ── Package manager detection ──────────────────────────────────────

    private static PackageManagerInfo DetectPackageManager(string workDir)
    {
        if (File.Exists(Path.Combine(workDir, "pnpm-lock.yaml")))
            return new PackageManagerInfo("pnpm", "pnpm", "audit --json", "outdated --format json");

        if (File.Exists(Path.Combine(workDir, "yarn.lock")))
            return new PackageManagerInfo("yarn", "yarn", "audit --json", "outdated --json");

        if (File.Exists(Path.Combine(workDir, "bun.lockb")) || File.Exists(Path.Combine(workDir, "bun.lock")))
            return new PackageManagerInfo("bun", "bun", "audit", "outdated");

        // Default: npm
        return new PackageManagerInfo("npm", "npm", "audit --json", "outdated --json");
    }

    // ── Audit parsing ──────────────────────────────────────────────────

    private static List<VulnerabilityInfo> ParseAuditOutput(string raw, string pm)
    {
        var vulns = new List<VulnerabilityInfo>();

        try
        {
            if (pm is "npm" or "pnpm")
            {
                using var doc = JsonDocument.Parse(ExtractJson(raw));

                // npm v7+ format: { "vulnerabilities": { "pkg": { ... } } }
                if (doc.RootElement.TryGetProperty("vulnerabilities", out var vulnMap))
                {
                    foreach (var prop in vulnMap.EnumerateObject())
                    {
                        var severity = prop.Value.TryGetProperty("severity", out var s) ? s.GetString() ?? "unknown" : "unknown";
                        var fixAvailable = prop.Value.TryGetProperty("fixAvailable", out var f) && f.ValueKind != JsonValueKind.False;
                        vulns.Add(new VulnerabilityInfo
                        {
                            Package = prop.Name,
                            Severity = severity,
                            FixAvailable = fixAvailable
                        });
                    }
                }
                // npm v6 format: { "advisories": { ... } }
                else if (doc.RootElement.TryGetProperty("advisories", out var advisories))
                {
                    foreach (var prop in advisories.EnumerateObject())
                    {
                        var severity = prop.Value.TryGetProperty("severity", out var s) ? s.GetString() ?? "unknown" : "unknown";
                        var moduleName = prop.Value.TryGetProperty("module_name", out var m) ? m.GetString() ?? prop.Name : prop.Name;
                        vulns.Add(new VulnerabilityInfo
                        {
                            Package = moduleName,
                            Severity = severity,
                            FixAvailable = false
                        });
                    }
                }
            }
            else if (pm is "yarn")
            {
                // yarn audit --json outputs newline-delimited JSON objects
                foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        using var lineDoc = JsonDocument.Parse(line.Trim());
                        if (lineDoc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "auditAdvisory")
                        {
                            var advisory = lineDoc.RootElement.GetProperty("data").GetProperty("advisory");
                            var pkgName = advisory.GetProperty("module_name").GetString() ?? "unknown";
                            var severity = advisory.GetProperty("severity").GetString() ?? "unknown";
                            vulns.Add(new VulnerabilityInfo { Package = pkgName, Severity = severity });
                        }
                    }
                    catch
                    {
                        // skip non-JSON lines
                    }
                }
            }
        }
        catch
        {
            // If JSON parsing fails, fall back to text scanning
            if (raw.Contains("critical", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("high", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("moderate", StringComparison.OrdinalIgnoreCase))
            {
                vulns.Add(new VulnerabilityInfo
                {
                    Package = "(see raw audit output)",
                    Severity = "unknown",
                    RawSnippet = Truncate(raw, 1000)
                });
            }
        }

        return vulns;
    }

    // ── Outdated parsing ───────────────────────────────────────────────

    private static List<OutdatedPackage> ParseOutdatedOutput(string raw, string pm)
    {
        var packages = new List<OutdatedPackage>();

        try
        {
            if (pm is "npm" or "pnpm")
            {
                // npm outdated --json => { "pkg": { "current": "x", "wanted": "y", "latest": "z" } }
                using var doc = JsonDocument.Parse(ExtractJson(raw));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var current = prop.Value.TryGetProperty("current", out var c) ? c.GetString() ?? "" : "";
                    var wanted = prop.Value.TryGetProperty("wanted", out var w) ? w.GetString() ?? "" : "";
                    var latest = prop.Value.TryGetProperty("latest", out var l) ? l.GetString() ?? "" : "";

                    packages.Add(new OutdatedPackage
                    {
                        Name = prop.Name,
                        Current = current,
                        Wanted = wanted,
                        Latest = latest,
                        HasMajorUpdate = IsMajorBump(current, latest)
                    });
                }
            }
            else if (pm is "yarn")
            {
                // yarn outdated --json outputs newline-delimited JSON
                foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        using var lineDoc = JsonDocument.Parse(line.Trim());
                        if (lineDoc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "table")
                        {
                            var body = lineDoc.RootElement.GetProperty("data").GetProperty("body");
                            foreach (var row in body.EnumerateArray())
                            {
                                var items = row.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
                                if (items.Length >= 4)
                                {
                                    packages.Add(new OutdatedPackage
                                    {
                                        Name = items[0],
                                        Current = items[1],
                                        Wanted = items[2],
                                        Latest = items[3],
                                        HasMajorUpdate = IsMajorBump(items[1], items[3])
                                    });
                                }
                            }
                        }
                    }
                    catch
                    {
                        // skip non-JSON lines
                    }
                }
            }
        }
        catch
        {
            // If JSON parsing fails completely, return empty — the raw summary is still available
        }

        return packages;
    }

    // ── Recommendations ────────────────────────────────────────────────

    private static List<string> GenerateRecommendations(HygieneResult r)
    {
        var recs = new List<string>();

        var criticalCount = r.Vulnerabilities.Count(v =>
            v.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase));
        var highCount = r.Vulnerabilities.Count(v =>
            v.Severity.Equals("high", StringComparison.OrdinalIgnoreCase));
        var fixableCount = r.Vulnerabilities.Count(v => v.FixAvailable);

        if (criticalCount > 0)
            recs.Add($"CRITICAL: {criticalCount} critical vulnerabilities — update immediately");
        if (highCount > 0)
            recs.Add($"HIGH: {highCount} high-severity vulnerabilities — update as soon as possible");
        if (fixableCount > 0)
            recs.Add($"Run '{r.PackageManager} audit fix' to auto-fix {fixableCount} vulnerabilities");

        var majorUpdates = r.OutdatedPackages.Where(p => p.HasMajorUpdate).ToList();
        if (majorUpdates.Count > 0)
            recs.Add($"WARNING: {majorUpdates.Count} packages have major version updates — review changelogs for breaking changes");

        var safeUpdates = r.OutdatedPackages.Count(p => !p.HasMajorUpdate);
        if (safeUpdates > 0)
            recs.Add($"Safe to update: {safeUpdates} packages with minor/patch updates");

        if (!r.HasVulnerabilities && !r.HasOutdated)
            recs.Add("All dependencies are up to date and secure");

        return recs;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static bool IsMajorBump(string current, string latest)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(latest))
            return false;

        var curMajor = current.TrimStart('v', '^', '~').Split('.').FirstOrDefault() ?? "";
        var latMajor = latest.TrimStart('v', '^', '~').Split('.').FirstOrDefault() ?? "";
        return curMajor != latMajor;
    }

    private static string ExtractJson(string raw)
    {
        // Find first '{' or '[' and last matching '}' or ']'
        var start = raw.IndexOfAny(['{', '[']);
        if (start < 0) return "{}";

        var openChar = raw[start];
        var closeChar = openChar == '{' ? '}' : ']';
        var last = raw.LastIndexOf(closeChar);
        if (last <= start) return "{}";

        return raw[start..(last + 1)];
    }

    /// <summary>On Windows, node-based CLIs (npm, pnpm, yarn, bun) are .cmd shims.</summary>
    private static string ResolveCommand(string command)
    {
        if (!OperatingSystem.IsWindows()) return command;

        // Already has extension
        if (command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return command;

        // Try .cmd first (npm.cmd, pnpm.cmd, yarn.cmd), then .exe
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [];
        foreach (var dir in pathDirs)
        {
            var cmdPath = Path.Combine(dir, command + ".cmd");
            if (File.Exists(cmdPath)) return cmdPath;
        }

        // Fallback: just append .cmd and let the OS resolve
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

    private sealed record PackageManagerInfo(string Name, string Command, string AuditArgs, string OutdatedArgs);

    private sealed class HygieneResult
    {
        public string PackageManager { get; set; } = "";
        public bool HasVulnerabilities { get; set; }
        public bool HasOutdated { get; set; }
        public List<VulnerabilityInfo> Vulnerabilities { get; set; } = [];
        public List<OutdatedPackage> OutdatedPackages { get; set; } = [];
        public List<string> Recommendations { get; set; } = [];
        public string? AuditSummary { get; set; }
        public string? Error { get; set; }
    }

    private sealed class VulnerabilityInfo
    {
        public string Package { get; set; } = "";
        public string Severity { get; set; } = "";
        public bool FixAvailable { get; set; }
        public string? RawSnippet { get; set; }
    }

    private sealed class OutdatedPackage
    {
        public string Name { get; set; } = "";
        public string Current { get; set; } = "";
        public string Wanted { get; set; } = "";
        public string Latest { get; set; } = "";
        public bool HasMajorUpdate { get; set; }
    }
}
