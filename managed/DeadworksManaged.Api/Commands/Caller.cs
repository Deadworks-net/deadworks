namespace DeadworksManaged.Api;

/// <summary>
/// Who ran a command: a player, or the server console (which includes rcon). Take it as the first parameter of a
/// <see cref="CommandAttribute"/> method. Unlike a nullable controller, the console is explicit here, so a failed
/// player lookup can never be mistaken for it.
/// </summary>
/// <remarks>
/// A player caller remembers who ran the command. Controllers outlive disconnects, so after an <c>await</c> the same
/// controller may belong to someone who joined since; once the player has left, <see cref="IsConnected"/> is false,
/// <see cref="HasPermission"/> and <see cref="CanTarget(CCitadelPlayerController)"/> refuse, and replies go nowhere.
/// </remarks>
public sealed class Caller
{
    /// <summary>The server console. It holds every permission and can target anyone.</summary>
    public static Caller Console { get; } = new(null);

    /// <summary>A player caller.</summary>
    public static Caller Of(CCitadelPlayerController player)
        => new(player ?? throw new ArgumentNullException(nameof(player), "A player caller needs a player; use Caller.Console for the console."));

    private readonly CCitadelPlayerController? _player;
    private readonly int _slot;

    private Caller(CCitadelPlayerController? player)
    {
        _player = player;
        _slot = player?.Slot ?? -1;
        SteamId64 = player == null ? 0 : Permissions.Backend?.GetSlotSteamId(player.Slot) ?? 0;
        Name = player?.PlayerName ?? "Console";
    }

    /// <summary>True for the server console (and rcon).</summary>
    public bool IsConsole => _player == null;

    /// <summary>
    /// Whether the player who ran the command is still on the server. Always true for the console. Check it after an
    /// <c>await</c> before acting for them.
    /// </summary>
    public bool IsConnected
        => _player == null || (Players.FromSlot(_slot) != null && Permissions.Backend?.GetSlotSteamId(_slot) == SteamId64);

    /// <summary>The player who ran the command, or null for the console, or once they've left the server.</summary>
    public CCitadelPlayerController? Player => IsConnected ? _player : null;

    /// <summary>The player's name when they ran the command, or "Console".</summary>
    public string Name { get; }

    /// <summary>The SteamID the player connected with (see <see cref="Permissions.GetSteamId64"/>), or 0 for the console.</summary>
    public ulong SteamId64 { get; }

    /// <summary>Whether the caller holds <paramref name="permission"/>. The console holds everything; a player who has left, nothing.</summary>
    public bool HasPermission(string permission) => IsConsole || (Player is { } p && p.HasPermission(permission));

    /// <summary>Whether the caller may act on <paramref name="target"/> given both players' immunity. The console may target anyone.</summary>
    public bool CanTarget(CCitadelPlayerController target) => IsConsole || (Player is { } p && p.CanTarget(target));

    /// <summary>
    /// Whether the caller may act on this SteamID, on the server or not, by immunity. The console always may. False while
    /// the target's entry is still loading; <see cref="Permissions.IsLoaded"/> tells the two apart.
    /// </summary>
    public bool CanTarget(ulong steamId64) => IsConsole || (IsConnected && Permissions.CanTarget(SteamId64, steamId64));

    /// <summary>A short reply: the player's chat, or the server console. Nothing if the player has left.</summary>
    public void Reply(string message)
    {
        if (IsConsole)
            System.Console.WriteLine(message);
        else if (Player is { } p)
            Chat.PrintToChat(p, message);
    }

    /// <summary>Output that doesn't fit in chat: the player's console, or the server console. Nothing if the player has left.</summary>
    public void PrintToConsole(string message)
    {
        if (IsConsole)
            System.Console.WriteLine(message);
        else if (Player is { } p)
            p.PrintToConsole(message);
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
