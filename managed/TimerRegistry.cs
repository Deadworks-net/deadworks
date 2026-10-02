using DeadworksManaged.Api;
using ITimer = DeadworksManaged.Api.ITimer;

namespace DeadworksManaged;

/// <summary>
/// Static registry mapping plugin instances to their TimerService.
/// Used by the IDeadworksPlugin.Timer default property implementation.
/// </summary>
internal static class TimerRegistry
{
    private static readonly Dictionary<IDeadworksPlugin, TimerService> _services = new();
    private static readonly object _lock = new();

    public static void Initialize()
    {
        TimerResolver.Resolve = Get;
        TimerResolver.NextTick = TimerEngine.EnqueueNextTick;
    }

    public static void Register(IDeadworksPlugin plugin, TimerService service)
    {
        lock (_lock)
        {
            _services[plugin] = service;
        }
    }

    public static void Unregister(IDeadworksPlugin plugin)
    {
        lock (_lock)
        {
            _services.Remove(plugin);
        }
    }

    public static ITimer Get(IDeadworksPlugin plugin)
    {
        lock (_lock)
        {
            if (_services.TryGetValue(plugin, out var service))
                return service;
        }

        throw new InvalidOperationException(
            $"No TimerService registered for plugin '{plugin.Name}'. " +
            "Timers are only available after OnLoad and before OnUnload.");
    }

    /// <summary>Get the TimerService for disposal during unload.</summary>
    public static TimerService? GetService(IDeadworksPlugin plugin)
    {
        lock (_lock)
        {
            return _services.GetValueOrDefault(plugin);
        }
    }

    /// <summary>Cancels all timers across all plugins that are marked CancelOnMapChange.</summary>
    public static void CancelAllMapChangeTimers()
    {
        lock (_lock)
        {
            foreach (var service in _services.Values)
                service.CancelMapChangeTimers();
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            foreach (var service in _services.Values)
                service.Dispose();
            _services.Clear();
        }
    }
}
