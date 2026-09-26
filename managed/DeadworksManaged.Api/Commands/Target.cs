using System.Collections;

namespace DeadworksManaged.Api;

/// <summary>
/// Players picked by a <see cref="CommandAttribute"/> argument. Accepts <c>@me</c>, <c>@all</c>, <c>@team</c>,
/// <c>@enemy</c>, <c>#slot</c>, a SteamID (64, Steam2 or Steam3), or part of a player's name.
/// Players the caller can't target are left out according to <see cref="CommandAttribute.TargetImmunity"/>.
/// Never empty: a pattern that matches nobody is a usage error before the command runs.
/// </summary>
public sealed class Target : IReadOnlyList<CCitadelPlayerController>
{
    private readonly IReadOnlyList<CCitadelPlayerController> _players;

    /// <summary>The argument as typed.</summary>
    public string Input { get; }

    /// <summary>True for <c>@all</c>, <c>@team</c> and <c>@enemy</c>, which may match several players.</summary>
    public bool IsGroup { get; }

    internal Target(string input, bool isGroup, IReadOnlyList<CCitadelPlayerController> players)
    {
        Input = input;
        IsGroup = isGroup;
        _players = players;
    }

    /// <summary>The one player matched, or a <see cref="CommandException"/> telling the caller to be more specific.</summary>
    public CCitadelPlayerController Single()
        => _players.Count == 1
            ? _players[0]
            : throw new CommandException($"'{Input}' matches {_players.Count} players; this command takes one.");

    /// <summary>How many players matched; at least one.</summary>
    public int Count => _players.Count;
    /// <summary>The matched player at <paramref name="index"/>.</summary>
    public CCitadelPlayerController this[int index] => _players[index];
    /// <inheritdoc/>
    public IEnumerator<CCitadelPlayerController> GetEnumerator() => _players.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class TargetResolver
{
    internal readonly record struct Candidate(int Slot, string Name, int Team, ulong SteamId64);

    /// <summary>
    /// Picks slots out of <paramref name="candidates"/>. <paramref name="canTarget"/> is null when immunity is ignored.
    /// Pure so it can be tested without an engine.
    /// </summary>
    internal static bool TryMatch(
        string input,
        Candidate? caller,
        IReadOnlyList<Candidate> candidates,
        Func<Candidate, bool>? canTarget,
        out List<int> slots,
        out bool isGroup,
        out string? error)
    {
        slots = [];
        isGroup = false;
        error = null;
        var token = input.Trim();

        List<Candidate> matched;
        switch (token.ToLowerInvariant())
        {
            case "@me":
                if (caller == null) { error = "@me needs a player caller."; return false; }
                matched = [caller.Value];
                break;
            case "@all":
                isGroup = true;
                matched = [.. candidates];
                break;
            case "@team":
            case "@enemy":
                if (caller == null) { error = $"{token} needs a player caller."; return false; }
                isGroup = true;
                var own = caller.Value.Team;
                matched = token.Equals("@team", StringComparison.OrdinalIgnoreCase)
                    ? candidates.Where(c => c.Team == own).ToList()
                    : candidates.Where(c => c.Team != own && c.Team > 1).ToList();
                break;
            default:
                if (!TryMatchSingle(token, candidates, out matched, out error))
                    return false;
                break;
        }

        if (matched.Count == 0)
        {
            error = $"No players match '{token}'.";
            return false;
        }

        if (canTarget != null)
        {
            var allowed = matched.Where(canTarget).ToList();
            if (allowed.Count == 0)
            {
                error = isGroup
                    ? $"You can't target any of the players matching '{token}'."
                    : $"You can't target {matched[0].Name}.";
                return false;
            }
            matched = allowed;
        }

        slots = matched.Select(c => c.Slot).ToList();
        return true;
    }

    private static bool TryMatchSingle(string token, IReadOnlyList<Candidate> candidates, out List<Candidate> matched, out string? error)
    {
        error = null;

        if (token.StartsWith('#') && int.TryParse(token.AsSpan(1), out var slot))
        {
            matched = candidates.Where(c => c.Slot == slot).ToList();
            if (matched.Count == 0) error = $"No player in slot {slot}.";
            return matched.Count > 0;
        }

        if (SteamIds.TryParse(token, out var steamId))
        {
            matched = candidates.Where(c => c.SteamId64 == steamId).ToList();
            if (matched.Count == 0) error = $"No connected player has SteamID {steamId}.";
            return matched.Count > 0;
        }

        matched = candidates.Where(c => string.Equals(c.Name, token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0)
            matched = candidates.Where(c => c.Name.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matched.Count > 1)
        {
            error = $"'{token}' matches several players: {string.Join(", ", matched.Select(c => $"{c.Name} (#{c.Slot})"))}. Use #slot.";
            return false;
        }
        if (matched.Count == 0)
        {
            error = $"No player matches '{token}'.";
            return false;
        }
        return true;
    }

    /// <summary>Resolves against the connected players, for <see cref="CommandAttribute"/> binding.</summary>
    internal static bool TryResolve(string input, CCitadelPlayerController? caller, bool enforceImmunity, out Target? target, out string? error)
    {
        target = null;
        var controllers = Players.GetAll().ToDictionary(p => p.Slot);
        var candidates = controllers.Values.Select(ToCandidate).ToList();
        Candidate? callerCandidate = caller != null ? ToCandidate(caller) : null;

        Func<Candidate, bool>? canTarget = enforceImmunity && caller != null
            ? c => Permissions.CanTarget(caller, controllers[c.Slot])
            : null;

        if (!TryMatch(input, callerCandidate, candidates, canTarget, out var slots, out var isGroup, out error))
            return false;

        target = new Target(input, isGroup, slots.Select(s => controllers.TryGetValue(s, out var c) ? c : caller!).ToList());
        return true;
    }

    private static Candidate ToCandidate(CCitadelPlayerController p)
        => new(p.Slot, p.PlayerName, p.TeamNum, Permissions.Backend?.GetSlotSteamId(p.Slot) ?? 0);
}
