using Google.Protobuf;

namespace DeadworksManaged.Api;

/// <summary>
/// Entry point for sending and hooking Source 2 network messages.
/// Messages are identified by their protobuf type; IDs are resolved via <see cref="NetMessageRegistry"/>.
/// </summary>
public static class NetMessages
{
	internal static Action<int, byte[], ulong, bool>? OnSend;
	internal static Func<int, NetMessageDirection, Delegate, IHandle>? OnHookAdd;
	internal static Action<int, NetMessageDirection, Delegate>? OnHookRemove;

	/// <summary>
	/// Sends a protobuf net message to the specified recipients.
	/// </summary>
	/// <typeparam name="T">The protobuf message type. Must be registered in <see cref="NetMessageRegistry"/>.</typeparam>
	/// <param name="message">The message to send.</param>
	/// <param name="recipients">Which players should receive this message.</param>
	/// <exception cref="InvalidOperationException">Thrown if <typeparamref name="T"/> has no registered message ID.</exception>
	/// <param name="reliable">
	/// Which net-channel buffer to use. Reliable guarantees ordered delivery but
	/// <b>disconnects</b> the client if the server outruns its acks
	/// (NETWORK_DISCONNECT_RELIABLEOVERFLOW) — it degrades off a cliff, not
	/// gracefully. Unreliable simply drops the packet under pressure, which is
	/// the right trade for latest-wins traffic such as telemetry or HUD state.
	/// Anything that must not be lost — layout, ordering, handshakes, or one
	/// fragment of a multi-part message — has to stay reliable.
	/// </param>
	public static void Send<T>(T message, RecipientFilter recipients, bool reliable = true) where T : IMessage<T>
	{
		int msgId = NetMessageRegistry.GetMessageId<T>();
		if (msgId < 0)
			throw new InvalidOperationException($"No message ID registered for {typeof(T).Name}");

		byte[] bytes = message.ToByteArray();
		OnSend?.Invoke(msgId, bytes, recipients.Mask, reliable);
	}

	/// <summary>
	/// Registers a hook that fires before a server→client message of type <typeparamref name="T"/> is sent.
	/// </summary>
	/// <typeparam name="T">The protobuf message type to intercept.</typeparam>
	/// <param name="handler">Called with the message context; return <see cref="HookResult.Handled"/> to suppress the message.</param>
	/// <returns>A handle that keeps the hook alive. Dispose or call <see cref="UnhookOutgoing{T}"/> to remove it.</returns>
	/// <exception cref="InvalidOperationException">Thrown if <typeparamref name="T"/> has no registered message ID.</exception>
	public static IHandle HookOutgoing<T>(Func<OutgoingMessageContext<T>, HookResult> handler)
		where T : IMessage<T>, new()
	{
		int msgId = NetMessageRegistry.GetMessageId<T>();
		if (msgId < 0)
			throw new InvalidOperationException($"No message ID registered for {typeof(T).Name}");

		return OnHookAdd?.Invoke(msgId, NetMessageDirection.Outgoing, handler) ?? CallbackHandle.Noop;
	}

	/// <summary>
	/// Registers a hook that fires when the server receives a client→server message of type <typeparamref name="T"/>.
	/// </summary>
	/// <typeparam name="T">The protobuf message type to intercept.</typeparam>
	/// <param name="handler">Called with the message context; return <see cref="HookResult.Handled"/> to suppress processing.</param>
	/// <returns>A handle that keeps the hook alive. Dispose or call <see cref="UnhookIncoming{T}"/> to remove it.</returns>
	/// <exception cref="InvalidOperationException">Thrown if <typeparamref name="T"/> has no registered message ID.</exception>
	public static IHandle HookIncoming<T>(Func<IncomingMessageContext<T>, HookResult> handler)
		where T : IMessage<T>, new()
	{
		int msgId = NetMessageRegistry.GetMessageId<T>();
		if (msgId < 0)
			throw new InvalidOperationException($"No message ID registered for {typeof(T).Name}");

		return OnHookAdd?.Invoke(msgId, NetMessageDirection.Incoming, handler) ?? CallbackHandle.Noop;
	}

	/// <summary>Removes a previously registered outgoing hook for message type <typeparamref name="T"/>.</summary>
	/// <typeparam name="T">The protobuf message type.</typeparam>
	/// <param name="handler">The exact delegate instance that was passed to <see cref="HookOutgoing{T}"/>.</param>
	public static void UnhookOutgoing<T>(Func<OutgoingMessageContext<T>, HookResult> handler)
		where T : IMessage<T>, new()
	{
		int msgId = NetMessageRegistry.GetMessageId<T>();
		if (msgId >= 0)
			OnHookRemove?.Invoke(msgId, NetMessageDirection.Outgoing, handler);
	}

	/// <summary>Removes a previously registered incoming hook for message type <typeparamref name="T"/>.</summary>
	/// <typeparam name="T">The protobuf message type.</typeparam>
	/// <param name="handler">The exact delegate instance that was passed to <see cref="HookIncoming{T}"/>.</param>
	public static void UnhookIncoming<T>(Func<IncomingMessageContext<T>, HookResult> handler)
		where T : IMessage<T>, new()
	{
		int msgId = NetMessageRegistry.GetMessageId<T>();
		if (msgId >= 0)
			OnHookRemove?.Invoke(msgId, NetMessageDirection.Incoming, handler);
	}
}
