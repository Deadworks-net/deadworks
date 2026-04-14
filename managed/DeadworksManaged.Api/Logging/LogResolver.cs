using Microsoft.Extensions.Logging;

namespace DeadworksManaged.Api;

/// <summary>
/// Internal resolver used by the IDeadworksPlugin.Logger default property.
/// Set up by the host during initialization.
/// </summary>
internal static class LogResolver
{
    internal static Func<IDeadworksPlugin, ILogger>? Resolve;

    public static ILogger Get(IDeadworksPlugin plugin)
    {
        if (Resolve == null)
            throw new InvalidOperationException("Logging system not initialized.");
        return Resolve(plugin);
    }
}
