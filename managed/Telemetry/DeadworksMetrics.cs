using System.Diagnostics.Metrics;

namespace DeadworksManaged.Telemetry;

/// <summary>
/// All metric instruments for Deadworks game server observability.
/// Uses <see cref="System.Diagnostics.Metrics"/> which OpenTelemetry hooks into automatically.
/// </summary>
internal static class DeadworksMetrics
{
    public const string MeterName = "Deadworks.Server";
    private static readonly Meter Meter = new(MeterName, "1.0.0");

    // --- Plugin Lifecycle ---
    public static readonly Counter<long> PluginsLoaded = Meter.CreateCounter<long>(
        "deadworks.plugins.loaded", "count", "Total plugin load operations");

    public static readonly Counter<long> PluginsUnloaded = Meter.CreateCounter<long>(
        "deadworks.plugins.unloaded", "count", "Total plugin unload operations");

    public static readonly Counter<long> PluginLoadErrors = Meter.CreateCounter<long>(
        "deadworks.plugins.load_errors", "count", "Plugin load failures");

    public static readonly Histogram<double> PluginLoadDuration = Meter.CreateHistogram<double>(
        "deadworks.plugins.load_duration_ms", "ms", "Time to load a plugin");

    // Registered lazily in DeadworksTelemetry.Initialize after PluginLoader is available
    public static ObservableGauge<int>? ActivePluginCount;

    public static void RegisterObservableGauges(Func<int> getActivePlugins, Func<int> getConnectedPlayers)
    {
        ActivePluginCount = Meter.CreateObservableGauge(
            "deadworks.plugins.active", getActivePlugins,
            "plugins", "Currently active plugins");

        Meter.CreateObservableGauge(
            "deadworks.players.count", getConnectedPlayers,
            "players", "Currently connected player count");
    }

    // --- Player Connections ---
    public static readonly Counter<long> PlayerConnections = Meter.CreateCounter<long>(
        "deadworks.players.connections_total", "count", "Total player connections");

    public static readonly Counter<long> PlayerDisconnections = Meter.CreateCounter<long>(
        "deadworks.players.disconnections_total", "count", "Total player disconnections");

    public static readonly Counter<long> PlayerConnectionsRejected = Meter.CreateCounter<long>(
        "deadworks.players.connections_rejected", "count", "Rejected connection attempts");

    // --- Frame Performance ---
    public static readonly Histogram<double> GameFrameDuration = Meter.CreateHistogram<double>(
        "deadworks.frame.duration_ms", "ms", "Total managed game frame processing time");

    // --- Timer Engine ---
    public static readonly Histogram<int> TimerTasksPerFrame = Meter.CreateHistogram<int>(
        "deadworks.timers.tasks_per_frame", "count", "Timer tasks executed per frame");

    public static readonly Counter<long> TimerErrors = Meter.CreateCounter<long>(
        "deadworks.timers.errors", "count", "Timer callback failures");

    // --- Server Browser / Heartbeat ---
    public static readonly Counter<long> HeartbeatsSent = Meter.CreateCounter<long>(
        "deadworks.heartbeat.sent_total", "count", "Heartbeat attempts");

    public static readonly Counter<long> HeartbeatsFailed = Meter.CreateCounter<long>(
        "deadworks.heartbeat.failed_total", "count", "Failed heartbeats");

    public static readonly Histogram<double> HeartbeatDuration = Meter.CreateHistogram<double>(
        "deadworks.heartbeat.duration_ms", "ms", "Heartbeat HTTP request duration");

    // --- Event Dispatch ---
    public static readonly Counter<long> EventsDispatched = Meter.CreateCounter<long>(
        "deadworks.events.dispatched_total", "count", "Game events dispatched to plugins");

    public static readonly Counter<long> EventHandlerErrors = Meter.CreateCounter<long>(
        "deadworks.events.handler_errors", "count", "Plugin event handler errors");

    public static readonly Counter<long> ChatMessagesProcessed = Meter.CreateCounter<long>(
        "deadworks.chat.messages_total", "count", "Chat messages processed");

    public static readonly Counter<long> CommandsDispatched = Meter.CreateCounter<long>(
        "deadworks.commands.dispatched_total", "count", "Console commands dispatched");
}
