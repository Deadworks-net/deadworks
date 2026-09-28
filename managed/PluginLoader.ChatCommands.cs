using System.Reflection;
using DeadworksManaged.Api;

namespace DeadworksManaged;

internal static partial class PluginLoader
{
    // --- Chat message dispatch (with command routing) ---

    public static HookResult DispatchChatMessage(ChatMessage message)
    {
        var result = HookResult.Continue;

        // A gag is core's to enforce: plugin OnChatMessage handlers can't stop each other seeing a message.
        // A gagged player's chat commands still run, but nothing they type reaches chat or any plugin.
        var gag = message.SenderSlot >= 0 ? AdminSystem.PenaltyManager.GagForSlot(message.SenderSlot) : null;
        if (gag != null)
            result = HookResult.Handled;

        if (TryParseChatCommand(message.ChatText, out var prefix, out var commandName, out var args))
        {
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
                        Console.WriteLine($"[PluginLoader] Chat command handler for '/{commandName}' threw: {ex.Message}");
                    }
                }

                if (result > HookResult.Continue)
                    return result;
            }
        }

        if (gag != null)
        {
            if (message.Controller is { } sender)
                Chat.PrintToChat(sender, AdminSystem.PenaltyManager.GagMessage(gag));
            return HookResult.Handled;
        }

        // Fall through to plugin OnChatMessage
        return DispatchToPluginsWithResult(p => p.OnChatMessage(message), nameof(IDeadworksPlugin.OnChatMessage));
    }

    /// <summary>
    /// Splits <c>/name args...</c> or <c>!name args...</c> into the command name and its arguments.
    /// Tokenized once, here: a double-quoted run is a single argument, so consumers must not re-join and re-split.
    /// </summary>
    internal static bool TryParseChatCommand(string chatText, out char prefix, out string commandName, out string[] args)
    {
        prefix = default;
        commandName = "";
        args = [];

        var text = chatText.Trim();
        if (text.Length <= 1 || (text[0] != '/' && text[0] != '!'))
            return false;

        var tokens = Commands.CommandTokenizer.Tokenize(text[1..]);
        if (tokens.Length == 0)
            return false;

        prefix = text[0];
        commandName = tokens[0];
        args = tokens[1..];
        return true;
    }
}
