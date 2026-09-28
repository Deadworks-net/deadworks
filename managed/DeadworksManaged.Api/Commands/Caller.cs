namespace DeadworksManaged.Api;

/// <summary>
/// Who ran a command: a player, or the server console (which includes rcon). Take it as the first parameter of a
/// <see cref="CommandAttribute"/> method. Unlike a nullable controller, the console is explicit here, so a failed
/// player lookup can never be mistaken for it.
/// </summary>
public sealed class Caller
{
    /// <summary>The server console. It holds every permission and can target anyone.</summary>
    public static Caller Console { get; } = new(null);

    /// <summary>A player caller.</summary>
    public static Caller Of(CCitadelPlayerController player)
        => new(player ?? throw new ArgumentNullException(nameof(player), "A player caller needs a player; use Caller.Console for the console."));

    private Caller(CCitadelPlayerController? player) => Player = player;

    /// <summary>The player who ran the command, or null for the server console.</summary>
    public CCitadelPlayerController? Player { get; }

    /// <summary>True for the server console (and rcon).</summary>
    public bool IsConsole => Player == null;

    /// <summary>The player's name, or "Console".</summary>
    public string Name => Player?.PlayerName ?? "Console";

    /// <summary>The SteamID the player connected with (see <see cref="Permissions.GetSteamId"/>), or 0 for the console.</summary>
    public ulong SteamId64 => Player == null ? 0 : Permissions.Backend?.GetSlotSteamId(Player.Slot) ?? 0;

    /// <summary>Whether the caller holds <paramref name="permission"/>. The console holds everything.</summary>
    public bool HasPermission(string permission) => Player == null || Player.HasPermission(permission);

    /// <summary>Whether the caller may act on <paramref name="target"/> given both players' immunity. The console may target anyone.</summary>
    public bool CanTarget(CCitadelPlayerController target) => Player == null || Player.CanTarget(target);

    /// <summary>A short reply: the player's chat, or the server console.</summary>
    public void Reply(string message)
    {
        if (Player != null)
            Chat.PrintToChat(Player, message);
        else
            System.Console.WriteLine(message);
    }

    /// <summary>Output that doesn't fit in chat: the player's console, or the server console.</summary>
    public void PrintToConsole(string message)
    {
        if (Player != null)
            Player.PrintToConsole(message);
        else
            System.Console.WriteLine(message);
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
