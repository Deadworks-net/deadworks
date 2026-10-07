namespace DeadworksManaged.Api;

/// <summary>Marks a plugin method as a handler for a chat command. Can be applied multiple times.</summary>
[Obsolete("Use [Command] instead. Commands with this attribute are no longer registered: they skipped permission checks.", error: true)]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class ChatCommandAttribute : Attribute
{
    /// <summary>The chat command string this attribute matches, including the prefix (e.g. <c>"!mycommand"</c>).</summary>
    public string Command { get; }

    /// <param name="command">The command string to match, including any prefix character.</param>
    public ChatCommandAttribute(string command) => Command = command;
}
