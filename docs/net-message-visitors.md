# Net message visitors

Deadworks now gates protobuf net-message work behind managed interest. The existing full protobuf APIs remain the compatibility/mutation path, while read-only observers can mount compact native visitors for selected message ids.

## Model

The net provider uses Deadworks' existing native hooks:

- incoming client->server messages: `CServerSideClientBase::FilterMessage`
- outgoing server->client messages: `IGameEventSystem::PostEventAbstract`

On each hook, Deadworks first checks native interest tables. With no serialized or fast interest for the message id, the hook returns without protobuf serialization and without managed dispatch.

There are two separate mounts:

1. **Fast visitor interest** - read-only native extraction for curated fields, dispatched through `OnFastNetMessage(FastNetMessageEvent args)`.
2. **Serialized interest** - full protobuf compatibility path, mounted automatically when plugins register `NetMessages.HookIncoming<T>()`, `NetMessages.HookOutgoing<T>()`, `[NetMessageHandler]`, chat commands, or `OnChatMessage`.

## Fast visitor API

```csharp
public override void OnLoad(bool isReload)
{
    NetMessages.VisitIncoming((int)CLC_Messages.ClcRequestPause);
    NetMessages.VisitOutgoing((int)SVC_Messages.SvcSetPause);
    NetMessages.VisitUserMessage(userMessageType: 123);
    NetMessages.VisitUserMessage<CCitadelUserMsg_ChatMsg>();
}

public override void OnFastNetMessage(FastNetMessageEvent args)
{
    if (args.HasPauseRequest)
        Console.WriteLine($"pause request slot={args.EndpointSlot} type={args.PauseType} group={args.PauseGroup}");

    if (args.HasPauseState)
        Console.WriteLine($"pause state paused={args.Paused}");

    if (args.HasUserMessageType)
        Console.WriteLine($"user message type={args.UserMessageType} recipients=0x{args.RecipientMask:X}");
}
```

Current curated fast fields:

| Message | Direction | Fields |
|---|---|---|
| `clc_RequestPause` (`33`) | incoming | `PauseType`, `PauseGroup` |
| `svc_SetPause` (`43`) | outgoing | `Paused` |
| `svc_UserMessage` (`72`) | outgoing | nested `UserMessageType` |

`NetMessages.VisitUserMessage(id)` is a second-level interest gate for outgoing user messages. When the engine surfaces the `svc_UserMessage` envelope, Deadworks reads only `msg_type` and calls managed code only when the nested type is mounted; if the hook receives an inner user-message id directly, the same interest table is used.

## Full protobuf compatibility path

Existing APIs still work and now mount serialized interest for only the requested ids. Outgoing user-message protobuf hooks also mount an inner `svc_UserMessage` gate, so the envelope payload is parsed only for matching `msg_type` values:

```csharp
private IHandle? _hook;

public override void OnLoad(bool isReload)
{
    _hook = NetMessages.HookIncoming<CCitadelClientMsg_ChatMsg>(ctx =>
    {
        Console.WriteLine(ctx.Message.ChatText);
        return HookResult.Continue;
    });
}

public override void OnUnload()
{
    _hook?.Cancel();
}
```

Use the full protobuf path when a plugin needs mutation, blocking, recipient-mask changes, or fields not present in `FastNetMessageEvent`.

## Capabilities

```csharp
if (NativeFeatures.HasCapability("netmsg.interest_gates")) { /* serialized path is gated */ }
if (NativeFeatures.HasCapability("netmsg.fast_read")) { /* message-id fast visitors */ }
if (NativeFeatures.HasCapability("netmsg.user_message_interest_gates")) { /* nested full-protobuf user-message gates */ }
if (NativeFeatures.HasCapability("netmsg.user_message_fast_read")) { /* nested fast svc_UserMessage gate */ }
```

## Diagnostics

Set `DEADWORKS_NETMSG_PROBE_LOG=1` to emit aggregate counters such as `incomingSeen`, `outgoingSeen`, `serializedIncomingHits`, `fastOutgoingCallbacks`, and `noInterestOutgoing`. `DEADWORKS_NETMSG_PROBE_LOG_WINDOW_MS` controls the log interval.

`examples/plugins/DiagnosticsPlugin` is a deliberately chatty local test plugin that mounts the visitor paths and exposes `/heal N`, `/damage N`, `/teleport`, and `/respawn`.
