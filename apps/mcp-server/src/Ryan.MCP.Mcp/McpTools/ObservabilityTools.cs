using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Configuration;
using Ryan.MCP.Mcp.Services.Knowledge;
using Ryan.MCP.Mcp.Services.Observability;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class ObservabilityTools(
    PlatformMetrics metrics,
    PromptPrefixCache promptPrefixCache,
    McpOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "platform_metrics_snapshot")]
    [Description("Read in-process platform metrics counters for guardrails, budgets, and workflow state usage.")]
    public string PlatformMetricsSnapshot()
    {
        var snapshot = metrics.Snapshot();
        return JsonSerializer.Serialize(new
        {
            generatedUtc = DateTime.UtcNow,
            snapshot.WorkflowStateUpserts,
            snapshot.WorkflowStateReads,
            snapshot.ExecuteAllow,
            snapshot.ExecuteDeny,
            snapshot.RoutingBudgetAllow,
            snapshot.RoutingBudgetDeny,
            snapshot.KnowledgeRetrievalCacheHit,
            snapshot.KnowledgeRetrievalCacheMiss,
            snapshot.PromptPrefixCacheHit,
            snapshot.PromptPrefixCacheMiss,
            snapshot.RoutingProjectedCostUsdTotal,
            optimizationLoop = new[]
            {
                "Review deny counters daily and tune allowlists/budgets.",
                "Compare routing projected cost trend against workflow output quality.",
                "Adjust per-workflow budgets when persistent budget_exceeded appears.",
                "Track retrieval cache hit ratio and tune TTL when misses remain high.",
                "Track prompt prefix cache hit ratio and refresh version keys on template changes."
            }
        }, JsonOptions);
    }

    [McpServerTool(Name = "observability_dashboard_loop")]
    [Description("Get practical dashboard query snippets and a daily optimization checklist for platform guardrails.")]
    public string ObservabilityDashboardLoop()
    {
        var snapshot = metrics.Snapshot();
        var totalCache = snapshot.KnowledgeRetrievalCacheHit + snapshot.KnowledgeRetrievalCacheMiss;
        var cacheHitRate = totalCache == 0 ? 0 : Math.Round((double)snapshot.KnowledgeRetrievalCacheHit / totalCache, 4);
        var totalPromptCache = snapshot.PromptPrefixCacheHit + snapshot.PromptPrefixCacheMiss;
        var promptCacheHitRate = totalPromptCache == 0 ? 0 : Math.Round((double)snapshot.PromptPrefixCacheHit / totalPromptCache, 4);

        return JsonSerializer.Serialize(new
        {
            generatedUtc = DateTime.UtcNow,
            current = new
            {
                executeDenyRate = snapshot.ExecuteAllow + snapshot.ExecuteDeny == 0
                    ? 0
                    : Math.Round((double)snapshot.ExecuteDeny / (snapshot.ExecuteAllow + snapshot.ExecuteDeny), 4),
                routingBudgetDenyRate = snapshot.RoutingBudgetAllow + snapshot.RoutingBudgetDeny == 0
                    ? 0
                    : Math.Round((double)snapshot.RoutingBudgetDeny / (snapshot.RoutingBudgetAllow + snapshot.RoutingBudgetDeny), 4),
                retrievalCacheHitRate = cacheHitRate,
                promptPrefixCacheHitRate = promptCacheHitRate
            },
            queryTemplates = new
            {
                executeDenyRate = "sum(rate(execute_allowlist_deny_total[1h])) / (sum(rate(execute_allowlist_allow_total[1h])) + sum(rate(execute_allowlist_deny_total[1h])))",
                routingBudgetDenyTrend = "sum by (workflow)(increase(routing_budget_deny_total[24h]))",
                projectedCostHistogram = "histogram_quantile(0.95, sum(rate(routing_projected_cost_usd_bucket[1h])) by (le))",
                retrievalCacheHitRatio = "sum(rate(knowledge_retrieval_cache_hit_total[1h])) / (sum(rate(knowledge_retrieval_cache_hit_total[1h])) + sum(rate(knowledge_retrieval_cache_miss_total[1h])))",
                promptPrefixCacheHitRatio = "sum(rate(prompt_prefix_cache_hit_total[1h])) / (sum(rate(prompt_prefix_cache_hit_total[1h])) + sum(rate(prompt_prefix_cache_miss_total[1h])))"
            },
            loop = new[]
            {
                "Inspect deny spikes and classify expected blocks vs false positives.",
                "If budget denials trend upward, prefer lower-cost classes or raise workflow ceilings explicitly.",
                "If cache hit ratio stays low, tighten query normalization and raise semantic cache TTL carefully.",
                "If prompt prefix cache misses stay high, update version tokens only when templates actually change.",
                "Re-run eval-regression workflow before changing routing thresholds globally."
            }
        }, JsonOptions);
    }

    [McpServerTool(Name = "prompt_cache_invalidate")]
    [Description("Invalidate prompt prefix cache entries by scope prefix. Use after changing workflow prompt templates.")]
    public string PromptCacheInvalidate(
        [Description("Cache key prefix to invalidate. Example: 'v1|ryan-mcp|run_workflow_step|/feature-delivery'.")] string keyPrefix)
    {
        if (string.IsNullOrWhiteSpace(keyPrefix))
        {
            return JsonSerializer.Serialize(new { error = "keyPrefix is required" }, JsonOptions);
        }

        var removed = promptPrefixCache.InvalidateByPrefix(keyPrefix.Trim());
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            removed,
            keyPrefix,
            cacheEnabled = options.PromptCache.EnablePrefixCache
        }, JsonOptions);
    }
}
