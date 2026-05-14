using System.Runtime.InteropServices;
using System.Text;
using DeadworksManaged.Api;
using Google.Protobuf;

namespace DeadworksManaged;

public static class EntryPoint
{
    [UnmanagedCallersOnly]
    public static unsafe void Initialize(nint callbacksPtr)
    {
        var callbacks = (NativeCallbacks*)callbacksPtr;

        Console.SetOut(new NativeLogWriter((delegate* unmanaged[Cdecl]<char*, void>)callbacks->Log));
        Console.WriteLine("Hello from .NET 10!");

        NativeInterop.Bind(callbacks);
        PluginLoader.LoadAll();
    }

    [UnmanagedCallersOnly]
    public static unsafe void OnStartupServer(byte* mapNamePtr)
    {
        Players.ResetAll();
        Server.MapName = Marshal.PtrToStringUTF8((nint)mapNamePtr) ?? "";
        PluginLoader.DispatchStartupServer();
    }

    [UnmanagedCallersOnly]
    public static void OnGameFrame(byte simulating, byte firstTick, byte lastTick)
    {
        PluginLoader.DispatchGameFrame(simulating != 0, firstTick != 0, lastTick != 0);
    }

    [UnmanagedCallersOnly]
    public static unsafe int OnGameEvent(byte* eventNamePtr, void* eventPtr)
    {
        string name = Marshal.PtrToStringUTF8((nint)eventNamePtr)!;
        var gameEvent = GameEventFactory.Create(name, (nint)eventPtr);
        return (int)PluginLoader.DispatchGameEvent(name, gameEvent);
    }

    [UnmanagedCallersOnly]
    public static unsafe byte OnTakeDamageOld(void* entity, void* info, void* result)
    {
        var args = new TakeDamageEvent
        {
            Entity = new CBaseEntity((nint)entity),
            Info = CTakeDamageInfo.FromExisting((nint)info)
        };

        return PluginLoader.DispatchTakeDamage(args) >= HookResult.Stop ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly]
    public static unsafe int OnClientConCommand(void* controller, byte* commandUtf8, int argc, byte** argv)
    {
        var command = Encoding.UTF8.GetString(
            MemoryMarshal.CreateReadOnlySpanFromNullTerminated(commandUtf8));

        var args = new string[argc];
        for (int i = 0; i < argc; i++)
        {
            args[i] = Encoding.UTF8.GetString(
                MemoryMarshal.CreateReadOnlySpanFromNullTerminated(argv[i]));
        }

        var e = new ClientConCommandEvent
        {
            ControllerPtr = (nint)controller,
            Command = command,
            Args = args
        };

        return (int)PluginLoader.DispatchClientConCommand(e);
    }

    [UnmanagedCallersOnly]
    public static unsafe void OnConCommandDispatch(int playerSlot, byte* commandUtf8, int argc, byte** argv)
    {
        var command = Encoding.UTF8.GetString(
            MemoryMarshal.CreateReadOnlySpanFromNullTerminated(commandUtf8));

        var args = new string[argc];
        for (int i = 0; i < argc; i++)
        {
            args[i] = Encoding.UTF8.GetString(
                MemoryMarshal.CreateReadOnlySpanFromNullTerminated(argv[i]));
        }

        ConCommandManager.Dispatch(playerSlot, command, args);
    }

    [UnmanagedCallersOnly]
    public static unsafe byte OnModifyCurrency(void* pawn, uint currencyType, int amount,
        uint source, byte silent, byte forceGain, byte spendOnly,
        void* sourceAbility, void* sourceEntity)
    {
        var args = new ModifyCurrencyEvent
        {
            Pawn = new CCitadelPlayerPawn((nint)pawn),
            CurrencyType = (ECurrencyType)currencyType,
            Amount = amount,
            Source = (ECurrencySource)source,
            Silent = silent != 0,
            ForceGain = forceGain != 0,
            SpendOnly = spendOnly != 0
        };

        return PluginLoader.DispatchModifyCurrency(args) >= HookResult.Stop ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly]
    public static unsafe int OnNetMessageOutgoing(int msgId, byte* protoBytes, int protoLen, ulong recipientMask, byte* outBytes, int* outLen, ulong* outRecipientMask)
    {
        var span = new ReadOnlySpan<byte>(protoBytes, protoLen);
        var result = PluginLoader.DispatchNetMessageOutgoing(msgId, span, recipientMask, out var modifiedBytes, out var modifiedRecipientMask);

        *outRecipientMask = modifiedRecipientMask;

        if (modifiedBytes != null)
        {
            var outSpan = new Span<byte>(outBytes, 65536);
            modifiedBytes.AsSpan().CopyTo(outSpan);
            *outLen = modifiedBytes.Length;
        }
        else
        {
            *outLen = 0;
        }

        return (int)result;
    }

    [UnmanagedCallersOnly]
    public static unsafe int OnNetMessageIncoming(int senderSlot, int msgId, byte* protoBytes, int protoLen)
    {
        var span = new ReadOnlySpan<byte>(protoBytes, protoLen);
        var result = PluginLoader.DispatchNetMessageIncoming(senderSlot, msgId, span);
        if (result >= HookResult.Stop)
            return (int)result;

        // Chat message dispatch
        if (msgId == (int)ECitadelClientMessages.CitadelCmChatMsg)
        {
            var chatMsg = CCitadelClientMsg_ChatMsg.Parser.ParseFrom(span);
            if (chatMsg.HasChatText)
            {
                var message = new ChatMessage
                {
                    SenderSlot = senderSlot,
                    SenderSteamId64 = TryGetSteamId64(senderSlot),
                    ChatText = chatMsg.ChatText,
                    AllChat = chatMsg.HasAllChat && chatMsg.AllChat,
                    LaneColor = chatMsg.HasLaneColor ? (LaneColor)(int)chatMsg.LaneColor : LaneColor.Invalid
                };

                if (PluginLoader.DispatchChatMessage(message) >= HookResult.Stop)
                    return (int)HookResult.Stop;
            }
        }

        return (int)result;
    }

    private static unsafe ulong? TryGetSteamId64(int slot)
    {
        if (NativeInterop.GetPlayerController == null)
            return null;

        var ptr = NativeInterop.GetPlayerController(slot);
        return ptr != null
            ? new CCitadelPlayerController((nint)ptr).PlayerSteamId
            : null;
    }

    [UnmanagedCallersOnly]
    public static unsafe byte OnClientConnect(int slot, char* name, ulong xuid, char* ipAddress)
    {
        var args = new ClientConnectEvent
        {
            Slot = slot,
            Name = new string(name),
            SteamId = xuid,
            IpAddress = new string(ipAddress)
        };

        return PluginLoader.DispatchClientConnect(args) ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly]
    public static unsafe void OnClientPutInServer(int slot, char* name, ulong xuid, byte isBot)
    {
        var args = new ClientPutInServerEvent
        {
            Slot = slot,
            Name = new string(name),
            Xuid = xuid,
            IsBot = isBot != 0
        };

        PluginLoader.DispatchClientPutInServer(args);
    }

    [UnmanagedCallersOnly]
    public static void OnClientFullConnect(int slot)
    {
        Players.SetConnected(slot, true);
        var args = new ClientFullConnectEvent { Slot = slot };
        PluginLoader.DispatchClientFullConnect(args);
    }

    [UnmanagedCallersOnly]
    public static void OnClientDisconnect(int slot, int reason)
    {
        var args = new ClientDisconnectedEvent { Slot = slot, Reason = reason };
        PluginLoader.DispatchClientDisconnect(args);
        Players.SetConnected(slot, false);
    }

    [UnmanagedCallersOnly]
    public static void OnPrecacheResources()
    {
        PluginLoader.DispatchPrecacheResources();
    }

    [UnmanagedCallersOnly]
    public static unsafe void OnEntityCreated(void* entity)
    {
        var args = new EntityCreatedEvent { Entity = new CBaseEntity((nint)entity) };
        PluginLoader.DispatchEntityCreated(args);
    }

    [UnmanagedCallersOnly]
    public static unsafe void OnEntitySpawned(void* entity)
    {
        var args = new EntitySpawnedEvent { Entity = new CBaseEntity((nint)entity) };
        PluginLoader.DispatchEntitySpawned(args);
    }

    [UnmanagedCallersOnly]
    public static unsafe void OnEntityDeleted(void* entity)
    {
        var args = new EntityDeletedEvent { Entity = new CBaseEntity((nint)entity) };
        PluginLoader.DispatchEntityDeleted(args);
    }

    [UnmanagedCallersOnly]
    public static unsafe void OnEntityStartTouch(void* entity, void* other)
    {
        if (entity == null || other == null) return;
        var args = new EntityTouchEvent
        {
            Entity = new CBaseEntity((nint)entity),
            Other = new CBaseEntity((nint)other)
        };
        PluginLoader.DispatchEntityStartTouch(args);
    }

    [UnmanagedCallersOnly]
    public static unsafe void OnEntityEndTouch(void* entity, void* other)
    {
        if (entity == null || other == null) return;
        var args = new EntityTouchEvent
        {
            Entity = new CBaseEntity((nint)entity),
            Other = new CBaseEntity((nint)other)
        };
        PluginLoader.DispatchEntityEndTouch(args);
    }

    [UnmanagedCallersOnly]
    public static unsafe void OnEntityAcceptInput(void* entity, void* activator, void* caller, byte* inputNameUtf8, byte* valueUtf8)
    {
        if (entity == null) return;
        var inputName = Marshal.PtrToStringUTF8((nint)inputNameUtf8) ?? "";
        var value = valueUtf8 != null ? Marshal.PtrToStringUTF8((nint)valueUtf8) : null;
        var ent = new CBaseEntity((nint)entity);
        var evt = new EntityInputEvent
        {
            Entity = ent,
            Activator = activator != null ? new CBaseEntity((nint)activator) : null,
            Caller = caller != null ? new CBaseEntity((nint)caller) : null,
            InputName = inputName,
            Value = value
        };
        PluginLoader.DispatchEntityAcceptInput(ent.DesignerName, evt);
    }
    [UnmanagedCallersOnly]
    public static unsafe ulong OnAbilityAttempt(int playerSlot, void* pawnEntity, ulong heldButtons, ulong changedButtons, ulong scrollButtons, ulong* outForcedButtons)
    {
        var args = new AbilityAttemptEvent
        {
            PlayerSlot = playerSlot,
            HeldButtons = (InputButton)heldButtons,
            ChangedButtons = (InputButton)changedButtons,
            ScrollButtons = (InputButton)scrollButtons
        };

        PluginLoader.DispatchAbilityAttempt(args);

        *outForcedButtons = (ulong)args.ForcedButtons;
        return (ulong)args.BlockedButtons;
    }

    [UnmanagedCallersOnly]
    public static unsafe void OnProcessUsercmds(int playerSlot, byte* batchBytes, int batchLen, int numCmds, byte paused, float margin, byte* outBatchBytes, int* outBatchLen)
    {
        *outBatchLen = 0;

        if (batchLen <= 0 || numCmds <= 0)
            return;

        var cmds = new List<CCitadelUserCmdPB>(numCmds);
        int offset = 0;
        var span = new ReadOnlySpan<byte>(batchBytes, batchLen);

        // Snapshot original bytes for modification detection
        var originalBytes = span.ToArray();

        for (int i = 0; i < numCmds && offset + 4 <= batchLen; i++)
        {
            int len = BitConverter.ToInt32(span.Slice(offset, 4));
            offset += 4;

            if (len <= 0 || offset + len > batchLen)
                break;

            var cmd = CCitadelUserCmdPB.Parser.ParseFrom(span.Slice(offset, len));
            cmds.Add(cmd);
            offset += len;
        }

        if (cmds.Count == 0)
            return;

        var args = new ProcessUsercmdsEvent
        {
            PlayerSlot = playerSlot,
            Usercmds = cmds,
            Paused = paused != 0,
            Margin = margin
        };

        PluginLoader.DispatchProcessUsercmds(args);

        // Re-serialize and write back if any modifications were made
        var outSpan = new Span<byte>(outBatchBytes, 65536);
        int writeOffset = 0;

        foreach (var cmd in args.Usercmds)
        {
            var bytes = cmd.ToByteArray();
            if (bytes.Length == 0)
                continue;

            if (writeOffset + 4 + bytes.Length > 65536)
                break;

            BitConverter.TryWriteBytes(outSpan.Slice(writeOffset, 4), bytes.Length);
            writeOffset += 4;

            bytes.CopyTo(outSpan.Slice(writeOffset, bytes.Length));
            writeOffset += bytes.Length;
        }

        // Only signal modification if the serialized output differs from the original
        if (writeOffset > 0 && !outSpan.Slice(0, writeOffset).SequenceEqual(originalBytes.AsSpan()))
        {
            *outBatchLen = writeOffset;
        }
    }

    [UnmanagedCallersOnly]
    public static unsafe void OnCheckTransmit(int playerSlot, void* transmitBits)
    {
        var args = new CheckTransmitEvent(playerSlot, (nint)transmitBits);
        PluginLoader.DispatchCheckTransmit(args);
    }

    [UnmanagedCallersOnly]
    public static unsafe void OnPawnHeroInitialized(void* pawn)
    {
        if (pawn == null) return;
        var p = new CCitadelPlayerPawn((nint)pawn);
        CCitadelPlayerPawn.DrainHeroInitializedContinuations(p);
        PluginLoader.DispatchPawnHeroInitialized(p);
    }

    [UnmanagedCallersOnly]
    public static unsafe int OnAddModifier(void* modifierProp, void** pCaster, uint* pHAbility, int* pITeam, void* vdata, void* modifierParams, void* kv)
    {
        if (modifierProp == null || vdata == null)
            return 0;

        var args = new AddModifierEvent
        {
            ModifierProperty = new CModifierProperty((nint)modifierProp),
            Caster = new CBaseEntity((nint)(*pCaster)),
            AbilityHandle = *pHAbility,
            Team = *pITeam,
            ModifierVData = new CCitadelModifierVData((nint)vdata),
            ModifierParams = modifierParams != null ? new KeyValues3((nint)modifierParams) : null,
            KeyValues = kv != null ? new KeyValues3((nint)kv) : null
        };

        var result = PluginLoader.DispatchAddModifier(args);

        // Write back modified values
        *pCaster = (void*)args.Caster.Handle;
        *pHAbility = args.AbilityHandle;
        *pITeam = args.Team;

        return (int)result;
    }
}
