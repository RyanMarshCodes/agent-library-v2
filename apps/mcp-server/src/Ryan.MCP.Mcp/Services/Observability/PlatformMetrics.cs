using System.Diagnostics.Metrics;

namespace Ryan.MCP.Mcp.Services.Observability;

public sealed class PlatformMetrics : IDisposable
{
    public const string MeterName = "Ryan.MCP.Platform";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _workflowStateUpserts;
    private readonly Counter<long> _workflowStateReads;
    private readonly Counter<long> _executeAllow;
    private readonly Counter<long> _executeDeny;
    private readonly Counter<long> _routingBudgetAllow;
    private readonly Counter<long> _routingBudgetDeny;
    private readonly Counter<long> _knowledgeCacheHit;
    private readonly Counter<long> _knowledgeCacheMiss;
    private readonly Counter<long> _promptPrefixCacheHit;
    private readonly Counter<long> _promptPrefixCacheMiss;
    private readonly Histogram<double> _routingProjectedCostUsd;
    private long _workflowStateUpsertsTotal;
    private long _workflowStateReadsTotal;
    private long _executeAllowTotal;
    private long _executeDenyTotal;
    private long _routingBudgetAllowTotal;
    private long _routingBudgetDenyTotal;
    private long _knowledgeCacheHitTotal;
    private long _knowledgeCacheMissTotal;
    private long _promptPrefixCacheHitTotal;
    private long _promptPrefixCacheMissTotal;
    private double _routingProjectedCostUsdTotal;

    public PlatformMetrics()
    {
        _workflowStateUpserts = _meter.CreateCounter<long>("workflow_state_upserts_total");
        _workflowStateReads = _meter.CreateCounter<long>("workflow_state_reads_total");
        _executeAllow = _meter.CreateCounter<long>("execute_allowlist_allow_total");
        _executeDeny = _meter.CreateCounter<long>("execute_allowlist_deny_total");
        _routingBudgetAllow = _meter.CreateCounter<long>("routing_budget_allow_total");
        _routingBudgetDeny = _meter.CreateCounter<long>("routing_budget_deny_total");
        _knowledgeCacheHit = _meter.CreateCounter<long>("knowledge_retrieval_cache_hit_total");
        _knowledgeCacheMiss = _meter.CreateCounter<long>("knowledge_retrieval_cache_miss_total");
        _promptPrefixCacheHit = _meter.CreateCounter<long>("prompt_prefix_cache_hit_total");
        _promptPrefixCacheMiss = _meter.CreateCounter<long>("prompt_prefix_cache_miss_total");
        _routingProjectedCostUsd = _meter.CreateHistogram<double>("routing_projected_cost_usd");
    }

    public void RecordWorkflowStateUpsert()
    {
        _workflowStateUpserts.Add(1);
        Interlocked.Increment(ref _workflowStateUpsertsTotal);
    }

    public void RecordWorkflowStateRead()
    {
        _workflowStateReads.Add(1);
        Interlocked.Increment(ref _workflowStateReadsTotal);
    }

    public void RecordExecuteAllow()
    {
        _executeAllow.Add(1);
        Interlocked.Increment(ref _executeAllowTotal);
    }

    public void RecordExecuteDeny()
    {
        _executeDeny.Add(1);
        Interlocked.Increment(ref _executeDenyTotal);
    }

    public void RecordRoutingBudgetDecision(bool allowed, decimal projectedCostUsd)
    {
        if (allowed)
        {
            _routingBudgetAllow.Add(1);
            Interlocked.Increment(ref _routingBudgetAllowTotal);
        }
        else
        {
            _routingBudgetDeny.Add(1);
            Interlocked.Increment(ref _routingBudgetDenyTotal);
        }

        _routingProjectedCostUsd.Record((double)projectedCostUsd);
        Interlocked.Exchange(
            ref _routingProjectedCostUsdTotal,
            Interlocked.CompareExchange(ref _routingProjectedCostUsdTotal, 0, 0) + (double)projectedCostUsd);
    }

    public void RecordKnowledgeCacheHit()
    {
        _knowledgeCacheHit.Add(1);
        Interlocked.Increment(ref _knowledgeCacheHitTotal);
    }

    public void RecordKnowledgeCacheMiss()
    {
        _knowledgeCacheMiss.Add(1);
        Interlocked.Increment(ref _knowledgeCacheMissTotal);
    }

    public void RecordPromptPrefixCacheHit()
    {
        _promptPrefixCacheHit.Add(1);
        Interlocked.Increment(ref _promptPrefixCacheHitTotal);
    }

    public void RecordPromptPrefixCacheMiss()
    {
        _promptPrefixCacheMiss.Add(1);
        Interlocked.Increment(ref _promptPrefixCacheMissTotal);
    }

    public PlatformMetricsSnapshot Snapshot() =>
        new(
            WorkflowStateUpserts: Interlocked.Read(ref _workflowStateUpsertsTotal),
            WorkflowStateReads: Interlocked.Read(ref _workflowStateReadsTotal),
            ExecuteAllow: Interlocked.Read(ref _executeAllowTotal),
            ExecuteDeny: Interlocked.Read(ref _executeDenyTotal),
            RoutingBudgetAllow: Interlocked.Read(ref _routingBudgetAllowTotal),
            RoutingBudgetDeny: Interlocked.Read(ref _routingBudgetDenyTotal),
            KnowledgeRetrievalCacheHit: Interlocked.Read(ref _knowledgeCacheHitTotal),
            KnowledgeRetrievalCacheMiss: Interlocked.Read(ref _knowledgeCacheMissTotal),
            PromptPrefixCacheHit: Interlocked.Read(ref _promptPrefixCacheHitTotal),
            PromptPrefixCacheMiss: Interlocked.Read(ref _promptPrefixCacheMissTotal),
            RoutingProjectedCostUsdTotal: Interlocked.CompareExchange(ref _routingProjectedCostUsdTotal, 0, 0));

    public void Dispose() => _meter.Dispose();
}

public sealed record PlatformMetricsSnapshot(
    long WorkflowStateUpserts,
    long WorkflowStateReads,
    long ExecuteAllow,
    long ExecuteDeny,
    long RoutingBudgetAllow,
    long RoutingBudgetDeny,
    long KnowledgeRetrievalCacheHit,
    long KnowledgeRetrievalCacheMiss,
    long PromptPrefixCacheHit,
    long PromptPrefixCacheMiss,
    double RoutingProjectedCostUsdTotal);
