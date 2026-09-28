using DeadworksManaged.Api;

namespace DeadworksManaged.PermissionSystem;

/// <summary>
/// The <c>dw_perm_*</c> and <c>dw_role_*</c> console commands. Registered like a plugin's [Command]s so they are
/// gated by core permissions, which lets in-game admins use them as well as the server console.
/// </summary>
internal sealed class PermissionCommands : DeadworksPluginBase
{
    public override string Name => "Deadworks";

    private const string View = "deadworks.permissions.view";
    private const string Manage = "deadworks.permissions.manage";

    [Command("perm_reload", Description = "Reload roles, players and command overrides", Permission = "deadworks.permissions.reload", ConsoleOnly = true)]
    public void PermReload(Caller caller)
    {
        var ok = PermissionManager.Reload();
        AdminActivity.Log(caller, ok ? "reloaded permissions" : "tried to reload permissions, which failed");
        // Say what's wrong: staff on a hosted server can't see its console.
        var error = PermissionManager.LastLoadError ?? CommandOverrides.LastError;
        caller.PrintToConsole(ok
            ? "Reloaded permissions."
            : $"Failed to reload permissions: {error?.TrimEnd('.') ?? "the server console has details"}. The previous settings are still in use.");
    }

    [Command("role_list", Description = "List roles with their immunity and permissions", Permission = View, ConsoleOnly = true)]
    public void RoleList(Caller caller)
    {
        var roles = PermissionManager.Roles;
        if (PermissionManager.StoreUnavailable)
            caller.PrintToConsole($"Warning: {PermissionManager.UnavailableMessage}");
        if (roles.Count == 0)
        {
            caller.PrintToConsole("No roles are defined.");
            return;
        }

        foreach (var (name, role) in roles.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
        {
            var immunity = PermissionEvaluator.Compile(roles, new PlayerEntry { Roles = [name] }).Immunity;
            var inherits = role.Inherits.Count > 0 ? $", inherits {string.Join(", ", role.Inherits)}" : "";
            caller.PrintToConsole($"{name} (immunity {immunity}{inherits}): {(role.Permissions.Count > 0 ? string.Join(", ", role.Permissions) : "no permissions")}");
        }
    }

    [Command("role_grant", Description = "Give a player a role. Add --temp to keep it for this session only", Permission = Manage, ConsoleOnly = true)]
    public void RoleGrant(Caller caller, string player, string role, string temp = "")
        => Change(caller, player, PermissionManager.ChangeKind.GrantRole, role, temp);

    [Command("role_revoke", Description = "Take a role from a player. Add --temp to undo it on restart", Permission = Manage, ConsoleOnly = true)]
    public void RoleRevoke(Caller caller, string player, string role, string temp = "")
        => Change(caller, player, PermissionManager.ChangeKind.RevokeRole, role, temp);

    [Command("perm_grant", Description = "Give a player a permission; prefix with - to deny it. Add --temp for this session only", Permission = Manage, ConsoleOnly = true)]
    public void PermGrant(Caller caller, string player, string permission, string temp = "")
        => Change(caller, player, PermissionManager.ChangeKind.GrantPermission, permission, temp);

    [Command("perm_revoke", Description = "Remove a permission (or a -deny) from a player's entry. Add --temp for this session only", Permission = Manage, ConsoleOnly = true)]
    public void PermRevoke(Caller caller, string player, string permission, string temp = "")
        => Change(caller, player, PermissionManager.ChangeKind.RevokePermission, permission, temp);

    [Command("perm_check", Description = "Show whether a player has a permission, and which grant decided it", Permission = View, ConsoleOnly = true)]
    public void PermCheck(Caller caller, string player, string permission)
    {
        var who = ResolvePlayer(caller, player);
        var result = who.Slot >= 0
            ? PermissionManager.ExplainSlot(who.Slot, permission)
            : PermissionManager.Explain(who.SteamId64, permission);
        caller.PrintToConsole($"{who.Display}: {permission.Trim().ToLowerInvariant()} is {result}");
    }

    [Command("perm_list", Description = "Show a player's roles, permissions and immunity", Permission = View, ConsoleOnly = true)]
    public void PermList(Caller caller, string player)
    {
        var who = ResolvePlayer(caller, player);
        var subject = PermissionManager.Describe(who.SteamId64);

        caller.PrintToConsole($"{who.Display}:");
        if (PermissionManager.StoreUnavailable)
            caller.PrintToConsole($"  Warning: {PermissionManager.UnavailableMessage}");
        if (who.Slot >= 0)
        {
            PermissionManager.DescribeSlot(who.Slot, out var authenticated);
            if (!authenticated)
                caller.PrintToConsole("  Not validated by Steam yet: only \"default\" applies until then.");
        }
        caller.PrintToConsole($"  Roles: {(subject.AssignedRoles.Count > 0 ? string.Join(", ", subject.AssignedRoles) : "none")}");
        caller.PrintToConsole($"  Permissions come from: {string.Join(", ", subject.EffectiveRoles.Select(r => $"role:{r}").Prepend("player"))}");
        var own = subject.PlayerGrants.Select(g => g.Raw).ToList();
        if (own.Count > 0)
            caller.PrintToConsole($"  Player grants: {string.Join(", ", own)}");
        caller.PrintToConsole($"  Immunity: {subject.Immunity}");
        if (PermissionManager.IsTemporary(who.SteamId64))
            caller.PrintToConsole("  Has --temp changes that will be lost on restart.");
    }

    private static void Change(Caller caller, string player, PermissionManager.ChangeKind kind, string value, string temp)
    {
        bool temporary = temp.Length > 0;
        if (temporary && !temp.Equals("--temp", StringComparison.OrdinalIgnoreCase) && !temp.Equals("temp", StringComparison.OrdinalIgnoreCase))
            throw new CommandException($"Unknown option '{temp}'. The only option is --temp.");

        var who = ResolvePlayer(caller, player);
        if (caller.Player is { } admin)
            CheckAllowed(admin.Slot, who, kind, value);

        var verb = kind switch
        {
            PermissionManager.ChangeKind.GrantRole => $"gave {who.Display} the role {value}",
            PermissionManager.ChangeKind.RevokeRole => $"took the role {value} from {who.Display}",
            PermissionManager.ChangeKind.GrantPermission => $"gave {who.Display} {value}",
            _ => $"removed {value} from {who.Display}",
        } + (temporary ? " until restart" : "");

        // The reply waits for the store, so "Gave ..." is only said once the change is really saved.
        var change = PermissionManager.ChangeAsync(who.SteamId64, kind, value, temporary, who.Name);
        var callerSlot = caller.Player?.Slot ?? -1;
        var callerId = caller.SteamId64;
        void Report(string? error)
        {
            // A slow save can finish after the caller left; don't tell whoever took their slot.
            if (callerSlot >= 0 && PermissionManager.GetSlotSteamId(callerSlot) != callerId)
                return;
            if (error == null)
                // Staff changes belong in the action log: who promoted whom is the first thing an owner asks.
                AdminActivity.Log(caller, verb, details: $"target={who.SteamId64}");
            caller.PrintToConsole(error ?? $"{char.ToUpperInvariant(verb[0])}{verb[1..]}.");
        }

        // A store that saves asynchronously completes on a later game frame, where replying is safe.
        if (change.IsCompleted)
            Report(change.Result);
        else
            change.ContinueWith(t => Report(t.Result), TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// What a player (not the console) may change: never themselves, only players with lower immunity, and only access
    /// they hold themselves. Removing a deny unlocks the permission, so it counts as giving it. The target is judged by
    /// their saved entry, not by whether Steam has confirmed them yet: this changes their record, not what they can do now.
    /// </summary>
    internal static void CheckAllowed(int adminSlot, ResolvedPlayer who, PermissionManager.ChangeKind kind, string value)
    {
        if (who.SteamId64 == PermissionManager.GetSlotSteamId(adminSlot))
            throw new CommandException("You can't change your own roles or permissions.");

        // Describe starts loading the target's entry; judging them as "default" before it arrives would fail open.
        var targetImmunity = PermissionManager.Describe(who.SteamId64).Immunity;
        if (!PermissionManager.IsLoaded(who.SteamId64))
            throw new CommandException($"{who.Display}'s permissions are still loading; try again in a moment.");

        var own = PermissionManager.DescribeSlot(adminSlot, out _);
        if (targetImmunity >= own.Immunity)
            throw new CommandException($"You can't change {who.Display}: you need higher immunity than theirs ({targetImmunity}).");

        switch (kind)
        {
            case PermissionManager.ChangeKind.GrantRole:
                if (PermissionEvaluator.RoleSubject(PermissionManager.Roles, value.Trim()) is not { } role)
                    break; // ChangeAsync reports the unknown role
                if (role.Immunity >= own.Immunity)
                    throw new CommandException($"You can't give out {value.Trim()}: its immunity ({role.Immunity}) isn't lower than yours ({own.Immunity}).");
                var missing = PermissionEvaluator.NotHeld(own, role);
                if (missing.Count > 0)
                    throw new CommandException($"You can only give out roles whose permissions you hold. You don't hold: {string.Join(", ", missing)}");
                break;

            case PermissionManager.ChangeKind.GrantPermission:
                if (Grant.TryParse(value, out var grant, out _) && !PermissionEvaluator.CanDelegate(own, grant))
                    throw new CommandException("You can only give out permissions you hold yourself.");
                break;

            case PermissionManager.ChangeKind.RevokePermission:
                if (Grant.TryParse(value, out var revoked, out _) && revoked.Deny
                    && Grant.TryParse(value.Trim()[1..], out var unlocked, out _) && !PermissionEvaluator.CanDelegate(own, unlocked))
                    throw new CommandException($"Removing {value} would give {who.Display} {value.Trim()[1..]}, which you don't hold yourself.");
                break;
        }
    }

    private static unsafe string? NameInSlot(int slot)
        => NativeInterop.GetPlayerController == null ? null : Players.FromSlot(slot)?.PlayerName;

    internal readonly record struct ResolvedPlayer(ulong SteamId64, int Slot, string? Name)
    {
        public string Display => Name != null ? $"{Name} ({SteamId64})" : SteamId64.ToString();
    }

    /// <summary>A SteamID (online or not), or a connected player by <c>#slot</c> or name.</summary>
    private static ResolvedPlayer ResolvePlayer(Caller caller, string input)
    {
        if (SteamIds.TryParse(input, out var steamId64))
        {
            var slot = PermissionManager.FindSlot(steamId64);
            return new ResolvedPlayer(steamId64, slot, slot >= 0 ? NameInSlot(slot) : null);
        }

        if (!TargetResolver.TryResolve(input, caller.Player, enforceImmunity: false, out var target, out var error))
            throw new CommandException(error ?? $"No player matches '{input}'.");
        if (target!.IsGroup)
            throw new CommandException("Name one player: a SteamID, #slot or part of their name.");

        var player = target.Single();
        var id = PermissionManager.GetSlotSteamId(player.Slot);
        if (id == 0)
            throw new CommandException($"{player.PlayerName} is a bot and can't hold permissions.");
        return new ResolvedPlayer(id, player.Slot, player.PlayerName);
    }
}
