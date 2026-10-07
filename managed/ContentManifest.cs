using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DeadworksManaged;

/// <summary>
/// One piece of advertised content: its name, plus the first <see cref="ContentManifest.HashLength"/>
/// hex digits of the SHA-256 of the file the server itself loads and that file's size in bytes.
/// Both are null when the server could not find the file, in which case clients have nothing to
/// verify a download against.
/// </summary>
internal readonly record struct ContentEntry(string Name, string? Hash, long? Size = null)
{
    public override string ToString() => (Hash, Size) switch
    {
        (null, _) => Name,
        (_, null) => $"{Name}:{Hash}",
        (_, { } size) => $"{Name}:{Hash}:{size.ToString(CultureInfo.InvariantCulture)}",
    };
}

/// <summary>A built A2S_RULES payload, plus how many entries of each list fit under its size cap.</summary>
internal sealed record RulesPayload(IReadOnlyList<KeyValuePair<string, string>> Rules, int AddonsListed, int MapsListed);

/// <summary>
/// The well-known A2S_RULES format a Deadworks server describes its content in, so launchers and
/// other clients can discover it from the server itself over the game port, with no intermediary.
/// <code>
/// dw_ver        1
/// dw_addons     name[:hash:size],...  content addons, in mount order
/// dw_addons_n   total number of addons (dw_addons may list fewer - see below)
/// dw_maps       name[:hash:size],...  the current map first, then serverbrowser.extra_maps
/// dw_maps_n     total number of maps
/// dw_digest     8 hex digits identifying the addon set, the same value as the dwa&lt;digest&gt; tag
/// dw_fastdl     optional base URL the content can be downloaded from
/// </code>
/// <para>
/// <b>hash</b> is the first 16 lowercase hex digits of the SHA-256 of the uncompressed .vpk. It is
/// the content's version: a new build of an addon is a new hash, with no counter to keep in sync.
/// <b>size</b> is that file's length in bytes - uncompressed, because the server never sees the
/// compressed copy on the fastDL host. It lets a client weight progress before downloading, and
/// reject a download that decompresses to any other length. An entry with neither could not be
/// read by the server and cannot be verified.
/// </para>
/// <para>
/// The whole reply has to fit one datagram: Steam sends nothing at all for a larger one, with no
/// split packets and no partial reply (a 1215-byte reply arrived, a 1324-byte one never did), so a
/// server that advertised too much would look like one that advertises nothing. The lists are
/// capped for that reason. Entries are dropped whole, from the end; when a list names fewer
/// entries than its _n key, a client knows it has not been told everything.
/// </para>
/// <para>
/// <b>dw_digest</b> is the first 8 hex digits of the SHA-256 of the addon entries as listed -
/// lowercased, sorted ordinally, joined with ',' - and is empty when there are no addons. It
/// covers content, not just names, so two servers share a digest only when they run identical
/// addon files.
/// </para>
/// <para>
/// <b>dw_fastdl</b> works like Source 1's sv_downloadurl. Clients fetch
/// {dw_fastdl}/addons/{name}_{hash}.vpk.bz2 and {dw_fastdl}/maps/{name}_{hash}.vpk.bz2, a bzip2
/// of the .vpk, and must check the hash after decompressing. The hash says which build, not whom
/// to trust: one operator picks both it and the host, so a mismatch means the host is out of date,
/// and a client may offer to install that copy anyway. Without dw_fastdl, content comes from the
/// Deadworks-hosted service.
/// </para>
/// <para>
/// Clients must refuse names outside a-z, 0-9 and '_' rather than use them in a file path, and
/// must not depend on the order Steam returns the keys in.
/// </para>
/// </summary>
internal static class ContentManifest
{
    public const string Version = "1";
    public const int HashLength = 16;
    public const string AddonsDir = "addons";
    public const string MapsDir = "maps";

    // Caps on the variable-length values, in characters. With the fixed keys and the longest
    // dw_fastdl they keep the largest possible reply under MaxReplyBytes.
    public const int AddonListBudget = 640;
    public const int MapListBudget = 300;
    // The most a whole A2S_RULES reply may take and still arrive; see the note on datagrams above.
    public const int MaxReplyBytes = 1200;
    public const int MaxFastDlUrlLength = 128;

    public static RulesPayload BuildRules(IReadOnlyList<ContentEntry> addons, IReadOnlyList<ContentEntry> maps, string? fastDlUrl)
    {
        var rules = new List<KeyValuePair<string, string>>
        {
            new("dw_ver", Version),
            new("dw_addons", JoinWithinBudget(addons, AddonListBudget, out var addonsListed)),
            new("dw_addons_n", addons.Count.ToString(CultureInfo.InvariantCulture)),
            new("dw_maps", JoinWithinBudget(maps, MapListBudget, out var mapsListed)),
            new("dw_maps_n", maps.Count.ToString(CultureInfo.InvariantCulture)),
            new("dw_digest", addons.Count > 0 ? Digest(addons) : ""),
        };

        if (!string.IsNullOrEmpty(fastDlUrl))
            rules.Add(new("dw_fastdl", fastDlUrl));

        return new RulesPayload(rules, addonsListed, mapsListed);
    }

    /// <summary>
    /// Identifies an addon set by its content. Order-independent and case-insensitive, and stable
    /// across processes - deliberately not string.GetHashCode, which is salted per process.
    /// </summary>
    public static string Digest(IEnumerable<ContentEntry> addons)
    {
        var canonical = string.Join(',', addons.Select(e => e.ToString().ToLowerInvariant()).Order(StringComparer.Ordinal));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical), hash);
        return Convert.ToHexStringLower(hash[..4]);
    }

    /// <summary>The advertised form of a full SHA-256 hex string.</summary>
    public static string ShortHash(string sha256Hex) => sha256Hex[..HashLength].ToLowerInvariant();

    /// <summary>The current map first, then the extra maps, without blanks or repeats.</summary>
    public static List<string> MapNames(string? currentMap, IEnumerable<string?> extraMaps)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in extraMaps.Prepend(currentMap))
        {
            var name = raw?.Trim();
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
                names.Add(name);
        }
        return names;
    }

    /// <summary>Where a client fetches an entry, relative to dw_fastdl. Null for an entry with no hash.</summary>
    public static string? FastDlPath(string dir, ContentEntry entry)
        => entry.Hash is null ? null : $"{dir}/{entry.Name}_{entry.Hash}.vpk.bz2";

    /// <summary>Whether every client can safely use this name as a file name.</summary>
    public static bool IsPortableName(string name)
        => name.Length > 0 && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    /// <summary>
    /// Validates serverbrowser.fastdl_url. An empty value is valid and means "not configured"
    /// (<paramref name="url"/> comes back null).
    /// </summary>
    public static bool TryNormalizeFastDlUrl(string? raw, out string? url, out string? error)
    {
        url = null;
        error = null;

        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
            return true;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "must be an absolute http:// or https:// URL";
            return false;
        }

        if (uri.UserInfo.Length > 0)
        {
            error = "must not contain credentials - it is sent to anyone who asks";
            return false;
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "must not have a query string or fragment, because clients append paths to it";
            return false;
        }

        value = value.TrimEnd('/');
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > MaxFastDlUrlLength)
        {
            error = $"is {bytes} bytes; the limit is {MaxFastDlUrlLength}, because it goes out in every A2S_RULES reply";
            return false;
        }

        url = value;
        return true;
    }

    private static string JoinWithinBudget(IReadOnlyList<ContentEntry> entries, int budget, out int listed)
    {
        // Budgets are bytes on the wire, and a name is not always ASCII.
        var sb = new StringBuilder();
        var used = 0;
        listed = 0;
        foreach (var entry in entries)
        {
            var text = entry.ToString();
            var cost = Encoding.UTF8.GetByteCount(text) + (sb.Length == 0 ? 0 : 1);
            if (used + cost > budget)
                break;
            used += cost;

            if (sb.Length > 0)
                sb.Append(',');
            sb.Append(text);
            listed++;
        }
        return sb.ToString();
    }
}
