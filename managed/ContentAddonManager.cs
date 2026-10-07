using DeadworksManaged.Api;

namespace DeadworksManaged;

/// <summary>
/// Owns the content addon set clients are asked to download: the static
/// <c>serverbrowser.content_addons</c> config list plus whatever loaded plugins declare through
/// <see cref="IDeadworksPlugin.ContentAddons"/>. Re-evaluated whenever plugins load or unload,
/// so a plugin can bring its own addons without them being configured ahead of time.
/// </summary>
internal static class ContentAddonManager
{
    private static readonly Lock _lock = new();
    private static readonly HashSet<string> _mounted = new(StringComparer.OrdinalIgnoreCase);

    private static Func<IReadOnlyList<IDeadworksPlugin>> _pluginSource = () => [];
    private static string[] _active = [];
    private static bool _applied;
    private static bool _rulesPublished;
    private static bool _saidSteamIsDown;
    private static long _nextRulesRetry;
    private static (ContentEntry[] Addons, ContentEntry[] Maps)? _pending;
    private static readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private static string? _lastFastDlListing;

    // game/bin/win64. Anchors the maps directory, and is where the addon directory normally lives.
    private static readonly string ExeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();

    /// <summary>The merged addon list currently advertised to clients.</summary>
    public static IReadOnlyList<string> Active
    {
        get { lock (_lock) return _active; }
    }

    public static void Initialize(Func<IReadOnlyList<IDeadworksPlugin>> pluginSource)
    {
        _pluginSource = pluginSource;
        ContentAddons.ResolveActive = () => Active;
        ContentAddons.RequestRefresh = Refresh;

        // Applies the config list on its own; plugins refresh it as they load.
        Update(remount: false);
    }

    /// <summary>Re-reads every plugin's declared addons and applies the merged list if it changed.</summary>
    public static void Refresh() => Update(remount: false);

    /// <summary>Unconditionally re-applies the merged list and re-mounts its VPKs on map load.</summary>
    public static void OnStartupServer() => Update(remount: true);

    /// <summary>
    /// Called every frame. Steam is often not logged on yet when the first map starts, and nothing
    /// else would publish the rules again before the next map change.
    /// </summary>
    public static void Tick()
    {
        if (_rulesPublished || Environment.TickCount64 < _nextRulesRetry)
            return;

        _nextRulesRetry = Environment.TickCount64 + RulesRetryMs;
        lock (_lock)
        {
            // Only the rules: the addon set itself was applied the first time round.
            if (!_rulesPublished && _pending is var (addons, maps))
                PublishRules(addons, maps);
        }
    }

    private const int RulesRetryMs = 5000;

    private static void Update(bool remount)
    {
        lock (_lock)
        {
            var merged = Collect();
            // _rulesPublished keeps us retrying when Steam was not logged on yet: without it an
            // unchanged addon set would early-return forever and the rules would stay empty.
            if (!remount && _rulesPublished && merged.SequenceEqual(_active, StringComparer.OrdinalIgnoreCase))
                return;

            _active = merged;

            var addons = merged.Select(name => HashEntry(name, FindAddonFile(name))).ToArray();
            var maps = ContentManifest.MapNames(Server.MapName, DeadworksConfig.ServerBrowser.ExtraMaps ?? [])
                .Select(name => HashEntry(name, FindMapFile(name)))
                .ToArray();

            // Always advertised, even with no addons - the marker tag is how a browser finds
            // Deadworks servers at all.
            ApplyTags(addons.Length > 0 ? ContentManifest.Digest(addons) : null);
            _pending = (addons, maps);
            PublishRules(addons, maps);

            // Nothing to advertise and nothing ever advertised: no need to tell the engine anything.
            if (merged.Length == 0 && !_applied)
                return;

            _applied = true;
            Server.SetAddons(string.Join(',', merged));
            Console.WriteLine(merged.Length > 0
                ? $"[ContentAddons] Clients will download: {string.Join(", ", merged)}"
                : "[ContentAddons] No content addons registered.");

            if (remount)
                _mounted.Clear();

            foreach (var addon in merged)
                Mount(addon);
        }
    }

    private static string[] Collect()
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var addon in DeadworksConfig.ServerBrowser.ContentAddons)
            Add(merged, seen, addon, "config");

        foreach (var plugin in _pluginSource())
        {
            IReadOnlyList<string>? declared;
            try
            {
                declared = plugin.ContentAddons;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ContentAddons] {plugin.Name}.ContentAddons threw: {ex.Message}");
                continue;
            }

            if (declared == null)
                continue;

            foreach (var addon in declared)
                Add(merged, seen, addon, plugin.Name);
        }

        return [.. merged];
    }

    private static void Add(List<string> merged, HashSet<string> seen, string? addon, string source)
    {
        var name = addon?.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        // The engine takes the addons as one comma-separated string, so a comma would split the entry;
        // a colon would split the name:hash entries A2S_RULES carries. The name also becomes a file
        // path that is opened and hashed, and the hash is published: it must stay inside the addon
        // folder, whatever a plugin or a config asks for.
        if (!ContentManifest.IsListableName(name))
        {
            Console.WriteLine($"[ContentAddons] Ignoring '{name}' from {source}: addon names cannot contain ',', ':', '/' or '\\', or start with '.'.");
            return;
        }

        if (!ContentManifest.IsPortableName(name))
            WarnOnce("name:" + name, $"[ContentAddons] '{name}' from {source} uses characters outside a-z, 0-9 and '_'; " +
                                     "third-party launchers may refuse to install it.");

        if (seen.Add(name))
            merged.Add(name);
    }

    // Tag advertisement.
    //
    // CNetworkGameServer::UpdateGameTags folds the sv_tags value into the server's tag string
    // and hands the result to ISteamGameServer::SetGameTags, which lands in the A2S_INFO
    // keywords field and - more usefully - is indexed by Steam, so clients can filter the
    // server list with "gametagsand" without querying a single server. Writing sv_tags fires
    // that rebuild on its own, so no hook is needed.
    //
    // Two tags are emitted: a fixed marker so a browser can find Deadworks servers, and a
    // digest of the addon set so it can find servers carrying specific content. Names do not
    // fit (see TagBudget), and Steam's filter matches whole entries anyway, so a digest is the
    // right shape here - the human-readable manifest belongs in A2S_RULES.
    private const string TagMarker = "dw1";
    private const string TagDigestPrefix = "dwa";
    // Set by serverbrowser.unlisted. Steam still lists the server and it still answers queries, so
    // players with its address get its content; server browsers leave it out of their lists.
    private const string TagUnlisted = "dwu";

    // The engine truncates its own tags plus sv_tags to 63 chars, warns, and only then calls
    // SetGameTags. It appends "secure"/"insecure" and situationally "hidden", "reserved" and
    // "empty" around our value, so reserve room for the worst case rather than the observed one.
    private const int TagBudget = 31;

    private static void ApplyTags(string? digest)
    {
        var cvar = ConVar.Find("sv_tags");
        if (cvar == null)
        {
            Console.WriteLine("[ContentAddons] sv_tags not found - skipping tag advertisement.");
            return;
        }

        var ours = new List<string> { TagMarker };
        if (digest != null)
            ours.Add(TagDigestPrefix + digest);
        if (DeadworksConfig.ServerBrowser.Unlisted)
            ours.Add(TagUnlisted);

        var current = cvar.GetString();

        // Preserve whatever the operator configured, dropping tags we wrote on a previous pass.
        var operatorTags = current
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !IsOurs(t));

        // Ours go first: the engine truncates from the end, so leading placement keeps discovery
        // working even when the combined string overflows.
        var combined = string.Join(',', ours.Concat(operatorTags));

        if (combined.Length > TagBudget)
            Console.WriteLine($"[ContentAddons] sv_tags is {combined.Length} chars; the engine truncates the full " +
                              $"tag string at 63 and may drop trailing tags (budget here is ~{TagBudget}).");

        if (combined == current)
            return;

        cvar.SetString(combined);
        Console.WriteLine($"[ContentAddons] Advertising tags: {string.Join(", ", ours)}");
    }

    // Publishes the manifest into the Steam gameserver rules store, which Steam serves as
    // A2S_RULES; the format is specified on ContentManifest. The tag digest says *whether* a
    // server's content matches; this says what that content is, without the tags' 63-char ceiling.
    private static void PublishRules(ContentEntry[] addons, ContentEntry[] maps)
    {
        if (!ServerRules.Clear())
        {
            // Expected before Steam logs the server on; Tick() tries again until it is.
            _rulesPublished = false;
            if (!_saidSteamIsDown)
                Console.WriteLine("[ContentAddons] Steam gameserver not up - rules not published yet.");
            _saidSteamIsDown = true;
            return;
        }

        _saidSteamIsDown = false;

        var configured = DeadworksConfig.ServerBrowser.FastDlUrl;
        if (!ContentManifest.TryNormalizeFastDlUrl(configured, out var fastDl, out var error))
            WarnOnce("fastdl:" + configured, $"[ContentAddons] serverbrowser.fastdl_url {error}; not advertised.");

        var payload = ContentManifest.BuildRules(addons, maps, fastDl);
        foreach (var (key, value) in payload.Rules)
            ServerRules.Set(key, value);
        _rulesPublished = true;

        if (payload.AddonsListed < addons.Length || payload.MapsListed < maps.Length)
            Console.WriteLine($"[ContentAddons] A2S_RULES lists {payload.AddonsListed} of {addons.Length} addons and " +
                              $"{payload.MapsListed} of {maps.Length} maps; the rest do not fit under its size cap.");

        if (fastDl != null)
            LogFastDlFiles(fastDl, addons, maps);

        Console.WriteLine($"[ContentAddons] Published {addons.Length} addon(s) and {maps.Length} map(s) to A2S_RULES.");
    }

    // The "dw" tag namespace belongs to the framework; an operator tag starting with it is
    // treated as ours and replaced.
    private static bool IsOurs(string tag) => tag.StartsWith("dw", StringComparison.OrdinalIgnoreCase);

    // A piece of content's version is its bytes: hash the file the server itself loads, so what
    // gets advertised can never drift from what is actually running.
    private static ContentEntry HashEntry(string name, string? path)
    {
        return (path != null ? ContentHashes.Get(path) : null) is { } file
            ? new ContentEntry(name, ContentManifest.ShortHash(file.Sha256), file.Size)
            : new ContentEntry(name, null);
    }

    // Mount() hands the engine this path relative to the working directory, so resolve it the same
    // way, then fall back to the executable's directory in case the server was started elsewhere.
    private static string? FindAddonFile(string name)
    {
        var relative = Path.Combine("deadworks_mods", "vpks", name + ".vpk");
        foreach (var root in new[] { Directory.GetCurrentDirectory(), ExeDir })
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (File.Exists(full))
                return full;
        }

        WarnOnce("addon:" + name, $"[ContentAddons] '{name}' has no file at {relative}; it is advertised without a " +
                                  "version hash, so clients cannot verify it or fetch it over fastDL.");
        return null;
    }

    // Custom maps sit loose in citadel/maps. A map the engine loads from anywhere else - a pak, an
    // addon directory - goes out without a hash.
    private static string? FindMapFile(string name)
    {
        var full = Path.GetFullPath(Path.Combine(ExeDir, "..", "..", "citadel", "maps", name + ".vpk"));
        if (File.Exists(full))
            return full;

        WarnOnce("map:" + name, $"[ContentAddons] Map '{name}' has no loose file at citadel/maps/{name}.vpk; " +
                                "it is advertised without a version hash.");
        return null;
    }

    // Tells the operator exactly which files their fastDL host has to serve. Only logged when the
    // list changes, since it would otherwise repeat on every map change.
    private static void LogFastDlFiles(string baseUrl, ContentEntry[] addons, ContentEntry[] maps)
    {
        var lines = new List<string>();
        foreach (var (dir, entries) in new[] { (ContentManifest.AddonsDir, addons), (ContentManifest.MapsDir, maps) })
        {
            foreach (var entry in entries)
            {
                lines.Add(ContentManifest.FastDlPath(dir, entry) is { } path
                    ? $"  {path}  (bzip2 of {entry.Name}.vpk)"
                    : $"  {entry.Name}: no hash, so it cannot be fetched from fastDL");
            }
        }

        var listing = string.Join('|', lines);
        if (listing == _lastFastDlListing)
            return;
        _lastFastDlListing = listing;

        Console.WriteLine($"[ContentAddons] fastDL: clients will fetch these from {baseUrl}/ " +
                          "(stock maps can be skipped - players already have them):");
        foreach (var line in lines)
            Console.WriteLine(line);
    }

    private static void WarnOnce(string key, string message)
    {
        if (_warned.Add(key))
            Console.WriteLine(message);
    }

    private static void Mount(string addon)
    {
        if (!_mounted.Add(addon))
            return;

        var vpkPath = $"deadworks_mods/vpks/{addon}.vpk";
        if (Server.AddSearchPath(vpkPath))
            Console.WriteLine($"[ContentAddons] Mounted: {vpkPath}");
        else
            Console.WriteLine($"[ContentAddons] Failed to mount: {vpkPath}");
    }
}
