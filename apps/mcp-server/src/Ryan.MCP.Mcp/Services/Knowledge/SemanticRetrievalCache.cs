using System.Collections.Concurrent;
using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Services.Knowledge;

public sealed class SemanticRetrievalCache(McpOptions options)
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string key, out string value)
    {
        value = string.Empty;
        if (!options.Retrieval.EnableSemanticCache)
        {
            return false;
        }

        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (entry.ExpiresUtc <= DateTime.UtcNow)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        value = entry.Payload;
        return true;
    }

    public void Set(string key, string payload)
    {
        if (!options.Retrieval.EnableSemanticCache)
        {
            return;
        }

        var ttlMinutes = Math.Max(1, options.Retrieval.SemanticCacheTtlMinutes);
        _entries[key] = new CacheEntry(payload, DateTime.UtcNow.AddMinutes(ttlMinutes));

        var maxEntries = Math.Max(50, options.Retrieval.SemanticCacheMaxEntries);
        if (_entries.Count <= maxEntries)
        {
            return;
        }

        foreach (var stale in _entries.Where(kvp => kvp.Value.ExpiresUtc <= DateTime.UtcNow).Select(kvp => kvp.Key))
        {
            _entries.TryRemove(stale, out _);
        }

        if (_entries.Count <= maxEntries)
        {
            return;
        }

        var overflow = _entries
            .OrderBy(kvp => kvp.Value.ExpiresUtc)
            .Take(Math.Max(1, _entries.Count - maxEntries))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var keyToRemove in overflow)
        {
            _entries.TryRemove(keyToRemove, out _);
        }
    }

    private sealed record CacheEntry(string Payload, DateTime ExpiresUtc);
}
