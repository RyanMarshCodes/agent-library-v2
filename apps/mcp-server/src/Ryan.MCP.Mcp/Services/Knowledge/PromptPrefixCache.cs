using System.Collections.Concurrent;
using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Services.Knowledge;

public sealed class PromptPrefixCache(McpOptions options)
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string key, out string value)
    {
        value = string.Empty;
        if (!options.PromptCache.EnablePrefixCache)
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
        if (!options.PromptCache.EnablePrefixCache)
        {
            return;
        }

        var ttlMinutes = Math.Max(1, options.PromptCache.PrefixCacheTtlMinutes);
        _entries[key] = new CacheEntry(payload, DateTime.UtcNow.AddMinutes(ttlMinutes));
        EvictOverflow();
    }

    public int InvalidateByPrefix(string keyPrefix)
    {
        if (string.IsNullOrWhiteSpace(keyPrefix))
        {
            return 0;
        }

        var removed = 0;
        foreach (var key in _entries.Keys.Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal)))
        {
            if (_entries.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    private void EvictOverflow()
    {
        var maxEntries = Math.Max(100, options.PromptCache.PrefixCacheMaxEntries);
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
            .Take(_entries.Count - maxEntries)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in overflow)
        {
            _entries.TryRemove(key, out _);
        }
    }

    private sealed record CacheEntry(string Payload, DateTime ExpiresUtc);
}
