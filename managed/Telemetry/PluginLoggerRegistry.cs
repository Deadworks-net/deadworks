using System.Collections.Concurrent;
using DeadworksManaged.Api;
using Microsoft.Extensions.Logging;

namespace DeadworksManaged.Telemetry;

/// <summary>
/// Static registry mapping plugin instances to their ILogger.
/// Mirrors <see cref="TimerRegistry"/> pattern exactly.
/// </summary>
internal static class PluginLoggerRegistry
{
    private static readonly ConcurrentDictionary<IDeadworksPlugin, ILogger> _loggers = new();

    public static void Initialize()
    {
        LogResolver.Resolve = Get;
    }

    public static void Register(IDeadworksPlugin plugin)
    {
        var logger = DeadworksTelemetry.CreateLogger($"Plugin.{plugin.Name}");
        _loggers[plugin] = logger;
    }

    public static void Unregister(IDeadworksPlugin plugin)
    {
        _loggers.TryRemove(plugin, out _);
    }

    public static ILogger Get(IDeadworksPlugin plugin)
    {
        if (_loggers.TryGetValue(plugin, out var logger))
            return logger;

        throw new InvalidOperationException(
            $"No logger registered for plugin '{plugin.Name}'. " +
            "Logger is only available after OnLoad and before OnUnload.");
    }

    public static void Clear()
    {
        _loggers.Clear();
    }
}
