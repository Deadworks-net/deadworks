using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DeadworksManaged.Api;
using DeadworksManaged.PermissionSystem;

namespace DeadworksManaged.Commands;

internal static class CommandRegistration
{
    internal const string DeniedMessage = "You don't have permission to use this command.";
    internal const string NotConfirmedMessage =
        "You don't have permission to use this command yet: your roles apply once Steam has confirmed your account, a few seconds after joining.";
    internal const string OverridesBrokenMessage = "Commands are unavailable until the server fixes an error in its permission settings.";

    /// <summary>A command's permission, looked up on every call so <c>dw_perm_reload</c> applies overrides without re-registering.</summary>
    private sealed class CommandGate(CommandAttribute attr, CommandOverrides.Owner owner)
    {
        public string Permission => CommandOverrides.Resolve(attr.Names, attr.Permission, owner, out _);

        // Auto follows the permission the plugin declares, not the overridden one: making a moderation command public
        // in overrides.jsonc must not let everyone use it on the admins.
        public bool EnforceImmunity => attr.TargetImmunity switch
        {
            TargetImmunity.Enforce => true,
            TargetImmunity.Ignore => false,
            _ => attr.Permission.Length > 0
        };

        /// <summary>
        /// Whether a player may run it. A null player is refused, even for public commands: a player's command whose
        /// controller can't be found must never be treated as the console.
        /// </summary>
        public static bool PlayerMay(CCitadelPlayerController? player, string permission)
            => player != null && !CommandOverrides.Unreadable
               && (permission.Length == 0 || PermissionManager.HasForSlot(player.Slot, permission));

        /// <summary>
        /// Why a player was refused. Staff who just joined hit this before Steam confirms them, and a bare "no permission"
        /// sends them to the owner for nothing; when Steam is down, every admin hits it at once.
        /// </summary>
        public static string RefusalFor(CCitadelPlayerController? player)
        {
            if (CommandOverrides.Unreadable)
                return OverridesBrokenMessage;
            return player != null && !Players.IsAuthorized(player.Slot) ? NotConfirmedMessage : DeniedMessage;
        }

        /// <summary>For listings such as <c>dw_help</c>. A null caller is the server console.</summary>
        public bool CanRun(CCitadelPlayerController? caller)
            => caller == null || (!attr.ServerOnly && PlayerMay(caller, Permission));
    }

    public static void RegisterPluginCommands(
        string normalizedPath,
        List<IDeadworksPlugin> plugins,
        HandlerRegistry<string, Func<ChatCommandContext, HookResult>> chatRegistry,
        string? manifestKey = null)
    {
        foreach (var plugin in plugins)
        {
            var owner = PermissionManifest.OwnerOf(normalizedPath, plugin);
            var manifestCommands = new List<PermissionManifest.CommandInfo>();
            var nullableCallers = new List<string>();
            var methods = plugin.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
            {
                var attrs = method.GetCustomAttributes<CommandAttribute>();
                foreach (var attr in attrs)
                {
                    if (attr.ChatOnly && attr.ConsoleOnly)
                    {
                        Console.WriteLine(
                            $"[CommandRegistration] {plugin.Name}.{method.Name}: ChatOnly and ConsoleOnly both set — skipping");
                        continue;
                    }

                    // An exception escaping an async void method can't be caught here and takes the whole server down.
                    if (method.ReturnType == typeof(void) && method.GetCustomAttribute<AsyncStateMachineAttribute>() != null)
                    {
                        Console.WriteLine($"[CommandRegistration] ERROR: {plugin.Name}.{method.Name} is async void, so an exception in it would "
                                          + "crash the server. Make it return Task. Not registered.");
                        continue;
                    }

                    // Said once at load, rather than as a bare usage line every time someone runs the command.
                    foreach (var parameter in method.GetParameters())
                    {
                        var type = parameter.ParameterType.IsArray ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
                        if (!IsInjected(type) && !CommandBinder.CanConvert(type))
                            Console.WriteLine($"[CommandRegistration] Warning: {plugin.Name}.{method.Name}: parameter '{parameter.Name}' is a "
                                              + $"{type.Name}, which Deadworks can't parse, so the command will only ever show its usage. "
                                              + "Register a parser with CommandConverters.Register in OnLoad.");
                    }

                    CommandBinder.Plan plan;
                    try
                    {
                        plan = CommandBinder.Build(method, attr.Names[0]);
                        if (plan.Slots.Any(s => s.Kind == CommandBinder.SlotKind.Caller && s.Type == typeof(CCitadelPlayerController) && s.CallerNullable))
                            nullableCallers.Add(method.Name);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CommandRegistration] {plugin.Name}.{method.Name}: {ex.Message}");
                        continue;
                    }

                    var gate = new CommandGate(attr, owner);
                    var names = attr.Names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    manifestCommands.Add(new PermissionManifest.CommandInfo(
                        names, attr.Description, attr.Permission.Trim(), attr.TargetImmunity, attr.ChatOnly, attr.ConsoleOnly, attr.ServerOnly, owner));

                    foreach (var name in names)
                    {
                        if (!attr.ConsoleOnly)
                            RegisterChat(normalizedPath, plugin, method, plan, name, attr, gate, chatRegistry);

                        if (!attr.ChatOnly)
                            RegisterConsole(normalizedPath, plugin, method, plan, name, attr, gate);
                    }
                }
            }

            PermissionManifest.Add(normalizedPath, plugin, manifestCommands, manifestKey);

            // One line per plugin: it's advice for the plugin's author, and many existing plugins do this.
            if (nullableCallers.Count > 0)
                Console.WriteLine($"[CommandRegistration] Note for {plugin.Name}'s author: {string.Join(", ", nullableCallers.Distinct())} take a "
                                  + "CCitadelPlayerController? caller, where null quietly means the server console. A Caller parameter "
                                  + "says it outright (caller.IsConsole), and Reply works for both.");
        }
    }

    private static void RegisterChat(
        string normalizedPath,
        IDeadworksPlugin plugin,
        MethodInfo method,
        CommandBinder.Plan plan,
        string name,
        CommandAttribute attr,
        CommandGate gate,
        HandlerRegistry<string, Func<ChatCommandContext, HookResult>> chatRegistry)
    {
        var namedPlan = name == plan.Name ? plan : new CommandBinder.Plan
        {
            Name = name,
            Slots = plan.Slots,
            HasCaller = plan.HasCaller,
            CallerNullable = plan.CallerNullable
        };

        Func<ChatCommandContext, HookResult> handler = ctx =>
        {
            if (attr.ServerOnly)
                return HookResult.Continue;

            var resultOnSuccess = (ctx.Prefix == '!' && !attr.SuppressChat)
                ? HookResult.Continue
                : HookResult.Handled;

            void reply(string msg) => ReplyViaChat(ctx.Controller, msg);

            // Denied attempts are never echoed to chat, so they don't advertise themselves.
            var permission = gate.Permission;
            if (!CommandGate.PlayerMay(ctx.Controller, permission))
            {
                reply(CommandGate.RefusalFor(ctx.Controller));
                NoteRefused(ctx.Controller, $"/{name}", permission);
                return HookResult.Handled;
            }

            if (!CommandBinder.TryBind(namedPlan, ctx.Args, ctx.Controller, out var boundArgs, out var error, out var silentSkip,
                    gate.EnforceImmunity))
            {
                if (silentSkip)
                    return resultOnSuccess;
                if (error != null)
                    reply(error);
                return resultOnSuccess;
            }

            // Chat arrives inside the sender's netchan ProcessMessages, which drops a client past a ~200 ms budget
            // (6712+). The block result above doesn't depend on the method, so it can run next tick.
            TimerEngine.EnqueueNextTick(() => InvokeDeferred(plugin, method, name, boundArgs, ctx.Controller, viaChat: true, $"/{name}"));
            return resultOnSuccess;
        };

        if (chatRegistry.Snapshot(name) is { Count: > 0 })
            Console.WriteLine($"[CommandRegistration] Warning: another plugin already registered /{name}; both will run. Rename one of them.");
        chatRegistry.AddForPlugin(normalizedPath, name, handler);
        PluginRegistrationTracker.Add(normalizedPath, "chat", $"/{name}", attr.Description, attr.Hidden, gate.CanRun,
            IsAlias(attr, name) ? $"/{attr.Names[0]}" : null);
        Console.WriteLine($"[CommandRegistration] Registered chat command: {plugin.Name} -> /{name}");
    }

    private static bool IsInjected(Type type)
        => type == typeof(Caller) || type == typeof(Target) || typeof(CCitadelPlayerController).IsAssignableFrom(type);

    private static bool IsAlias(CommandAttribute attr, string name) => !name.Equals(attr.Names[0], StringComparison.OrdinalIgnoreCase);

    private static string ConCommandName(string name) => "dw_" + name;

    private static void RegisterConsole(
        string normalizedPath,
        IDeadworksPlugin plugin,
        MethodInfo method,
        CommandBinder.Plan plan,
        string name,
        CommandAttribute attr,
        CommandGate gate)
    {
        var conName = ConCommandName(name);
        var namedPlan = conName == plan.Name ? plan : new CommandBinder.Plan
        {
            Name = conName,
            Slots = plan.Slots,
            HasCaller = plan.HasCaller,
            CallerNullable = plan.CallerNullable
        };

        Action<ConCommandContext> handler = ctx =>
        {
            if (attr.ServerOnly && !ctx.IsServerCommand)
                return;

            void reply(string msg) => ReplyViaConsole(ctx.Controller, msg);

            // The engine's CCommand has already tokenized the line and stripped the quotes, so
            // re-tokenizing a space-joined copy would split "one two" back into two tokens.
            var tokens = ctx.Args.Length > 1 ? ctx.Args[1..] : [];

            var permission = gate.Permission;
            var caller = ctx.Controller;
            if (!ctx.IsServerCommand && !CommandGate.PlayerMay(caller, permission))
            {
                reply(CommandGate.RefusalFor(caller));
                NoteRefused(caller, conName, permission);
                return;
            }

            if (!CommandBinder.TryBind(namedPlan, tokens, caller, out var boundArgs, out var error, out var silentSkip,
                    gate.EnforceImmunity))
            {
                // The server console ran a command written for players only; say so rather than do nothing.
                if (silentSkip)
                {
                    reply("Only players can run this command.");
                    return;
                }
                if (error != null)
                    reply(error);
                return;
            }

            // Client commands share the netchan budget (see RegisterChat); server/RCON stay synchronous for output capture.
            if (ctx.IsServerCommand)
                Invoke(plugin, method, conName, boundArgs, ctx.Controller, viaChat: false);
            else
                TimerEngine.EnqueueNextTick(() => InvokeDeferred(plugin, method, conName, boundArgs, ctx.Controller, viaChat: false, conName));
        };

        if (ConCommandManager.IsRegistered(conName))
            Console.WriteLine($"[CommandRegistration] Warning: {conName} is already registered by another plugin; both will run. Rename one of them.");
        ConCommandManager.RegisterExternal(normalizedPath, conName, attr.Description, serverOnly: false, handler, attr.Hidden, gate.CanRun,
            IsAlias(attr, name) ? ConCommandName(attr.Names[0]) : null);
        Console.WriteLine($"[CommandRegistration] Registered console command: {plugin.Name} -> {conName}{(attr.ServerOnly ? " (server-only)" : "")}");
    }

    internal const string FailedMessage = "That command failed. The server console has details.";

    private static readonly Dictionary<(ulong, string), long> _lastRefusal = [];

    /// <summary>
    /// Records a refused attempt, so the owner can see staff trying what they weren't given and diagnose "it says I
    /// don't have permission" without asking. The server console gets every one; the admin log only those of players
    /// holding a role, since a random player typing !ban isn't news. At most once a minute per player and command.
    /// </summary>
    private static void NoteRefused(CCitadelPlayerController? player, string command, string permission)
    {
        if (player == null || CommandOverrides.Unreadable)
            return;
        var id = PermissionManager.GetSlotSteamId(player.Slot);
        var now = Environment.TickCount64;
        lock (_lastRefusal)
        {
            if (_lastRefusal.TryGetValue((id, command), out var last) && now - last < 60_000)
                return;
            _lastRefusal[(id, command)] = now;
        }

        var unconfirmed = !Players.IsAuthorized(player.Slot) ? " (not confirmed by Steam yet)" : "";
        var needs = permission.Length > 0 ? $"needs {permission}" : "not allowed";
        Console.WriteLine($"[Permissions] {player.PlayerName} ({id}) was refused {command}: {needs}{unconfirmed}");
        if (id != 0 && PermissionManager.Describe(id).AssignedRoles.Count > 0) // their saved roles: staff even before Steam confirms them
            AdminActivity.Log(Caller.Of(player), $"was refused {command} ({needs}){unconfirmed}");
    }

    /// <summary>
    /// Runs a command and deals with how it ends: a <see cref="CommandException"/> is the caller's answer; anything else
    /// is a bug in the plugin, logged with its stack while the caller hears the command failed. A command returning a
    /// Task is followed to the end the same way, back on the game thread, and only answers the player who ran it.
    /// </summary>
    private static void Invoke(IDeadworksPlugin plugin, MethodInfo method, string name, object?[] boundArgs,
        CCitadelPlayerController? player, bool viaChat)
    {
        void reply(string message)
        {
            if (viaChat)
                ReplyViaChat(player, message);
            else
                ReplyViaConsole(player, message);
        }

        object? result;
        try
        {
            // Awaits inside the command resume on the game thread, not the thread pool.
            result = GameThreadContext.Run(() => method.Invoke(plugin, boundArgs));
        }
        catch (TargetInvocationException tie) when (tie.InnerException is CommandException cex)
        {
            reply(cex.Message);
            return;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            Failed(plugin, name, tie.InnerException, reply);
            return;
        }

        if (AsTask(result) is not { } task || task.IsCompletedSuccessfully)
            return;

        // By the time it finishes the player may have left, and someone else may have their slot and controller.
        var slot = player?.Slot ?? -1;
        var steamId64 = slot >= 0 ? PermissionManager.GetSlotSteamId(slot) : 0;
        void replyIfStillHere(string message)
        {
            if (slot < 0)
            {
                Console.WriteLine(message);
                return;
            }
            if (Players.FromSlot(slot) is not { } still || PermissionManager.GetSlotSteamId(slot) != steamId64)
                return;
            player = still;
            reply(message);
        }

        task.ContinueWith(t => TimerEngine.EnqueueNextTick(() =>
        {
            if (t.Exception?.GetBaseException() is CommandException cex)
                replyIfStillHere(cex.Message);
            else if (t.Exception != null)
                Failed(plugin, name, t.Exception.GetBaseException(), replyIfStillHere);
        }), TaskScheduler.Default);
    }

    /// <summary>A command's Task, ValueTask or ValueTask&lt;T&gt; as a Task to follow, or null for anything else.</summary>
    private static Task? AsTask(object? result) => result switch
    {
        Task task => task,
        ValueTask valueTask => valueTask.AsTask(),
        not null when result.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(ValueTask<>)
            => (Task?)type.GetMethod(nameof(ValueTask<int>.AsTask))!.Invoke(result, null),
        _ => null,
    };

    private static void Failed(IDeadworksPlugin plugin, string name, Exception ex, Action<string> reply)
    {
        Console.WriteLine($"[CommandRegistration] {plugin.Name}: command '{name}' threw {ex}");
        reply(FailedMessage);
    }

    private static void InvokeDeferred(
        IDeadworksPlugin plugin,
        MethodInfo method,
        string name,
        object?[] boundArgs,
        CCitadelPlayerController? player,
        bool viaChat,
        string label)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            Invoke(plugin, method, name, boundArgs, player, viaChat);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CommandRegistration] {plugin.Name} {label} threw: {ex.Message}");
        }
        PluginLoader.WarnIfSlow(start, $"{plugin.Name} {label}");
    }

    private static void ReplyViaChat(CCitadelPlayerController? to, string message)
    {
        if (to != null)
            Chat.PrintToChat(to, message);
    }

    private static void ReplyViaConsole(CCitadelPlayerController? to, string message)
    {
        if (to != null)
            to.PrintToConsole(message);
        else
            Console.WriteLine(message);
    }
}
