namespace DeadworksManaged.Api;

/// <summary>
/// Native-fired usercmd trigger. Produced only when a mounted native predicate matches
/// (for example, a watched button transitions from up to down).
/// </summary>
public sealed class UsercmdTriggerEvent
{
    public required int PlayerSlot { get; init; }
    public required FastUsercmd Usercmd { get; init; }
    public required InputButton PressedButtons { get; init; }
    public required InputButton TriggerButtons { get; init; }
    public required bool Paused { get; init; }
    public required float Margin { get; init; }

    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    public unsafe CCitadelPlayerController? Controller
    {
        get
        {
            if (NativeInterop.GetPlayerController == null)
                return null;
            var ptr = NativeInterop.GetPlayerController(PlayerSlot);
            return ptr != null ? new CCitadelPlayerController((nint)ptr) : null;
        }
    }
}
