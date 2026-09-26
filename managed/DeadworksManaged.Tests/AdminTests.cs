using DeadworksAdmin;
using DeadworksManaged.AdminSystem;
using DeadworksManaged.Api;
using DeadworksManaged.Commands;
using DeadworksManaged.PermissionSystem;
using Xunit;

namespace DeadworksManaged.Tests;

/// <summary>Sets up permissions, penalties and the admin log in a temp directory, with a clock tests can move.</summary>
public abstract class AdminTestBase : IDisposable
{
    protected const ulong Lapka = 76561197960287931UL;
    protected const ulong Greeny = 76561197960287932UL;

    protected readonly string Dir = Directory.CreateTempSubdirectory().FullName;
    protected DateTime Clock = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
    protected readonly List<AdminLogEntry> Logged = [];

    protected string PenaltiesFile => Path.Combine(Dir, "penalties", "penalties.jsonc");
    protected string LogDir => Path.Combine(Dir, "logs");

    protected AdminTestBase()
    {
        PermissionManager.IsSlotAuthenticated = _ => true;
        PermissionManager.Initialize(Path.Combine(Dir, "permissions"));
        PenaltyManager.Now = () => Clock;
        AdminActivityService.Now = () => Clock;
        PenaltyManager.Initialize(Path.Combine(Dir, "penalties"));
        AdminActivityService.Initialize(LogDir, AdminActivityService.Visibility.Anonymous, AdminActivityService.Visibility.Named);
        AdminActivity.Logged += OnLogged;
    }

    private void OnLogged(AdminLogEntry entry) => Logged.Add(entry);

    public virtual void Dispose()
    {
        AdminActivity.Logged -= OnLogged;
        PenaltyManager.Now = () => DateTime.UtcNow;
        AdminActivityService.Now = () => DateTime.UtcNow;
        for (int i = 0; i < Players.MaxSlot; i++)
            PermissionManager.SetSlotSteamIdForTests(i, 0);
        Directory.Delete(Dir, recursive: true);
    }
}

public sealed class PenaltyTests : AdminTestBase
{
    [Fact]
    public void Ban_applies_until_it_expires()
    {
        var added = new List<Penalty>();
        var removed = new List<Penalty>();
        Penalties.Added += added.Add;
        Penalties.Removed += removed.Add;
        try
        {
            Penalties.Add(PenaltyType.Ban, Lapka, TimeSpan.FromMinutes(60), "spam", admin: null, playerName: "lapka");
            Assert.True(Penalties.IsBanned(Lapka));
            Assert.False(Penalties.IsBanned(Greeny));
            Assert.Equal("You are banned from this server for 1h 0m. Reason: spam", PenaltyManager.ConnectRejection(Lapka));

            Clock = Clock.AddMinutes(61);
            Assert.False(Penalties.IsBanned(Lapka));
            Assert.Null(PenaltyManager.ConnectRejection(Lapka));
        }
        finally
        {
            Penalties.Added -= added.Add;
            Penalties.Removed -= removed.Add;
        }
        Assert.Single(added);
        Assert.Equal(added[0].Id, Assert.Single(removed).Id); // expiry raises Removed
    }

    [Fact]
    public void Permanent_penalties_have_no_end()
    {
        Penalties.Add(PenaltyType.Gag, Lapka, null, "", admin: null);
        Clock = Clock.AddYears(10);
        var gag = Penalties.GetActive(PenaltyType.Gag, Lapka);
        Assert.NotNull(gag);
        Assert.True(gag.IsPermanent);
        Assert.Equal("You are gagged permanently and can't use chat.", PenaltyManager.GagMessage(gag));
    }

    [Fact]
    public void A_new_penalty_replaces_the_active_one_of_the_same_type()
    {
        var first = Penalties.Add(PenaltyType.Ban, Lapka, TimeSpan.FromDays(1), "first", admin: null);
        Penalties.Add(PenaltyType.Gag, Lapka, TimeSpan.FromDays(1), "gag", admin: null);
        var second = Penalties.Add(PenaltyType.Ban, Lapka, null, "second", admin: null);

        Assert.Equal(second.Id, Penalties.GetActive(PenaltyType.Ban, Lapka)!.Id);
        Assert.Equal(2, Penalties.GetActive().Count); // the ban and the gag

        var history = Penalties.GetHistoryAsync(Lapka).Result;
        Assert.Equal(3, history.Count);
        Assert.NotNull(history.Single(p => p.Id == first.Id).RemovedUtc);
    }

    [Fact]
    public void Remove_lifts_the_penalty_and_keeps_history()
    {
        Penalties.Add(PenaltyType.Ban, Lapka, null, "x", admin: null);
        Assert.True(Penalties.Remove(PenaltyType.Ban, Lapka, admin: null));
        Assert.False(Penalties.Remove(PenaltyType.Ban, Lapka, admin: null));
        Assert.False(Penalties.IsBanned(Lapka));
        Assert.NotNull(Assert.Single(Penalties.GetHistoryAsync(Lapka).Result).RemovedUtc);
    }

    [Fact]
    public void Penalties_survive_a_restart()
    {
        Penalties.Add(PenaltyType.Ban, Lapka, TimeSpan.FromDays(7), "it's \"cheating\"", admin: null, playerName: "lapka");

        var text = File.ReadAllText(PenaltiesFile);
        Assert.StartsWith("// Bans, gags and mutes.", text);
        Assert.Contains("\"type\": \"Ban\"", text);
        Assert.Contains("it's", text); // written readably, not as '

        PenaltyManager.Initialize(Path.Combine(Dir, "penalties"));
        var ban = Penalties.GetActive(PenaltyType.Ban, Lapka);
        Assert.NotNull(ban);
        Assert.Equal("it's \"cheating\"", ban.Reason);
        Assert.Equal("lapka", ban.PlayerName);
    }

    [Fact]
    public void Old_history_is_pruned_on_load()
    {
        Penalties.Add(PenaltyType.Ban, Lapka, TimeSpan.FromMinutes(1), "old", admin: null);
        Penalties.Add(PenaltyType.Ban, Greeny, null, "current", admin: null);
        Clock = Clock.AddDays(91);

        PenaltyManager.Initialize(Path.Combine(Dir, "penalties"), historyDays: 90);
        Assert.Empty(Penalties.GetHistoryAsync(Lapka).Result);
        Assert.True(Penalties.IsBanned(Greeny)); // active penalties are never pruned
        Assert.DoesNotContain(Lapka.ToString(), File.ReadAllText(PenaltiesFile));
    }

    [Fact]
    public void An_unreadable_file_is_not_overwritten()
    {
        Penalties.Add(PenaltyType.Ban, Greeny, null, "keep me", admin: null);
        File.WriteAllText(PenaltiesFile, File.ReadAllText(PenaltiesFile) + "{ broken");

        Assert.False(PenaltyManager.Reload());
        Assert.True(Penalties.IsBanned(Greeny)); // the previous penalties stay in force

        Penalties.Add(PenaltyType.Ban, Lapka, null, "new", admin: null);
        Assert.Contains("{ broken", File.ReadAllText(PenaltiesFile)); // and the file keeps its history
    }

    [Fact]
    public void Gagged_players_chat_is_blocked_before_plugins_see_it()
    {
        PermissionManager.SetSlotSteamIdForTests(3, Lapka);
        var message = new ChatMessage { SenderSlot = 3, ChatText = "hello", AllChat = true, LaneColor = default };
        Assert.Equal(HookResult.Continue, PluginLoader.DispatchChatMessage(message));

        Penalties.Add(PenaltyType.Gag, Lapka, TimeSpan.FromMinutes(5), "spam", admin: null);
        Assert.Equal(HookResult.Handled, PluginLoader.DispatchChatMessage(message));

        // Chat commands still count as handled, so they're never shown in chat either.
        var command = new ChatMessage { SenderSlot = 3, ChatText = "!unknowncommand", AllChat = true, LaneColor = default };
        Assert.Equal(HookResult.Handled, PluginLoader.DispatchChatMessage(command));
    }

    [Fact]
    public void Activity_is_logged_to_a_daily_file_with_details()
    {
        AdminActivity.Show(null, "kicked lapka: spam", details: $"target={Lapka}");
        AdminActivity.Log(null, "unbanned 1");

        var file = Path.Combine(LogDir, "admin-2026-09-26.log");
        var lines = File.ReadAllLines(file);
        Assert.Equal($"2026-09-26T12:00:00Z Console kicked lapka: spam [target={Lapka}]", lines[0]);
        Assert.Equal("2026-09-26T12:00:00Z Console unbanned 1", lines[1]);
        Assert.Equal(2, Logged.Count);
    }

    [Theory]
    [InlineData("Named", "wisp: kicked lapka")]
    [InlineData("Anonymous", "ADMIN: kicked lapka")]
    [InlineData("None", null)]
    public void Activity_visibility(string visibility, string? expected)
        => Assert.Equal(expected, AdminActivityService.Format(Enum.Parse<AdminActivityService.Visibility>(visibility), "wisp", "kicked lapka"));

    [Theory]
    [InlineData("dl_midtown", true)]
    [InlineData("maps/street_test", true)]
    [InlineData("dl_midtown; quit", false)]
    [InlineData("../../secret", false)]
    [InlineData("\"quoted\"", false)]
    [InlineData("", false)]
    public void Map_names_are_checked_before_they_reach_a_command(string map, bool wellFormed)
        => Assert.Equal(wellFormed, Server.IsMapNameWellFormed(map));

    [Fact]
    public void Echo_cannot_be_split_into_a_second_client_command()
        => Assert.Equal("echo lapka； quit", CCitadelPlayerController.EchoCommand("lapka; quit"));
}

/// <summary>Drives the Admin plugin's commands through real console dispatch, as the server console.</summary>
public sealed class AdminPluginTests : AdminTestBase
{
    private const string PluginPath = "test://AdminPluginTests";
    private readonly AdminPlugin _plugin = new();

    public AdminPluginTests()
    {
        var chat = new HandlerRegistry<string, Func<ChatCommandContext, HookResult>>(StringComparer.OrdinalIgnoreCase);
        CommandRegistration.RegisterPluginCommands(PluginPath, [_plugin], chat);
    }

    public override void Dispose()
    {
        ConCommandManager.UnregisterPlugin(PluginPath);
        PluginRegistrationTracker.Remove(PluginPath);
        PermissionManifest.Remove(PluginPath);
        base.Dispose();
    }

    private static void Console_(params string[] argv) => ConCommandManager.Dispatch(-1, argv[0], argv);

    [Fact]
    public void Addban_and_unban_by_steamid_in_any_format()
    {
        Console_("dw_addban", "STEAM_0:1:11101", "60", "ban", "evasion");
        var ban = Penalties.GetActive(PenaltyType.Ban, Lapka);
        Assert.NotNull(ban);
        Assert.Equal("ban evasion", ban.Reason);
        Assert.Equal(Clock.AddMinutes(60), ban.ExpiresUtc);
        Assert.Equal("banned 76561197960287931 for 1 hour: ban evasion", Logged[^1].Action);

        Console_("dw_unban", "[U:1:22203]");
        Assert.False(Penalties.IsBanned(Lapka));
        Assert.Equal("unbanned 76561197960287931", Logged[^1].Action);
    }

    [Fact]
    public void The_console_can_ban_permanently_and_uses_the_default_reason()
    {
        Console_("dw_addban", Lapka.ToString(), "0");
        var ban = Penalties.GetActive(PenaltyType.Ban, Lapka);
        Assert.NotNull(ban);
        Assert.True(ban.IsPermanent);
        Assert.Equal("Banned by an admin", ban.Reason);
    }

    [Fact]
    public void Bad_input_changes_nothing()
    {
        Console_("dw_addban", "not-a-steamid", "60");
        Console_("dw_addban", Lapka.ToString(), "-5");
        Console_("dw_unban", Lapka.ToString()); // not banned
        Assert.Empty(Penalties.GetActive());
        Assert.Empty(Logged);
    }

    [Fact]
    public void Require_reason_setting_is_enforced()
    {
        _plugin.Config.RequireReason = true;
        Console_("dw_addban", Lapka.ToString(), "60");
        Assert.False(Penalties.IsBanned(Lapka));
        Console_("dw_addban", Lapka.ToString(), "60", "cheating");
        Assert.True(Penalties.IsBanned(Lapka));
    }

    [Fact]
    public void Generated_file_lists_every_permission()
    {
        var text = File.ReadAllText(Path.Combine(Dir, "permissions", "generated", "AdminPlugin.jsonc"));
        foreach (var perm in new[]
                 {
                     "admin.moderation.kick", "admin.moderation.ban", "admin.moderation.ban.permanent", "admin.moderation.ban.offline",
                     "admin.moderation.unban", "admin.moderation.gag", "admin.moderation.slay", "admin.moderation.who",
                     "admin.server.map", "admin.server.rcon", "admin.server.cvar", "admin.server.cvar.cheats",
                     "admin.server.cvar.protected", "admin.server.config"
                 })
            Assert.Contains($"\"tag\": \"{perm}\"", text);
    }

    [Theory]
    [InlineData(30, "for 30 minutes")]
    [InlineData(1, "for 1 minute")]
    [InlineData(60, "for 1 hour")]
    [InlineData(120, "for 2 hours")]
    [InlineData(1440, "for 1 day")]
    [InlineData(10080, "for 7 days")]
    [InlineData(90, "for 1h 30m")]
    public void Durations_read_naturally(int minutes, string expected)
        => Assert.Equal(expected, AdminPlugin.DescribeDuration(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Permanent_duration() => Assert.Equal("permanently", AdminPlugin.DescribeDuration(null));

    [Theory]
    [InlineData("server.cfg", true)]
    [InlineData("events/lan.cfg", true)]
    [InlineData("../../evil.cfg", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("a.cfg; quit", false)]
    public void Config_names_stay_inside_cfg(string file, bool ok) => Assert.Equal(ok, AdminPlugin.IsSafeConfigName(file));
}
