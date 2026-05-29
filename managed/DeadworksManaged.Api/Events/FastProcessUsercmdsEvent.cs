using System.Runtime.InteropServices;

namespace DeadworksManaged.Api;

/// <summary>
/// Compact, native-extracted usercmd fields. This avoids protobuf serialization/parsing and is
/// intended for high-frequency read-only plugins (telemetry, anticheat, input analytics).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct FastUsercmd
{
    public readonly int CommandIndex;
    public readonly int ClientTick;
    public readonly ulong ButtonsRaw;
    public readonly float Pitch;
    public readonly float Yaw;
    public readonly float Roll;
    public readonly float ForwardMove;
    public readonly float LeftMove;
    private readonly int _hasBase;
    private readonly int _hasButtons;
    private readonly int _hasViewAngles;

    public bool HasBase => _hasBase != 0;
    public bool HasButtons => _hasButtons != 0;
    public bool HasViewAngles => _hasViewAngles != 0;
    public InputButton Buttons => (InputButton)ButtonsRaw;

    public bool IsHeld(InputButton button) => (Buttons & button) != 0;
}

/// <summary>
/// Read-only fast usercmd event. For mutation, use <see cref="ProcessUsercmdsEvent"/> instead;
/// this event is optimized for cheap observation.
/// </summary>
public sealed class FastProcessUsercmdsEvent
{
    public required int PlayerSlot { get; init; }
    public required FastUsercmd[] Usercmds { get; init; }
    public required bool Paused { get; init; }
    public required float Margin { get; init; }

    public int Count => Usercmds.Length;
    public FastUsercmd Latest => Usercmds.Length > 0 ? Usercmds[^1] : default;

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
