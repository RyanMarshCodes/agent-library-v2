using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using ModelContextProtocol.Server;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed partial class FetchTools(IHttpClientFactory httpClientFactory, ILogger<FetchTools> logger)
{
    private const int DefaultMaxLength = 50_000;
    private const int AbsoluteMaxLength = 200_000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>
    /// Element names that never contain useful content — always removed entirely.
    /// </summary>
    private static readonly HashSet<string> NeverContentElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "svg", "iframe", "object", "embed",
        "form", "button", "input", "select", "textarea",
    };

    /// <summary>
    /// Element names to remove only when working on the full document (not within a found main area).
    /// </summary>
    private static readonly HashSet<string> ChromeElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "nav", "header", "footer", "aside",
    };

    /// <summary>
    /// Block-level elements that should produce line breaks in text output.
    /// </summary>
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6",
        "li", "tr", "blockquote", "pre", "section", "article",
        "dt", "dd", "figcaption", "summary",
    };

    [McpServerTool(Name = "fetch")]
    [Description(
        "Fetch a URL and return its content as clean text. " +
        "HTML pages are stripped of scripts, styles, and tags — only readable text is returned. " +
        "JSON and plain-text responses are returned as-is. Ideal for pulling documentation, " +
        "NuGet package pages, GitHub READMEs, or MS Learn articles into context.")]
    public async Task<string> Fetch(
        [Description("The URL to fetch (must be http or https)")] string url,
        [Description("Maximum characters to return (default 50000, max 200000)")] int? maxLength = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Err("url is required");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
             && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
            return Err("url must be a valid http or https URL");

        var limit = Math.Clamp(maxLength ?? DefaultMaxLength, 1, AbsoluteMaxLength);
        var client = httpClientFactory.CreateClient("fetch");

        using (logger.BeginScope(new Dictionary<string, object?> { ["ToolName"] = "FetchTools.Fetch", ["Url"] = url, ["MaxLength"] = maxLength }))
        {
            logger.LogDebug("Fetch invoked for {Url}", url);

            try
            {
                using var response = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

                if (!response.IsSuccessStatusCode)
                {
                    return JsonSerializer.Serialize(new
                    {
                        url,
                        statusCode = (int)response.StatusCode,
                        error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    }, JsonOptions);
                }

                var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                string content;
                string format;

                if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    content = ExtractTextFromHtml(raw);
                    format = "html→text";
                }
                else
                {
                    content = raw;
                    format = contentType is { Length: > 0 } ? contentType : "text";
                }

                var truncated = content.Length > limit;

                return JsonSerializer.Serialize(new
                {
                    url,
                    statusCode = (int)response.StatusCode,
                    contentType,
                    format,
                    totalLength = content.Length,
                    truncated,
                    truncatedAt = truncated ? (int?)limit : null,
                    content = truncated ? content[..limit] : content,
                }, JsonOptions);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Err($"Request to {url} timed out");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Fetch failed for {Url}", url);
                return Err($"Network error: {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unexpected error fetching {Url}", url);
                return Err($"Unexpected error: {ex.Message}");
            }
        }
    }

    private static string Err(string message) =>
        JsonSerializer.Serialize(new { error = message });

    // ── HTML-to-text extraction ──────────────────────────────────────────────

    /// <summary>
    /// DOM-based content extraction. Strategy:
    /// 1. Strip elements that are never content (script, style, etc.)
    /// 2. Locate the main content area (main, article, role=main) BEFORE stripping chrome
    /// 3. If found, extract text from that subtree only
    /// 4. If not found, strip chrome elements (nav, header, footer, aside), then extract from body
    /// </summary>
    private static string ExtractTextFromHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        if (doc.DocumentNode is null)
            return string.Empty;

        // Phase 1: Remove elements that are never content (script, style, etc.)
        RemoveByTagName(doc.DocumentNode, NeverContentElements);

        // Phase 2: Try to find the main content area BEFORE removing chrome
        var contentRoot = FindMainContent(doc.DocumentNode);

        if (contentRoot is null)
        {
            // No clear content area found — strip chrome tags and use body/document
            RemoveByTagName(doc.DocumentNode, ChromeElements);
            RemoveHiddenElements(doc.DocumentNode);
            contentRoot = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        }

        // Phase 3: Extract text with structure-aware formatting
        var sb = new StringBuilder(html.Length / 4);
        ExtractText(contentRoot, sb);

        // Phase 4: Clean up whitespace
        var result = sb.ToString();
        result = InlineWhitespacePattern().Replace(result, " ");
        result = ExcessiveNewlinesPattern().Replace(result, "\n\n");

        return result.Trim();
    }

    private static void RemoveByTagName(HtmlNode root, HashSet<string> tagNames)
    {
        // Collect all matching nodes first to avoid modifying tree during traversal
        var toRemove = root.SelectNodes("//*")
            ?.Where(n => n.NodeType == HtmlNodeType.Element && tagNames.Contains(n.Name))
            .ToList();

        if (toRemove is null) return;

        foreach (var node in toRemove)
            node.Remove();
    }

    /// <summary>
    /// Remove elements with aria-hidden="true" or role="presentation" (decorative/hidden).
    /// </summary>
    private static void RemoveHiddenElements(HtmlNode root)
    {
        var toRemove = root.SelectNodes("//*[@aria-hidden='true' or @role='presentation']")?.ToList();
        if (toRemove is null) return;

        foreach (var node in toRemove)
            node.Remove();
    }

    /// <summary>
    /// Locate the main content area using semantic HTML elements and common patterns.
    /// Returns null if no sufficiently large content area is found.
    /// </summary>
    private static HtmlNode? FindMainContent(HtmlNode root)
    {
        // Try semantic elements first (most reliable)
        HtmlNode?[] candidates =
        [
            root.SelectSingleNode("//main"),
            root.SelectSingleNode("//article"),
            root.SelectSingleNode("//*[@role='main']"),
            root.SelectSingleNode("//*[@id='content']"),
            root.SelectSingleNode("//*[@id='main-content']"),
            root.SelectSingleNode("//*[@id='primary-content']"),
        ];

        foreach (var candidate in candidates)
        {
            if (candidate is not null && candidate.InnerText.Trim().Length > 200)
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Recursively extract text from the DOM tree, respecting block-level element boundaries.
    /// </summary>
    private static void ExtractText(HtmlNode node, StringBuilder sb)
    {
        switch (node.NodeType)
        {
            case HtmlNodeType.Text:
                var text = WebUtility.HtmlDecode(node.InnerText);
                if (!string.IsNullOrWhiteSpace(text))
                    sb.Append(text);
                break;

            case HtmlNodeType.Element:
                // Skip hidden elements within the content area
                if (IsHiddenElement(node))
                    break;

                var isBlock = BlockElements.Contains(node.Name);
                var isHeading = node.Name.Length == 2 && node.Name[0] == 'h'
                    && char.IsDigit(node.Name[1]);

                if (string.Equals(node.Name, "br", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine();
                    break;
                }

                if (isBlock || isHeading)
                    sb.AppendLine();

                // Markdown-style heading prefix for structure
                if (isHeading)
                    sb.Append(new string('#', node.Name[1] - '0')).Append(' ');

                foreach (var child in node.ChildNodes)
                    ExtractText(child, sb);

                if (isBlock || isHeading)
                    sb.AppendLine();
                break;

            case HtmlNodeType.Document:
                foreach (var child in node.ChildNodes)
                    ExtractText(child, sb);
                break;
        }
    }

    private static bool IsHiddenElement(HtmlNode node) =>
        string.Equals(node.GetAttributeValue("aria-hidden", ""), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(node.GetAttributeValue("hidden", null), "", StringComparison.Ordinal)
        || node.GetAttributeValue("style", "").Contains("display:none", StringComparison.OrdinalIgnoreCase)
        || node.GetAttributeValue("style", "").Contains("display: none", StringComparison.OrdinalIgnoreCase);

    // Source-generated regexes — compiled once, allocation-free matching
    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex InlineWhitespacePattern();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveNewlinesPattern();
}
