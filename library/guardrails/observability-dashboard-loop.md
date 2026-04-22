# Observability Dashboard Loop

Use this loop to continuously tune routing, budget, and execution guardrails.
Primary MCP entry points:

- `platform_metrics_snapshot`
- `observability_dashboard_loop`

## Core Metrics

- `workflow_state_upserts_total`
- `workflow_state_reads_total`
- `execute_allowlist_allow_total`
- `execute_allowlist_deny_total`
- `routing_budget_allow_total`
- `routing_budget_deny_total`
- `routing_projected_cost_usd`
- `knowledge_retrieval_cache_hit_total`
- `knowledge_retrieval_cache_miss_total`
- `prompt_prefix_cache_hit_total`
- `prompt_prefix_cache_miss_total`

## Review Cadence

- Daily: inspect allowlist denials and budget denials.
- Per release: compare routing cost trend to workflow completion quality.
- Weekly: tune `McpOptions:RoutingBudget:WorkflowBudgetUsd` and command allowlist based on denial spikes.

## Suggested Dashboard Panels

1. **Execute Deny Rate** = `deny / (allow + deny)` over time.
2. **Routing Budget Deny Trend** by workflow key.
3. **Projected Cost Histogram** (`routing_projected_cost_usd`) by day.
4. **Workflow State Throughput** (`upserts` and `reads`) for adoption tracking.
5. **Knowledge Retrieval Cache Hit Ratio** = `cache_hit / (cache_hit + cache_miss)`.
6. **Prompt Prefix Cache Hit Ratio** = `prompt_hit / (prompt_hit + prompt_miss)`.

## Query Starters (PromQL)

- Execute deny rate:
  - `sum(rate(execute_allowlist_deny_total[1h])) / (sum(rate(execute_allowlist_allow_total[1h])) + sum(rate(execute_allowlist_deny_total[1h])))`
- Routing budget deny trend:
  - `sum by (workflow)(increase(routing_budget_deny_total[24h]))`
- P95 projected routing cost:
  - `histogram_quantile(0.95, sum(rate(routing_projected_cost_usd_bucket[1h])) by (le))`
- Retrieval cache hit ratio:
  - `sum(rate(knowledge_retrieval_cache_hit_total[1h])) / (sum(rate(knowledge_retrieval_cache_hit_total[1h])) + sum(rate(knowledge_retrieval_cache_miss_total[1h])))`
- Prompt prefix cache hit ratio:
  - `sum(rate(prompt_prefix_cache_hit_total[1h])) / (sum(rate(prompt_prefix_cache_hit_total[1h])) + sum(rate(prompt_prefix_cache_miss_total[1h])))`

## Optimization Loop

1. Detect spikes in `execute_allowlist_deny_total` or `routing_budget_deny_total`.
2. Sample denied requests and classify as expected blocks vs false positives.
3. Tune policy:
   - add/remove safe commands in `ExecutePolicy:AllowedCommands`,
   - adjust workflow budget ceilings in `RoutingBudget:WorkflowBudgetUsd`,
   - tune retrieval cache controls in `Retrieval` (`SemanticCacheTtlMinutes`, `SemanticCacheMaxEntries`),
   - tune prompt cache controls in `PromptCache` (`PrefixCacheTtlMinutes`, `PrefixCacheMaxEntries`).
4. Re-measure denial rates and projected cost next day.
