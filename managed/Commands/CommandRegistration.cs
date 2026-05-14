using System.Reflection;
using System.Runtime.ExceptionServices;
using DeadworksManaged.Api;
using DeadworksManaged.Permissions;

namespace DeadworksManaged.Commands;

internal static class CommandRegistration
{
    private sealed class SourceCommandRegistration
    {
        public required IDeadworksPlugin Plugin { get; init; }
        public required MethodInfo Method { get; init; }
        public required CommandAttribute Attribute { get; init; }
        public required CommandBinder.Plan Plan { get; init; }
        public required PluginCommandManifestSource ManifestSource { get; init; }
    }

    public static void RegisterPluginCommands(
        string normalizedPath,
        List<IDeadworksPlugin> plugins,
        HandlerRegistry<string, Func<ChatCommandContext, HookResult>> chatRegistry)
    {
        foreach (var plugin in plugins)
        {
            var sourceCommands = new List<SourceCommandRegistration>();
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
                            $"[CommandRegistration] {plugin.Name}.{method.Name}: ChatOnly and ConsoleOnly both set - skipping");
                        continue;
                    }

                    var sourceName = attr.Names[0];
                    CommandBinder.Plan plan;
                    try
                    {
                        plan = CommandBinder.Build(method, sourceName);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CommandRegistration] {plugin.Name}.{method.Name}: {ex.Message}");
                        continue;
                    }

                    var aliases = attr.Names
                        .Skip(1)
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    sourceCommands.Add(new SourceCommandRegistration
                    {
                        Plugin = plugin,
                        Method = method,
                        Attribute = attr,
                        Plan = plan,
                        ManifestSource = new PluginCommandManifestSource
                        {
                            Id = PluginCommandManifestManager.GetCommandId(plugin.Name, sourceName),
                            Name = sourceName,
                            Aliases = aliases,
                            Description = attr.Description,
                            Permission = attr.Permission
                        }
                    });
                }
            }

            var effectiveCommands = PluginCommandManifestManager.LoadOrCreate(
                plugin.GetType().Name,
                plugin.Name,
                sourceCommands.Select(c => c.ManifestSource).ToArray());

            foreach (var source in sourceCommands)
            {
                if (!effectiveCommands.TryGetValue(source.ManifestSource.Id, out var command))
                    continue;

                var commandNames = new[] { command.Name ?? source.ManifestSource.Name }
                    .Concat(command.Aliases ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var name in commandNames)
                {
                    if (!source.Attribute.ConsoleOnly)
                        RegisterChat(normalizedPath, source, command, name, chatRegistry);

                    if (!source.Attribute.ChatOnly)
                        RegisterConsole(normalizedPath, source, command, name);
                }
            }
        }
    }

    private static void RegisterChat(
        string normalizedPath,
        SourceCommandRegistration source,
        PluginCommandManifestEntry command,
        string name,
        HandlerRegistry<string, Func<ChatCommandContext, HookResult>> chatRegistry)
    {
        var attr = source.Attribute;
        var namedPlan = name == source.Plan.Name ? source.Plan : new CommandBinder.Plan
        {
            Name = name,
            Slots = source.Plan.Slots,
            HasCaller = source.Plan.HasCaller,
            CallerNullable = source.Plan.CallerNullable
        };

        Func<ChatCommandContext, HookResult> handler = ctx =>
        {
            if (attr.ServerOnly)
                return HookResult.Continue;

            var resultOnSuccess = (ctx.Prefix == '!' && !attr.SuppressChat)
                ? HookResult.Continue
                : HookResult.Handled;

            void reply(string msg) => ReplyViaChat(ctx.Controller, msg);

            if (!CanRunPlayerCommand(ctx.Controller, command.Permission ?? ""))
            {
                reply("You do not have permission to use this command.");
                return HookResult.Handled;
            }

            var argString = ctx.Args.Length > 0 ? string.Join(" ", ctx.Args) : "";
            var tokens = CommandTokenizer.Tokenize(argString);

            if (!CommandBinder.TryBind(namedPlan, tokens, ctx.Controller, out var boundArgs, out var error, out var silentSkip))
            {
                if (silentSkip)
                    return resultOnSuccess;
                if (error != null)
                    reply(error);
                return resultOnSuccess;
            }

            Invoke(source.Plugin, source.Method, boundArgs, reply);
            return resultOnSuccess;
        };

        chatRegistry.AddForPlugin(normalizedPath, name, handler);
        PluginRegistrationTracker.Add(normalizedPath, "chat", $"/{name}", command.Description ?? "", attr.Hidden);
        Console.WriteLine($"[CommandRegistration] Registered chat command: {source.Plugin.Name} -> /{name}");
    }

    private static void RegisterConsole(
        string normalizedPath,
        SourceCommandRegistration source,
        PluginCommandManifestEntry command,
        string name)
    {
        var attr = source.Attribute;
        var conName = "dw_" + name;
        var namedPlan = conName == source.Plan.Name ? source.Plan : new CommandBinder.Plan
        {
            Name = conName,
            Slots = source.Plan.Slots,
            HasCaller = source.Plan.HasCaller,
            CallerNullable = source.Plan.CallerNullable
        };

        Action<ConCommandContext> handler = ctx =>
        {
            if (attr.ServerOnly && !ctx.IsServerCommand)
                return;

            void reply(string msg) => ReplyViaConsole(ctx.Controller, msg);

            if (!ctx.IsServerCommand && !CanRunPlayerCommand(ctx.Controller, command.Permission ?? ""))
            {
                reply("You do not have permission to use this command.");
                return;
            }

            var argString = ctx.Args.Length > 1
                ? string.Join(" ", ctx.Args, 1, ctx.Args.Length - 1)
                : "";
            var tokens = CommandTokenizer.Tokenize(argString);

            if (!CommandBinder.TryBind(namedPlan, tokens, ctx.Controller, out var boundArgs, out var error, out var silentSkip))
            {
                if (silentSkip)
                    return;
                if (error != null)
                    reply(error);
                return;
            }

            Invoke(source.Plugin, source.Method, boundArgs, reply);
        };

        ConCommandManager.RegisterExternal(normalizedPath, conName, command.Description ?? "", serverOnly: false, handler, attr.Hidden);
        Console.WriteLine($"[CommandRegistration] Registered console command: {source.Plugin.Name} -> {conName}{(attr.ServerOnly ? " (server-only)" : "")}");
    }

    private static bool CanRunPlayerCommand(CCitadelPlayerController? controller, string permission)
    {
        if (string.IsNullOrWhiteSpace(permission))
            return true;

        if (controller == null)
            return false;

        return PermissionManager.HasPermission(controller.PlayerSteamId, permission);
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
