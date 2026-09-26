using System.Reflection;
using System.Runtime.ExceptionServices;
using DeadworksManaged.Api;
using DeadworksManaged.PermissionSystem;

namespace DeadworksManaged.Commands;

internal static class CommandRegistration
{
    internal const string DeniedMessage = "You don't have permission to use this command.";

    /// <summary>A command's permission, looked up on every call so <c>dw_perm_reload</c> applies overrides without re-registering.</summary>
    private sealed class CommandGate(CommandAttribute attr)
    {
        public string Permission => CommandOverrides.Resolve(attr.Names, attr.Permission, out _);

        public bool EnforceImmunity(string permission) => attr.TargetImmunity switch
        {
            TargetImmunity.Enforce => true,
            TargetImmunity.Ignore => false,
            _ => permission.Length > 0
        };

        public static bool PlayerMay(CCitadelPlayerController? player, string permission)
            => permission.Length == 0 || (player != null && PermissionManager.HasForSlot(player.Slot, permission));

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
            var manifestCommands = new List<PermissionManifest.CommandInfo>();
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

                    CommandBinder.Plan plan;
                    try
                    {
                        plan = CommandBinder.Build(method, attr.Names[0]);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CommandRegistration] {plugin.Name}.{method.Name}: {ex.Message}");
                        continue;
                    }

                    var gate = new CommandGate(attr);
                    var names = attr.Names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    manifestCommands.Add(new PermissionManifest.CommandInfo(
                        names, attr.Description, attr.Permission.Trim(), attr.TargetImmunity, attr.ChatOnly, attr.ConsoleOnly, attr.ServerOnly));

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
                reply(DeniedMessage);
                return HookResult.Handled;
            }

            if (!CommandBinder.TryBind(namedPlan, ctx.Args, ctx.Controller, out var boundArgs, out var error, out var silentSkip,
                    gate.EnforceImmunity(permission)))
            {
                if (silentSkip)
                    return resultOnSuccess;
                if (error != null)
                    reply(error);
                return resultOnSuccess;
            }

            Invoke(plugin, method, boundArgs, reply);
            return resultOnSuccess;
        };

        if (chatRegistry.Snapshot(name) is { Count: > 0 })
            Console.WriteLine($"[CommandRegistration] Warning: another plugin already registered /{name}; both will run. Rename one of them.");
        chatRegistry.AddForPlugin(normalizedPath, name, handler);
        PluginRegistrationTracker.Add(normalizedPath, "chat", $"/{name}", attr.Description, attr.Hidden, gate.CanRun);
        Console.WriteLine($"[CommandRegistration] Registered chat command: {plugin.Name} -> /{name}");
    }

    private static void RegisterConsole(
        string normalizedPath,
        IDeadworksPlugin plugin,
        MethodInfo method,
        CommandBinder.Plan plan,
        string name,
        CommandAttribute attr,
        CommandGate gate)
    {
        var conName = "dw_" + name;
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
                reply(DeniedMessage);
                return;
            }

            if (!CommandBinder.TryBind(namedPlan, tokens, caller, out var boundArgs, out var error, out var silentSkip,
                    gate.EnforceImmunity(permission)))
            {
                if (silentSkip)
                    return;
                if (error != null)
                    reply(error);
                return;
            }

            Invoke(plugin, method, boundArgs, reply);
        };

        if (ConCommandManager.IsRegistered(conName))
            Console.WriteLine($"[CommandRegistration] Warning: {conName} is already registered by another plugin; both will run. Rename one of them.");
        ConCommandManager.RegisterExternal(normalizedPath, conName, attr.Description, serverOnly: false, handler, attr.Hidden, gate.CanRun);
        Console.WriteLine($"[CommandRegistration] Registered console command: {plugin.Name} -> {conName}{(attr.ServerOnly ? " (server-only)" : "")}");
    }

    private static void Invoke(
        IDeadworksPlugin plugin,
        MethodInfo method,
        object?[] boundArgs,
        Action<string> reply)
    {
        try
        {
            method.Invoke(plugin, boundArgs);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is CommandException cex)
        {
            reply(cex.Message);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
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
