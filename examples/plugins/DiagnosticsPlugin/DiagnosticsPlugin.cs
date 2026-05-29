using System.Numerics;
using DeadworksManaged.Api;

namespace DiagnosticsPlugin;

public sealed class DiagnosticsPlugin : DeadworksPluginBase
{
    public override string Name => "Diagnostics";

    private readonly List<IHandle> _handles = [];
    private readonly Dictionary<int, int> _usercmdBatches = new();
    private readonly bool _verboseTouch = EnvEnabled("DEADWORKS_DIAGNOSTICS_VERBOSE_TOUCH");
    private long _touchStarts;
    private long _touchEnds;
    private string? _lastTouchStart;
    private string? _lastTouchEnd;

    public override void OnLoad(bool isReload)
    {
        _handles.Add(Timer.Every(5.Seconds(), FlushTouchSummary));

        if (NativeFeatures.HasCapability("netmsg.fast_read"))
        {
            _handles.Add(NetMessages.VisitIncoming((int)CLC_Messages.ClcRequestPause));
            _handles.Add(NetMessages.VisitOutgoing((int)SVC_Messages.SvcSetPause));
            _handles.Add(NetMessages.VisitUserMessage<CCitadelUserMsg_ChatMsg>());
        }

        Console.WriteLine(isReload ? "[Diagnostics] reloaded" : "[Diagnostics] loaded");
    }

    public override void OnUnload()
    {
        foreach (var handle in _handles)
            handle.Cancel();
        _handles.Clear();
        Console.WriteLine("[Diagnostics] unloaded");
    }

    public override void OnFastProcessUsercmds(FastProcessUsercmdsEvent args)
    {
        int count = _usercmdBatches.GetValueOrDefault(args.PlayerSlot) + 1;
        _usercmdBatches[args.PlayerSlot] = count;
        if (count % 500 != 0)
            return;

        var latest = args.Latest;
        Console.WriteLine($"[Diagnostics] usercmd slot={args.PlayerSlot} cmds={args.Usercmds.Length} tick={latest.ClientTick} buttons=0x{latest.Buttons:X} yaw={latest.Yaw:F1}");
    }

    public override void OnFastNetMessage(FastNetMessageEvent args)
    {
        if (args.HasPauseRequest)
        {
            Console.WriteLine($"[Diagnostics] net pause request slot={args.EndpointSlot} type={args.PauseType} group={args.PauseGroup}");
            return;
        }

        if (args.HasPauseState)
        {
            Console.WriteLine($"[Diagnostics] net pause state paused={args.Paused} recipients=0x{args.RecipientMask:X}");
            return;
        }

        if (args.HasUserMessageType)
            Console.WriteLine($"[Diagnostics] net usermessage type={args.UserMessageType} recipients=0x{args.RecipientMask:X}");
    }

    public override HookResult OnChatMessage(ChatMessage message)
    {
        Console.WriteLine($"[Diagnostics] chat slot={message.SenderSlot} all={message.AllChat}: {message.ChatText}");
        return HookResult.Continue;
    }

    public override void OnClientFullConnect(ClientFullConnectEvent args)
        => Console.WriteLine($"[Diagnostics] client full connect slot={args.Slot}");

    public override void OnClientDisconnect(ClientDisconnectedEvent args)
        => Console.WriteLine($"[Diagnostics] client disconnect slot={args.Slot} reason={args.Reason}");

    public override void OnEntityStartTouch(EntityTouchEvent args)
        => RecordTouch(started: true, args);

    public override void OnEntityEndTouch(EntityTouchEvent args)
        => RecordTouch(started: false, args);

    [GameEventHandler("player_death")]
    public HookResult OnPlayerDeath(PlayerDeathEvent args)
    {
        Console.WriteLine($"[Diagnostics] gameevent player_death victim={args.UseridPawn?.EntityIndex ?? -1} attacker={args.AttackerPawn?.EntityIndex ?? -1}");
        return HookResult.Continue;
    }

    [GameEventHandler("player_respawned")]
    public HookResult OnPlayerRespawned(PlayerRespawnedEvent args)
    {
        Console.WriteLine($"[Diagnostics] gameevent player_respawned pawn={args.Userid?.EntityIndex ?? -1}");
        return HookResult.Continue;
    }

    [Command("heal", Description = "Heal yourself by N health")]
    public void Heal(CCitadelPlayerController caller, float amount)
    {
        var pawn = RequirePawn(caller);
        int healed = pawn.Heal(MathF.Max(0, amount));
        Reply(caller, $"Healed {healed}. Health={pawn.Health}/{pawn.GetMaxHealth()}");
        Console.WriteLine($"[Diagnostics] /heal slot={caller.Slot} amount={amount} healed={healed}");
    }

    [Command("damage", Description = "Damage yourself by N health")]
    public void Damage(CCitadelPlayerController caller, float amount)
    {
        var pawn = RequirePawn(caller);
        pawn.Hurt(MathF.Max(0, amount), attacker: pawn, inflictor: pawn);
        Reply(caller, $"Damaged {amount}. Health={pawn.Health}/{pawn.GetMaxHealth()}");
        Console.WriteLine($"[Diagnostics] /damage slot={caller.Slot} amount={amount}");
    }

    [Command("teleport", "tp", Description = "Teleport to the point you are looking at")]
    public void Teleport(CCitadelPlayerController caller)
    {
        var pawn = RequirePawn(caller);
        var forward = ForwardFromAngles(pawn.ViewAngles);
        var start = pawn.EyePosition;
        var end = start + forward * 8192f;
        var trace = Trace.Ray(start, end, ignore: pawn);
        var destination = (trace.DidHit ? trace.HitPosition - forward * 48f : end) + new Vector3(0, 0, 16f);

        pawn.Teleport(position: destination, velocity: Vector3.Zero);
        Reply(caller, $"Teleported to {destination.X:F0}, {destination.Y:F0}, {destination.Z:F0}");
        Console.WriteLine($"[Diagnostics] /teleport slot={caller.Slot} hit={trace.DidHit} dest={destination}");
    }

    [Command("respawn", Description = "Request an immediate respawn")]
    public void Respawn(CCitadelPlayerController caller)
    {
        var pawn = caller.GetHeroPawn();
        if (pawn != null)
            pawn.RespawnTime = 0;
        Server.ClientCommand(caller.Slot, "respawn");
        Reply(caller, "Respawn requested");
        Console.WriteLine($"[Diagnostics] /respawn slot={caller.Slot}");
    }

    private void RecordTouch(bool started, EntityTouchEvent args)
    {
        string description = $"{Describe(args.Entity)} -> {Describe(args.Other)}";
        if (_verboseTouch)
        {
            Console.WriteLine($"[Diagnostics] touch {(started ? "start" : "end")} {description}");
            return;
        }

        if (started)
        {
            _touchStarts++;
            _lastTouchStart = description;
        }
        else
        {
            _touchEnds++;
            _lastTouchEnd = description;
        }
    }

    private void FlushTouchSummary()
    {
        if (_verboseTouch)
            return;

        long starts = _touchStarts;
        long ends = _touchEnds;
        if (starts == 0 && ends == 0)
            return;

        _touchStarts = 0;
        _touchEnds = 0;
        string lastStart = _lastTouchStart ?? "<none>";
        string lastEnd = _lastTouchEnd ?? "<none>";
        _lastTouchStart = null;
        _lastTouchEnd = null;
        Console.WriteLine($"[Diagnostics] touch summary start={starts} end={ends} lastStart={lastStart} lastEnd={lastEnd}");
    }

    private static CCitadelPlayerPawn RequirePawn(CCitadelPlayerController caller)
        => caller.GetHeroPawn() ?? throw new CommandException("You do not have a hero pawn yet.");

    private static void Reply(CCitadelPlayerController caller, string message)
        => Chat.PrintToChat(caller, "[Diagnostics] " + message);

    private static bool EnvEnabled(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value)
            && value != "0"
            && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(CBaseEntity? entity)
        => entity == null || !entity.IsValid ? "<null>" : $"{entity.Classname}#{entity.EntityIndex}";

    private static Vector3 ForwardFromAngles(Vector3 angles)
    {
        float pitch = angles.X * MathF.PI / 180f;
        float yaw = angles.Y * MathF.PI / 180f;
        return Vector3.Normalize(new Vector3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Cos(pitch) * MathF.Sin(yaw),
            -MathF.Sin(pitch)));
    }
}
