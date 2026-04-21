using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed partial class LogForensicsTools(ILogger<LogForensicsTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly string[] ErrorTokens =
    [
        " exception",
        " unhandled",
        " failed",
        " error",
        " fatal",
        " critical",
        " timeout",
        " refused",
    ];

    [GeneratedRegex(@"\b(trace[_-]?id|request[_-]?id)\b[:=\s]+([A-Za-z0-9\-\._]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CorrelationRegex();

    [McpServerTool(Name = "analyze_runtime_logs")]
    [Description("Analyze exported log files and return top error patterns with suspected root-cause clues.")]
    public string AnalyzeRuntimeLogs(
        [Description("Absolute or relative path to a log file.")] string path,
        [Description("Optional limit for lines read from file tail. Default 4000, max 20000.")] int maxLines = 4000)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "LogForensicsTools.AnalyzeRuntimeLogs",
            ["Path"] = path,
            ["MaxLines"] = maxLines,
        });
        logger.LogDebug("AnalyzeRuntimeLogs invoked");

        if (string.IsNullOrWhiteSpace(path))
        {
            return Error("invalid_request", "path is required");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return Error("not_found", $"Log file not found: {fullPath}");
        }

        maxLines = Math.Clamp(maxLines, 100, 20000);
        var lines = File.ReadLines(fullPath).TakeLast(maxLines).ToList();
        if (lines.Count == 0)
        {
            return JsonSerializer.Serialize(new { status = "empty", file = fullPath, lines = 0 }, JsonOptions);
        }

        var total = lines.Count;
        var errorLines = lines.Where(IsErrorLike).ToList();
        var bySignature = errorLines
            .GroupBy(SignatureOf, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new { signature = g.Key, count = g.Count(), sample = g.FirstOrDefault() ?? string.Empty })
            .ToList();

        var correlationHits = lines
            .SelectMany(ExtractCorrelations)
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new { id = g.Key, occurrences = g.Count() })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            file = fullPath,
            scannedLines = total,
            errorLines = errorLines.Count,
            errorRate = Math.Round(errorLines.Count * 100.0 / total, 2),
            topErrorPatterns = bySignature,
            topCorrelationIds = correlationHits,
            hint = "Use incident_timeline with start/end around first and last error timestamps for a causal sequence.",
        }, JsonOptions);
    }

    [McpServerTool(Name = "summarize_errors")]
    [Description("Summarize errors from an exported log file grouped by signature or level.")]
    public string SummarizeErrors(
        [Description("Absolute or relative path to a log file.")] string path,
        [Description("Grouping mode: signature or level.")] string groupBy = "signature")
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "LogForensicsTools.SummarizeErrors",
            ["Path"] = path,
            ["GroupBy"] = groupBy,
        });
        logger.LogDebug("SummarizeErrors invoked");

        if (string.IsNullOrWhiteSpace(path))
        {
            return Error("invalid_request", "path is required");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return Error("not_found", $"Log file not found: {fullPath}");
        }

        var lines = File.ReadLines(fullPath).ToList();
        if (lines.Count == 0)
        {
            return JsonSerializer.Serialize(new { status = "empty", file = fullPath, lines = 0 }, JsonOptions);
        }

        var errors = lines.Where(IsErrorLike).ToList();
        IEnumerable<object> groups = groupBy.Equals("level", StringComparison.OrdinalIgnoreCase)
            ? errors
                .GroupBy(LevelOf, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => (object)new { key = g.Key, count = g.Count() })
            : errors
                .GroupBy(SignatureOf, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Take(20)
                .Select(g => (object)new { key = g.Key, count = g.Count(), sample = g.FirstOrDefault() ?? string.Empty });

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            file = fullPath,
            totalLines = lines.Count,
            errorLines = errors.Count,
            groupedBy = groupBy,
            groups,
        }, JsonOptions);
    }

    [McpServerTool(Name = "incident_timeline")]
    [Description("Create a time-ordered incident timeline from a log file and UTC time window.")]
    public string IncidentTimeline(
        [Description("Absolute or relative path to a log file.")] string path,
        [Description("UTC ISO8601 start, e.g. 2026-03-31T05:00:00Z")] string start,
        [Description("UTC ISO8601 end, e.g. 2026-03-31T06:00:00Z")] string end,
        [Description("Maximum timeline events to return.")] int maxEvents = 200)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "LogForensicsTools.IncidentTimeline",
            ["Path"] = path,
            ["Start"] = start,
            ["End"] = end,
            ["MaxEvents"] = maxEvents,
        });
        logger.LogDebug("IncidentTimeline invoked");

        if (!DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var startUtc) ||
            !DateTimeOffset.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var endUtc))
        {
            return Error("invalid_request", "start and end must be valid UTC timestamps");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return Error("not_found", $"Log file not found: {fullPath}");
        }

        maxEvents = Math.Clamp(maxEvents, 10, 1000);
        var events = new List<object>();

        foreach (var line in File.ReadLines(fullPath))
        {
            if (!TryParseTimestamp(line, out var ts))
            {
                continue;
            }

            if (ts < startUtc || ts > endUtc)
            {
                continue;
            }

            events.Add(new
            {
                timestampUtc = ts.ToUniversalTime().ToString("O"),
                level = LevelOf(line),
                isError = IsErrorLike(line),
                message = line.Length > 500 ? line[..500] : line,
            });

            if (events.Count >= maxEvents)
            {
                break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            file = fullPath,
            window = new { start = startUtc.ToString("O"), end = endUtc.ToString("O") },
            count = events.Count,
            events,
        }, JsonOptions);
    }

    private static bool IsErrorLike(string line)
    {
        var lower = line.ToLowerInvariant();
        return lower.Contains("error") || lower.Contains("fatal") || lower.Contains("critical") || lower.Contains("exception");
    }

    private static string SignatureOf(string line)
    {
        var compact = line.Trim();
        if (compact.Length > 160)
        {
            compact = compact[..160];
        }

        foreach (var token in ErrorTokens)
        {
            var idx = compact.ToLowerInvariant().IndexOf(token, StringComparison.Ordinal);
            if (idx >= 0)
            {
                return compact[idx..].Trim();
            }
        }

        return compact;
    }

    private static string LevelOf(string line)
    {
        var upper = line.ToUpperInvariant();
        if (upper.Contains(" CRITICAL ") || upper.Contains(" FATAL "))
        {
            return "CRITICAL";
        }

        if (upper.Contains(" ERROR "))
        {
            return "ERROR";
        }

        if (upper.Contains(" WARN "))
        {
            return "WARN";
        }

        if (upper.Contains(" INFO "))
        {
            return "INFO";
        }

        if (upper.Contains(" DEBUG ") || upper.Contains(" TRACE "))
        {
            return "DEBUG";
        }

        return "UNKNOWN";
    }

    private static IEnumerable<string> ExtractCorrelations(string line)
    {
        foreach (Match m in CorrelationRegex().Matches(line))
        {
            if (m.Groups.Count >= 3 && !string.IsNullOrWhiteSpace(m.Groups[2].Value))
            {
                yield return m.Groups[2].Value.Trim();
            }
        }
    }

    private static bool TryParseTimestamp(string line, out DateTimeOffset timestamp)
    {
        var firstSpace = line.IndexOf(' ');
        if (firstSpace > 0 && DateTimeOffset.TryParse(line[..firstSpace], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp))
        {
            return true;
        }

        // Common fallback for leading [timestamp]
        if (line.StartsWith('['))
        {
            var end = line.IndexOf(']');
            if (end > 1 && DateTimeOffset.TryParse(line[1..end], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp))
            {
                return true;
            }
        }

        timestamp = default;
        return false;
    }

    private static string Error(string code, string message)
        => JsonSerializer.Serialize(new { status = "error", code, message }, JsonOptions);
}
