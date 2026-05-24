namespace DeadworksManaged.Api;

/// <summary>
/// A handle to a cancellable subscription — returned by timers, game-event listeners,
/// net-message hooks, entity I/O hooks, and any other API that registers a long-lived callback.
/// Allows cancellation and status checking.
/// </summary>
public interface IHandle
{
    /// <summary>Cancel this subscription. If already finished or cancelled, this is a no-op.</summary>
    void Cancel();

    /// <summary>Whether this subscription has completed or been cancelled.</summary>
    bool IsFinished { get; }

    /// <summary>
    /// Marks this subscription to be automatically cancelled on map change (when <c>OnStartupServer</c> fires).
    /// Primarily meaningful for timers; non-timer handles typically return themselves unchanged.
    /// Returns the handle itself for fluent chaining.
    /// </summary>
    IHandle CancelOnMapChange();
}
