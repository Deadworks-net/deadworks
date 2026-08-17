using System.Reflection;
using Google.Protobuf;
using DeadworksManaged.Api;

namespace DeadworksManaged;

internal static partial class PluginLoader
{
    // --- Net message infrastructure ---

    private static unsafe void OnNetMessageSend(int msgId, byte[] bytes, ulong recipientMask, bool reliable)
    {
        fixed (byte* ptr = bytes)
        {
            // NetChannelBufType_t: BUF_UNRELIABLE = 0, BUF_RELIABLE = 1.
            NativeInterop.SendNetMessage(msgId, ptr, bytes.Length, recipientMask, reliable ? 1 : 0);
        }
    }

    private static IHandle OnNetMessageHookAddWithHandle(int msgId, NetMessageDirection direction, Delegate handler)
    {
        lock (_lock)
        {
            var dict = direction == NetMessageDirection.Outgoing ? _outgoingNetMsgHandlers : _incomingNetMsgHandlers;
            if (!dict.TryGetValue(msgId, out var list))
            {
                list = new List<Delegate>();
                dict[msgId] = list;
            }
            list.Add(handler);
        }

        return new CallbackHandle(() =>
        {
            lock (_lock)
            {
                var dict = direction == NetMessageDirection.Outgoing ? _outgoingNetMsgHandlers : _incomingNetMsgHandlers;
                if (dict.TryGetValue(msgId, out var list))
                {
                    list.Remove(handler);
                    if (list.Count == 0)
                        dict.Remove(msgId);
                }
            }
        });
    }

    private static void OnNetMessageHookRemove(int msgId, NetMessageDirection direction, Delegate handler)
    {
        lock (_lock)
        {
            var dict = direction == NetMessageDirection.Outgoing ? _outgoingNetMsgHandlers : _incomingNetMsgHandlers;
            if (dict.TryGetValue(msgId, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0)
                    dict.Remove(msgId);
            }
        }
    }

    private static void RegisterPluginNetMessageHandlers(string normalizedPath, List<IDeadworksPlugin> plugins)
    {
        var handlers = new List<(int msgId, NetMessageDirection dir, Delegate handler)>();

        foreach (var plugin in plugins)
        {
            var methods = plugin.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
            {
                if (!method.IsDefined(typeof(NetMessageHandlerAttribute), false))
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1) continue;

                var paramType = parameters[0].ParameterType;
                if (!paramType.IsGenericType) continue;

                // Derive direction from parameter type
                var genDef = paramType.GetGenericTypeDefinition();
                Type? protoType = paramType.GetGenericArguments().FirstOrDefault();
                if (protoType == null) continue;

                NetMessageDirection direction;
                if (genDef == typeof(OutgoingMessageContext<>))
                    direction = NetMessageDirection.Outgoing;
                else if (genDef == typeof(IncomingMessageContext<>))
                    direction = NetMessageDirection.Incoming;
                else
                    continue;

                // Derive message ID from the proto type via registry
                int msgId = NetMessageRegistry.GetMessageId(protoType);
                if (msgId < 0)
                {
                    Console.WriteLine($"[PluginLoader] Warning: no message ID found for {protoType.Name} in {plugin.Name}.{method.Name}, skipping");
                    continue;
                }

                var funcType = typeof(Func<,>).MakeGenericType(paramType, typeof(HookResult));
                var del = Delegate.CreateDelegate(funcType, plugin, method);

                handlers.Add((msgId, direction, del));
                PluginRegistrationTracker.Add(normalizedPath, "netmsg", $"{protoType.Name} ({direction})");

                var dict = direction == NetMessageDirection.Outgoing ? _outgoingNetMsgHandlers : _incomingNetMsgHandlers;
                if (!dict.TryGetValue(msgId, out var list))
                {
                    list = new List<Delegate>();
                    dict[msgId] = list;
                }
                list.Add(del);
                Console.WriteLine($"[PluginLoader] Registered net message handler: {plugin.Name}.{method.Name} -> {protoType.Name} msgId={msgId} ({direction})");
            }
        }

        _pluginNetMsgHandlers[normalizedPath] = handlers;
    }

    private static void UnregisterPluginNetMessageHandlers(string normalizedPath)
    {
        if (!_pluginNetMsgHandlers.Remove(normalizedPath, out var handlers))
            return;

        foreach (var (msgId, dir, handler) in handlers)
        {
            var dict = dir == NetMessageDirection.Outgoing ? _outgoingNetMsgHandlers : _incomingNetMsgHandlers;
            if (dict.TryGetValue(msgId, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0)
                    dict.Remove(msgId);
            }
        }
    }

    public static HookResult DispatchNetMessageOutgoing(int msgId, ReadOnlySpan<byte> protoBytes, ulong recipientMask, out byte[]? modifiedBytes, out ulong modifiedRecipientMask)
    {
        modifiedBytes = null;
        modifiedRecipientMask = recipientMask;

        List<Delegate>? handlers;
        lock (_lock)
        {
            if (!_outgoingNetMsgHandlers.TryGetValue(msgId, out handlers))
                return HookResult.Continue;
            handlers = [.. handlers]; // snapshot
        }

        var parser = NetMessageRegistry.GetParser(msgId);
        if (parser == null)
            return HookResult.Continue;

        IMessage message;
        try
        {
            message = parser.ParseFrom(protoBytes);
        }
        catch
        {
            return HookResult.Continue;
        }

        // Snapshot original bytes for comparison after handlers run
        var originalBytes = protoBytes.ToArray();

        var result = HookResult.Continue;
        var currentRecipientMask = recipientMask;
        foreach (var handler in handlers)
        {
            try
            {
                var hr = InvokeOutgoingHandler(handler, message, msgId, ref currentRecipientMask);
                if (hr > result) result = hr;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginLoader] Net message outgoing handler for msgId={msgId} threw: {ex.Message}");
            }
        }

        modifiedRecipientMask = currentRecipientMask;

        // Re-serialize if any handler may have modified the message
        var newBytes = message.ToByteArray();
        if (!newBytes.AsSpan().SequenceEqual(originalBytes))
            modifiedBytes = newBytes;

        return result;
    }

    public static HookResult DispatchNetMessageIncoming(int senderSlot, int msgId, ReadOnlySpan<byte> protoBytes)
    {
        List<Delegate>? handlers;
        lock (_lock)
        {
            if (!_incomingNetMsgHandlers.TryGetValue(msgId, out handlers))
                return HookResult.Continue;
            handlers = [.. handlers]; // snapshot
        }

        var parser = NetMessageRegistry.GetParser(msgId);
        if (parser == null)
            return HookResult.Continue;

        IMessage message;
        try
        {
            message = parser.ParseFrom(protoBytes);
        }
        catch
        {
            return HookResult.Continue;
        }

        var result = HookResult.Continue;
        foreach (var handler in handlers)
        {
            try
            {
                var hr = InvokeIncomingHandler(handler, message, msgId, senderSlot);
                if (hr > result) result = hr;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginLoader] Net message incoming handler for msgId={msgId} threw: {ex.Message}");
            }
        }

        return result;
    }

    private static HookResult InvokeOutgoingHandler(Delegate handler, IMessage message, int msgId, ref ulong recipientMask)
    {
        // handler is Func<OutgoingMessageContext<T>, HookResult> - invoke via DynamicInvoke
        var handlerType = handler.GetType();
        var genArgs = handlerType.GetGenericArguments();
        if (genArgs.Length < 1) return HookResult.Continue;

        var contextParamType = genArgs[0]; // OutgoingMessageContext<T>
        if (!contextParamType.IsGenericType) return HookResult.Continue;

        var filter = new RecipientFilter { Mask = recipientMask };
        var ctx = Activator.CreateInstance(contextParamType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, [message, msgId, filter], null);

        var result = (HookResult)handler.DynamicInvoke(ctx)!;

        // Read back possibly modified recipient mask
        var recipientsProp = contextParamType.GetProperty("Recipients");
        if (recipientsProp != null)
        {
            var modifiedFilter = (RecipientFilter)recipientsProp.GetValue(ctx)!;
            recipientMask = modifiedFilter.Mask;
        }

        return result;
    }

    private static HookResult InvokeIncomingHandler(Delegate handler, IMessage message, int msgId, int senderSlot)
    {
        var handlerType = handler.GetType();
        var genArgs = handlerType.GetGenericArguments();
        if (genArgs.Length < 1) return HookResult.Continue;

        var contextParamType = genArgs[0]; // IncomingMessageContext<T>
        if (!contextParamType.IsGenericType) return HookResult.Continue;

        var ctx = Activator.CreateInstance(contextParamType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, [message, msgId, senderSlot], null);

        return (HookResult)handler.DynamicInvoke(ctx)!;
    }
}
