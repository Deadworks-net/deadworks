namespace DeadworksManaged.Api;

/// <summary>
/// Internal resolver used by the IDeadworksPlugin.Timer default property.
/// Set up by the host during initialization.
/// </summary>
internal static class TimerResolver
{
    internal static Func<IDeadworksPlugin, ITimer>? Resolve;

    /// <summary>
    /// Host-provided "run on the next game tick" dispatcher, for API helpers that need to defer work
    /// without holding a plugin reference. Set by the host alongside <see cref="Resolve"/>.
    /// </summary>
    internal static Action<Action>? NextTick;

    /// <summary>Runs <paramref name="action"/> on the next tick if the host is initialised, otherwise immediately.</summary>
    internal static void RunNextTick(Action action)
    {
        if (NextTick != null)
            NextTick(action);
        else
            action();
    }

    public static ITimer Get(IDeadworksPlugin plugin)
    {
        if (Resolve == null)
            throw new InvalidOperationException("Timer system not initialized.");
        return Resolve(plugin);
    }
}
