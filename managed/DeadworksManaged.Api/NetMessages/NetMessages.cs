using Google.Protobuf;

namespace DeadworksManaged.Api;

/// <summary>
/// Entry point for sending and hooking Source 2 network messages.
/// Messages are identified by their protobuf type; IDs are resolved via <see cref="NetMessageRegistry"/>.
/// </summary>
public static unsafe class NetMessages
{
	internal static Action<int, byte[], ulong>? OnSend;
	internal static Func<int, NetMessageDirection, Delegate, IHandle>? OnHookAdd;
	internal static Action<int, NetMessageDirection, Delegate>? OnHookRemove;

	private static readonly object InterestLock = new();
	private static readonly Dictionary<(NetMessageDirection Direction, int MsgId), int> SerializedInterestCounts = new();
	private static readonly Dictionary<(NetMessageDirection Direction, int MsgId), int> FastInterestCounts = new();
	private static readonly Dictionary<int, int> UserMessageSerializedInterestCounts = new();
	private static readonly Dictionary<int, int> UserMessageFastInterestCounts = new();

	/// <summary>
	/// Sends a protobuf net message to the specified recipients.
	/// </summary>
	/// <typeparam name="T">The protobuf message type. Must be registered in <see cref="NetMessageRegistry"/>.</typeparam>
	/// <param name="message">The message to send.</param>
	/// <param name="recipients">Which players should receive this message.</param>
	/// <exception cref="InvalidOperationException">Thrown if <typeparamref name="T"/> has no registered message ID.</exception>
	public static void Send<T>(T message, RecipientFilter recipients) where T : IMessage<T>
	{
		int msgId = NetMessageRegistry.GetMessageId<T>();
		if (msgId < 0)
			throw new InvalidOperationException($"No message ID registered for {typeof(T).Name}");

		byte[] bytes = message.ToByteArray();
		OnSend?.Invoke(msgId, bytes, recipients.Mask);
	}

	/// <summary>
	/// Registers a hook that fires before a server→client message of type <typeparamref name="T"/> is sent.
	/// </summary>
	/// <typeparam name="T">The protobuf message type to intercept.</typeparam>
	/// <param name="handler">Called with the message context; return <see cref="HookResult.Handled"/> to suppress the message.</param>
	/// <returns>A handle that keeps the hook alive. Call <see cref="IHandle.Cancel"/> or <see cref="UnhookOutgoing{T}"/> to remove it.</returns>
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
	/// <returns>A handle that keeps the hook alive. Call <see cref="IHandle.Cancel"/> or <see cref="UnhookIncoming{T}"/> to remove it.</returns>
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

	/// <summary>Mounts the read-only native visitor for an incoming message id.</summary>
	public static IHandle VisitIncoming(int msgId) => AddFastInterest(NetMessageDirection.Incoming, msgId);

	/// <summary>Mounts the read-only native visitor for an outgoing message id.</summary>
	public static IHandle VisitOutgoing(int msgId) => AddFastInterest(NetMessageDirection.Outgoing, msgId);

	/// <summary>Mounts the read-only native visitor for an incoming message type.</summary>
	public static IHandle VisitIncoming<T>() where T : IMessage<T>, new()
	{
		int msgId = NetMessageRegistry.GetMessageId<T>();
		if (msgId < 0)
			throw new InvalidOperationException($"No message ID registered for {typeof(T).Name}");
		return VisitIncoming(msgId);
	}

	/// <summary>Mounts the read-only native visitor for an outgoing message type.</summary>
	public static IHandle VisitOutgoing<T>() where T : IMessage<T>, new()
	{
		int msgId = NetMessageRegistry.GetMessageId<T>();
		if (msgId < 0)
			throw new InvalidOperationException($"No message ID registered for {typeof(T).Name}");
		return VisitOutgoing(msgId);
	}

	/// <summary>
	/// Mounts the read-only native visitor for an outgoing <c>svc_UserMessage</c> inner message type.
	/// The full user-message payload is not parsed; <see cref="FastNetMessageEvent.UserMessageType"/> identifies matches.
	/// </summary>
	public static IHandle VisitUserMessage(int userMessageType) => AddUserMessageFastInterest(userMessageType);

	/// <summary>Mounts the read-only native visitor for an outgoing <c>svc_UserMessage</c> inner protobuf type.</summary>
	public static IHandle VisitUserMessage<T>() where T : IMessage<T>, new()
	{
		int userMessageType = NetMessageRegistry.GetMessageId<T>();
		if (userMessageType < 0)
			throw new InvalidOperationException($"No message ID registered for {typeof(T).Name}");
		return VisitUserMessage(userMessageType);
	}

	internal static void AddSerializedInterest(NetMessageDirection direction, int msgId)
	{
		if (NativeInterop.SetNetMessageSerializedInterest == null)
			return;

		var key = (direction, msgId);
		lock (InterestLock)
		{
			SerializedInterestCounts.TryGetValue(key, out int count);
			SerializedInterestCounts[key] = count + 1;
			if (count == 0)
				NativeInterop.SetNetMessageSerializedInterest((int)direction, msgId, 1);

			if (direction == NetMessageDirection.Outgoing && NetMessageRegistry.IsUserMessageId(msgId))
				AddUserMessageSerializedInterestLocked(msgId);
		}
	}

	internal static void RemoveSerializedInterest(NetMessageDirection direction, int msgId)
	{
		if (NativeInterop.SetNetMessageSerializedInterest == null)
			return;

		var key = (direction, msgId);
		lock (InterestLock)
		{
			if (!SerializedInterestCounts.TryGetValue(key, out int count))
				return;
			if (count <= 1)
			{
				SerializedInterestCounts.Remove(key);
				NativeInterop.SetNetMessageSerializedInterest((int)direction, msgId, 0);
			}
			else
			{
				SerializedInterestCounts[key] = count - 1;
			}

			if (direction == NetMessageDirection.Outgoing && NetMessageRegistry.IsUserMessageId(msgId))
				RemoveUserMessageSerializedInterestLocked(msgId);
		}
	}

	private static IHandle AddFastInterest(NetMessageDirection direction, int msgId)
	{
		if (NativeInterop.SetNetMessageFastInterest == null)
			throw new NotSupportedException("Native net message visitors are not available in this Deadworks build.");

		var key = (direction, msgId);
		lock (InterestLock)
		{
			FastInterestCounts.TryGetValue(key, out int count);
			FastInterestCounts[key] = count + 1;
			if (count == 0)
				NativeInterop.SetNetMessageFastInterest((int)direction, msgId, 1);
		}

		return new CallbackHandle(() => RemoveFastInterest(direction, msgId));
	}

	private static void RemoveFastInterest(NetMessageDirection direction, int msgId)
	{
		if (NativeInterop.SetNetMessageFastInterest == null)
			return;

		var key = (direction, msgId);
		lock (InterestLock)
		{
			if (!FastInterestCounts.TryGetValue(key, out int count))
				return;
			if (count <= 1)
			{
				FastInterestCounts.Remove(key);
				NativeInterop.SetNetMessageFastInterest((int)direction, msgId, 0);
			}
			else
			{
				FastInterestCounts[key] = count - 1;
			}
		}
	}

	private static void AddUserMessageSerializedInterestLocked(int userMessageType)
	{
		if (NativeInterop.SetUserMessageSerializedInterest == null)
			return;

		UserMessageSerializedInterestCounts.TryGetValue(userMessageType, out int count);
		UserMessageSerializedInterestCounts[userMessageType] = count + 1;
		if (count == 0)
			NativeInterop.SetUserMessageSerializedInterest(userMessageType, 1);
	}

	private static void RemoveUserMessageSerializedInterestLocked(int userMessageType)
	{
		if (NativeInterop.SetUserMessageSerializedInterest == null)
			return;

		if (!UserMessageSerializedInterestCounts.TryGetValue(userMessageType, out int count))
			return;
		if (count <= 1)
		{
			UserMessageSerializedInterestCounts.Remove(userMessageType);
			NativeInterop.SetUserMessageSerializedInterest(userMessageType, 0);
		}
		else
		{
			UserMessageSerializedInterestCounts[userMessageType] = count - 1;
		}
	}

	private static IHandle AddUserMessageFastInterest(int userMessageType)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(userMessageType);
		if (NativeInterop.SetUserMessageFastInterest == null)
			throw new NotSupportedException("Native user-message visitors are not available in this Deadworks build.");

		lock (InterestLock)
		{
			UserMessageFastInterestCounts.TryGetValue(userMessageType, out int count);
			UserMessageFastInterestCounts[userMessageType] = count + 1;
			if (count == 0)
				NativeInterop.SetUserMessageFastInterest(userMessageType, 1);
		}

		return new CallbackHandle(() => RemoveUserMessageFastInterest(userMessageType));
	}

	private static void RemoveUserMessageFastInterest(int userMessageType)
	{
		if (NativeInterop.SetUserMessageFastInterest == null)
			return;

		lock (InterestLock)
		{
			if (!UserMessageFastInterestCounts.TryGetValue(userMessageType, out int count))
				return;
			if (count <= 1)
			{
				UserMessageFastInterestCounts.Remove(userMessageType);
				NativeInterop.SetUserMessageFastInterest(userMessageType, 0);
			}
			else
			{
				UserMessageFastInterestCounts[userMessageType] = count - 1;
			}
		}
	}
}
