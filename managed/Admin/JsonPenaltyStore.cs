using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadworksManaged.Api;
using DeadworksManaged.PermissionSystem;

namespace DeadworksManaged.AdminSystem;

/// <summary>The built-in penalty store: <c>configs/penalties/penalties.jsonc</c>, active penalties and history together.</summary>
internal sealed class JsonPenaltyStore : IPenaltyStore
{
    public const string StoreName = "json";

    private sealed class PenaltyFile
    {
        public List<Penalty> Penalties { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly int _historyDays;
    private readonly Func<DateTime> _now;
    private readonly Lock _lock = new();
    private List<Penalty> _all = [];
    // Set when the file couldn't be read, so a save doesn't replace history we failed to load.
    private bool _unreadable;

    public JsonPenaltyStore(string path, int historyDays, Func<DateTime> now)
    {
        _path = path;
        _historyDays = historyDays;
        _now = now;
    }

    public event Action? Changed { add { } remove { } }

    public Task<IReadOnlyList<Penalty>> LoadActiveAsync(CancellationToken ct)
    {
        Load();
        var now = _now();
        lock (_lock)
            return Task.FromResult<IReadOnlyList<Penalty>>(_all.Where(p => p.IsActiveAt(now)).ToList());
    }

    public Task AddAsync(Penalty penalty, CancellationToken ct)
    {
        Change(all => all.Add(penalty));
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Penalty penalty, CancellationToken ct)
    {
        Change(all =>
        {
            var index = all.FindIndex(p => p.Id == penalty.Id);
            if (index >= 0)
                all[index] = penalty;
            else
                all.Add(penalty);
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies one change to the file as it is now, not as it was at the last reload, so entries edited, added or
    /// deleted by hand in the meantime are kept as written. They take effect on dw_penalties_reload.
    /// </summary>
    private void Change(Action<List<Penalty>> apply)
    {
        lock (_lock)
        {
            List<Penalty> current;
            try
            {
                current = ReadFile();
            }
            catch (InvalidDataException ex)
            {
                _unreadable = true;
                throw new InvalidDataException($"Not saved, so the hand edits aren't overwritten. {ex.Message}", ex);
            }
            apply(current);
            _all = current;
            _unreadable = false;
            WriteLocked();
        }
    }

    /// <summary>
    /// Null if the file can be read and so written, otherwise why not. Checked before a penalty is added or lifted, so
    /// a file broken by hand since the last reload refuses the change instead of silently losing it.
    /// </summary>
    internal string? CheckReadable()
    {
        lock (_lock)
        {
            try
            {
                ReadFile();
                return null;
            }
            catch (InvalidDataException ex)
            {
                _unreadable = true;
                return ex.Message;
            }
        }
    }

    public Task<IReadOnlyList<Penalty>> LoadHistoryAsync(ulong steamId64, CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<Penalty>>(
                _all.Where(p => p.SteamId64 == steamId64).OrderByDescending(p => p.CreatedUtc).ToList());
    }

    /// <summary>True when the file couldn't be read; saving then would lose its history, so nothing is saved.</summary>
    internal bool Unreadable
    {
        get { lock (_lock) return _unreadable; }
    }

    private void Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!File.Exists(_path))
        {
            lock (_lock)
            {
                _all = [];
                _unreadable = false;
            }
            Save();
            return;
        }

        List<Penalty> kept;
        int total;
        lock (_lock)
        {
            try
            {
                var all = Parse();
                total = all.Count;
                kept = Trim(all);
            }
            catch (InvalidDataException)
            {
                _unreadable = true;
                throw;
            }
            _all = kept;
            _unreadable = false;
        }
        if (kept.Count != total)
            Save();
    }

    /// <summary>The file's penalties, minus history older than penalties.history_days; empty if there's no file.</summary>
    private List<Penalty> ReadFile() => File.Exists(_path) ? Trim(Parse()) : [];

    private List<Penalty> Parse()
    {
        try
        {
            return JsonSerializer.Deserialize<PenaltyFile>(File.ReadAllText(_path), Options)?.Penalties ?? [];
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException($"{Path.GetFileName(_path)}: {ex.Message}", ex);
        }
    }

    // History only needs to go back so far; drop entries that ended before then.
    private List<Penalty> Trim(List<Penalty> all)
    {
        var now = _now();
        var cutoff = now.AddDays(-Math.Max(0, _historyDays));
        return all.Where(p => p.IsActiveAt(now) || (p.RemovedUtc ?? p.ExpiresUtc ?? DateTime.MaxValue) >= cutoff).ToList();
    }

    private void Save()
    {
        lock (_lock)
        {
            if (_unreadable)
            {
                Console.WriteLine($"[Penalties] Not saving {Path.GetFileName(_path)}: it couldn't be read, and saving would lose its history. Fix it and run dw_penalties_reload.");
                return;
            }
            WriteLocked();
        }
    }

    private void WriteLocked()
        => JsonPermissionStore.AtomicWrite(_path, Header + JsonSerializer.Serialize(new PenaltyFile { Penalties = _all }, Options) + "\n");

    private const string Header =
        """
        // Bans, gags and mutes. Deadworks enforces everything here that hasn't been removed or expired.
        //
        // Add and remove penalties with the Admin plugin (ban, unban, gag, ungag, ...). This file is rewritten
        // whenever that happens: your entries are kept, but not your comments (only this header is). If you edit it
        // by hand, run dw_penalties_reload for the changes to take effect. While it has an error, penalties can't be
        // added or lifted, so nothing you wrote is overwritten.
        //
        // Lifted and expired penalties stay here as history for penalties.history_days in deadworks.jsonc.

        """;
}
