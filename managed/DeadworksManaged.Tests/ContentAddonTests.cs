using Xunit;

namespace DeadworksManaged.Tests;

/// <summary>
/// Covers the parts of <c>dw_addons</c> that do not need the engine: addon name validation and the
/// <c>deadworks.jsonc</c> round trip that <c>add</c>, <c>remove</c>, and <c>reload</c> rely on.
/// The engine-facing apply step (SetAddons / AddSearchPath) is exercised on a live server.
/// </summary>
public class ContentAddonTests
{
    [Theory]
    [InlineData("mymap_assets")]
    [InlineData("Hud-Pack.v2")]
    [InlineData("  padded  ", "padded")]
    public void AcceptsFileNameSafeNames(string input, string? expected = null)
    {
        Assert.True(ContentAddonManager.TryValidateName(input, out var name, out var error), error);
        Assert.Equal(expected ?? input, name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a,b")]
    [InlineData("has space")]
    [InlineData("sub/dir")]
    [InlineData("back\\slash")]
    [InlineData("drive:c")]
    [InlineData("glob*")]
    [InlineData("quote\"d")]
    [InlineData("ctrl\u0001")]
    public void RejectsNamesTheEngineOrLauncherCannotUse(string input)
    {
        Assert.False(ContentAddonManager.TryValidateName(input, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void RejectsVpkSuffixAndSuggestsTheStem()
    {
        Assert.False(ContentAddonManager.TryValidateName("mymap.VPK", out _, out var error));
        Assert.Contains("'mymap'", error);
    }

    [Fact]
    public void RejectsOverlongNames()
    {
        Assert.False(ContentAddonManager.TryValidateName(new string('a', 129), out _, out _));
        Assert.True(ContentAddonManager.TryValidateName(new string('a', 128), out _, out _));
    }

    [Fact]
    public void ReloadPicksUpExternalEditsAndKeepsPreviousOnParseError()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "deadworks.jsonc");
        File.WriteAllText(path, "// comment\n{ \"serverbrowser\": { \"content_addons\": [\"one\"] } }\n");

        DeadworksConfig.InitializeAt(path);
        Assert.Equal(["one"], DeadworksConfig.ServerBrowser.ContentAddons);

        // The hosting portal writes the file directly, then asks the server to reload it.
        File.WriteAllText(path, "{ \"serverbrowser\": { \"content_addons\": [\"one\", \"two\"], \"extra_maps\": [\"dl_custom\"] } }\n");
        Assert.True(DeadworksConfig.Reload());
        Assert.Equal(["one", "two"], DeadworksConfig.ServerBrowser.ContentAddons);
        Assert.Equal(["dl_custom"], DeadworksConfig.ServerBrowser.ExtraMaps);

        File.WriteAllText(path, "{ not json");
        Assert.False(DeadworksConfig.Reload());
        Assert.Equal(["one", "two"], DeadworksConfig.ServerBrowser.ContentAddons);
    }

    [Fact]
    public void SaveRoundTripsThroughTheCommentSkippingReader()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "deadworks.jsonc");
        DeadworksConfig.InitializeAt(path);
        Assert.True(File.Exists(path)); // a default file is created on first load

        DeadworksConfig.ServerBrowser.ContentAddons.Add("first");
        DeadworksConfig.ServerBrowser.Unlisted = true;
        Assert.True(DeadworksConfig.Save());

        var text = File.ReadAllText(path);
        Assert.StartsWith("// Deadworks configuration", text);

        Assert.True(DeadworksConfig.Reload());
        Assert.Equal(["first"], DeadworksConfig.ServerBrowser.ContentAddons);
        Assert.True(DeadworksConfig.ServerBrowser.Unlisted);
    }

    [Fact]
    public void CreatesADefaultFileWhenNoneExists()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "missing", "deadworks.jsonc");
        DeadworksConfig.InitializeAt(path);
        Assert.True(File.Exists(path));
        Assert.Empty(DeadworksConfig.ServerBrowser.ContentAddons);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dw-addons-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
