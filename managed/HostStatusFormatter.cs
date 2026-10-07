using System.Buffers;
using System.Text;
using System.Text.Json;

namespace DeadworksManaged;

/// <summary>A connected player. <paramref name="Roles"/> are the roles assigned to them that apply right now; none for a bot.</summary>
internal sealed record HostStatusPlayer(
    int Slot, ulong SteamId64, string Name, int Team, string? Hero, bool Bot, long? ConnectedSeconds, IReadOnlyList<string> Roles);

/// <summary><paramref name="Moderation"/> is whether <c>dw_kick</c> and <c>dw_ban</c> exist.</summary>
internal sealed record HostStatusSnapshot(
    string Map,
    long UptimeSeconds,
    int? MaxPlayers,
    string Version,
    bool Moderation,
    IReadOnlyList<HostStatusPlayer> Players);

/// <summary>
/// Builds the single <c>DWHOST [token] {json}</c> line that <c>dw_host_status</c> prints for the launcher.
/// The JSON is ASCII-only (the default encoder escapes everything else) and never contains a raw
/// <c>%</c>, because the native logger hands each line to LoggingSystem_Log as a printf format string.
/// </summary>
internal static class HostStatusFormatter
{
    public const string Prefix = "DWHOST ";
    public const int ProtocolVersion = 2;
    public const int MaxTokenLength = 64;

    /// <summary>
    /// LoggingSystem_Log formats into MAX_LOGGING_MESSAGE_LENGTH (2048) bytes, and the native logger puts
    /// "[time] [deadworks] [INFO] [managed] " in front, so a longer line would come out cut off.
    /// </summary>
    public const int DefaultMaxLineBytes = 1900;

    /// <summary>
    /// The caller's token is echoed back so it can tell this line from one a player got into the log (a name, chat
    /// a plugin prints). Letters and digits only, so it can't itself break the line.
    /// </summary>
    public static bool IsValidToken(string token) =>
        token.Length <= MaxTokenLength && token.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// Formats the players in slot <paramref name="fromSlot"/> and up. When they don't all fit in
    /// <paramref name="maxLineBytes"/>, the line holds as many as do and <c>next</c> names the slot to ask for next.
    /// At least one player is always included so paging makes progress; a player too big to fit alone is listed
    /// without roles. Everything else is repeated on every line and counts towards its length.
    /// </summary>
    public static string Format(HostStatusSnapshot status, int fromSlot = 0, string token = "", int maxLineBytes = DefaultMaxLineBytes)
    {
        var candidates = status.Players.Where(p => p.Slot >= fromSlot).OrderBy(p => p.Slot).ToList();
        var page = new List<string>();
        int? next = null;

        foreach (var player in candidates)
        {
            page.Add(SerializePlayer(player));
            if (Compose(status, token, page, player.Slot).Length <= maxLineBytes)
                continue;

            if (page.Count > 1)
            {
                page.RemoveAt(page.Count - 1);
                next = player.Slot;
                break;
            }
            page[0] = SerializePlayer(player with { Roles = [] });
        }

        return Compose(status, token, page, next);
    }

    private static string Compose(HostStatusSnapshot status, string token, List<string> players, int? next)
    {
        var json = Serialize(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("v", ProtocolVersion);
            w.WriteString("map", status.Map);
            w.WriteNumber("uptime", status.UptimeSeconds);
            if (status.MaxPlayers is { } maxPlayers)
                w.WriteNumber("maxPlayers", maxPlayers);
            else
                w.WriteNull("maxPlayers");
            w.WriteString("deadworks", status.Version);
            w.WriteNumber("playerCount", status.Players.Count);
            w.WriteBoolean("moderation", status.Moderation);

            w.WriteStartArray("players");
            foreach (var player in players)
                w.WriteRawValue(player);
            w.WriteEndArray();

            if (next is { } nextSlot)
                w.WriteNumber("next", nextSlot);

            w.WriteEndObject();
        });

        return Prefix + (token.Length > 0 ? token + " " : "") + EscapePercent(json);
    }

    private static string SerializePlayer(HostStatusPlayer player) => EscapePercent(Serialize(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("slot", player.Slot);
        // A string, because a SteamID64 doesn't fit a JavaScript number.
        if (player.SteamId64 != 0)
            w.WriteString("steamId64", player.SteamId64.ToString());
        else
            w.WriteNull("steamId64");
        w.WriteString("name", player.Name);
        w.WriteNumber("team", player.Team);
        if (player.Hero != null)
            w.WriteString("hero", player.Hero);
        else
            w.WriteNull("hero");
        w.WriteBoolean("bot", player.Bot);
        if (player.ConnectedSeconds is { } connected)
            w.WriteNumber("connected", connected);
        else
            w.WriteNull("connected");
        w.WriteStartArray("roles");
        foreach (var role in player.Roles)
            w.WriteStringValue(role);
        w.WriteEndArray();
        w.WriteEndObject();
    }));

    private static string Serialize(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // '%' only ever appears inside JSON strings here, where % means the same thing.
    private static string EscapePercent(string json) => json.Replace("%", "\\u0025");
}
