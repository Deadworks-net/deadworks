using System.Reflection;
using Microsoft.Extensions.Logging;
using DeadworksManaged.Api;
using DeadworksManaged.Telemetry;

namespace DeadworksManaged;

internal static partial class PluginLoader
{
    // --- Chat message dispatch (with command routing) ---

    public static HookResult DispatchChatMessage(ChatMessage message)
    {
        DeadworksMetrics.ChatMessagesProcessed.Add(1);
        var result = HookResult.Continue;

        var text = message.ChatText.Trim();
        if (text.Length > 1 && (text[0] == '/' || text[0] == '!'))
        {
            var prefix = text[0];
            var parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var commandName = parts[0];
            var args = parts.Length > 1 ? parts[1..] : [];

            List<Func<ChatCommandContext, HookResult>>? handlers;
            lock (_lock)
            {
                handlers = _chatCommandRegistry.Snapshot(commandName);
            }

            if (handlers != null)
            {
                var ctx = new ChatCommandContext(message, commandName, args, prefix);
                foreach (var handler in handlers)
                {
                    try
                    {
                        var hr = handler(ctx);
                        if (hr > result) result = hr;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Chat command handler for /{CommandName} threw", commandName);
                    }
                }

                if (result > HookResult.Continue)
                    return result;
            }
        }

        // Fall through to plugin OnChatMessage
        return DispatchToPluginsWithResult(p => p.OnChatMessage(message), nameof(IDeadworksPlugin.OnChatMessage));
    }

    // --- Chat command registration ---

    private static void RegisterPluginChatCommands(string normalizedPath, List<IDeadworksPlugin> plugins)
    {
        foreach (var plugin in plugins)
        {
            var methods = plugin.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
            {
#pragma warning disable CS0618 // ChatCommandAttribute is obsolete; intentionally scanned for back-compat
                var attrs = method.GetCustomAttributes<ChatCommandAttribute>();
#pragma warning restore CS0618
                foreach (var attr in attrs)
                {
                    var del = (Func<ChatCommandContext, HookResult>)Delegate.CreateDelegate(
                        typeof(Func<ChatCommandContext, HookResult>), plugin, method);

                    _chatCommandRegistry.AddForPlugin(normalizedPath, attr.Command, del);
                    PluginRegistrationTracker.Add(normalizedPath, "chat", $"/{attr.Command}");
                    _logger.LogDebug("Registered chat command: {PluginName} -> /{CommandName}", plugin.Name, attr.Command);
                }
            }
        }
    }
}
