using System.Text.Json;
using DeadworksManaged.Api;
using DeadworksManaged.Commands;
using DeadworksManaged.PermissionSystem;
using Xunit;

namespace DeadworksManaged.Tests;

public class GrantTests
{
    [Theory]
    [InlineData("moderation.player.ban", "moderation.player.ban", true)]
    [InlineData("Moderation.Player.BAN", "moderation.player.ban", true)]
    [InlineData("moderation.player.*", "moderation.player.ban", true)]
    [InlineData("moderation.player.*", "moderation.player.ban.permanent", true)]
    [InlineData("moderation.player.*", "moderation.player", false)]
    [InlineData("moderation.player.*", "moderation.playerban", false)]
    [InlineData("moderation.player.mute", "moderation.player.ban", false)]
    [InlineData("*", "anything.at.all", true)]
    public void Wildcards_stop_at_dots_and_case_is_ignored(string granted, string requested, bool expected)
    {
        Assert.True(Grant.TryParse(granted, out var grant, out _));
        Assert.Equal(expected, grant.Matches(PermissionEvaluator.Normalize(requested)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("a..b")]
    [InlineData("a.*.b")]
    [InlineData("a.b*")]
    [InlineData("a b")]
    public void Malformed_grants_are_rejected(string raw)
    {
        Assert.False(Grant.TryParse(raw, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Exact_beats_longer_wildcard_beats_shorter_wildcard_beats_star()
    {
        static int S(string g) { Assert.True(Grant.TryParse(g, out var x, out _)); return x.Specificity; }
        Assert.True(S("a.b.c") > S("a.b.*"));
        Assert.True(S("a.b.*") > S("a.*"));
        Assert.True(S("a.*") > S("*"));
    }
}

public class EvaluatorTests
{
    private static Dictionary<string, RoleDefinition> Roles() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = new() { Permissions = ["rtd.use"] },
        ["vip"] = new() { Permissions = ["moderation.player.mute"] },
        ["moderator"] = new() { Inherits = ["vip"], Permissions = ["moderation.player.*", "-moderation.player.ban"], Immunity = 50 },
        ["admin"] = new() { Permissions = ["*"], Immunity = 90 },
    };

    private static bool Has(PlayerEntry? player, string permission)
        => PermissionEvaluator.Evaluate(PermissionEvaluator.Compile(Roles(), player), permission).Allowed;

    [Fact]
    public void Nothing_matching_is_denied_and_empty_is_allowed()
    {
        Assert.False(Has(null, "moderation.player.kick"));
        Assert.True(Has(null, ""));
    }

    [Fact]
    public void Default_role_applies_to_everyone()
    {
        Assert.True(Has(null, "rtd.use"));
        Assert.True(Has(new PlayerEntry { Roles = ["admin"] }, "rtd.use"));
    }

    [Fact]
    public void Exact_deny_beats_role_wildcard()
    {
        var mod = new PlayerEntry { Roles = ["moderator"] };
        Assert.True(Has(mod, "moderation.player.kick"));
        Assert.False(Has(mod, "moderation.player.ban"));
    }

    [Fact]
    public void Inherited_role_permissions_apply()
        => Assert.True(Has(new PlayerEntry { Roles = ["moderator"] }, "moderation.player.mute"));

    [Fact]
    public void Admin_missing_one_permission()
    {
        var admin = new PlayerEntry { Roles = ["admin"], Permissions = ["-moderation.player.ban"] };
        Assert.True(Has(admin, "server.map"));
        Assert.False(Has(admin, "moderation.player.ban"));
    }

    [Fact]
    public void Player_grant_beats_role_grant_on_a_tie()
    {
        // moderator denies moderation.player.ban exactly; an exact player grant of equal specificity wins.
        var mod = new PlayerEntry { Roles = ["moderator"], Permissions = ["moderation.player.ban"] };
        Assert.True(Has(mod, "moderation.player.ban"));
    }

    [Fact]
    public void Deny_beats_allow_on_a_tie_from_the_same_scope()
    {
        var p = new PlayerEntry { Permissions = ["a.b", "-a.b"] };
        Assert.False(Has(p, "a.b"));
    }

    [Fact]
    public void Explanation_names_the_deciding_grant_and_source()
    {
        var result = PermissionEvaluator.Evaluate(
            PermissionEvaluator.Compile(Roles(), new PlayerEntry { Roles = ["moderator"] }), "moderation.player.ban");
        Assert.Equal(new PermissionExplanation(false, "-moderation.player.ban", "role:moderator"), result);
    }

    [Fact]
    public void Immunity_is_the_highest_assigned_role_and_not_inherited()
    {
        var roles = Roles();
        roles["trainee"] = new() { Inherits = ["admin"] };
        Assert.Equal(0, PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["trainee"] }).Immunity);
        Assert.Equal(90, PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["moderator", "admin"] }).Immunity);
        Assert.Equal(10, PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["admin"], Immunity = 10 }).Immunity);
    }

    [Fact]
    public void Inheritance_cycles_are_reported_and_do_not_hang()
    {
        var roles = new Dictionary<string, RoleDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new() { Inherits = ["b"], Permissions = ["x.a"] },
            ["b"] = new() { Inherits = ["a"], Permissions = ["x.b"] },
        };
        var subject = PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["a"] });
        Assert.True(PermissionEvaluator.Evaluate(subject, "x.b").Allowed);
        Assert.Contains(PermissionEvaluator.Validate(roles), w => w.Contains("inherits itself"));
    }

    [Theory]
    [InlineData("moderation.player.kick", true)]
    [InlineData("moderation.player.ban", false)]   // denied to the moderator
    [InlineData("moderation.player.*", false)]     // a deny carves ban out of it
    [InlineData("moderation.*", false)]
    [InlineData("-anything", true)]                // denies only take access away
    public void Moderators_can_only_delegate_what_they_hold(string grant, bool expected)
    {
        var mod = PermissionEvaluator.Compile(Roles(), new PlayerEntry { Roles = ["moderator"] });
        Assert.True(Grant.TryParse(grant, out var g, out _));
        Assert.Equal(expected, PermissionEvaluator.CanDelegate(mod, g));
    }

    [Fact]
    public void Star_holders_can_delegate_anything()
    {
        var admin = PermissionEvaluator.Compile(Roles(), new PlayerEntry { Roles = ["admin"] });
        Assert.True(Grant.TryParse("*", out var g, out _));
        Assert.True(PermissionEvaluator.CanDelegate(admin, g));
    }
}

public class SteamIdTests
{
    [Theory]
    [InlineData("STEAM_0:0:11101", 76561197960287930UL)]
    [InlineData("STEAM_1:0:11101", 76561197960287930UL)]
    [InlineData("[U:1:22202]", 76561197960287930UL)]
    [InlineData("76561197960287930", 76561197960287930UL)]
    public void Common_formats_parse(string value, ulong expected)
    {
        Assert.True(SteamIds.TryParse(value, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("U:1:22202")]
    [InlineData("wisp")]
    [InlineData("12345")]
    public void Other_text_does_not_parse(string value) => Assert.False(SteamIds.TryParse(value, out _));

    [Fact]
    public void Formats_round_trip()
    {
        Assert.Equal("STEAM_0:0:11101", SteamIds.ToSteam2(76561197960287930UL));
        Assert.Equal("[U:1:22202]", SteamIds.ToSteam3(76561197960287930UL));
    }
}

public class TargetResolverTests
{
    private static readonly TargetResolver.Candidate Wisp = new(0, "wisp", 2, 76561197960287930UL);
    private static readonly TargetResolver.Candidate Lapka = new(1, "lapka", 2, 76561197960287931UL);
    private static readonly TargetResolver.Candidate Greeny = new(2, "greeny", 3, 76561197960287932UL);
    private static readonly TargetResolver.Candidate Green = new(3, "green", 3, 76561197960287933UL);
    private static readonly TargetResolver.Candidate[] All = [Wisp, Lapka, Greeny, Green];

    private static List<int> Match(string input, Func<TargetResolver.Candidate, bool>? canTarget = null)
    {
        Assert.True(TargetResolver.TryMatch(input, Wisp, All, canTarget, out var slots, out _, out var error), error);
        return slots;
    }

    [Theory]
    [InlineData("@me", new[] { 0 })]
    [InlineData("@all", new[] { 0, 1, 2, 3 })]
    [InlineData("@team", new[] { 0, 1 })]
    [InlineData("@enemy", new[] { 2, 3 })]
    [InlineData("#2", new[] { 2 })]
    [InlineData("76561197960287931", new[] { 1 })]
    [InlineData("STEAM_0:1:11101", new[] { 1 })]
    [InlineData("LAP", new[] { 1 })]
    [InlineData("green", new[] { 3 })] // an exact name wins over a longer name containing it
    public void Selectors(string input, int[] expected) => Assert.Equal(expected, Match(input));

    [Fact]
    public void Ambiguous_name_lists_the_matches()
    {
        Assert.False(TargetResolver.TryMatch("gre", Wisp, All, null, out _, out _, out var error));
        Assert.Contains("greeny (#2)", error);
        Assert.Contains("green (#3)", error);
    }

    [Fact]
    public void Immune_single_target_is_an_error()
    {
        Assert.False(TargetResolver.TryMatch("lapka", Wisp, All, c => c.Slot != 1, out _, out _, out var error));
        Assert.Equal("You can't target lapka.", error);
    }

    [Fact]
    public void Immune_players_are_dropped_from_groups()
        => Assert.Equal([0, 2, 3], Match("@all", c => c.Slot != 1));

    [Fact]
    public void Console_has_no_me()
        => Assert.False(TargetResolver.TryMatch("@me", null, All, null, out _, out _, out _));
}

public class CommandOverrideTests
{
    [Fact]
    public void Any_name_of_a_command_can_be_overridden_and_prefixes_are_ignored()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "overrides.jsonc");
            File.WriteAllText(path, """
                // comment
                { "commands": { "!gag": "custom.chat", "dw_rtd": "", } }
                """);
            CommandOverrides.Load(path);

            Assert.Equal("custom.chat", CommandOverrides.Resolve(["mute", "gag"], "moderation.player.mute", out var overridden));
            Assert.True(overridden);
            Assert.Equal("", CommandOverrides.Resolve(["rtd"], "rtd.use", out _));
            Assert.Equal("moderation.player.kick", CommandOverrides.Resolve(["kick"], "moderation.player.kick", out overridden));
            Assert.False(overridden);
        }
        finally
        {
            CommandOverrides.Set([]);
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>Drives <see cref="PermissionManager"/> against the JSON store in a temp directory.</summary>
public sealed class PermissionManagerTests : IDisposable
{
    private const ulong Admin = 76561197960287930UL;
    private const ulong Moderator = 76561197960287931UL;
    private const ulong Nobody = 76561197960287939UL;

    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public PermissionManagerTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "roles.jsonc"), """
            {
              // comments and trailing commas are fine
              "default":   { "permissions": ["rtd.use"] },
              "moderator": { "permissions": ["moderation.player.*"], "immunity": 50, },
              "admin":     { "permissions": ["*"], "immunity": 90 }
            }
            """);
        File.WriteAllText(Path.Combine(_dir, "players.jsonc"), """
            {
              "STEAM_0:0:11101": { "roles": ["admin"] },
              "[U:1:22203]": { "roles": ["moderator"], "permissions": ["-moderation.player.ban"] }
            }
            """);
        PermissionManager.IsSlotAuthenticated = _ => true;
        PermissionManager.Initialize(_dir);
    }

    public void Dispose()
    {
        for (int i = 0; i < Players.MaxSlot; i++)
            PermissionManager.SetSlotSteamIdForTests(i, 0);
        PermissionManager.IsSlotAuthenticated = _ => true;
        PermissionManager.RequireSteamAuth = true;
        CommandOverrides.Set([]);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Loads_jsonc_with_any_steam_id_format()
    {
        Assert.True(Permissions.Has(Admin, "server.rcon"));
        Assert.True(Permissions.Has(Moderator, "moderation.player.kick"));
        Assert.False(Permissions.Has(Moderator, "moderation.player.ban"));
        Assert.True(Permissions.Has(Nobody, "rtd.use"));
        Assert.False(Permissions.Has(Nobody, "moderation.player.kick"));
    }

    [Fact]
    public void Writes_default_files_and_generated_dir_on_first_run()
    {
        var fresh = Directory.CreateTempSubdirectory().FullName;
        try
        {
            PermissionManager.Initialize(fresh);
            Assert.True(File.Exists(Path.Combine(fresh, "roles.jsonc")));
            Assert.True(File.Exists(Path.Combine(fresh, "players.jsonc")));
            Assert.True(File.Exists(Path.Combine(fresh, "overrides.jsonc")));
            Assert.Equal(["admin", "default"], PermissionManager.Roles.Keys.Order());
            Assert.False(Permissions.Has(Admin, "anything")); // nobody is listed yet
        }
        finally
        {
            Directory.Delete(fresh, recursive: true);
        }
    }

    [Fact]
    public void Saved_grants_persist_and_survive_reload()
    {
        Assert.Null(PermissionManager.Change(Nobody, PermissionManager.ChangeKind.GrantRole, "moderator", temporary: false, "newbie"));
        Assert.True(Permissions.Has(Nobody, "moderation.player.kick"));

        var text = File.ReadAllText(Path.Combine(_dir, "players.jsonc"));
        Assert.StartsWith("// Players and the roles they hold.", text);
        Assert.Contains("\"76561197960287939\"", text);
        Assert.Contains("\"newbie\"", text);

        Assert.True(PermissionManager.Reload());
        Assert.True(Permissions.Has(Nobody, "moderation.player.kick"));
        Assert.True(Permissions.Has(Moderator, "moderation.player.kick")); // untouched entries survive the rewrite
        Assert.False(Permissions.Has(Moderator, "moderation.player.ban"));

        Assert.Null(PermissionManager.Change(Nobody, PermissionManager.ChangeKind.RevokeRole, "moderator", temporary: false));
        Assert.False(Permissions.Has(Nobody, "moderation.player.kick"));
        Assert.DoesNotContain("76561197960287939", File.ReadAllText(Path.Combine(_dir, "players.jsonc")));
    }

    [Fact]
    public void Temporary_grants_are_not_saved_and_vanish_on_restart()
    {
        Assert.Null(PermissionManager.Change(Nobody, PermissionManager.ChangeKind.GrantPermission, "moderation.player.kick", temporary: true));
        Assert.True(Permissions.Has(Nobody, "moderation.player.kick"));
        Assert.DoesNotContain("76561197960287939", File.ReadAllText(Path.Combine(_dir, "players.jsonc")));

        Assert.Null(PermissionManager.Change(Admin, PermissionManager.ChangeKind.RevokeRole, "admin", temporary: true));
        Assert.False(Permissions.Has(Admin, "server.rcon"));

        PermissionManager.Initialize(_dir);
        Assert.False(Permissions.Has(Nobody, "moderation.player.kick"));
        Assert.True(Permissions.Has(Admin, "server.rcon"));
    }

    [Fact]
    public void Changes_are_validated()
    {
        Assert.NotNull(PermissionManager.Change(Nobody, PermissionManager.ChangeKind.GrantRole, "nosuchrole", false));
        Assert.NotNull(PermissionManager.Change(Nobody, PermissionManager.ChangeKind.GrantRole, "default", false));
        Assert.NotNull(PermissionManager.Change(Nobody, PermissionManager.ChangeKind.GrantPermission, "a..b", false));
        Assert.NotNull(PermissionManager.Change(Admin, PermissionManager.ChangeKind.GrantRole, "admin", false));
        Assert.NotNull(PermissionManager.Change(Nobody, PermissionManager.ChangeKind.RevokeRole, "admin", false));
    }

    [Fact]
    public void Connected_players_only_get_default_until_steam_validates_them()
    {
        PermissionManager.OnClientConnect(4, Admin);
        PermissionManager.IsSlotAuthenticated = _ => false;
        Assert.False(PermissionManager.HasForSlot(4, "server.rcon"));
        Assert.True(PermissionManager.HasForSlot(4, "rtd.use"));
        Assert.StartsWith("unauthenticated", PermissionManager.ExplainSlot(4, "server.rcon").Source);

        PermissionManager.IsSlotAuthenticated = _ => true;
        Assert.True(PermissionManager.HasForSlot(4, "server.rcon"));

        PermissionManager.IsSlotAuthenticated = _ => false;
        PermissionManager.RequireSteamAuth = false;
        Assert.True(PermissionManager.HasForSlot(4, "server.rcon"));
    }

    [Fact]
    public void Slot_identity_is_the_connect_steam_id_and_clears_on_disconnect()
    {
        PermissionManager.OnClientConnect(5, Admin);
        Assert.Equal(Admin, Permissions.GetSteamId(5));
        PermissionManager.OnClientDisconnect(5);
        Assert.Equal(0UL, Permissions.GetSteamId(5));
        Assert.False(PermissionManager.HasForSlot(5, "server.rcon"));

        PermissionManager.OnClientConnect(6, Admin);
        PermissionManager.OnClientPutInServer(6, isBot: true);
        Assert.False(PermissionManager.HasForSlot(6, "server.rcon"));
    }

    [Fact]
    public void Immunity_decides_targeting()
    {
        PermissionManager.OnClientConnect(1, Admin);
        PermissionManager.OnClientConnect(2, Moderator);
        PermissionManager.OnClientConnect(3, Nobody);

        Assert.True(PermissionManager.CanTargetSlots(1, 2));
        Assert.False(PermissionManager.CanTargetSlots(2, 1));
        Assert.True(PermissionManager.CanTargetSlots(2, 2));  // self
        Assert.True(PermissionManager.CanTargetSlots(-1, 1)); // console
        Assert.True(PermissionManager.CanTargetSlots(3, 3));
        Assert.False(PermissionManager.CanTargetSlots(3, 2));
        Assert.True(Permissions.CanTarget(Moderator, Nobody));
        Assert.Equal(90, Permissions.GetImmunity(Admin));
    }

    [Fact]
    public void Changed_fires_on_grants()
    {
        var seen = new List<ulong?>();
        void Handler(ulong? id) => seen.Add(id);
        Permissions.Changed += Handler;
        try
        {
            PermissionManager.Change(Nobody, PermissionManager.ChangeKind.GrantPermission, "a.b", temporary: true);
            PermissionManager.Reload();
        }
        finally
        {
            Permissions.Changed -= Handler;
        }
        Assert.Equal([Nobody, null], seen);
    }

    // --- Command dispatch ---

    private sealed class GatedPlugin : DeadworksPluginBase
    {
        public override string Name => "Moderation";
        public List<string> Ran { get; } = [];

        [Command("kick", Permission = "moderation.player.kick")]
        public void Kick(CCitadelPlayerController? caller) => Ran.Add("kick");

        [Command("ping")]
        public void Ping(CCitadelPlayerController? caller) => Ran.Add("ping");
    }

    private const string PluginPath = "test://PermissionManagerTests";

    private static GatedPlugin Dispatch(Action<HandlerRegistry<string, Func<ChatCommandContext, HookResult>>> dispatch)
    {
        var plugin = new GatedPlugin();
        var chat = new HandlerRegistry<string, Func<ChatCommandContext, HookResult>>(StringComparer.OrdinalIgnoreCase);
        CommandRegistration.RegisterPluginCommands(PluginPath, [plugin], chat);
        try
        {
            dispatch(chat);
        }
        finally
        {
            ConCommandManager.UnregisterPlugin(PluginPath);
            PluginRegistrationTracker.Remove(PluginPath);
            PermissionManifest.Remove(PluginPath);
        }
        return plugin;
    }

    [Fact]
    public void Server_console_runs_gated_commands()
        => Assert.Equal(["kick"], Dispatch(_ => ConCommandManager.Dispatch(-1, "dw_kick", ["dw_kick"])).Ran);

    [Fact]
    public void Unidentified_player_is_denied_gated_but_not_public_commands()
    {
        // No engine here, so a player slot resolves to no controller: the most restricted caller there is.
        var plugin = Dispatch(_ =>
        {
            ConCommandManager.Dispatch(7, "dw_kick", ["dw_kick"]);
            ConCommandManager.Dispatch(7, "dw_ping", ["dw_ping"]);
        });
        Assert.Equal(["ping"], plugin.Ran);
    }

    [Fact]
    public void Denied_chat_command_is_handled_so_it_is_not_broadcast()
    {
        HookResult result = HookResult.Continue;
        var plugin = Dispatch(chat =>
        {
            var message = new ChatMessage { SenderSlot = -1, ChatText = "!kick", AllChat = true, LaneColor = default };
            foreach (var handler in chat.Snapshot("kick") ?? [])
                result = handler(new ChatCommandContext(message, "kick", [], '!'));
        });
        Assert.Empty(plugin.Ran);
        Assert.Equal(HookResult.Handled, result);
    }

    [Fact]
    public void Overrides_apply_at_dispatch_without_reregistering()
    {
        var plugin = Dispatch(_ =>
        {
            CommandOverrides.Set(new() { ["kick"] = "" });
            ConCommandManager.Dispatch(7, "dw_kick", ["dw_kick"]);
            CommandOverrides.Set(new() { ["ping"] = "moderation.player.ping" });
            ConCommandManager.Dispatch(7, "dw_ping", ["dw_ping"]);
        });
        Assert.Equal(["kick"], plugin.Ran);
    }

    [Fact]
    public void Registering_writes_a_commented_generated_file()
    {
        Dispatch(_ => { });
        var path = Path.Combine(_dir, "generated", "GatedPlugin.jsonc");
        var text = File.ReadAllText(path);

        Assert.StartsWith("// ====", text);
        Assert.Contains("AUTO-GENERATED. DO NOT EDIT", text);
        Assert.Contains("\"Moderation\" plugin", text);
        Assert.Contains("// !kick / /kick / dw_kick", text);

        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var kick = doc.RootElement.GetProperty("commands").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "kick");
        Assert.Equal("moderation.player.kick", kick.GetProperty("permission").GetString());
        Assert.Equal("Enforce", kick.GetProperty("targetImmunity").GetString());
        Assert.Equal("moderation.player.kick", doc.RootElement.GetProperty("permissions")[0].GetProperty("tag").GetString());
    }

    [Fact]
    public void Generated_file_shows_overrides()
    {
        var info = new PermissionManifest.PluginInfo("Moderation", "Moderation",
            [new PermissionManifest.CommandInfo(["kick", "k"], "Kick", "moderation.player.kick", TargetImmunity.Auto, false, false, false)],
            [new DeclarePermissionAttribute("moderation.player.kick.silent") { Description = "Kick without a message" }]);
        CommandOverrides.Set(new() { ["k"] = "custom.kick" });

        var text = PermissionManifest.Render(info);
        Assert.Contains("OVERRIDDEN in overrides.jsonc. The plugin asks for \"moderation.player.kick\".", text);
        Assert.Contains("(aliases: k)", text);
        Assert.Contains("// Checked in code. Kick without a message", text);

        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var kick = doc.RootElement.GetProperty("commands")[0];
        Assert.Equal("custom.kick", kick.GetProperty("permission").GetString());
        Assert.Equal("moderation.player.kick", kick.GetProperty("declaredPermission").GetString());
    }

    [Fact]
    public void Stale_generated_files_are_deleted()
    {
        var generated = Path.Combine(_dir, "generated");
        Directory.CreateDirectory(generated);
        File.WriteAllText(Path.Combine(generated, "UninstalledPlugin.jsonc"), "{}");
        PermissionManifest.DeleteStale();
        Assert.False(File.Exists(Path.Combine(generated, "UninstalledPlugin.jsonc")));
    }
}
