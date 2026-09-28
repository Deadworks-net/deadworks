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
    public void Immunity_is_inherited_unless_a_role_or_player_sets_it()
    {
        var roles = Roles();
        roles["trainee"] = new() { Inherits = ["admin"] };
        roles["intern"] = new() { Inherits = ["admin"], Immunity = 5 };
        Assert.Equal(90, PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["trainee"] }).Immunity);
        Assert.Equal(5, PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["intern"] }).Immunity);
        Assert.Equal(90, PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["moderator", "admin"] }).Immunity);
        Assert.Equal(10, PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["admin"], Immunity = 10 }).Immunity);
    }

    [Fact]
    public void A_role_can_undo_a_deny_it_inherits()
    {
        var roles = Roles();
        roles["senior"] = new() { Inherits = ["moderator"], Permissions = ["moderation.player.ban"] };
        var senior = PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["senior"] });
        Assert.True(PermissionEvaluator.Evaluate(senior, "moderation.player.ban").Allowed);
        Assert.True(PermissionEvaluator.Evaluate(senior, "moderation.player.kick").Allowed); // still inherited
    }

    [Fact]
    public void A_roles_own_wildcard_beats_an_inherited_exact_deny()
    {
        var roles = Roles();
        roles["admin"] = new() { Inherits = ["moderator"], Permissions = ["*"] };
        Assert.True(Has(new PlayerEntry { Roles = ["admin"] }, "moderation.player.ban"));
    }

    [Fact]
    public void A_deny_in_one_role_does_not_take_away_what_another_role_gives()
    {
        var roles = Roles();
        roles["eventhost"] = new() { Permissions = ["server.map"] };
        roles["trial"] = new() { Permissions = ["moderation.player.*", "-server.map"] };
        var subject = PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["trial", "eventhost"] });
        Assert.True(PermissionEvaluator.Evaluate(subject, "server.map").Allowed);

        // A deny on the player's own entry does.
        var limited = PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["trial", "eventhost"], Permissions = ["-server.map"] });
        Assert.Equal(new PermissionExplanation(false, "-server.map", "player"), PermissionEvaluator.Evaluate(limited, "server.map"));
    }

    [Fact]
    public void The_players_own_entry_decides_before_any_role()
    {
        // Even a wildcard on the player beats a more specific role grant.
        var p = new PlayerEntry { Roles = ["admin"], Permissions = ["-moderation.*"] };
        Assert.False(Has(p, "moderation.player.kick"));
        Assert.True(Has(p, "server.map"));
    }

    [Fact]
    public void Explanation_names_the_role_a_grant_was_inherited_through()
    {
        // moderator's own moderation.player.* would decide moderation.player.mute, so use something only vip gives.
        var roles = Roles();
        roles["vip"].Permissions.Add("chat.color");
        var result = PermissionEvaluator.Evaluate(PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["moderator"] }), "chat.color");
        Assert.Equal(new PermissionExplanation(true, "chat.color", "role:vip (via moderator)"), result);
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
    public void Handing_out_a_role_needs_its_allows_except_what_the_role_itself_denies()
    {
        var roles = new Dictionary<string, RoleDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new(),
            ["admin"] = new() { Permissions = ["server.*", "-server.rcon"], Immunity = 90 },
            ["base"] = new() { Permissions = ["-server.rcon"] },
            ["sneaky"] = new() { Inherits = ["base"], Permissions = ["server.*"] },
        };
        var admin = PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["admin"] });

        IReadOnlyList<string> NotHeld(string role) => PermissionEvaluator.NotHeld(admin, PermissionEvaluator.RoleSubject(roles, role)!);

        Assert.Empty(NotHeld("admin"));                  // the role denies rcon too
        Assert.Equal(["server.rcon"], NotHeld("sneaky")); // its own server.* beats the inherited deny, so it really gives rcon
    }

    [Fact]
    public void A_more_specific_allow_in_the_role_is_not_excused_by_its_broader_deny()
    {
        var roles = new Dictionary<string, RoleDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["tricky"] = new() { Permissions = ["-a.*", "a.b.*"] },
        };
        var giver = PermissionEvaluator.Compile(roles, new PlayerEntry { Permissions = ["a.b.*", "-a.b.x"] });
        Assert.Equal(["a.b.x"], PermissionEvaluator.NotHeld(giver, PermissionEvaluator.RoleSubject(roles, "tricky")!));
    }

    [Fact]
    public void Holding_each_permission_separately_is_enough_to_hand_out_a_wildcard_that_names_nothing_else()
    {
        var giver = PermissionEvaluator.Compile(Roles(), new PlayerEntry { Permissions = ["x.*"] });
        Assert.True(Grant.TryParse("x.y.*", out var narrower, out _));
        Assert.True(PermissionEvaluator.CanDelegate(giver, narrower));
        Assert.True(Grant.TryParse("x.*", out var same, out _));
        Assert.True(PermissionEvaluator.CanDelegate(giver, same));
        Assert.True(Grant.TryParse("*", out var everything, out _));
        Assert.False(PermissionEvaluator.CanDelegate(giver, everything));
    }

    [Fact]
    public void NotHeld_agrees_with_checking_every_permission()
    {
        var rng = new Random(12345);
        string[] segments = ["a", "b"];
        string RandomGrant()
        {
            var parts = Enumerable.Range(0, rng.Next(1, 4)).Select(_ => segments[rng.Next(segments.Length)]).ToList();
            var kind = rng.Next(10);
            var pattern = kind == 0 ? "*" : kind < 5 ? string.Join('.', parts) + ".*" : string.Join('.', parts);
            return (rng.Next(3) == 0 ? "-" : "") + pattern;
        }
        List<string> Grants(int max) => Enumerable.Range(0, rng.Next(0, max)).Select(_ => RandomGrant()).ToList();

        // Every dotted permission up to depth 4 over a, b and a name no grant uses.
        var universe = new List<string>();
        void Generate(string prefix, int depth)
        {
            if (depth > 4) return;
            foreach (var s in new[] { "a", "b", "x" })
            {
                var q = prefix.Length == 0 ? s : prefix + "." + s;
                universe.Add(q);
                Generate(q, depth + 1);
            }
        }
        Generate("", 1);

        for (int i = 0; i < 2000; i++)
        {
            var roles = new Dictionary<string, RoleDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = new() { Permissions = Grants(2) },
                ["p"] = new() { Permissions = Grants(3) },
                ["g"] = new() { Permissions = Grants(3), Inherits = rng.Next(2) == 0 ? ["p"] : [] },
                ["r"] = new() { Permissions = Grants(4), Inherits = rng.Next(2) == 0 ? ["p"] : [] },
            };
            var giver = PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = ["g"], Permissions = Grants(3) });
            var gift = PermissionEvaluator.RoleSubject(roles, "r")!;
            var missing = universe.Any(q => PermissionEvaluator.Evaluate(gift, q).Allowed && !PermissionEvaluator.Evaluate(giver, q).Allowed);
            Assert.Equal(missing, PermissionEvaluator.NotHeld(giver, gift).Count > 0);
        }
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
    private static readonly CommandOverrides.Owner Owner = new("Admin", "AdminPlugin");
    private static readonly CommandOverrides.Owner Other = new("Fun", "FunPlugin");

    [Fact]
    public void A_plugin_qualified_entry_beats_a_bare_one_and_only_hits_that_plugin()
    {
        try
        {
            CommandOverrides.Set(new() { ["kick"] = "all.kick", ["Admin:kick"] = "admin.kick", ["AdminPlugin:!ban"] = "" });
            Assert.Equal("admin.kick", CommandOverrides.Resolve(["kick"], "x", Owner, out _));
            Assert.Equal("all.kick", CommandOverrides.Resolve(["kick"], "x", Other, out _));
            Assert.Equal("", CommandOverrides.Resolve(["ban"], "x", Owner, out var overridden)); // by DLL name, prefix ignored
            Assert.True(overridden);
            Assert.Equal("x", CommandOverrides.Resolve(["ban"], "x", Other, out overridden));
            Assert.False(overridden);
        }
        finally
        {
            CommandOverrides.Set([]);
        }
    }

    [Fact]
    public void Entries_matching_no_command_are_reported()
    {
        try
        {
            CommandOverrides.Set(new() { ["kick"] = "", ["Admin:kick"] = "", ["Fun:kick"] = "", ["kcik"] = "" });
            var commands = new List<(CommandOverrides.Owner, IReadOnlyList<string>)> { (Owner, ["kick", "k"]) };
            Assert.Equal(["Fun:kick", "kcik"], CommandOverrides.UnknownKeys(commands).Order());
        }
        finally
        {
            CommandOverrides.Set([]);
        }
    }

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

            Assert.Equal("custom.chat", CommandOverrides.Resolve(["mute", "gag"], "moderation.player.mute", Owner, out var overridden));
            Assert.True(overridden);
            Assert.Equal("", CommandOverrides.Resolve(["rtd"], "rtd.use", Owner, out _));
            Assert.Equal("moderation.player.kick", CommandOverrides.Resolve(["kick"], "moderation.player.kick", Owner, out overridden));
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

    private static string? Change(ulong id, PermissionManager.ChangeKind kind, string value, bool temporary, string? name = null)
    {
        var task = PermissionManager.ChangeAsync(id, kind, value, temporary, name);
        Assert.True(task.IsCompleted); // the JSON store saves synchronously
        return task.Result;
    }

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
        PermissionManager.IsLanServer = () => false;
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
    public void A_failed_reload_keeps_the_previous_roles_and_says_why()
    {
        File.AppendAllText(Path.Combine(_dir, "players.jsonc"), "oops");
        Assert.False(PermissionManager.Reload());
        Assert.StartsWith("players.jsonc:", PermissionManager.LastLoadError);
        Assert.True(Permissions.Has(Admin, "server.rcon")); // still the previous settings

        File.WriteAllText(Path.Combine(_dir, "players.jsonc"), File.ReadAllText(Path.Combine(_dir, "players.jsonc"))[..^"oops".Length]);
        Assert.True(PermissionManager.Reload());
        Assert.Null(PermissionManager.LastLoadError);
    }

    [Fact]
    public void Saved_grants_persist_and_survive_reload()
    {
        Assert.Null(Change(Nobody, PermissionManager.ChangeKind.GrantRole, "moderator", temporary: false, "newbie"));
        Assert.True(Permissions.Has(Nobody, "moderation.player.kick"));

        var text = File.ReadAllText(Path.Combine(_dir, "players.jsonc"));
        Assert.StartsWith("// Players and the roles they hold.", text);
        Assert.Contains("\"76561197960287939\"", text);
        Assert.Contains("\"newbie\"", text);

        Assert.True(PermissionManager.Reload());
        Assert.True(Permissions.Has(Nobody, "moderation.player.kick"));
        Assert.True(Permissions.Has(Moderator, "moderation.player.kick")); // untouched entries survive the rewrite
        Assert.False(Permissions.Has(Moderator, "moderation.player.ban"));

        Assert.Null(Change(Nobody, PermissionManager.ChangeKind.RevokeRole, "moderator", temporary: false));
        Assert.False(Permissions.Has(Nobody, "moderation.player.kick"));
        Assert.DoesNotContain("76561197960287939", File.ReadAllText(Path.Combine(_dir, "players.jsonc")));
    }

    [Fact]
    public void Temporary_grants_are_not_saved_and_vanish_on_restart()
    {
        Assert.Null(Change(Nobody, PermissionManager.ChangeKind.GrantPermission, "moderation.player.kick", temporary: true));
        Assert.True(Permissions.Has(Nobody, "moderation.player.kick"));
        Assert.DoesNotContain("76561197960287939", File.ReadAllText(Path.Combine(_dir, "players.jsonc")));

        Assert.Null(Change(Admin, PermissionManager.ChangeKind.RevokeRole, "admin", temporary: true));
        Assert.False(Permissions.Has(Admin, "server.rcon"));

        PermissionManager.Initialize(_dir);
        Assert.False(Permissions.Has(Nobody, "moderation.player.kick"));
        Assert.True(Permissions.Has(Admin, "server.rcon"));
    }

    [Fact]
    public void Changes_are_validated()
    {
        Assert.NotNull(Change(Nobody, PermissionManager.ChangeKind.GrantRole, "nosuchrole", false));
        Assert.NotNull(Change(Nobody, PermissionManager.ChangeKind.GrantRole, "default", false));
        Assert.NotNull(Change(Nobody, PermissionManager.ChangeKind.GrantPermission, "a..b", false));
        Assert.NotNull(Change(Admin, PermissionManager.ChangeKind.GrantRole, "admin", false));
        Assert.NotNull(Change(Nobody, PermissionManager.ChangeKind.RevokeRole, "admin", false));
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
    public void Steam_validating_a_player_counts_as_their_permissions_changing()
    {
        var changed = new List<ulong?>();
        void OnChanged(ulong? id) => changed.Add(id);
        PermissionManager.Changed += OnChanged;
        try
        {
            PermissionManager.OnClientConnect(4, Admin);
            PermissionManager.IsSlotAuthenticated = _ => false;
            Assert.Empty(PermissionManager.TakeNewlyAuthorized());
            changed.Clear();

            PermissionManager.IsSlotAuthenticated = _ => true;
            Assert.Single(PermissionManager.TakeNewlyAuthorized());
            Assert.Equal([Admin], changed);
        }
        finally
        {
            PermissionManager.Changed -= OnChanged;
        }
    }

    [Fact]
    public void An_admin_steam_hasnt_confirmed_yet_is_still_protected_by_their_immunity()
    {
        PermissionManager.OnClientConnect(4, Admin);     // immunity 90 once confirmed
        PermissionManager.OnClientConnect(5, Moderator); // immunity 50
        PermissionManager.IsSlotAuthenticated = slot => slot == 5;
        Assert.False(PermissionManager.CanTargetSlots(5, 4)); // not kickable just because Steam is slow or down
        Assert.False(Permissions.CanTarget(Moderator, Admin));
    }

    [Fact]
    public void Checking_by_steam_id_waits_for_steam_too()
    {
        // Plugins often check caller.SteamId64 instead of the slot; that mustn't hand out permissions early.
        Assert.True(Permissions.Has(Admin, "server.rcon")); // offline: judged by the saved entry
        PermissionManager.OnClientConnect(4, Admin);
        PermissionManager.IsSlotAuthenticated = _ => false;
        Assert.False(Permissions.Has(Admin, "server.rcon"));
        Assert.True(Permissions.Has(Admin, "rtd.use"));
        Assert.False(Permissions.CanTarget(Admin, Moderator)); // acts with default's immunity 0, not admin's 90

        PermissionManager.IsSlotAuthenticated = _ => true;
        Assert.True(Permissions.Has(Admin, "server.rcon"));
        Assert.True(Permissions.CanTarget(Admin, Moderator));
    }

    [Fact]
    public void Sv_lan_skips_the_steam_wait()
    {
        PermissionManager.OnClientConnect(4, Admin);
        PermissionManager.IsSlotAuthenticated = _ => false;
        PermissionManager.IsLanServer = () => true;
        Assert.True(PermissionManager.HasForSlot(4, "server.rcon"));
        Assert.True(Players.IsAuthenticated(4));
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
        PermissionManager.Changed += Handler;
        try
        {
            Change(Nobody, PermissionManager.ChangeKind.GrantPermission, "a.b", temporary: true);
            PermissionManager.Reload();
        }
        finally
        {
            PermissionManager.Changed -= Handler;
        }
        Assert.Equal([Nobody, null], seen);
    }

    private sealed class CountingStore : IPermissionStore
    {
        public int PlayerLoads;
        public bool Fail;
        public Task<IReadOnlyDictionary<string, RoleDefinition>> LoadRolesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, RoleDefinition>>(new Dictionary<string, RoleDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["admin"] = new() { Permissions = ["*"] }
            });
        public Task<PlayerEntry?> LoadPlayerAsync(ulong steamId64, CancellationToken ct)
        {
            PlayerLoads++;
            return Fail ? Task.FromException<PlayerEntry?>(new IOException("database down")) : Task.FromResult<PlayerEntry?>(new PlayerEntry { Roles = ["admin"] });
        }
        public Task SavePlayerAsync(ulong steamId64, PlayerEntry? entry, CancellationToken ct) => Task.CompletedTask;
        public event Action<ulong?>? Changed { add { } remove { } }
    }

    private sealed class StoreOwner : DeadworksPluginBase
    {
        public override string Name => "Store";
    }

    [Fact]
    public void A_configured_store_that_is_not_registered_fails_closed()
    {
        PermissionManager.Initialize(_dir, "mysql");
        Assert.True(PermissionManager.StoreUnavailable);
        Assert.False(Permissions.Has(Admin, "server.rcon")); // players.jsonc lists Admin, but it isn't consulted
        Assert.NotNull(Change(Nobody, PermissionManager.ChangeKind.GrantPermission, "a.b", temporary: false));

        var owner = new StoreOwner();
        Permissions.RegisterStore(owner, "mysql", new CountingStore());
        Assert.False(Permissions.Has(Admin, "server.rcon")); // it loads on the next tick
        TimerEngine.OnTick();
        Assert.True(Permissions.Has(Admin, "server.rcon"));

        PermissionManager.UnregisterStoresOwnedBy([owner]);
        Assert.True(PermissionManager.StoreUnavailable);
        Assert.False(Permissions.Has(Admin, "server.rcon"));
    }

    [Fact]
    public void A_failed_player_load_is_not_retried_on_every_check()
    {
        PermissionManager.Initialize(_dir, "mysql");
        var store = new CountingStore { Fail = true };
        Permissions.RegisterStore(new StoreOwner(), "mysql", store);
        TimerEngine.OnTick();

        Assert.False(Permissions.Has(Admin, "server.rcon"));
        Assert.False(Permissions.Has(Admin, "server.rcon"));
        Assert.False(Permissions.Has(Admin, "server.map"));
        Assert.Equal(1, store.PlayerLoads);
    }

    // --- Management rules for player callers ---

    private static PermissionCommands.ResolvedPlayer Target(ulong id, int slot) => new(id, slot, null);

    [Fact]
    public void Players_cannot_change_themselves_or_anyone_with_equal_or_higher_immunity()
    {
        PermissionManager.OnClientConnect(1, Admin);
        PermissionManager.OnClientConnect(2, Moderator);
        File.WriteAllText(Path.Combine(_dir, "players.jsonc"), """
            {
              "STEAM_0:0:11101": { "roles": ["admin"] },
              "[U:1:22203]": { "roles": ["moderator"] },
              "76561197960287938": { "roles": ["moderator"] }
            }
            """);
        PermissionManager.Reload();

        var self = Assert.Throws<CommandException>(() => PermissionCommands.CheckAllowed(1, Target(Admin, 1), PermissionManager.ChangeKind.RevokePermission, "a.b"));
        Assert.Contains("your own", self.Message);
        Assert.Throws<CommandException>(() => PermissionCommands.CheckAllowed(2, Target(Admin, 1), PermissionManager.ChangeKind.RevokeRole, "admin"));
        Assert.Throws<CommandException>(() => PermissionCommands.CheckAllowed(2, Target(76561197960287938UL, -1), PermissionManager.ChangeKind.RevokeRole, "moderator")); // equal
        PermissionCommands.CheckAllowed(1, Target(Moderator, 2), PermissionManager.ChangeKind.RevokeRole, "moderator");
    }

    [Fact]
    public void Players_can_only_give_out_what_they_hold_and_lifting_a_deny_counts_as_giving()
    {
        PermissionManager.OnClientConnect(2, Moderator); // moderation.player.* minus ban (on their own entry)

        PermissionCommands.CheckAllowed(2, Target(Nobody, -1), PermissionManager.ChangeKind.GrantPermission, "moderation.player.kick");
        Assert.Throws<CommandException>(() => PermissionCommands.CheckAllowed(2, Target(Nobody, -1), PermissionManager.ChangeKind.GrantPermission, "moderation.player.ban"));
        Assert.Throws<CommandException>(() => PermissionCommands.CheckAllowed(2, Target(Nobody, -1), PermissionManager.ChangeKind.GrantRole, "admin"));
        Assert.Throws<CommandException>(() => PermissionCommands.CheckAllowed(2, Target(Nobody, -1), PermissionManager.ChangeKind.RevokePermission, "-moderation.player.ban"));
        PermissionCommands.CheckAllowed(2, Target(Nobody, -1), PermissionManager.ChangeKind.RevokePermission, "-moderation.player.kick");
        PermissionCommands.CheckAllowed(2, Target(Nobody, -1), PermissionManager.ChangeKind.GrantPermission, "-anything"); // adding a deny needs nothing
    }

    [Fact]
    public void Management_judges_an_unconfirmed_target_by_their_saved_entry()
    {
        PermissionManager.OnClientConnect(1, Admin);
        PermissionManager.OnClientConnect(2, Moderator);
        File.WriteAllText(Path.Combine(_dir, "players.jsonc"), """
            {
              "STEAM_0:0:11101": { "roles": ["admin"] },
              "[U:1:22203]": { "roles": ["admin"], "immunity": 50 }
            }
            """);
        PermissionManager.Reload();
        PermissionManager.IsSlotAuthenticated = slot => slot != 1; // the admin in slot 1 isn't confirmed yet

        var error = Assert.Throws<CommandException>(() => PermissionCommands.CheckAllowed(2, Target(Admin, 1), PermissionManager.ChangeKind.RevokeRole, "admin"));
        Assert.Contains("higher immunity", error.Message);
    }

    [Fact]
    public void Roles_with_immunity_at_or_above_yours_cannot_be_handed_out()
    {
        File.WriteAllText(Path.Combine(_dir, "roles.jsonc"), """
            {
              "default": { "permissions": [] },
              "moderator": { "permissions": ["deadworks.permissions.manage", "moderation.player.*"], "immunity": 50 },
              "shield": { "permissions": ["moderation.player.kick"], "immunity": 100 },
              "helper": { "permissions": ["moderation.player.kick"], "immunity": 10 }
            }
            """);
        PermissionManager.Reload();
        PermissionManager.OnClientConnect(2, Moderator);

        var error = Assert.Throws<CommandException>(() => PermissionCommands.CheckAllowed(2, Target(Nobody, -1), PermissionManager.ChangeKind.GrantRole, "shield"));
        Assert.Contains("immunity", error.Message);
        PermissionCommands.CheckAllowed(2, Target(Nobody, -1), PermissionManager.ChangeKind.GrantRole, "helper");
    }

    private sealed class SlowStore : IPermissionStore
    {
        public readonly TaskCompletionSource<PlayerEntry?> Pending = new();
        public Task<IReadOnlyDictionary<string, RoleDefinition>> LoadRolesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, RoleDefinition>>(new Dictionary<string, RoleDefinition>
            {
                ["admin"] = new() { Permissions = ["*"], Immunity = 90 }
            });
        public Task<PlayerEntry?> LoadPlayerAsync(ulong steamId64, CancellationToken ct)
            => steamId64 == Moderator ? Task.FromResult<PlayerEntry?>(new PlayerEntry { Roles = ["admin"], Immunity = 50 }) : Pending.Task;
        public Task SavePlayerAsync(ulong steamId64, PlayerEntry? entry, CancellationToken ct) => Task.CompletedTask;
        public event Action<ulong?>? Changed { add { } remove { } }
    }

    [Fact]
    public void Someone_whose_entry_is_still_loading_cannot_be_targeted_or_changed()
    {
        PermissionManager.Initialize(_dir, "slow");
        Permissions.RegisterStore(new StoreOwner(), "slow", new SlowStore());
        TimerEngine.OnTick();
        PermissionManager.OnClientConnect(2, Moderator);

        Assert.False(Permissions.CanTarget(Moderator, Admin)); // Admin's entry hasn't arrived
        var error = Assert.Throws<CommandException>(() => PermissionCommands.CheckAllowed(2, Target(Admin, -1), PermissionManager.ChangeKind.RevokeRole, "admin"));
        Assert.Contains("still loading", error.Message);
        Assert.Contains("still loading", PermissionManager.ChangeAsync(Admin, PermissionManager.ChangeKind.RevokeRole, "admin", temporary: true).Result);
    }

    [Fact]
    public void Null_lists_in_a_hand_edited_file_are_treated_as_empty()
    {
        File.WriteAllText(Path.Combine(_dir, "roles.jsonc"), """
            { "default": { "permissions": null, "inherits": null }, "admin": { "permissions": ["*", null] } }
            """);
        File.WriteAllText(Path.Combine(_dir, "players.jsonc"), """
            { "STEAM_0:0:11101": { "roles": ["admin"], "permissions": null }, "[U:1:22203]": { "roles": null } }
            """);
        Assert.True(PermissionManager.Reload());
        Assert.True(Permissions.Has(Admin, "anything"));
        Assert.False(Permissions.Has(Moderator, "anything"));
    }

    [Fact]
    public void Saving_keeps_hand_edits_made_since_the_last_reload()
    {
        var path = Path.Combine(_dir, "players.jsonc");
        File.WriteAllText(path, """
            {
              "STEAM_0:0:11101": { "roles": ["admin"] },
              "[U:1:22203]": { "roles": ["moderator"] },
              "76561197960287950": { "roles": ["moderator"] },
              "not-a-steamid": { "roles": ["admin"] }
            }
            """);
        Assert.Null(Change(Nobody, PermissionManager.ChangeKind.GrantRole, "moderator", temporary: false));

        var text = File.ReadAllText(path);
        Assert.Contains("76561197960287950", text); // added by hand, never reloaded
        Assert.Contains("[U:1:22203]", text);       // other entries keep their key format
        Assert.Contains("not-a-steamid", text);     // even ones Deadworks can't read
        Assert.Contains("76561197960287939", text);
    }

    [Fact]
    public void Saving_leaves_a_broken_players_file_alone()
    {
        var path = Path.Combine(_dir, "players.jsonc");
        File.WriteAllText(path, "{ \"STEAM_0:0:11101\": { \"roles\": [\"admin\"] }, oops");
        var error = Change(Nobody, PermissionManager.ChangeKind.GrantRole, "moderator", temporary: false);
        Assert.Contains("has an error", error);
        Assert.EndsWith("oops", File.ReadAllText(path));
        Assert.False(Permissions.Has(Nobody, "moderation.player.kick"));
    }

    [Fact]
    public void A_role_that_no_longer_exists_can_still_be_revoked()
    {
        File.WriteAllText(Path.Combine(_dir, "players.jsonc"), """{ "STEAM_0:0:11101": { "roles": ["admin", "retired"] } }""");
        PermissionManager.Reload();
        Assert.Null(Change(Admin, PermissionManager.ChangeKind.RevokeRole, "retired", temporary: false));
        Assert.DoesNotContain("retired", File.ReadAllText(Path.Combine(_dir, "players.jsonc")));
    }

    [Fact]
    public void A_steam_confirmed_slot_wins_when_two_claim_a_steam_id()
    {
        PermissionManager.OnClientConnect(2, Admin);
        PermissionManager.OnClientConnect(5, Admin);
        PermissionManager.IsSlotAuthenticated = slot => slot == 5;
        Assert.Equal(5, PermissionManager.FindSlot(Admin));
    }

    [Fact]
    public void Reconnecting_after_a_map_change_does_not_raise_OnClientAuthorized_again()
    {
        PermissionManager.OnClientConnect(3, Admin);
        Assert.Equal([(3, Admin)], PermissionManager.TakeNewlyAuthorized());

        // A map change runs connect again for the same player in the same slot, with no disconnect in between.
        PermissionManager.OnClientConnect(3, Admin);
        Assert.Empty(PermissionManager.TakeNewlyAuthorized());
        Assert.True(PermissionManager.HasForSlot(3, "server.rcon"));

        // Someone else in that slot is a new connection.
        PermissionManager.OnClientDisconnect(3);
        PermissionManager.OnClientConnect(3, Moderator);
        Assert.Equal([(3, Moderator)], PermissionManager.TakeNewlyAuthorized());
    }

    [Fact]
    public void Disconnecting_forgets_the_entry_so_the_next_connect_reads_the_store()
    {
        PermissionManager.OnClientConnect(3, Admin);
        Assert.True(PermissionManager.HasArrived(Admin));
        PermissionManager.OnClientDisconnect(3);
        Assert.False(PermissionManager.HasArrived(Admin));
        Assert.True(PermissionManager.IsLoaded(Admin)); // asking loads it again; the JSON store answers at once
    }

    [Fact]
    public void Generated_files_are_named_after_the_dll()
    {
        var path = Path.Combine(_dir, "..", "SomePlugin.dll");
        PermissionManifest.Add(Path.GetFullPath(path), new StoreOwner(), []);
        try
        {
            Assert.True(File.Exists(Path.Combine(_dir, "generated", "SomePlugin.jsonc")));
            Assert.False(File.Exists(Path.Combine(_dir, "generated", "StoreOwner.jsonc")));
        }
        finally
        {
            PermissionManifest.Remove(Path.GetFullPath(path));
        }
    }

    [Fact]
    public void Null_lists_in_players_jsonc_do_not_break_the_startup_warnings()
    {
        File.WriteAllText(Path.Combine(_dir, "players.jsonc"), $$"""{ "{{Nobody}}": { "permissions": null, "roles": null } }""");
        Assert.True(PermissionManager.Reload());
        PermissionManager.OnStartupComplete();
    }

    [Fact]
    public void A_hand_edit_to_the_same_player_is_not_overwritten()
    {
        var path = Path.Combine(_dir, "players.jsonc");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"roles\": [\"admin\"]", "\"roles\": [\"admin\", \"moderator\"]"));
        var error = Change(Admin, PermissionManager.ChangeKind.GrantPermission, "a.b", temporary: false);
        Assert.Contains("edited for this player since the last reload", error);
        Assert.Contains("\"moderator\"", File.ReadAllText(path));

        PermissionManager.Reload();
        Assert.Null(Change(Admin, PermissionManager.ChangeKind.GrantPermission, "a.b", temporary: false));
    }

    private sealed class SlowSaveStore : IPermissionStore
    {
        public readonly TaskCompletionSource Saved = new();
        public Task<IReadOnlyDictionary<string, RoleDefinition>> LoadRolesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, RoleDefinition>>(new Dictionary<string, RoleDefinition> { ["admin"] = new() { Permissions = ["*"] } });
        public Task<PlayerEntry?> LoadPlayerAsync(ulong steamId64, CancellationToken ct) => Task.FromResult<PlayerEntry?>(null);
        public Task SavePlayerAsync(ulong steamId64, PlayerEntry? entry, CancellationToken ct) => Saved.Task;
        public event Action<ulong?>? Changed { add { } remove { } }
    }

    [Fact]
    public void A_save_that_finishes_after_a_reload_does_not_overwrite_the_newer_data()
    {
        PermissionManager.Initialize(_dir, "slow");
        var store = new SlowSaveStore();
        Permissions.RegisterStore(new StoreOwner(), "slow", store);
        TimerEngine.OnTick();

        var change = PermissionManager.ChangeAsync(Nobody, PermissionManager.ChangeKind.GrantRole, "admin", temporary: false);
        Assert.False(change.IsCompleted);
        PermissionManager.Reload();  // the store's data is now newer than the pending change
        store.Saved.SetResult();
        // The save's continuation reaches the tick queue from the thread pool, so keep ticking until it lands.
        for (var i = 0; i < 500 && !change.IsCompleted; i++)
        {
            TimerEngine.OnTick();
            Thread.Sleep(5);
        }
        Assert.True(change.IsCompleted);

        Assert.Null(change.Result);
        Assert.False(Permissions.Has(Nobody, "anything")); // what the store says, not the stale in-flight entry
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

        [Command("whoami")]
        public void WhoAmI(Caller caller) => Ran.Add(caller.IsConsole ? "console" : caller.Name);
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
    public void A_caller_parameter_is_the_console_for_console_commands()
        => Assert.Equal(["console"], Dispatch(_ => ConCommandManager.Dispatch(-1, "dw_whoami", ["dw_whoami"])).Ran);

    [Fact]
    public void Server_console_runs_gated_commands()
        => Assert.Equal(["kick"], Dispatch(_ => ConCommandManager.Dispatch(-1, "dw_kick", ["dw_kick"])).Ran);

    [Fact]
    public void A_player_slot_without_a_controller_runs_nothing_not_even_public_commands()
    {
        // No engine here, so a player slot resolves to no controller. It must never be mistaken for the console.
        var plugin = Dispatch(_ =>
        {
            ConCommandManager.Dispatch(7, "dw_kick", ["dw_kick"]);
            ConCommandManager.Dispatch(7, "dw_ping", ["dw_ping"]);
            ConCommandManager.Dispatch(7, "dw_whoami", ["dw_whoami"]);
        });
        Assert.Empty(plugin.Ran);
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
        // Seen through the generated file, since there's no player controller to dispatch as.
        CommandOverrides.Set(new() { ["kick"] = "" });
        Dispatch(_ => { });
        var text = File.ReadAllText(Path.Combine(_dir, "generated", "GatedPlugin.jsonc"));
        Assert.Contains("OVERRIDDEN in overrides.jsonc", text);
    }

    [Fact]
    public void An_unreadable_overrides_file_at_startup_locks_player_commands()
    {
        var path = Path.Combine(_dir, "overrides.jsonc");
        try
        {
            CommandOverrides.ResetForTests();
            File.WriteAllText(path, "{ broken");
            Assert.False(CommandOverrides.Load(path));
            Assert.True(CommandOverrides.Unreadable);

            // A later failure keeps the last good overrides instead.
            File.WriteAllText(path, """{ "commands": { "kick": "" } }""");
            Assert.True(CommandOverrides.Load(path));
            Assert.False(CommandOverrides.Unreadable);
            File.WriteAllText(path, "{ broken");
            Assert.False(CommandOverrides.Load(path));
            Assert.False(CommandOverrides.Unreadable);
            Assert.False(PermissionManager.Reload()); // and dw_perm_reload says it failed
        }
        finally
        {
            CommandOverrides.ResetForTests();
        }
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
        Assert.Contains("/* !kick / /kick / dw_kick */", text);

        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var kick = doc.RootElement.GetProperty("commands").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "kick");
        Assert.Equal("moderation.player.kick", kick.GetProperty("permission").GetString());
        Assert.Equal("Enforce", kick.GetProperty("targetImmunity").GetString());
        Assert.Equal("moderation.player.kick", doc.RootElement.GetProperty("permissions")[0].GetProperty("tag").GetString());
    }

    [Fact]
    public void Generated_file_shows_overrides()
    {
        var info = new PermissionManifest.PluginInfo("Moderation", new CommandOverrides.Owner("Moderation", "Moderation"),
            [new PermissionManifest.CommandInfo(["kick", "k"], "Kick someone who's \"afk\"", "moderation.player.kick", TargetImmunity.Auto, false, false, false)],
            [new DeclarePermissionAttribute("moderation.player.kick.silent") { Description = "Kick without a message" }]);
        CommandOverrides.Set(new() { ["k"] = "custom.kick" });

        var text = PermissionManifest.Render(info);
        Assert.Contains("OVERRIDDEN in overrides.jsonc. The plugin asks for \"moderation.player.kick\".", text);
        Assert.Contains("(aliases: k)", text);
        Assert.Contains("""
            "description": "Kick someone who's \"afk\"",
            """, text); // readable, but still valid JSON
        Assert.Contains("/* Checked in code. Kick without a message */", text);

        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var kick = doc.RootElement.GetProperty("commands")[0];
        Assert.Equal("custom.kick", kick.GetProperty("permission").GetString());
        Assert.Equal("moderation.player.kick", kick.GetProperty("declaredPermission").GetString());
    }

    [Fact]
    public void Making_a_moderation_command_public_keeps_immunity()
    {
        var info = new PermissionManifest.PluginInfo("Moderation", new CommandOverrides.Owner("Moderation", "Moderation"),
            [new PermissionManifest.CommandInfo(["slay"], "Kill a hero", "moderation.slay", TargetImmunity.Auto, false, false, false),
             new PermissionManifest.CommandInfo(["stats"], "Show stats", "", TargetImmunity.Auto, false, false, false)],
            []);
        CommandOverrides.Set(new() { ["slay"] = "", ["stats"] = "vip.stats" });

        using var doc = JsonDocument.Parse(PermissionManifest.Render(info), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var commands = doc.RootElement.GetProperty("commands");
        Assert.Equal("", commands[0].GetProperty("permission").GetString());
        Assert.Equal("Enforce", commands[0].GetProperty("targetImmunity").GetString());
        Assert.Equal("vip.stats", commands[1].GetProperty("permission").GetString());
        Assert.Equal("Ignore", commands[1].GetProperty("targetImmunity").GetString());
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
