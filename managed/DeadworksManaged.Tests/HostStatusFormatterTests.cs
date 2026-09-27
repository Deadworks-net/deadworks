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
        players ?? [],
        plugins ?? [new HostStatusPlugin("DeathmatchPlugin", true, true, true, 1)]);

    private static HostStatusPlayer Player(int slot, string name = "x") =>
        new(slot, 76561198000000000UL + (ulong)slot, name, 2, "hero_inferno", false, 812);

    private static JsonElement Parse(string line)
    {
        Assert.StartsWith(HostStatusFormatter.Prefix, line);
        return JsonDocument.Parse(line[HostStatusFormatter.Prefix.Length..]).RootElement.Clone();
    }

    [Fact]
    public void FormatsAllFieldsOnOneLine()
    {
        var line = HostStatusFormatter.Format(Snapshot([Player(0)]));

        Assert.Equal(
            "DWHOST {\"v\":1,\"map\":\"dl_midtown\",\"uptime\":123,\"maxPlayers\":31,\"deadworks\":\"v0.4.16\",\"playerCount\":1," +
            "\"players\":[{\"slot\":0,\"steamId64\":\"76561198000000000\",\"name\":\"x\",\"team\":2,\"hero\":\"hero_inferno\",\"bot\":false,\"connected\":812}]," +
            "\"plugins\":[{\"name\":\"DeathmatchPlugin\",\"enabled\":true,\"loaded\":true,\"onDisk\":true,\"instances\":1}]}",
            line);
    }

    [Fact]
    public void UnknownValuesAreNull()
    {
        var bot = new HostStatusPlayer(3, 0, "Bot", 2, null, true, null);
        var status = Snapshot([bot], [new HostStatusPlugin("Off", false, false, true, null)]) with { MaxPlayers = null };

        var root = Parse(HostStatusFormatter.Format(status));

        Assert.Equal(JsonValueKind.Null, root.GetProperty("maxPlayers").ValueKind);
        var player = root.GetProperty("players")[0];
        Assert.Equal(JsonValueKind.Null, player.GetProperty("steamId64").ValueKind);
        Assert.Equal(JsonValueKind.Null, player.GetProperty("hero").ValueKind);
        Assert.Equal(JsonValueKind.Null, player.GetProperty("connected").ValueKind);
        Assert.True(player.GetProperty("bot").GetBoolean());
        Assert.False(root.GetProperty("plugins")[0].TryGetProperty("instances", out _));
    }

    [Theory]
    [InlineData("quote \" back\\slash")]
    [InlineData("new\nline\ttab\r")]
    [InlineData("100% %s %n")]
    [InlineData("héros 日本語 😀")]
    [InlineData("<b>&'+</b>")]
    public void NamesRoundTripOnOneAsciiLineWithoutPercent(string name)
    {
        var line = HostStatusFormatter.Format(Snapshot([Player(0, name)], [new HostStatusPlugin(name, true, false, true, null)]));

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('%', line);
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));

        var root = Parse(line);
        Assert.Equal(name, root.GetProperty("players")[0].GetProperty("name").GetString());
        Assert.Equal(name, root.GetProperty("plugins")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void FullServerPagesByNextSlotWithinTheByteBudget()
    {
        var players = Enumerable.Range(0, 31).Select(slot => Player(slot, new string('n', 60))).ToList();
        var status = Snapshot(players);

        var seen = new List<int>();
        int? fromSlot = 0;
        while (fromSlot is { } from)
        {
            var line = HostStatusFormatter.Format(status, from);
            Assert.True(line.Length <= HostStatusFormatter.DefaultMaxLineBytes, $"line is {line.Length} bytes");

            var root = Parse(line);
            Assert.Equal(31, root.GetProperty("playerCount").GetInt32());
            seen.AddRange(root.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("slot").GetInt32()));
            fromSlot = root.TryGetProperty("next", out var next) ? next.GetInt32() : null;
        }

        Assert.Equal(Enumerable.Range(0, 31), seen);
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
