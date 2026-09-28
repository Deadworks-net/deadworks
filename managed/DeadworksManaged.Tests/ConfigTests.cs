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
