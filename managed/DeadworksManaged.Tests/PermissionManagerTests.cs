using DeadworksManaged.Api;
using DeadworksManaged.Permissions;
using Xunit;

namespace DeadworksManaged.Tests;

public sealed class PermissionManagerTests : IDisposable
{
    private readonly string _tempDir;

    public PermissionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "deadworks-permissions-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Theory]
    [InlineData("*", "moderation.player.ban", true)]
    [InlineData("moderation.player.ban", "moderation.player.ban", true)]
    [InlineData("moderation.player.*", "moderation.player.ban", true)]
    [InlineData("moderation.player.*", "moderation.player.kick", true)]
    [InlineData("moderation.player.*", "moderation.player", false)]
    [InlineData("moderation.player.*", "moderation.playerban", false)]
    [InlineData("moderation.player.mute", "moderation.player.ban", false)]
    public void PermissionMatches_uses_exact_and_segment_wildcards(string granted, string requested, bool expected)
    {
        Assert.Equal(expected, PermissionManager.PermissionMatches(granted, requested));
    }

    [Theory]
    [InlineData("STEAM_0:0:11101", 76561197960287930UL)]
    [InlineData("[U:1:22202]", 76561197960287930UL)]
    [InlineData("76561197960287930", 76561197960287930UL)]
    public void SteamIds_parse_common_formats(string value, ulong expected)
    {
        Assert.True(SteamIds.TryParse(value, out var steamId64));
        Assert.Equal(expected, steamId64);
    }

    [Theory]
    [InlineData("U:1:22202")]
    [InlineData("steamID64:76561197960287930")]
    public void SteamIds_reject_unsupported_formats(string value)
    {
        Assert.False(SteamIds.TryParse(value, out _));
    }

    [Fact]
    public void SteamIds_parse_and_format_equivalent_ids()
    {
        const string steam2 = "STEAM_0:0:11101";
        const string steam3 = "[U:1:22202]";
        const ulong steamId64 = 76561197960287930UL;

        Assert.True(SteamIds.TryParse(steam2, out var parsedSteam2));
        Assert.True(SteamIds.TryParse(steam3, out var parsedSteam3));
        Assert.True(SteamIds.TryParse(steamId64.ToString(), out var parsedSteamId64));

        Assert.Equal(steamId64, parsedSteam2);
        Assert.Equal(steamId64, parsedSteam3);
        Assert.Equal(steamId64, parsedSteamId64);

        Assert.Equal(steam2, SteamIds.ToSteam2(steamId64));
        Assert.Equal(steam3, SteamIds.ToSteam3(steamId64));
        Assert.Equal(steamId64.ToString(), SteamIds.ToPermissionKey(steamId64));
    }

    [Fact]
    public void HasPermission_loads_jsonc_roles_and_players()
    {
        File.WriteAllText(Path.Combine(_tempDir, "roles.jsonc"),
            """
            {
              "admin": {
                "permissions": ["*"]
              },
              "moderator": {
                "permissions": [
                  "moderation.player.*",
                ]
              },
              "vip": {
                "permissions": [
                  "moderation.player.mute",
                ]
              }
            }
            """);

        File.WriteAllText(Path.Combine(_tempDir, "players.jsonc"),
            """
            {
              // Comments and trailing commas are valid in this file.
              "76561198000000001": {
                "roles": ["moderator"],
              },
              "76561198000000002": {
                "roles": ["vip"]
              }
            }
            """);

        PermissionManager.Initialize(_tempDir);

        Assert.True(PermissionManager.HasPermission(76561198000000001, "moderation.player.ban"));
        Assert.True(PermissionManager.HasPermission(76561198000000001, "moderation.player.kick"));
        Assert.False(PermissionManager.HasPermission(76561198000000002, "moderation.player.ban"));
        Assert.True(PermissionManager.HasPermission(76561198000000002, "moderation.player.mute"));
        Assert.False(PermissionManager.HasPermission(76561198000000003, "moderation.player.mute"));
    }

    [Fact]
    public void GetRoles_returns_player_roles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "roles.jsonc"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, "players.jsonc"),
            """
            {
              "76561198000000001": {
                "roles": ["admin", "moderator"]
              }
            }
            """);

        PermissionManager.Initialize(_tempDir);

        Assert.Equal(["admin", "moderator"], PermissionManager.GetRoles(76561198000000001));
        Assert.Empty(PermissionManager.GetRoles(76561198000000002));
    }

    [Theory]
    [InlineData("STEAM_0:0:11101")]
    [InlineData("[U:1:22202]")]
    [InlineData("76561197960287930")]
    public void Player_permissions_accept_common_steam_id_formats(string key)
    {
        File.WriteAllText(Path.Combine(_tempDir, "roles.jsonc"),
            """
            {
              "admin": {
                "permissions": ["*"]
              }
            }
            """);
        File.WriteAllText(Path.Combine(_tempDir, "players.jsonc"),
            $$"""
            {
              "{{key}}": {
                "roles": ["admin"]
              }
            }
            """);

        PermissionManager.Initialize(_tempDir);

        Assert.True(PermissionManager.HasPermission(76561197960287930, "server.rcon"));
        Assert.Equal(["admin"], PermissionManager.GetRoles(76561197960287930));
    }

}
