using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class NuGetTools(ILogger<NuGetTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "nuget_hygiene")]
    [Description("Check for vulnerable NuGet dependencies, propose updates, and warn about breaking changes. Use to keep dependencies healthy.")]
    public async Task<string> NugetHygiene(
        [Description("Working directory (defaults to current)")] string? workingDirectory = null,
        [Description("Check for vulnerabilities only (skip outdated check)")] bool vulnerabilitiesOnly = false,
        [Description("Include preview versions in updates")] bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        var workDir = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory;

        using (logger.BeginScope(new Dictionary<string, object?> { ["ToolName"] = "NuGetTools.NugetHygiene", ["WorkingDirectory"] = workDir }))
        {
            logger.LogDebug("NugetHygiene invoked for workDir={WorkDir}", workDir);

            var results = new NuGetHygieneResult();

            try
            {
                var (hasOutdated, outdatedOutput) = await CheckOutdatedAsync(workDir, includePrerelease, cancellationToken);
                results.HasOutdated = hasOutdated;
                results.OutdatedPackages = ParseOutdatedPackages(outdatedOutput);

                if (!vulnerabilitiesOnly)
                {
                    var (hasVulns, vulnOutput) = await CheckVulnerabilitiesAsync(workDir, cancellationToken);
                    results.HasVulnerabilities = hasVulns;
                    results.Vulnerabilities = ParseVulnerabilities(vulnOutput);
                }

                results.BreakingChanges = await CheckBreakingChangesAsync(workDir, results.OutdatedPackages, cancellationToken);
                results.Recommendations = GenerateRecommendations(results);
                results.ProjectFiles = await FindProjectFilesAsync(workDir, cancellationToken);
            }
            catch (Exception ex)
            {
                results.Error = ex.Message;
            }

            return JsonSerializer.Serialize(results, JsonOptions);
        }
    }

    private static async Task<(bool, string)> CheckOutdatedAsync(string workDir, bool includePrerelease, CancellationToken ct)
    {
        var args = includePrerelease ? " outdated --include-prerelease" : " outdated";
        var (success, output, error) = await RunDotnetCommandAsync(workDir, $"list {args}", ct);

        if (output.Contains("The following packages are outdated"))
            return (true, output);

        if (error.Contains("The following packages are outdated"))
            return (true, error);

        return (false, output);
    }

    private static async Task<(bool, string)> CheckVulnerabilitiesAsync(string workDir, CancellationToken ct)
    {
        var (success, output, error) = await RunDotnetCommandAsync(workDir, "list package --vulnerable", ct);

        var hasVulns = output.Contains("Vulnerable") ||
                       output.Contains("Has known vulnerabilities") ||
                       error.Contains("Vulnerable") ||
                       error.Contains("vulnerable");

        return (hasVulns, output + "\n" + error);
    }

    private static async Task<List<BreakingChangeWarning>> CheckBreakingChangesAsync(
        string workDir, List<OutdatedPackage> packages, CancellationToken ct)
    {
        var warnings = new List<BreakingChangeWarning>();

        var majorUpdates = packages.Where(p => p.HasMajorUpdate).ToList();
        foreach (var pkg in majorUpdates)
        {
            warnings.Add(new BreakingChangeWarning
            {
                Package = pkg.Name,
                CurrentVersion = pkg.CurrentVersion,
                LatestVersion = pkg.LatestVersion,
                Severity = "High",
                Message = $"Major version bump from {pkg.CurrentVersion} to {pkg.LatestVersion} - likely contains breaking changes"
            });
        }

        return warnings;
    }

    private static List<OutdatedPackage> ParseOutdatedPackages(string output)
    {
        var packages = new List<OutdatedPackage>();
        var lines = output.Split('\n');

        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"(\S+)\s+(\S+)\s+(\S+)");
            if (match.Success)
            {
                var name = match.Groups[1].Value.Trim();
                var current = match.Groups[2].Value.Trim();
                var latest = match.Groups[3].Value.Trim();

                if (name != "Package" && current != "Version" && !string.IsNullOrWhiteSpace(name))
                {
                    packages.Add(new OutdatedPackage
                    {
                        Name = name,
                        CurrentVersion = current,
                        LatestVersion = latest,
                        HasMajorUpdate = latest.StartsWith(current.Split('.')[0] + ".") == false
                    });
                }
            }
        }

        return packages;
    }

    private static List<Vulnerability> ParseVulnerabilities(string output)
    {
        var vulns = new List<Vulnerability>();
        var lines = output.Split('\n');

        foreach (var line in lines)
        {
            if (line.Contains("Vulnerable") || line.Contains("vulnerable"))
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    vulns.Add(new Vulnerability
                    {
                        Package = parts[0],
                        AdvisoryUrl = parts.Length > 1 ? parts[1] : "Unknown"
                    });
                }
            }
        }

        return vulns;
    }

    private static List<string> GenerateRecommendations(NuGetHygieneResult r)
    {
        var recs = new List<string>();

        if (r.HasVulnerabilities)
        {
            recs.Add("URGENT: Update packages with known vulnerabilities immediately");
        }

        if (r.BreakingChanges.Count > 0)
        {
            recs.Add($"WARNING: {r.BreakingChanges.Count} major version updates may contain breaking changes - review changelogs");
        }

        if (r.HasOutdated && r.OutdatedPackages.Count > 0)
        {
            var minor = r.OutdatedPackages.Count(p => !p.HasMajorUpdate);
            if (minor > 0)
                recs.Add($"Safe to update: {minor} packages with minor/patch updates");
        }

        if (!r.HasVulnerabilities && !r.HasOutdated)
        {
            recs.Add("All dependencies are up to date and secure");
        }

        if (r.ProjectFiles.Count > 0)
        {
            recs.Add($"Found {r.ProjectFiles.Count} project files: {string.Join(", ", r.ProjectFiles.Select(Path.GetFileName))}");
        }

        return recs;
    }

    private static async Task<List<string>> FindProjectFilesAsync(string workDir, CancellationToken ct)
    {
        var (success, output, _) = await RunDotnetCommandAsync(workDir, "list reference", ct);
        if (!success) return [];

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Trim().EndsWith(".csproj"))
            .Select(l => l.Trim())
            .ToList();
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

    private sealed class NuGetHygieneResult
    {
        public bool HasOutdated { get; set; }
        public bool HasVulnerabilities { get; set; }
        public List<OutdatedPackage> OutdatedPackages { get; set; } = [];
        public List<Vulnerability> Vulnerabilities { get; set; } = [];
        public List<BreakingChangeWarning> BreakingChanges { get; set; } = [];
        public List<string> Recommendations { get; set; } = [];
        public List<string> ProjectFiles { get; set; } = [];
        public string? Error { get; set; }
    }

    private sealed class OutdatedPackage
    {
        public string Name { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public bool HasMajorUpdate { get; set; }
    }

    private sealed class Vulnerability
    {
        public string Package { get; set; } = "";
        public string AdvisoryUrl { get; set; } = "";
    }

    private sealed class BreakingChangeWarning
    {
        public string Package { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Message { get; set; } = "";
    }
}