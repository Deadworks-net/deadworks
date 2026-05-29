# Usercmd visitors

This change keeps Deadworks' existing native hook chassis and adds an opt-in visitor surface for hot usercmd telemetry without forcing the full protobuf path.

Although the first provider is usercmd data, the intended pattern is generic: native hook providers expose a small curated set of fields, managed plugins declare interest in those fields, and Deadworks only does the native reads/callbacks required by mounted interests.

## Visitor model

A visitor is an opt-in native data provider for a hot engine path. It has four parts:

1. **Provider hook** - the existing native hook that observes the engine object at the right time. For usercmds this is `CBasePlayerController::ProcessUsercmds`.
2. **Interest declaration** - managed code or environment configuration requests a mounted feature and, when applicable, a field mask.
3. **Native field extraction** - native code reads only the requested curated fields into a compact ABI-safe struct.
4. **Managed dispatch** - managed callbacks receive the compact event without parsing unrelated protobufs or walking arbitrary memory.

The visitor path is intentionally different from arbitrary memory access:

- fields are curated by Deadworks and exposed as named flags;
- providers should be read-only by default;
- field reads should be guarded by pointer checks, presence bits when available, and exception boundaries around unsafe memory reads;
- no mounted interest means the hook should return to the engine with minimal work;
- full parse/mutation paths remain separate compatibility mounts.

For usercmds, this means `Usercmds.Visit(UsercmdFields.Buttons)` reads buttons but does not read view angles, movement, or serialize `CCitadelUserCmdPB`.

## What Deadworks already hooks

Deadworks already resolves Source 2 interfaces and installs native hooks with SafetyHook and vtable patches. Relevant existing hooks include:

- `CBasePlayerController::ProcessUsercmds` (pre-engine usercmd processing)
- incoming `CServerSideClientBase::FilterMessage`
- outgoing `IGameEventSystem::PostEventAbstract`
- `IGameEventManager2::FireEvent`
- `ISource2GameEntities::CheckTransmit`
- client connect/put-in-server/disconnect
- damage/currency/ability/modifier/entity I/O hooks

So the right place for usercmd memory work is Deadworks' native hook layer, not a second injected shim.

## Usercmd modes

The native `ProcessUsercmds` hook now supports these modes:

| Mode | Value | Behavior |
|---|---:|---|
| `DefaultFromEnvironment` | `-1` | Use env defaults. |
| `SerializeManaged` | `0` | Legacy full protobuf path. Serializes `CCitadelUserCmdPB` and calls `OnProcessUsercmds`. |
| `Off` | `1` | Count nothing beyond hook entry; no protobuf/direct field reads. |
| `CountOnly` | `2` | Count batches/cmds only. |
| `DirectSample` | `3` | Read compact fields natively and log counters only. |
| `FastDispatch` | `4` | Read compact fields and call `OnFastProcessUsercmds`. |
| `MountedPolicy` | `5` | Production policy. Only mounted features do work. |

Default when unset is `MountedPolicy`. With no mounts, the hook does not serialize protobufs and does not read nested usercmd fields.

## Mounted features

Managed API:

```csharp
if (NativeFeatures.HasCapability("usercmd.fast_read"))
{
    Usercmds.Visit(UsercmdFields.Buttons | UsercmdFields.ViewAngles | UsercmdFields.Movement);
}

Usercmds.MountButtonTriggers(InputButton.Attack | InputButton.Ability1);
Usercmds.MountFullProtobuf(); // only for mutation/debug plugins
```

Plugin callbacks:

```csharp
public override void OnFastProcessUsercmds(FastProcessUsercmdsEvent args)
{
    var latest = args.Latest;
    if (latest.HasButtons && latest.IsHeld(InputButton.Attack))
    {
        // Cheap read-only telemetry / anticheat / analytics path.
    }
}

public override void OnUsercmdTrigger(UsercmdTriggerEvent args)
{
    // Fires only when native edge-triggered buttons match.
}
```

`OnProcessUsercmds(ProcessUsercmdsEvent)` remains the full protobuf mutation/debug path. `PluginLoader` auto-mounts `FullProtobuf`, `FastRead`, and `ButtonTriggers` when a loaded plugin overrides the matching callback.

## Fast fields

`FastUsercmd` can carry:

- command index
- client tick
- first 64-bit button state (`InputButton`)
- pitch/yaw/roll
- forward/left move
- has-base/buttons/viewangles flags

Use `Usercmds.Visit(UsercmdFields.X | UsercmdFields.Y)` or `DEADWORKS_USERCMD_FIELDS` to request only the fields a plugin needs. Reads are guarded by protobuf has-bits, pointer checks, and SEH. This path is read-only.

## Environment profile

See `config/deadworks_usercmd_visitors.env.example` for launch variables. The common production profile is:

```text
DEADWORKS_USERCMD_NATIVE_MODE=mounted
DEADWORKS_USERCMD_MOUNTS=fast
DEADWORKS_USERCMD_FIELDS=buttons,view,movement
```

Enable `DEADWORKS_USERCMD_PROBE_LOG=1` to emit `[UsercmdVisitor]` aggregate counters for benchmarking.

## Examples

Read only buttons, view angles, and movement without protobuf serialization:

```csharp
public override void OnLoad(bool isReload)
{
    Usercmds.Visit(UsercmdFields.Buttons | UsercmdFields.ViewAngles | UsercmdFields.Movement);
}

public override void OnFastProcessUsercmds(FastProcessUsercmdsEvent args)
{
    FastUsercmd latest = args.Latest;
    if (latest.HasButtons && latest.IsHeld(InputButton.Attack))
    {
        // Read-only hot-path telemetry.
    }
}
```

Mount native edge-triggered button callbacks:

```csharp
public override void OnLoad(bool isReload)
{
    Usercmds.MountButtonTriggers(InputButton.Attack | InputButton.Jump);
}

public override void OnUsercmdTrigger(UsercmdTriggerEvent args)
{
    Console.WriteLine($"{args.PlayerSlot}: {args.TriggerButtons}");
}
```

Use the legacy protobuf path for plugins that need full command protobuf access or mutation:

```csharp
public override void OnProcessUsercmds(ProcessUsercmdsEvent args)
{
    // Existing Deadworks plugins continue to work. PluginLoader mounts
    // FullProtobuf automatically when this callback is overridden.
}
```

## Public API shape

The PR-facing managed names are Deadworks-generic and avoid product-specific wording:

- `NativeFeatures.NativeApiVersion` and `NativeFeatures.HasCapability(...)`
- `Usercmds`
- `UsercmdFields`
- `FastUsercmd`
- `FastProcessUsercmdsEvent`
- `UsercmdTriggerEvent`

## Adding another visitor provider

Use this shape when adding visitor support for another hot path, for example selected game-rule properties, pawn/controller telemetry, net-message summaries, or trace/visibility signals.

### 1. Pick the provider hook

Start from an existing Deadworks hook whenever possible. Good providers are hooks that already have the native object pointer and timing you need. Avoid adding a new hook if an existing one can publish the same stable data.

A provider should define:

- the native engine object(s) it observes;
- when the snapshot is taken relative to engine processing;
- whether the provider is read-only or supports a separate mutation path;
- the minimum work done when no interest is mounted.

### 2. Define a curated field catalog

Add a `[Flags]` managed enum for fields, similar to `UsercmdFields`. Keep fields coarse enough that plugins can request intent without knowing offsets, but precise enough to avoid unnecessary reads.

```csharp
[Flags]
public enum ExampleFields : uint
{
    None = 0,
    Health = 1u << 0,
    Team = 1u << 1,
    Position = 1u << 2,
    All = Health | Team | Position,
}
```

Do not expose arbitrary "class + field + offset" reads as a visitor. If a field is useful in a hot path, add it to the catalog by name and document the semantics.

### 3. Define the native/managed event ABI

Use a blittable struct for native-to-managed dispatch. Include `Has*` flags when a field can be absent or failed to read.

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct FastExample
{
    public int EntityIndex;
    public byte HasHealth;
    public int Health;
    public byte HasPosition;
    public Vector3 Position;
}
```

Keep ABI structs append-only once public. If a later change needs incompatible layout, add a new struct/callback or bump `NativeFeatures.NativeApiVersion` and gate the managed helper.

### 4. Add mount and field-mask controls

A provider needs native state equivalent to:

- mode or enabled flag;
- mounted features bitmask;
- field mask;
- optional predicate/trigger mask;
- capability string in `HasNativeCapability`.

For usercmds these are `UsercmdMount`, `UsercmdFields`, and capabilities like `usercmd.fast_read`.

### 5. Auto-mount for compatibility callbacks

If a pre-existing plugin callback requires expensive behavior, keep that callback working and mount the expensive path only when needed. Usercmds do this for `OnProcessUsercmds(ProcessUsercmdsEvent)` by mounting `FullProtobuf` only when a plugin overrides that method.

New providers should follow the same rule: old plugins keep working, new plugins can choose cheaper visitor callbacks.

### 6. Instrument and test

Every provider should have counters for:

- batches/events observed;
- managed callbacks dispatched;
- direct/native read operations;
- direct read exceptions/failures;
- expensive parse/serialize operations avoided or performed;
- cumulative native/managed time when practical.

E2E should cover:

- no mounts: no expensive reads and no managed dispatch;
- narrow field mask: only requested fields are read;
- full/legacy mount if compatibility exists;
- mixed deployment behavior via capability/null checks;
- a live server/client smoke test.

## Native callback ABI

This change appends optional native callback entries to the managed callback table:

- `GetNativeApiVersion`
- `HasNativeCapability`
- `SetUsercmdNativeMode` / `GetUsercmdNativeMode`
- `SetUsercmdMountMask` / `GetUsercmdMountMask`
- `SetUsercmdButtonTriggerMask` / `GetUsercmdButtonTriggerMask`
- `SetUsercmdFieldMask` / `GetUsercmdFieldMask`

The entries are appended after existing callbacks so existing plugins that never call the new API keep using the old callback surface. Managed helpers check for null callback pointers and report unsupported features with `NativeFeatures.NativeApiVersion == 0`, `NativeFeatures.HasCapability(...) == false`, or `NotSupportedException` for setter calls.

Deploy native and managed artifacts from the same build when using the new visitor APIs.

## E2E notes

2026-05-29 local dedicated-server smoke test on `dl_midtown`:

- Deadlock/Deadworks server booted and opened UDP `27067`.
- Probe plugin loaded and mounted `FastRead` plus `ButtonTriggers`.
- Client connected, reached full signon, moved/aimed/pressed buttons, then disconnected cleanly.
- Visitor counters showed `serializedCmds=0`, `serializedBytes=0`, `serializeOps=0`, and `parseOps=0` on the fast visitor path.
- Fast managed callbacks fired (`managedCallbacks > 0`).
- Button trigger callbacks fired (`triggerCallbacks > 0`).
- Native direct reads succeeded with `directExceptions=0` while reading buttons, view angles, and movement.
- Existing local plugin smoke test: `DeadlockBhopRuntime.dll` loaded on `dl_midtown`, registered its commands, and did not call the new visitor APIs.
- Runtime long-frame checks were repeated with a quiet probe and with an empty plugin directory/no usercmd mounts. Long frames still reproduced in the empty baseline, while visitor counters stayed at zero/disabled, indicating those long frames are baseline Deadlock/Deadworks server behavior rather than visitor-induced work.

Additional E2E modes to run with a connected client before merge:

- Legacy-only plugin overriding `OnProcessUsercmds(ProcessUsercmdsEvent)` should show `FullProtobuf` auto-mounted and legacy callback counters increasing.
- Buttons-only plugin using `Usercmds.Visit(UsercmdFields.Buttons)` should show button reads/callbacks without angle or movement reads.
