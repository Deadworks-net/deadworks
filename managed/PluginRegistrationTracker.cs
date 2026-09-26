namespace DeadworksManaged;

/// <summary>
/// Central index of everything a plugin has registered (commands, convars, chat commands,
/// event handlers, net message handlers). Each subsystem calls <see cref="Add"/> at
/// registration time; <see cref="Remove"/> bulk-clears on unload. Dispatch stays in the
/// owning subsystem - this is metadata only.
/// </summary>
internal static class PluginRegistrationTracker
{
    /// <param name="CanRun">Whether a caller may run it, for listings; null means anyone.</param>
    public readonly record struct Entry(
        string Kind, string Name, string Description, bool Hidden = false, Func<DeadworksManaged.Api.CCitadelPlayerController?, bool>? CanRun = null);

    private static readonly Dictionary<string, List<Entry>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _lock = new();

    public static void Add(string normalizedPath, string kind, string name, string description = "", bool hidden = false,
        Func<DeadworksManaged.Api.CCitadelPlayerController?, bool>? canRun = null)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(normalizedPath, out var list))
            {
                list = [];
                _entries[normalizedPath] = list;
            }
            list.Add(new Entry(kind, name, description, hidden, canRun));
        }
    }

    /// <summary>Returns a flat snapshot of every registered entry across every plugin.</summary>
    public static List<Entry> GetAllEntries()
    {
        lock (_lock)
        {
            var all = new List<Entry>();
            foreach (var list in _entries.Values)
                all.AddRange(list);
            return all;
        }
    }

    public static void Remove(string normalizedPath)
    {
        lock (_lock)
        {
            _entries.Remove(normalizedPath);
        }
    }

    public static List<Entry> GetEntries(string normalizedPath)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(normalizedPath, out var list) ? [.. list] : [];
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
