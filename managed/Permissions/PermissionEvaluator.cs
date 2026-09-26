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
            if (seg.Length == 0 || (seg.Contains('*') && !(isLast && seg == "*")) || seg.Any(char.IsWhiteSpace))
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

/// <summary>Everything needed to answer checks for one player, built once and cached until something changes.</summary>
internal sealed class CompiledSubject
{
    public required IReadOnlyList<Rule> Rules { get; init; }
    public required int Immunity { get; init; }
    /// <summary>Roles assigned to the player, excluding <c>default</c>.</summary>
    public required IReadOnlyList<string> AssignedRoles { get; init; }
    /// <summary>Every role the player gets permissions from, including <c>default</c> and inherited roles.</summary>
    public required IReadOnlyList<string> EffectiveRoles { get; init; }
}

internal static class PermissionEvaluator
{
    public const string DefaultRole = "default";

    public static string Normalize(string permission) => permission.Trim().ToLowerInvariant();

    public static CompiledSubject Compile(IReadOnlyDictionary<string, RoleDefinition> roles, PlayerEntry? player)
    {
        var rules = new List<Rule>();

        if (player != null)
        {
            foreach (var raw in player.Permissions)
                if (Grant.TryParse(raw, out var g, out _))
                    rules.Add(new Rule(g, FromPlayer: true, "player"));
        }

        var assigned = (player?.Roles ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r) && !r.Equals(DefaultRole, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var effective = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in assigned.Prepend(DefaultRole))
            CollectRole(roles, role, visited, effective, rules);

        int immunity = 0;
        foreach (var role in assigned.Prepend(DefaultRole))
            if (roles.TryGetValue(role, out var def) && def.Immunity is { } i)
                immunity = Math.Max(immunity, i);
        if (player?.Immunity is { } overrideImmunity)
            immunity = overrideImmunity;

        return new CompiledSubject { Rules = rules, Immunity = immunity, AssignedRoles = assigned, EffectiveRoles = effective };
    }

    private static void CollectRole(
        IReadOnlyDictionary<string, RoleDefinition> roles, string name, HashSet<string> visited, List<string> effective, List<Rule> rules)
    {
        // Visiting each role once both breaks inheritance cycles and stops diamonds adding duplicate rules.
        if (!visited.Add(name) || !roles.TryGetValue(name, out var role))
            return;

        effective.Add(name);
        foreach (var raw in role.Permissions)
            if (Grant.TryParse(raw, out var g, out _))
                rules.Add(new Rule(g, FromPlayer: false, $"role:{name}"));

        foreach (var parent in role.Inherits)
            CollectRole(roles, parent, visited, effective, rules);
    }

    /// <summary>
    /// The most specific matching grant wins; on a tie a player grant beats a role grant, then deny beats allow.
    /// Nothing matching means deny.
    /// </summary>
    public static PermissionExplanation Evaluate(CompiledSubject subject, string permission)
    {
        var query = Normalize(permission);
        if (query.Length == 0)
            return new PermissionExplanation(true, null, null);

        Rule? best = null;
        foreach (var rule in subject.Rules)
        {
            if (!rule.Grant.Matches(query))
                continue;
            if (best == null || Beats(rule, best.Value))
                best = rule;
        }

        return best is { } b
            ? new PermissionExplanation(!b.Grant.Deny, b.Grant.Raw, b.Source)
            : new PermissionExplanation(false, null, null);
    }

    private static bool Beats(Rule a, Rule b)
    {
        if (a.Grant.Specificity != b.Grant.Specificity)
            return a.Grant.Specificity > b.Grant.Specificity;
        if (a.FromPlayer != b.FromPlayer)
            return a.FromPlayer;
        return a.Grant.Deny && !b.Grant.Deny;
    }

    /// <summary>
    /// Whether the subject may hand out <paramref name="grant"/> with the management commands: they must hold
    /// everything it would allow. Denies only take access away, so anyone who can target the player may add them.
    /// </summary>
    public static bool CanDelegate(CompiledSubject subject, Grant grant)
    {
        if (grant.Deny)
            return true;

        if (!grant.Wildcard)
            return Evaluate(subject, grant.Pattern).Allowed;

        // For a wildcard, some allow grant must cover the whole range, and no deny may carve anything out of it.
        var covered = subject.Rules.Any(r => !r.Grant.Deny && grant.IsCoveredBy(r.Grant));
        var carvedOut = subject.Rules.Any(r => r.Grant.Deny && (r.Grant.IsCoveredBy(grant) || grant.IsCoveredBy(r.Grant)));
        return covered && !carvedOut;
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
}
