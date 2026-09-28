using Xunit;

namespace DeadworksManaged.Tests;

public sealed class DeadworksConfigTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dw-config-{Guid.NewGuid():N}.jsonc");

    public void Dispose()
    {
        File.Delete(_path);
        DeadworksConfig.LoadForTests(null);
    }

    [Fact]
    public void Trailing_commas_are_fine()
    {
        File.WriteAllText(_path, """
            // Deadworks configuration
            {
              "permissions": { "store": "mysql", },
              "serverbrowser": { "unlisted": true, },
            }
            """);
        DeadworksConfig.LoadForTests(_path);

        Assert.False(DeadworksConfig.Broken);
        Assert.Equal("mysql", DeadworksConfig.Permissions.Store);
        Assert.True(DeadworksConfig.ServerBrowser.Unlisted);
    }

    [Fact]
    public void A_broken_file_fails_closed_instead_of_falling_back_to_defaults()
    {
        // A missing quote: with defaults, a server using a database store would silently switch to the JSON files and
        // stop enforcing its bans, and an unlisted server would be listed.
        File.WriteAllText(_path, """{ "penalties": { "store: "mysql" }, "serverbrowser": { "unlisted": false } }""");
        DeadworksConfig.LoadForTests(_path);

        Assert.True(DeadworksConfig.Broken);
        Assert.Equal(DeadworksConfig.BrokenStoreName, DeadworksConfig.Permissions.Store);
        Assert.Equal(DeadworksConfig.BrokenStoreName, DeadworksConfig.Penalties.Store);
        Assert.True(DeadworksConfig.ServerBrowser.Unlisted);
        Assert.True(DeadworksConfig.Permissions.RequireSteamAuth);
    }
}

public sealed class UnknownJsonKeyTests
{
    [Fact]
    public void Typos_in_roles_are_reported_with_a_suggestion()
    {
        var found = UnknownJsonKeys.Find("""
            // comment
            {
              "moderator": { "permissions": ["a.b"], "immunty": 50, "inherit": ["vip"], },
              "vip": { "permissions": [] }
            }
            """, typeof(Dictionary<string, DeadworksManaged.Api.RoleDefinition>));
        Assert.Equal(["in 'moderator', 'immunty' (did you mean 'immunity'?)", "in 'moderator', 'inherit' (did you mean 'inherits'?)"], found);
    }

    [Fact]
    public void Known_keys_in_any_case_and_nested_sections_are_fine()
    {
        Assert.Empty(UnknownJsonKeys.Find("""{ "Permissions": { "store": "json", "require_steam_auth": true }, "serverbrowser": { "unlisted": true } }""",
            typeof(DeadworksConfigRoot)));
        Assert.Equal(["in 'admin', 'show_activty' (did you mean 'show_activity'?)"],
            UnknownJsonKeys.Find("""{ "admin": { "show_activty": {} } }""", typeof(DeadworksConfigRoot)));
    }

    [Fact]
    public void Unrelated_keys_get_no_suggestion()
        => Assert.Equal(["'banana'"], UnknownJsonKeys.Find("""{ "banana": 1 }""", typeof(DeadworksConfigRoot)));
}
