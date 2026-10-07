using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadworksManaged;

/// <summary>
/// SHA-256 of the content files the server actually loads - the version it advertises in
/// A2S_RULES. Cached by (path, size, last write time) in memory and in configs/content_hashes.json,
/// so a restart does not re-read every map. A stale entry can only ever make clients reject a
/// download, never accept a wrong one.
/// </summary>
internal static class ContentHashes
{
    private sealed class Entry
    {
        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("mtime")]
        public long MtimeTicks { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Lock _lock = new();

    private static string _cachePath = DefaultCachePath();
    private static Dictionary<string, Entry>? _cache;

    /// <summary>
    /// Full lowercase hex SHA-256 of the file and the size that was hashed, or null if it does not
    /// exist or cannot be read. Reads the whole file on a cache miss.
    /// </summary>
    public static (string Sha256, long Size)? Get(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return null;

        lock (_lock)
        {
            var cache = Load();
            var size = info.Length;
            var mtime = info.LastWriteTimeUtc.Ticks;
            if (cache.TryGetValue(fullPath, out var hit) && hit.Size == size && hit.MtimeTicks == mtime)
                return (hit.Sha256, size);

            var timer = Stopwatch.StartNew();
            string hash;
            try
            {
                // The engine keeps mounted VPKs open, so share everything.
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
                hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[ContentAddons] Could not hash {fullPath}: {ex.Message}");
                return null;
            }

            if (timer.ElapsedMilliseconds >= 250)
                Console.WriteLine($"[ContentAddons] Hashed {Path.GetFileName(fullPath)} ({size / (1024 * 1024)} MB) " +
                                  $"in {timer.ElapsedMilliseconds} ms; cached until the file changes.");

            // A file rewritten while it was being read has a hash that describes neither version:
            // report nothing this time, and hash it again next time.
            info.Refresh();
            if (!info.Exists || info.Length != size || info.LastWriteTimeUtc.Ticks != mtime)
                return null;

            cache[fullPath] = new Entry { Size = size, MtimeTicks = mtime, Sha256 = hash };
            Save(cache);
            return (hash, size);
        }
    }

    /// <summary>Points the cache at another file and forgets anything loaded. For tests.</summary>
    internal static void UseCacheFile(string path)
    {
        lock (_lock)
        {
            _cachePath = path;
            _cache = null;
        }
    }

    private static string DefaultCachePath()
    {
        var managedDir = Path.GetDirectoryName(typeof(ContentHashes).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(managedDir, "..", "configs", "content_hashes.json"));
    }

    private static Dictionary<string, Entry> Load()
    {
        if (_cache != null)
            return _cache;

        // Windows paths: the same file can be spelled with different casing.
        _cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_cachePath))
            {
                var stored = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_cachePath));
                foreach (var (path, entry) in stored ?? [])
                    _cache[path] = entry;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.WriteLine($"[ContentAddons] Ignoring unreadable hash cache {_cachePath}: {ex.Message}");
        }
        return _cache;
    }

    private static void Save(Dictionary<string, Entry> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache, JsonOptions));
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[ContentAddons] Could not save hash cache {_cachePath}: {ex.Message}");
        }
    }
}
