namespace DeadworksManaged.Api;

/// <summary>
/// Compact native-extracted net message observation. This is the read-only visitor path;
/// use <see cref="NetMessages.HookIncoming{T}"/> or <see cref="NetMessages.HookOutgoing{T}"/>
/// when a plugin needs the full protobuf object or mutation/blocking semantics.
/// </summary>
public sealed class FastNetMessageEvent
{
    /// <summary>Message direction relative to the server.</summary>
    public required NetMessageDirection Direction { get; init; }

    /// <summary>Incoming sender slot, or -1 for outgoing messages.</summary>
    public required int EndpointSlot { get; init; }

    /// <summary>Source 2 net message id.</summary>
    public required int MessageId { get; init; }

    /// <summary>Outgoing recipient mask, or zero for incoming messages.</summary>
    public required ulong RecipientMask { get; init; }

    /// <summary>True when this is an outgoing <c>svc_UserMessage</c> and <see cref="UserMessageType"/> is populated.</summary>
    public required bool HasUserMessageType { get; init; }

    /// <summary>Nested user-message id for <c>svc_UserMessage</c>, or -1 when unavailable.</summary>
    public required int UserMessageType { get; init; }

    /// <summary>True when this is an incoming pause request and pause request fields are populated.</summary>
    public required bool HasPauseRequest { get; init; }

    /// <summary>Pause request type for <c>clc_RequestPause</c>.</summary>
    public required int PauseType { get; init; }

    /// <summary>Pause request group for <c>clc_RequestPause</c>.</summary>
    public required int PauseGroup { get; init; }

    /// <summary>True when this is an outgoing pause state update and <see cref="Paused"/> is populated.</summary>
    public required bool HasPauseState { get; init; }

    /// <summary>Pause state for <c>svc_SetPause</c>.</summary>
    public required bool Paused { get; init; }
}
