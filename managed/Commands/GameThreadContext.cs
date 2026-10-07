namespace DeadworksManaged.Commands;

/// <summary>
/// Makes <c>await</c> in a command come back on the game thread: continuations are posted to the next tick instead
/// of running on the thread pool, where touching entities or calling the engine (a ban's kick, a reply) can crash
/// the server. Installed only while a command's method is starting, so code outside commands is unaffected.
/// </summary>
internal sealed class GameThreadContext : SynchronizationContext
{
    public static readonly GameThreadContext Instance = new();

    // Run the continuation with this context current, so the command's next await posts back here too.
    public override void Post(SendOrPostCallback d, object? state) => TimerEngine.EnqueueNextTick(() => Run(() => d(state)));

    // Nothing in Deadworks waits on the game thread for work it would then have to run itself; run it in place.
    public override void Send(SendOrPostCallback d, object? state) => d(state);

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs <paramref name="action"/> with this context current, restoring whatever was there before.</summary>
    public static void Run(Action action) => Run(() => { action(); return 0; });

    /// <summary>Runs <paramref name="action"/> with this context current, restoring whatever was there before.</summary>
    public static T Run<T>(Func<T> action)
    {
        var previous = Current;
        SetSynchronizationContext(Instance);
        try
        {
            return action();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}
