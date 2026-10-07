using System.Text.Json;
using Xunit;

namespace DeadworksManaged.Tests;

public class HostStatusFormatterTests
{
    private static HostStatusSnapshot Snapshot(IReadOnlyList<HostStatusPlayer>? players = null, IReadOnlyList<HostStatusPlugin>? plugins = null) => new(
        "dl_midtown",
        123,
        31,
        "v0.4.16",
        ["admin", "moderator"],
        true,
        players ?? [],
        plugins ?? [new HostStatusPlugin("DeathmatchPlugin", true, true, true, false, 1)]);

    private static HostStatusPlayer Player(int slot, string name = "x", params string[] roles) =>
        new(slot, 76561198000000000UL + (ulong)slot, name, 2, "hero_inferno", false, 812, roles);

    private static JsonElement Parse(string line)
    {
        Assert.StartsWith(HostStatusFormatter.Prefix, line);
        return JsonDocument.Parse(line[HostStatusFormatter.Prefix.Length..]).RootElement.Clone();
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    /// <summary>Every line the launcher gets by following <c>next</c> from slot 0.</summary>
    private static List<string> Pages(HostStatusSnapshot status, int maxLineBytes = HostStatusFormatter.DefaultMaxLineBytes)
    {
        var lines = new List<string>();
        int? fromSlot = 0;
        while (fromSlot is { } from)
        {
            var line = HostStatusFormatter.Format(status, from, maxLineBytes);
            lines.Add(line);
            fromSlot = Parse(line).TryGetProperty("next", out var next) ? next.GetInt32() : null;
        }
        return lines;
    }

    private static IEnumerable<int> Slots(IEnumerable<string> lines) =>
        lines.SelectMany(line => Parse(line).GetProperty("players").EnumerateArray().Select(p => p.GetProperty("slot").GetInt32()));

    [Fact]
    public void FormatsAllFieldsOnOneLine()
    {
        var line = HostStatusFormatter.Format(Snapshot([Player(0, "x", "admin")]));

        Assert.Equal(
            "DWHOST {\"v\":1,\"map\":\"dl_midtown\",\"uptime\":123,\"maxPlayers\":31,\"deadworks\":\"v0.4.16\",\"playerCount\":1," +
            "\"roles\":[\"admin\",\"moderator\"],\"moderation\":true," +
            "\"players\":[{\"slot\":0,\"steamId64\":\"76561198000000000\",\"name\":\"x\",\"team\":2,\"hero\":\"hero_inferno\",\"bot\":false,\"connected\":812,\"roles\":[\"admin\"]}]," +
            "\"plugins\":[{\"name\":\"DeathmatchPlugin\",\"enabled\":true,\"loaded\":true,\"onDisk\":true,\"builtin\":false,\"instances\":1}]}",
            line);
    }

    [Fact]
    public void UnknownValuesAreNull()
    {
        var bot = new HostStatusPlayer(3, 0, "Bot", 2, null, true, null, []);
        var status = Snapshot([bot], [new HostStatusPlugin("Off", false, false, true, true, null)]) with { MaxPlayers = null };

        var root = Parse(HostStatusFormatter.Format(status));

        Assert.Equal(JsonValueKind.Null, root.GetProperty("maxPlayers").ValueKind);
        var player = root.GetProperty("players")[0];
        Assert.Equal(JsonValueKind.Null, player.GetProperty("steamId64").ValueKind);
        Assert.Equal(JsonValueKind.Null, player.GetProperty("hero").ValueKind);
        Assert.Equal(JsonValueKind.Null, player.GetProperty("connected").ValueKind);
        Assert.True(player.GetProperty("bot").GetBoolean());
        Assert.False(root.GetProperty("plugins")[0].TryGetProperty("instances", out _));
        Assert.True(root.GetProperty("plugins")[0].GetProperty("builtin").GetBoolean());
    }

    [Fact]
    public void NoRolesAndNoModerationAreAnEmptyArrayAndFalse()
    {
        var status = Snapshot([Player(0), Player(1, "y", "admin", "moderator")]) with { Roles = [], Moderation = false };

        var root = Parse(HostStatusFormatter.Format(status));

        Assert.Empty(Strings(root.GetProperty("roles")));
        Assert.False(root.GetProperty("moderation").GetBoolean());
        Assert.Empty(Strings(root.GetProperty("players")[0].GetProperty("roles")));
        Assert.Equal(["admin", "moderator"], Strings(root.GetProperty("players")[1].GetProperty("roles")));
    }

    [Theory]
    [InlineData("quote \" back\\slash")]
    [InlineData("new\nline\ttab\r")]
    [InlineData("100% %s %n")]
    [InlineData("héros 日本語 😀")]
    [InlineData("<b>&'+</b>")]
    public void NamesRoundTripOnOneAsciiLineWithoutPercent(string name)
    {
        var status = Snapshot([Player(0, name, name)], [new HostStatusPlugin(name, true, false, true, false, null)]) with { Roles = [name] };
        var line = HostStatusFormatter.Format(status);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('%', line);
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));

        var root = Parse(line);
        Assert.Equal(name, root.GetProperty("players")[0].GetProperty("name").GetString());
        Assert.Equal(name, root.GetProperty("plugins")[0].GetProperty("name").GetString());
        Assert.Equal([name], Strings(root.GetProperty("roles")));
        Assert.Equal([name], Strings(root.GetProperty("players")[0].GetProperty("roles")));
    }

    [Fact]
    public void FullServerPagesByNextSlotWithinTheByteBudget()
    {
        var players = Enumerable.Range(0, 31).Select(slot => Player(slot, new string('n', 60), "admin", "moderator")).ToList();
        var pages = Pages(Snapshot(players));

        Assert.True(pages.Count > 1);
        foreach (var line in pages)
        {
            Assert.True(line.Length <= HostStatusFormatter.DefaultMaxLineBytes, $"line is {line.Length} bytes");

            // Every line carries the server-wide fields, so the launcher can read them from whichever it gets.
            var root = Parse(line);
            Assert.Equal(31, root.GetProperty("playerCount").GetInt32());
            Assert.Equal(["admin", "moderator"], Strings(root.GetProperty("roles")));
            Assert.True(root.GetProperty("moderation").GetBoolean());
            Assert.Single(root.GetProperty("plugins").EnumerateArray());
        }

        Assert.Equal(Enumerable.Range(0, 31), Slots(pages));
    }

    [Theory]
    [InlineData(900)]
    [InlineData(1200)]
    [InlineData(HostStatusFormatter.DefaultMaxLineBytes)]
    public void RolesCountTowardsTheByteBudget(int maxLineBytes)
    {
        string[] roles = [.. Enumerable.Range(0, 6).Select(i => $"role-{i}-{new string('r', 12)}")];
        var plain = Snapshot(Enumerable.Range(0, 31).Select(slot => Player(slot, "player")).ToList()) with { Roles = [] };
        var withRoles = Snapshot(Enumerable.Range(0, 31).Select(slot => Player(slot, "player", roles)).ToList()) with { Roles = roles };

        var pages = Pages(withRoles, maxLineBytes);

        Assert.All(pages, line => Assert.True(line.Length <= maxLineBytes, $"line is {line.Length} bytes, over {maxLineBytes}"));
        Assert.True(pages.Count > Pages(plain, maxLineBytes).Count);
        Assert.Equal(Enumerable.Range(0, 31), Slots(pages));
    }

    [Fact]
    public void OversizedPlayerStillMakesProgress()
    {
        var status = Snapshot([Player(0, new string('n', 2000)), Player(5)]);

        var first = Parse(HostStatusFormatter.Format(status));
        Assert.Equal(1, first.GetProperty("players").GetArrayLength());
        Assert.Equal(5, first.GetProperty("next").GetInt32());

        var second = Parse(HostStatusFormatter.Format(status, 5));
        Assert.Equal(5, second.GetProperty("players")[0].GetProperty("slot").GetInt32());
        Assert.False(second.TryGetProperty("next", out _));
    }
}
