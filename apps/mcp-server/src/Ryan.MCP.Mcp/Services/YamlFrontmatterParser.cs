namespace Ryan.MCP.Mcp.Services;

/// <summary>
/// Result of parsing YAML frontmatter.
/// </summary>
public class FrontmatterParseResult
{
    /// <summary>Gets or sets whether parsing succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the parsed frontmatter dictionary.</summary>
    public Dictionary<string, object> Frontmatter { get; set; } = [];

    /// <summary>Gets or sets the remaining body content after frontmatter.</summary>
    public string BodyContent { get; set; } = string.Empty;
}

/// <summary>
/// Parses YAML frontmatter from Markdown files.
/// </summary>
public static class YamlFrontmatterParser
{
    private static readonly string[] SplitDelimiters = ["\r\n", "\n"];

    /// <summary>
    /// Extracts and parses YAML frontmatter from Markdown content.
    /// </summary>
    public static FrontmatterParseResult TryParseFrontmatter(string content)
    {
        var result = new FrontmatterParseResult { BodyContent = content };

        if (string.IsNullOrWhiteSpace(content))
        {
            return result;
        }

        var lines = content.Split(SplitDelimiters, StringSplitOptions.None);

        if (lines.Length < 3 || !lines[0].Trim().Equals("---", StringComparison.Ordinal))
        {
            return result;
        }

        int endIndex = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Equals("---", StringComparison.Ordinal))
            {
                endIndex = i;
                break;
            }
        }

        if (endIndex == -1)
        {
            return result;
        }

        var frontmatterLines = lines.Skip(1).Take(endIndex - 1).ToList();
        result.BodyContent = string.Join("\n", lines.Skip(endIndex + 1)).Trim();

        foreach (var line in frontmatterLines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            var colonIndex = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex <= 0)
            {
                continue;
            }

            var key = trimmed[..colonIndex].Trim();
            var value = trimmed[(colonIndex + 1)..].Trim();

            if ((value.StartsWith('"') && value.EndsWith('"')) ||
                (value.StartsWith('\'') && value.EndsWith('\'')))
            {
                value = value[1..^1];
            }

            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                var items = value[1..^1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().Trim('"', '\''))
                    .Cast<object>()
                    .ToList();
                result.Frontmatter[key] = items;
            }
            else
            {
                result.Frontmatter[key] = value;
            }
        }

        result.Success = result.Frontmatter.Count > 0;
        return result;
    }

    /// <summary>
    /// Gets a string value from frontmatter, with optional default.
    /// </summary>
    public static string? GetString(Dictionary<string, object> frontmatter, string key, string? defaultValue = null)
    {
        return frontmatter.TryGetValue(key, out var value) ? value?.ToString() : defaultValue;
    }

    /// <summary>
    /// Gets a list of strings from frontmatter.
    /// </summary>
    public static List<string> GetStringList(Dictionary<string, object> frontmatter, string key)
    {
        if (!frontmatter.TryGetValue(key, out var value))
        {
            return [];
        }

        return value switch
        {
            List<string> list => list,
            List<object> objList => objList.Select(x => x?.ToString() ?? string.Empty).ToList(),
            string str => [str],
            _ => [],
        };
    }
}
