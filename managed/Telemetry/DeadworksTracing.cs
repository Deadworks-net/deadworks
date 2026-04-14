using System.Diagnostics;

namespace DeadworksManaged.Telemetry;

/// <summary>
/// ActivitySource for Deadworks distributed tracing.
/// Spans are created only for infrequent lifecycle events — never for per-frame hooks.
/// </summary>
internal static class DeadworksTracing
{
    public const string SourceName = "Deadworks.Server";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
