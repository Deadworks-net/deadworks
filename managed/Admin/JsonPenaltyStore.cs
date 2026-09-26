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
        lock (_lock)
            _all.Add(penalty);
        Save();
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Penalty penalty, CancellationToken ct)
    {
        lock (_lock)
        {
            var index = _all.FindIndex(p => p.Id == penalty.Id);
            if (index >= 0)
                _all[index] = penalty;
            else
                _all.Add(penalty);
        }
        Save();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Penalty>> LoadHistoryAsync(ulong steamId64, CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<Penalty>>(
                _all.Where(p => p.SteamId64 == steamId64).OrderByDescending(p => p.CreatedUtc).ToList());
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

        List<Penalty> all;
        try
        {
            all = JsonSerializer.Deserialize<PenaltyFile>(File.ReadAllText(_path), Options)?.Penalties ?? [];
        }
        catch (Exception ex)
        {
            lock (_lock)
                _unreadable = true;
            throw new InvalidDataException($"{Path.GetFileName(_path)}: {ex.Message}", ex);
        }

        // History only needs to go back so far; drop entries that ended before then.
        var cutoff = _now().AddDays(-Math.Max(0, _historyDays));
        var kept = all.Where(p => p.IsActiveAt(_now()) || (p.RemovedUtc ?? p.ExpiresUtc ?? DateTime.MaxValue) >= cutoff).ToList();

        lock (_lock)
        {
            _all = kept;
            _unreadable = false;
        }
        if (kept.Count != all.Count)
            Save();
    }

    private void Save()
    {
        string json;
        lock (_lock)
        {
            if (_unreadable)
            {
                Console.WriteLine($"[Penalties] Not saving {Path.GetFileName(_path)}: it couldn't be read, and saving would lose its history. Fix it and run dw_penalties_reload.");
                return;
            }
            json = JsonSerializer.Serialize(new PenaltyFile { Penalties = _all }, Options);
        }
        JsonPermissionStore.AtomicWrite(_path, Header + json + "\n");
    }

    private const string Header =
        """
        // Bans, gags and mutes. Deadworks enforces everything here that hasn't been removed or expired.
        //
        // Add and remove penalties with the Admin plugin (ban, addban, unban, gag, ungag, ...). This file is rewritten
        // whenever that happens, and only this header is kept. If you edit it by hand, run dw_penalties_reload.
        //
        // Lifted and expired penalties stay here as history for penalties.history_days in deadworks.jsonc.

        """;
}
