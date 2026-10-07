using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DeadworksManaged.Tests;

public class ContentManifestTests
{
    private static readonly string HashA = new('a', ContentManifest.HashLength);
    private static readonly string HashB = new('b', ContentManifest.HashLength);

    private static string Rule(RulesPayload payload, string key) => payload.Rules.Single(r => r.Key == key).Value;

    [Fact]
    public void Entry_IsNameHashAndSize_OrABareNameWithoutThem()
    {
        Assert.Equal($"turbo:{HashA}:11965", new ContentEntry("turbo", HashA, 11965).ToString());
        Assert.Equal($"turbo:{HashA}", new ContentEntry("turbo", HashA).ToString());
        Assert.Equal("ware", new ContentEntry("ware", null).ToString());
    }

    [Fact]
    public void Digest_ChangesWhenAnAddonsContentChanges()
    {
        // The bug the per-entry hash exists to fix: same names, different build.
        var v3 = new[] { new ContentEntry("turbo", HashA) };
        var v4 = new[] { new ContentEntry("turbo", HashB) };
        Assert.NotEqual(ContentManifest.Digest(v3), ContentManifest.Digest(v4));
    }

    [Fact]
    public void Digest_IgnoresOrderAndNameCase()
    {
        var a = new[] { new ContentEntry("turbo", HashA), new ContentEntry("ware", HashB) };
        var b = new[] { new ContentEntry("WARE", HashB), new ContentEntry("Turbo", HashA) };
        Assert.Equal(ContentManifest.Digest(a), ContentManifest.Digest(b));
    }

    [Fact]
    public void Digest_MatchesTheDocumentedExample()
    {
        // Third-party clients reimplement this from the Content Discovery page, so pin its
        // worked example exactly: canonical "turbo:9330149c74886724:11965,ware".
        var entries = new[] { new ContentEntry("ware", null), new ContentEntry("turbo", "9330149c74886724", 11965) };
        Assert.Equal("cce084bf", ContentManifest.Digest(entries));

        // ...and the digest in its example rules reply.
        Assert.Equal("8a1b839d", ContentManifest.Digest([new ContentEntry("turbo", "9330149c74886724", 11965)]));
    }

    [Fact]
    public void BuildRules_AlwaysPublishesTheCoreKeys()
    {
        var payload = ContentManifest.BuildRules([], [], null);
        Assert.Equal(
            new[] { "dw_ver", "dw_addons", "dw_addons_n", "dw_maps", "dw_maps_n", "dw_digest" },
            payload.Rules.Select(r => r.Key));
        Assert.Equal("1", Rule(payload, "dw_ver"));
        Assert.Equal("0", Rule(payload, "dw_addons_n"));
        Assert.Equal("", Rule(payload, "dw_digest"));
    }

    [Fact]
    public void BuildRules_AdvertisesFastDlOnlyWhenConfigured()
    {
        Assert.DoesNotContain(ContentManifest.BuildRules([], [], null).Rules, r => r.Key == "dw_fastdl");
        Assert.Equal("https://dl.example.com/dw",
            Rule(ContentManifest.BuildRules([], [], "https://dl.example.com/dw"), "dw_fastdl"));
    }

    // What Steam puts on the wire for a rules reply: a 7-byte header, then each key and value
    // with its terminator.
    private static int ReplyBytes(RulesPayload payload)
        => 7 + payload.Rules.Sum(r => System.Text.Encoding.UTF8.GetByteCount(r.Key) + 1 + System.Text.Encoding.UTF8.GetByteCount(r.Value) + 1);

    [Fact]
    public void BuildRules_TheLargestPossibleReplyStillFitsOneDatagram()
    {
        // Far more content than fits, the longest names a launcher accepts, and the longest URL.
        var addons = Enumerable.Range(0, 200).Select(i => new ContentEntry(new string('a', 61) + $"{i:D3}", HashA, 4_000_000_000 + i)).ToArray();
        var maps = Enumerable.Range(0, 200).Select(i => new ContentEntry(new string('m', 61) + $"{i:D3}", HashA, 4_000_000_000 + i)).ToArray();
        var url = "https://" + new string('h', ContentManifest.MaxFastDlUrlLength - 8);

        var payload = ContentManifest.BuildRules(addons, maps, url);

        Assert.InRange(ReplyBytes(payload), 1, ContentManifest.MaxReplyBytes);
        Assert.True(payload.AddonsListed > 0 && payload.MapsListed > 0);
    }

    [Theory]
    [InlineData("a", 1)]        // short entries pack the budget tightest
    [InlineData("a", 7)]
    [InlineData("é", 5)]   // two bytes a character on the wire
    [InlineData("中", 9)]   // three
    public void BuildRules_FitsOneDatagram_WhateverTheNamesAreMadeOf(string unit, int length)
    {
        var name = string.Concat(Enumerable.Repeat(unit, length));
        var addons = Enumerable.Range(0, 400).Select(i => new ContentEntry(name + i, HashA, 4_000_000_000 + i)).ToArray();
        var maps = Enumerable.Range(0, 400).Select(i => new ContentEntry(name + i, HashA, 4_000_000_000 + i)).ToArray();
        var url = "https://" + new string('h', ContentManifest.MaxFastDlUrlLength - 8);

        var payload = ContentManifest.BuildRules(addons, maps, url);

        Assert.InRange(ReplyBytes(payload), 1, ContentManifest.MaxReplyBytes);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(Rule(payload, "dw_addons")), 1, ContentManifest.AddonListBudget);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(Rule(payload, "dw_maps")), 1, ContentManifest.MapListBudget);
    }

    [Fact]
    public void BuildRules_DropsWholeEntriesPastTheCap_AndKeepsTheTrueCount()
    {
        var addons = Enumerable.Range(0, 40).Select(i => new ContentEntry($"addon_number_{i:D2}", HashA)).ToArray();
        var payload = ContentManifest.BuildRules(addons, [], null);

        var listed = Rule(payload, "dw_addons");
        Assert.True(listed.Length <= ContentManifest.AddonListBudget);
        Assert.InRange(payload.AddonsListed, 1, addons.Length - 1);
        Assert.Equal("40", Rule(payload, "dw_addons_n"));

        // What is listed is an intact prefix, in the server's order.
        Assert.Equal(addons.Take(payload.AddonsListed).Select(e => e.ToString()), listed.Split(','));
    }

    [Fact]
    public void MapNames_PutsTheCurrentMapFirst_WithoutBlanksOrRepeats()
    {
        Assert.Equal(new[] { "ware_arena", "dl_express", "bhop" },
            ContentManifest.MapNames("ware_arena", ["dl_express", "WARE_ARENA", " ", null, "bhop", "dl_express"]));
        Assert.Equal(new[] { "dl_express" }, ContentManifest.MapNames("", ["dl_express"]));
    }

    [Fact]
    public void MapNames_LeavesOutNamesThatWouldBreakTheListOrLeaveTheFolder()
    {
        Assert.Equal(new[] { "dl_express" },
            ContentManifest.MapNames("dl_express", ["evil,injected:0000000000000000", "a:b", @"..\..\secret", "sub/map", ".hidden"]));
        Assert.True(ContentManifest.IsListableName("My-Addon 2"));
        foreach (var bad in new[] { "", "a,b", "a:b", "..", @"..\x", "x/y", @"\\host\share\x", ".x" })
            Assert.False(ContentManifest.IsListableName(bad), bad);
    }

    [Fact]
    public void FastDlPath_EmbedsTheHash_AndIsNullWithoutOne()
    {
        Assert.Equal($"addons/turbo_{HashA}.vpk.bz2", ContentManifest.FastDlPath(ContentManifest.AddonsDir, new("turbo", HashA)));
        Assert.Null(ContentManifest.FastDlPath(ContentManifest.MapsDir, new("dl_express", null)));
    }

    [Theory]
    [InlineData("https://dl.example.com/fastdl/", "https://dl.example.com/fastdl")]
    [InlineData("  http://10.0.0.5:8080  ", "http://10.0.0.5:8080")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FastDlUrl_AcceptsHttpAndHttps_AndTrims(string? raw, string? expected)
    {
        Assert.True(ContentManifest.TryNormalizeFastDlUrl(raw, out var url, out var error));
        Assert.Equal(expected, url);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("ftp://dl.example.com/fastdl")]
    [InlineData("dl.example.com/fastdl")]
    [InlineData("https://dl.example.com/fastdl?token=abc")]
    [InlineData("https://dl.example.com/fastdl#top")]
    [InlineData("https://user:secret@dl.example.com/fastdl")]
    public void FastDlUrl_RejectsWhatClientsCannotAppendTo(string raw)
    {
        Assert.False(ContentManifest.TryNormalizeFastDlUrl(raw, out var url, out var error));
        Assert.Null(url);
        Assert.NotNull(error);
    }

    [Fact]
    public void FastDlUrl_RejectsOverlongUrls()
        => Assert.False(ContentManifest.TryNormalizeFastDlUrl("https://dl.example.com/" + new string('x', 200), out _, out _));

    [Theory]
    [InlineData("turbo_2", true)]
    [InlineData("Turbo", false)]
    [InlineData("dl_express_vdata.", false)]
    [InlineData("../evil", false)]
    [InlineData("", false)]
    public void IsPortableName_AllowsOnlyLowercaseDigitsAndUnderscore(string name, bool expected)
        => Assert.Equal(expected, ContentManifest.IsPortableName(name));
}

public sealed class ContentHashesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dw-hash-").FullName;

    private string CacheFile => Path.Combine(_dir, "content_hashes.json");

    public ContentHashesTests() => ContentHashes.UseCacheFile(CacheFile);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public void HashesTheFileBytes_AndRehashesAfterTheFileChanges()
    {
        var file = Path.Combine(_dir, "turbo.vpk");
        File.WriteAllBytes(file, [1, 2, 3]);
        Assert.Equal((Sha([1, 2, 3]), 3L), ContentHashes.Get(file));

        File.WriteAllBytes(file, [4, 5, 6, 7]);
        Assert.Equal((Sha([4, 5, 6, 7]), 4L), ContentHashes.Get(file));
    }

    [Fact]
    public void MissingFile_HasNoHash() => Assert.Null(ContentHashes.Get(Path.Combine(_dir, "missing.vpk")));

    [Fact]
    public void PersistedCache_IsReusedAfterARestart()
    {
        var file = Path.Combine(_dir, "map.vpk");
        File.WriteAllBytes(file, [9, 9, 9]);
        ContentHashes.Get(file);
        Assert.True(File.Exists(CacheFile));

        // Plant a different hash for the same size and write time, then "restart". If the cache is
        // honored the planted value comes back, proving the file was not read again.
        var info = new FileInfo(file);
        var planted = new string('f', 64);
        File.WriteAllText(CacheFile, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [file] = new { size = info.Length, mtime = info.LastWriteTimeUtc.Ticks, sha256 = planted },
        }));
        ContentHashes.UseCacheFile(CacheFile);

        Assert.Equal((planted, 3L), ContentHashes.Get(file));
    }
}
