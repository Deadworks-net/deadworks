namespace DeadworksManaged;

/// <summary>
/// Generic handler registry that manages a main handler dictionary and per-plugin tracking
/// for clean registration/unregistration of plugin handlers.
/// </summary>
internal sealed class HandlerRegistry<TKey, THandler> where TKey : notnull
{
    private readonly Dictionary<TKey, List<THandler>> _handlers;
    private readonly Dictionary<string, List<(TKey key, THandler handler)>> _pluginTracking = new(StringComparer.OrdinalIgnoreCase);

    public HandlerRegistry(IEqualityComparer<TKey>? comparer = null)
    {
        _handlers = new(comparer);
    }

    /// <summary>
    /// Adds a handler for the given key. Returns true if this is the first handler for that key.
    /// </summary>
    public bool Add(TKey key, THandler handler)
    {
        if (!_handlers.TryGetValue(key, out var list))
        {
            list = new List<THandler>();
            _handlers[key] = list;
            list.Add(handler);
            return true;
        }
        list.Add(handler);
        return false;
    }

    public void Remove(TKey key, THandler handler)
    {
        if (_handlers.TryGetValue(key, out var list))
        {
            list.Remove(handler);
            if (list.Count == 0)
                _handlers.Remove(key);
        }
    }

    /// <summary>
    /// Adds a handler and tracks it for the given plugin path so it can be bulk-removed later.
    /// Returns true if this is the first handler for that key.
    /// </summary>
    public bool AddForPlugin(string pluginPath, TKey key, THandler handler)
    {
        var isFirst = Add(key, handler);
        if (!_pluginTracking.TryGetValue(pluginPath, out var tracked))
        {
            tracked = new List<(TKey, THandler)>();
            _pluginTracking[pluginPath] = tracked;
        }
        tracked.Add((key, handler));
        return isFirst;
    }

    /// <summary>
    /// Removes all handlers registered by the given plugin.
    /// </summary>
    public void UnregisterPlugin(string pluginPath)
    {
        if (!_pluginTracking.Remove(pluginPath, out var handlers))
            return;
        foreach (var (key, handler) in handlers)
            Remove(key, handler);
    }

    /// <summary>
    /// Returns a snapshot (copy) of the handler list for the given key, or null if no handlers exist.
    /// </summary>
    public List<THandler>? Snapshot(TKey key)
    {
        return _handlers.TryGetValue(key, out var list) ? [.. list] : null;
    }

    public void Clear()
    {
        _handlers.Clear();
        _pluginTracking.Clear();
    }

    /// <summary>Snapshot of (key, handler count) for every registered key. Caller must hold external lock if relevant.</summary>
    public List<(TKey Key, int HandlerCount)> ListHandlers()
    {
        var result = new List<(TKey, int)>(_handlers.Count);
        foreach (var kv in _handlers)
            result.Add((kv.Key, kv.Value.Count));
        return result;
    }

    /// <summary>Returns the plugin paths that have registered at least one handler for <paramref name="key"/>.</summary>
    public List<string> PluginsWithHandlerFor(TKey key)
    {
        var result = new List<string>();
        var comparer = _handlers.Comparer;
        foreach (var (pluginPath, tracked) in _pluginTracking)
        {
            foreach (var (k, _) in tracked)
            {
                if (comparer.Equals(k, key))
                {
                    result.Add(pluginPath);
                    break;
                }
            }
        }
        return result;
    }
}
