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
        AdminActivityService.Logged += OnLogged;
    }

    private void OnLogged(AdminLogEntry entry) => Logged.Add(entry);

    public virtual void Dispose()
    {
        AdminActivityService.Logged -= OnLogged;
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
        PenaltyManager.Added += added.Add;
        PenaltyManager.Removed += removed.Add;
        try
        {
            Penalties.Add(PenaltyType.Ban, Lapka, TimeSpan.FromMinutes(60), "spam", by: Caller.Console, playerName: "lapka");
            Assert.True(Penalties.IsBanned(Lapka));
            Assert.False(Penalties.IsBanned(Greeny));
            Assert.Equal("You are banned from this server for 1 hour. Reason: spam", PenaltyManager.ConnectRejection(Lapka));

            Clock = Clock.AddMinutes(61);
            Assert.False(Penalties.IsBanned(Lapka));
            Assert.Null(PenaltyManager.ConnectRejection(Lapka));
        }
        finally
        {
            PenaltyManager.Added -= added.Add;
            PenaltyManager.Removed -= removed.Add;
        }
        Assert.Single(added);
        Assert.Equal(added[0].Id, Assert.Single(removed).Id); // expiry raises Removed
    }

    [Fact]
    public void Permanent_penalties_have_no_end()
    {
        Penalties.Add(PenaltyType.Gag, Lapka, null, "", by: Caller.Console);
        Clock = Clock.AddYears(10);
        var gag = Penalties.GetActive(PenaltyType.Gag, Lapka);
        Assert.NotNull(gag);
        Assert.True(gag.IsPermanent);
        Assert.Equal("You are gagged permanently and can't use chat.", PenaltyManager.GagMessage(gag));
    }

    [Fact]
    public void A_new_penalty_replaces_the_active_one_of_the_same_type()
    {
        var first = Penalties.Add(PenaltyType.Ban, Lapka, TimeSpan.FromDays(1), "first", by: Caller.Console);
        Penalties.Add(PenaltyType.Gag, Lapka, TimeSpan.FromDays(1), "gag", by: Caller.Console);
        var second = Penalties.Add(PenaltyType.Ban, Lapka, null, "second", by: Caller.Console);

        Assert.Equal(second.Id, Penalties.GetActive(PenaltyType.Ban, Lapka)!.Id);
        Assert.Equal(2, Penalties.GetActive().Count); // the ban and the gag

        var history = Penalties.GetHistoryAsync(Lapka).Result;
        Assert.Equal(3, history.Count);
        Assert.NotNull(history.Single(p => p.Id == first.Id).RemovedUtc);
    }

    [Fact]
    public void Remove_lifts_the_penalty_and_keeps_history()
    {
        Penalties.Add(PenaltyType.Ban, Lapka, null, "x", by: Caller.Console);
        Assert.True(Penalties.Remove(PenaltyType.Ban, Lapka, by: Caller.Console));
        Assert.False(Penalties.Remove(PenaltyType.Ban, Lapka, by: Caller.Console));
        Assert.False(Penalties.IsBanned(Lapka));
        Assert.NotNull(Assert.Single(Penalties.GetHistoryAsync(Lapka).Result).RemovedUtc);
    }

    [Fact]
    public void Penalties_survive_a_restart()
    {
        Penalties.Add(PenaltyType.Ban, Lapka, TimeSpan.FromDays(7), "it's \"cheating\"", by: Caller.Console, playerName: "lapka");

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
        Penalties.Add(PenaltyType.Ban, Lapka, TimeSpan.FromMinutes(1), "old", by: Caller.Console);
        Penalties.Add(PenaltyType.Ban, Greeny, null, "current", by: Caller.Console);
        Clock = Clock.AddDays(91);

        PenaltyManager.Initialize(Path.Combine(Dir, "penalties"), historyDays: 90);
        Assert.Empty(Penalties.GetHistoryAsync(Lapka).Result);
        Assert.True(Penalties.IsBanned(Greeny)); // active penalties are never pruned
        Assert.DoesNotContain(Lapka.ToString(), File.ReadAllText(PenaltiesFile));
    }

    [Fact]
    public void An_unreadable_file_is_not_overwritten()
    {
        Penalties.Add(PenaltyType.Ban, Greeny, null, "keep me", by: Caller.Console);
        File.WriteAllText(PenaltiesFile, File.ReadAllText(PenaltiesFile) + "{ broken");

        Assert.False(PenaltyManager.Reload());
        Assert.True(Penalties.IsBanned(Greeny)); // the previous penalties stay in force

        // New ones are refused rather than enforced until restart and then lost.
        var error = Assert.Throws<CommandException>(() => Penalties.Add(PenaltyType.Ban, Lapka, null, "new", by: Caller.Console));
        Assert.Contains("penalties.jsonc has an error", error.Message);
        Assert.Contains("{ broken", File.ReadAllText(PenaltiesFile)); // and the file keeps its history
    }

    [Fact]
    public void A_ban_list_that_never_loaded_keeps_new_players_out()
    {
        File.WriteAllText(PenaltiesFile, "{ broken");
        PenaltyManager.Initialize(Path.Combine(Dir, "penalties"));
        Assert.Equal(PenaltyManager.UnavailableRejection, PenaltyManager.ConnectRejection(Lapka));
        Assert.Null(PenaltyManager.ConnectRejection(0)); // bots
        Assert.Throws<CommandException>(() => Penalties.Add(PenaltyType.Gag, Lapka, null, "x", Caller.Console));
    }

    private sealed class MemoryPenaltyStore : IPenaltyStore
    {
        public Task<IReadOnlyList<Penalty>> LoadActiveAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Penalty>>([]);
        public Task AddAsync(Penalty penalty, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateAsync(Penalty penalty, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Penalty>> LoadHistoryAsync(ulong steamId64, CancellationToken ct) => Task.FromResult<IReadOnlyList<Penalty>>([]);
        public event Action? Changed { add { } remove { } }
    }

    private sealed class StoreOwner : DeadworksPluginBase
    {
        public override string Name => "Store";
    }

    [Fact]
    public void Players_reloading_after_a_map_change_are_not_refused_while_the_ban_list_is_down()
    {
        PenaltyManager.Initialize(Path.Combine(Dir, "penalties"), "mysql");
        Assert.Equal(PenaltyManager.UnavailableRejection, PenaltyManager.ConnectRejection(Lapka));
        Assert.Null(PenaltyManager.ConnectRejection(Lapka, isMapChangeReconnect: true));
    }

    [Fact]
    public void A_configured_penalty_store_that_is_not_registered_fails_closed()
    {
        PenaltyManager.Initialize(Path.Combine(Dir, "penalties"), "mysql");
        Assert.Equal(PenaltyManager.UnavailableRejection, PenaltyManager.ConnectRejection(Lapka));

        var owner = new StoreOwner();
        Penalties.RegisterStore(owner, "mysql", new MemoryPenaltyStore());
        TimerEngine.OnTick(); // it loads on the next tick
        Assert.Null(PenaltyManager.ConnectRejection(Lapka));
        Penalties.Add(PenaltyType.Ban, Lapka, null, "x", Caller.Console);
        Assert.NotNull(PenaltyManager.ConnectRejection(Lapka));

        PenaltyManager.UnregisterStoresOwnedBy([owner]);
        Assert.Equal(PenaltyManager.UnavailableRejection, PenaltyManager.ConnectRejection(Greeny));
    }

    [Fact]
    public void Players_steam_has_not_verified_yet_cannot_be_penalized()
    {
        PermissionManager.OnClientConnect(4, Lapka);
        PermissionManager.IsSlotAuthenticated = _ => false;
        try
        {
            var error = Assert.Throws<CommandException>(() => Penalties.Add(PenaltyType.Ban, Lapka, null, "x", Caller.Console));
            Assert.Contains("hasn't been verified by Steam yet", error.Message);
            Assert.Throws<CommandException>(() => Penalties.Add(PenaltyType.Gag, Lapka, null, "x", Caller.Console));
            Assert.Empty(Penalties.GetActive());

            // Offline SteamIDs are unaffected, and so is everyone once Steam has confirmed them.
            Penalties.Add(PenaltyType.Ban, Greeny, null, "x", Caller.Console);
            PermissionManager.IsSlotAuthenticated = _ => true;
            Penalties.Add(PenaltyType.Gag, Lapka, null, "x", Caller.Console);
            Assert.Equal(2, Penalties.GetActive().Count);
        }
        finally
        {
            PermissionManager.IsSlotAuthenticated = _ => true;
        }
    }

    [Fact]
    public void Hand_edits_to_penalties_jsonc_survive_the_next_change()
    {
        Penalties.Add(PenaltyType.Ban, Lapka, TimeSpan.FromHours(1), "first", Caller.Console);

        // The owner adds a ban by hand and changes the first ban's reason, without reloading.
        var text = File.ReadAllText(PenaltiesFile)
            .Replace("\"reason\": \"first\"", "\"reason\": \"edited by hand\"")
            .Replace("\"penalties\": [", "\"penalties\": [ { \"type\": \"Ban\", \"steamId64\": 76561197960287999, \"reason\": \"added by hand\" },");
        File.WriteAllText(PenaltiesFile, text);

        Penalties.Add(PenaltyType.Gag, Greeny, TimeSpan.FromMinutes(5), "spam", Caller.Console);

        var saved = File.ReadAllText(PenaltiesFile);
        Assert.Contains("edited by hand", saved);
        Assert.Contains("added by hand", saved);
        Assert.Contains("spam", saved);
    }

    [Fact]
    public void A_penalties_file_broken_after_loading_refuses_changes_and_isnt_overwritten()
    {
        Penalties.Add(PenaltyType.Ban, Lapka, TimeSpan.FromHours(1), "first", Caller.Console);
        var broken = File.ReadAllText(PenaltiesFile) + "oops";
        File.WriteAllText(PenaltiesFile, broken);

        var ex = Assert.Throws<CommandException>(() => Penalties.Add(PenaltyType.Gag, Greeny, TimeSpan.FromMinutes(5), "spam", Caller.Console));
        Assert.StartsWith("Penalties can't be changed right now: penalties.jsonc has an error.", ex.Message);
        Assert.Throws<CommandException>(() => Penalties.Remove(PenaltyType.Ban, Lapka, Caller.Console));
        Assert.Equal(broken, File.ReadAllText(PenaltiesFile));
        Assert.True(Penalties.IsBanned(Lapka)); // what was loaded keeps being enforced
    }

    [Fact]
    public void WouldShorten_finds_the_penalty_a_new_one_cuts_short()
    {
        Assert.Null(Penalties.WouldShorten(PenaltyType.Ban, Lapka, TimeSpan.FromMinutes(5))); // nothing to replace

        Penalties.Add(PenaltyType.Ban, Lapka, TimeSpan.FromHours(2), "", by: Caller.Console);
        Assert.NotNull(Penalties.WouldShorten(PenaltyType.Ban, Lapka, TimeSpan.FromMinutes(5)));
        Assert.Null(Penalties.WouldShorten(PenaltyType.Ban, Lapka, TimeSpan.FromHours(3)));
        Assert.Null(Penalties.WouldShorten(PenaltyType.Ban, Lapka, null));
        Assert.Null(Penalties.WouldShorten(PenaltyType.Gag, Lapka, TimeSpan.FromMinutes(5))); // other types don't count

        Penalties.Add(PenaltyType.Ban, Lapka, null, "", by: Caller.Console);
        Assert.NotNull(Penalties.WouldShorten(PenaltyType.Ban, Lapka, TimeSpan.FromDays(3650)));
        Assert.Null(Penalties.WouldShorten(PenaltyType.Ban, Lapka, null));
    }

    [Fact]
    public void A_zero_or_negative_duration_is_refused_rather_than_expiring_at_once()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Penalties.Add(PenaltyType.Ban, Lapka, TimeSpan.Zero, "", Caller.Console));
        Assert.Throws<ArgumentOutOfRangeException>(() => Penalties.Add(PenaltyType.Gag, Lapka, TimeSpan.FromMinutes(-1), "", Caller.Console));
        Assert.Empty(Penalties.GetActive());
    }

    [Fact]
    public void Muted_players_voice_is_dropped()
    {
        const int voice = (int)CLC_Messages.ClcVoiceData;
        PermissionManager.SetSlotSteamIdForTests(3, Lapka);
        Assert.False(PenaltyManager.DropsVoice(3, voice));

        Penalties.Add(PenaltyType.Mute, Lapka, TimeSpan.FromMinutes(5), "mic spam", by: Caller.Console);
        Assert.True(PenaltyManager.DropsVoice(3, voice));
        Assert.False(PenaltyManager.DropsVoice(3, (int)CLC_Messages.ClcMove)); // only voice
        Assert.False(PenaltyManager.DropsVoice(4, voice));                       // only them

        Clock = Clock.AddMinutes(6); // expired, even before anything sweeps it
        Assert.False(PenaltyManager.DropsVoice(3, voice));
    }

    [Fact]
    public void Unmuting_lets_them_talk_again()
    {
        const int voice = (int)CLC_Messages.ClcVoiceData;
        PermissionManager.SetSlotSteamIdForTests(3, Lapka);
        Penalties.Add(PenaltyType.Mute, Lapka, null, "", by: Caller.Console);
        Assert.True(PenaltyManager.DropsVoice(3, voice));
        Assert.True(Penalties.Remove(PenaltyType.Mute, Lapka, Caller.Console));
        Assert.False(PenaltyManager.DropsVoice(3, voice));
    }

    [Fact]
    public void Gagged_players_chat_is_blocked_before_plugins_see_it()
    {
        PermissionManager.SetSlotSteamIdForTests(3, Lapka);
        var message = new ChatMessage { SenderSlot = 3, ChatText = "hello", AllChat = true, LaneColor = default };
        Assert.Equal(HookResult.Continue, PluginLoader.DispatchChatMessage(message));

        Penalties.Add(PenaltyType.Gag, Lapka, TimeSpan.FromMinutes(5), "spam", by: Caller.Console);
        Assert.Equal(HookResult.Handled, PluginLoader.DispatchChatMessage(message));

        // Chat commands still count as handled, so they're never shown in chat either.
        var command = new ChatMessage { SenderSlot = 3, ChatText = "!unknowncommand", AllChat = true, LaneColor = default };
        Assert.Equal(HookResult.Handled, PluginLoader.DispatchChatMessage(command));
    }

    [Fact]
    public void Activity_is_logged_to_a_daily_file_with_details()
    {
        AdminActivity.Show(Caller.Console, "kicked lapka: spam", details: $"target={Lapka}");
        AdminActivity.Log(Caller.Console, "unbanned 1");

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
    public void Ban_and_unban_by_steamid_in_any_format()
    {
        Console_("dw_ban", "STEAM_0:1:11101", "60", "ban", "evasion");
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
    public void Replacing_a_ban_says_what_it_replaced()
    {
        Console_("dw_ban", Lapka.ToString(), "0", "cheating");
        Console_("dw_ban", Lapka.ToString(), "60", "appeal");
        Assert.Equal($"banned {Lapka} for 1 hour: appeal (replaces a permanent ban by Console)", Logged[^1].Action);
        Assert.Equal(Clock.AddMinutes(60), Penalties.GetActive(PenaltyType.Ban, Lapka)!.ExpiresUtc);
    }

    [Fact]
    public void The_console_can_ban_permanently_and_uses_the_default_reason()
    {
        Console_("dw_ban", Lapka.ToString(), "0");
        var ban = Penalties.GetActive(PenaltyType.Ban, Lapka);
        Assert.NotNull(ban);
        Assert.True(ban.IsPermanent);
        Assert.Equal("Banned by an admin", ban.Reason);
    }

    [Fact]
    public void Bad_input_changes_nothing()
    {
        Console_("dw_ban", "not-a-steamid", "60");
        Console_("dw_ban", Lapka.ToString(), "-5");
        Console_("dw_unban", Lapka.ToString()); // not banned
        Assert.Empty(Penalties.GetActive());
        Assert.Empty(Logged);
    }

    [Fact]
    public void Addban_is_another_name_for_ban()
    {
        Console_("dw_addban", "[U:1:22203]", "30");
        Assert.True(Penalties.IsBanned(Lapka));
        Assert.Equal("banned 76561197960287931 for 30 minutes: Banned by an admin", Logged[^1].Action);
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
                     "admin.moderation.kick", "admin.moderation.ban", "admin.moderation.unban", "admin.moderation.gag", "admin.moderation.mute",
                     "admin.moderation.slay", "admin.moderation.who",
                     "admin.server.map", "admin.server.rcon", "admin.server.cvar", "admin.server.config"
                 })
            Assert.Contains($"\"tag\": \"{perm}\"", text);

        // No permission is also the parent of another.
        Assert.DoesNotContain("admin.moderation.ban.", text);
        Assert.DoesNotContain("admin.server.cvar.", text);
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
    [InlineData(0.5, "for 1 minute")]
    [InlineData(59.5, "for 1 hour")]      // rounded up, never "60m"
    [InlineData(65, "for 1h 5m")]
    [InlineData(120, "for 2 hours")]
    [InlineData(1440, "for 1 day")]
    [InlineData(1500, "for 1d 1h")]
    [InlineData(4320, "for 3 days")]
    public void Time_left_reads_like_announcements(double minutes, string expected)
    {
        var now = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        var penalty = new Penalty { Type = PenaltyType.Ban, SteamId64 = Lapka, ExpiresUtc = now.AddMinutes(minutes) };
        Assert.Equal(expected, penalty.DescribeRemaining(now));
    }

    [Theory]
    [InlineData("lapka", "lapka")]
    [InlineData("lapka,wisp", "lapka and wisp")]
    [InlineData("lapka,wisp,dingus", "lapka, wisp and dingus")]
    [InlineData("lapka,wisp,dingus,fella", "lapka, wisp, dingus and fella")]
    [InlineData("lapka,wisp,dingus,fella,bot", "lapka, wisp, dingus and 2 others")]
    public void Group_actions_name_their_targets_in_one_line(string names, string expected)
        => Assert.Equal(expected, AdminPlugin.ListNames(names.Split(',')));

    [Theory]
    [InlineData("sv_password hunter2", "sv_password (value hidden)")]
    [InlineData("\"sv_password\" hunter2", "sv_password (value hidden)")]
    [InlineData("sv_password", "sv_password")]   // reading it isn't a secret in the log
    [InlineData("sv_cheats 1", "sv_cheats 1")]
    [InlineData("sv_cheats 1; sv_password hunter2", "sv_cheats 1; sv_password (value hidden)")]
    public void Rcon_lines_that_set_passwords_are_redacted_in_the_log(string line, string logged)
        => Assert.Equal(logged, AdminPlugin.Redact(line, name => name == "sv_password"));

    [Theory]
    [InlineData("server.cfg", true)]
    [InlineData("events/lan.cfg", true)]
    [InlineData("../../evil.cfg", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("a.cfg; quit", false)]
    public void Config_names_stay_inside_cfg(string file, bool ok) => Assert.Equal(ok, AdminPlugin.IsSafeConfigName(file));
}

/// <summary>Staff changes made through the core permission commands, as the server console.</summary>
public sealed class StaffChangeLogTests : AdminTestBase
{
    private const string PluginPath = "test://StaffChangeLogTests";

    public StaffChangeLogTests()
    {
        var chat = new HandlerRegistry<string, Func<ChatCommandContext, HookResult>>(StringComparer.OrdinalIgnoreCase);
        CommandRegistration.RegisterPluginCommands(PluginPath, [new PermissionCommands(), new AdminPlugin()], chat);
    }

    private static string Run(params string[] argv)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            ConCommandManager.Dispatch(-1, argv[0], argv);
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }

    [Fact]
    public void Perm_check_takes_a_command_name_and_explains_what_it_needs()
    {
        Run("dw_role_grant", Lapka.ToString(), "admin");
        var output = Run("dw_perm_check", Lapka.ToString(), "!ban");
        Assert.Contains("ban (Admin) needs admin.moderation.ban, which is allowed", output);
        Assert.Contains("open to everyone", Run("dw_perm_check", Greeny.ToString(), "penalties"));
    }

    [Fact]
    public void Granting_or_checking_something_no_plugin_declares_gets_a_note()
    {
        Assert.Contains("no loaded plugin declares admin.moderation.bna", Run("dw_perm_grant", Lapka.ToString(), "admin.moderation.bna"));
        Assert.DoesNotContain("no loaded plugin", Run("dw_perm_grant", Lapka.ToString(), "admin.moderation.*"));
        Assert.Contains("no loaded plugin declares admin.moderation.bna", Run("dw_perm_check", Lapka.ToString(), "admin.moderation.bna"));
    }

    public override void Dispose()
    {
        ConCommandManager.UnregisterPlugin(PluginPath);
        PluginRegistrationTracker.Remove(PluginPath);
        PermissionManifest.Remove(PluginPath);
        base.Dispose();
    }

    [Fact]
    public void Role_and_permission_changes_go_in_the_admin_log()
    {
        ConCommandManager.Dispatch(-1, "dw_role_grant", ["dw_role_grant", Lapka.ToString(), "admin"]);
        ConCommandManager.Dispatch(-1, "dw_perm_grant", ["dw_perm_grant", Lapka.ToString(), "-admin.server.rcon", "--temp"]);

        Assert.Equal(["gave 76561197960287931 the role admin", "gave 76561197960287931 -admin.server.rcon until restart"],
            Logged.Select(e => e.Action));
        Assert.All(Logged, e => Assert.Equal($"target={Lapka}", e.Details));
    }
}
