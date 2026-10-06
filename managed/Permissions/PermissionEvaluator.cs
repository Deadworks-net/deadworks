using DeadworksManaged.Api;

namespace DeadworksManaged.PermissionSystem;

/// <summary>One parsed grant: <c>*</c>, <c>a.b.*</c> or <c>a.b.c</c>, optionally prefixed with <c>-</c> to deny.</summary>
internal readonly record struct Grant(string Raw, string Pattern, bool Deny, bool Wildcard, int Specificity)
{
    /// <summary>Parses a grant, lowercased. Wildcards only count as a whole last segment, so <c>a.*</c> never matches <c>ab</c>.</summary>
    public static bool TryParse(string? raw, out Grant grant, out string? error)
    {
        grant = default;
        error = null;
        var text = raw?.Trim() ?? "";
        var deny = text.StartsWith('-');
        var pattern = (deny ? text[1..] : text).Trim().ToLowerInvariant();

        if (pattern.Length == 0)
        {
            error = "empty grant";
            return false;
        }

        if (pattern == "*")
        {
            grant = new Grant(text, "", deny, Wildcard: true, Specificity: 0);
            return true;
        }

        var segments = pattern.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            var isLast = i == segments.Length - 1;
            if (seg.Length == 0 || (seg.Contains('*') && !(isLast && seg == "*")) || seg.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                error = $"'{text}' is not a valid permission (use a.b.c, a.b.* or *)";
                return false;
            }
        }

        var wildcard = segments[^1] == "*";
        grant = wildcard
            // "a.b.*" is stored as the prefix "a.b." so matching stays on segment boundaries.
            ? new Grant(text, pattern[..^1], deny, Wildcard: true, Specificity: 2 * (segments.Length - 1))
            : new Grant(text, pattern, deny, Wildcard: false, Specificity: 2 * segments.Length + 1);
        return true;
    }

    /// <summary><paramref name="query"/> must already be normalized with <see cref="PermissionEvaluator.Normalize"/>.</summary>
    public bool Matches(string query) => Wildcard ? query.StartsWith(Pattern, StringComparison.Ordinal) : query == Pattern;

    /// <summary>True when every permission this grant matches is also matched by <paramref name="other"/>.</summary>
    public bool IsCoveredBy(Grant other)
    {
        if (!other.Wildcard)
            return !Wildcard && other.Pattern == Pattern;
        return Pattern.StartsWith(other.Pattern, StringComparison.Ordinal) && (Wildcard || Pattern.Length > other.Pattern.Length);
    }
}

internal readonly record struct Rule(Grant Grant, bool FromPlayer, string Source);

/// <summary>A role with its own grants and the roles it inherits, resolved once per compile.</summary>
internal sealed class RoleNode
{
    public required string Name { get; init; }
    public required IReadOnlyList<Grant> Own { get; init; }
    public required IReadOnlyList<RoleNode> Parents { get; init; }
    /// <summary>The role's own immunity, or the highest of the roles it inherits.</summary>
    public required int Immunity { get; init; }
}

/// <summary>Everything needed to answer checks for one player, built once and cached until something changes.</summary>
internal sealed class CompiledSubject
{
    /// <summary>Grants on the player's own entry. They decide first.</summary>
    public required IReadOnlyList<Grant> PlayerGrants { get; init; }
    /// <summary><c>default</c> and the player's assigned roles, each with its inheritance chain.</summary>
    public required IReadOnlyList<RoleNode> Roles { get; init; }
    /// <summary>Every grant the player has anywhere, for listings and delegation checks.</summary>
    public required IReadOnlyList<Rule> Rules { get; init; }
    public required int Immunity { get; init; }
    /// <summary>Roles assigned to the player, excluding <c>default</c>.</summary>
    public required IReadOnlyList<string> AssignedRoles { get; init; }
    /// <summary>Every role the player gets permissions from, including <c>default</c> and inherited roles.</summary>
    public required IReadOnlyList<string> EffectiveRoles { get; init; }
}

/// <summary>
/// Decides permissions. The player's own entry decides first. Otherwise every role the player has (including
/// <c>default</c>) is asked separately: a role's own grants beat what it inherits, and nearer inherited roles beat
/// farther ones. The player has a permission if any of their roles gives it, so a deny in one role never takes away
/// what another role gives. Within one list the most specific grant wins, and a deny wins a tie.
/// </summary>
internal static class PermissionEvaluator
{
    public const string DefaultRole = "default";

    public static string Normalize(string permission) => permission.Trim().ToLowerInvariant();

    public static CompiledSubject Compile(IReadOnlyDictionary<string, RoleDefinition> roles, PlayerEntry? player)
    {
        var playerGrants = ParseAll(player?.Permissions ?? []);

        var assigned = (player?.Roles ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r) && !r.Equals(DefaultRole, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var built = new Dictionary<string, RoleNode>(StringComparer.OrdinalIgnoreCase);
        var nodes = assigned.Prepend(DefaultRole)
            .Select(name => BuildNode(roles, name, built, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            .OfType<RoleNode>()
            .ToList();

        var rules = playerGrants.Select(g => new Rule(g, FromPlayer: true, "player"))
            .Concat(built.Values.SelectMany(n => n.Own.Select(g => new Rule(g, FromPlayer: false, $"role:{n.Name}"))))
            .ToList();

        return new CompiledSubject
        {
            PlayerGrants = playerGrants,
            Roles = nodes,
            Rules = rules,
            Immunity = player?.Immunity ?? nodes.Select(n => n.Immunity).DefaultIfEmpty(0).Max(),
            AssignedRoles = assigned,
            EffectiveRoles = [.. built.Values.Select(n => n.Name)]
        };
    }

    private static RoleNode? BuildNode(
        IReadOnlyDictionary<string, RoleDefinition> roles, string name, Dictionary<string, RoleNode> built, HashSet<string> path)
    {
        if (built.TryGetValue(name, out var existing))
            return existing;
        // A role already on the path is an inheritance cycle; dropping that edge keeps evaluation finite.
        if (!roles.TryGetValue(name, out var role) || !path.Add(name))
            return null;

        var parents = role.Inherits
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(parent => BuildNode(roles, parent, built, path))
            .OfType<RoleNode>()
            .ToList();
        path.Remove(name);

        var node = new RoleNode
        {
            Name = name,
            Own = ParseAll(role.Permissions),
            Parents = parents,
            Immunity = role.Immunity ?? parents.Select(p => p.Immunity).DefaultIfEmpty(0).Max()
        };
        built[name] = node;
        return node;
    }

    private static List<Grant> ParseAll(IEnumerable<string> raw)
    {
        var grants = new List<Grant>();
        foreach (var r in raw)
            if (Grant.TryParse(r, out var g, out _))
                grants.Add(g);
        return grants;
    }

    public static PermissionExplanation Evaluate(CompiledSubject subject, string permission)
    {
        var query = Normalize(permission);
        if (query.Length == 0)
            return new PermissionExplanation(true, null, null);

        if (MostSpecific(subject.PlayerGrants, query) is { } own)
            return new PermissionExplanation(!own.Deny, own.Raw, "player");

        PermissionExplanation? denied = null;
        foreach (var role in subject.Roles)
        {
            if (EvaluateRole(role, query, via: null) is not { } result)
                continue;
            if (result.Allowed)
                return result;
            denied ??= result;
        }
        return denied ?? new PermissionExplanation(false, null, null);
    }

    /// <summary>The role's own grants decide if any match; otherwise any inherited role allowing is enough. Null when nothing matches.</summary>
    private static PermissionExplanation? EvaluateRole(RoleNode role, string query, string? via)
    {
        if (MostSpecific(role.Own, query) is { } own)
            return new PermissionExplanation(!own.Deny, own.Raw, via == null ? $"role:{role.Name}" : $"role:{role.Name} (via {via})");

        PermissionExplanation? denied = null;
        foreach (var parent in role.Parents)
        {
            if (EvaluateRole(parent, query, via ?? role.Name) is not { } result)
                continue;
            if (result.Allowed)
                return result;
            denied ??= result;
        }
        return denied;
    }

    private static Grant? MostSpecific(IReadOnlyList<Grant> grants, string query)
    {
        Grant? best = null;
        foreach (var grant in grants)
        {
            if (!grant.Matches(query))
                continue;
            if (best is not { } b || grant.Specificity > b.Specificity || (grant.Specificity == b.Specificity && grant.Deny && !b.Deny))
                best = grant;
        }
        return best;
    }

    // A segment no real permission uses, so "a.b." + Probe stands for "anything under a.b. that isn't named".
    private const string Probe = "\u0001";

    /// <summary>
    /// What <paramref name="gift"/> allows that <paramref name="giver"/> doesn't hold, as grants (e.g. <c>a.b.x</c>,
    /// <c>a.b.*</c>). Empty means the giver holds everything the gift would give.
    /// </summary>
    /// <remarks>
    /// Exact, not an approximation. Which grants match a permission depends only on whether it equals an exact grant
    /// and on the longest wildcard prefix it falls under, so every exact pattern and one unnamed child of every
    /// wildcard in either subject together cover every case the evaluator can tell apart.
    /// </remarks>
    public static IReadOnlyList<string> NotHeld(CompiledSubject giver, CompiledSubject gift)
    {
        var probes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in giver.Rules.Concat(gift.Rules))
            probes.Add(rule.Grant.Wildcard ? rule.Grant.Pattern + Probe : rule.Grant.Pattern);

        return probes
            .Where(q => Evaluate(gift, q).Allowed && !Evaluate(giver, q).Allowed)
            .Select(q => q.EndsWith(Probe, StringComparison.Ordinal) ? q[..^Probe.Length] + "*" : q)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Whether the subject may hand out <paramref name="grant"/> with the management commands: they must hold
    /// everything it would allow. Adding a deny only takes access away, so it needs nothing.
    /// </summary>
    public static bool CanDelegate(CompiledSubject subject, Grant grant)
        => grant.Deny || NotHeld(subject, GrantSubject(grant)).Count == 0;

    /// <summary>Someone holding exactly <paramref name="grant"/>.</summary>
    public static CompiledSubject GrantSubject(Grant grant) => new()
    {
        PlayerGrants = [grant],
        Roles = [],
        Rules = [new Rule(grant, FromPlayer: true, "player")],
        Immunity = 0,
        AssignedRoles = [],
        EffectiveRoles = []
    };

    /// <summary>Someone holding exactly one role (not even <c>default</c>), or null if there's no such role.</summary>
    public static CompiledSubject? RoleSubject(IReadOnlyDictionary<string, RoleDefinition> roles, string roleName)
    {
        var built = new Dictionary<string, RoleNode>(StringComparer.OrdinalIgnoreCase);
        if (BuildNode(roles, roleName, built, new HashSet<string>(StringComparer.OrdinalIgnoreCase)) is not { } node)
            return null;
        return new CompiledSubject
        {
            PlayerGrants = [],
            Roles = [node],
            Rules = built.Values.SelectMany(n => n.Own.Select(g => new Rule(g, FromPlayer: false, $"role:{n.Name}"))).ToList(),
            Immunity = node.Immunity,
            AssignedRoles = [node.Name],
            EffectiveRoles = [.. built.Values.Select(n => n.Name)]
        };
    }

    /// <summary>Problems worth logging after a load: bad grants, unknown or cyclic inherits.</summary>
    public static IEnumerable<string> Validate(IReadOnlyDictionary<string, RoleDefinition> roles)
    {
        foreach (var (name, role) in roles)
        {
            foreach (var raw in role.Permissions)
                if (!Grant.TryParse(raw, out _, out var error))
                    yield return $"role '{name}': {error}";

            foreach (var parent in role.Inherits)
                if (!roles.ContainsKey(parent))
                    yield return $"role '{name}' inherits unknown role '{parent}'";

            if (InheritsItself(roles, name))
                yield return $"role '{name}' inherits itself; the cycle is ignored";
        }
    }

    private static bool InheritsItself(IReadOnlyDictionary<string, RoleDefinition> roles, string start)
    {
        var stack = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (roles.TryGetValue(start, out var r))
            foreach (var p in r.Inherits) stack.Push(p);

        while (stack.Count > 0)
        {
            var name = stack.Pop();
            if (name.Equals(start, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!seen.Add(name) || !roles.TryGetValue(name, out var role))
                continue;
            foreach (var p in role.Inherits) stack.Push(p);
        }
        return false;
    }

    public static IEnumerable<string> Validate(ulong steamId64, PlayerEntry player, IReadOnlyDictionary<string, RoleDefinition> roles)
    {
        foreach (var raw in player.Permissions)
            if (!Grant.TryParse(raw, out _, out var error))
                yield return $"player {steamId64}: {error}";
        foreach (var role in player.Roles)
            if (!roles.ContainsKey(role))
                yield return $"player {steamId64} has unknown role '{role}'";
    }

    /// <summary>
    /// Grants that match no declared permission, e.g. a typo or a plugin that isn't installed. <c>*</c> never counts,
    /// and a wildcard is fine if it matches at least one declared permission. <paramref name="declared"/> must be normalized.
    /// </summary>
    public static IEnumerable<string> UnknownGrants(IEnumerable<string>? raw, IReadOnlyCollection<string> declared)
    {
        foreach (var r in raw ?? [])
        {
            if (!Grant.TryParse(r, out var g, out _) || g.Pattern.Length == 0)
                continue;
            if (!declared.Any(g.Matches))
                yield return r.Trim();
        }
    }
}
