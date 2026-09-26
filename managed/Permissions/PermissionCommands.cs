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
    public void PermReload(CCitadelPlayerController? caller)
    {
        Reply(caller, PermissionManager.Reload()
            ? "Reloaded permissions."
            : "Failed to reload permissions; the server console has details. The previous settings are still in use.");
    }

    [Command("role_list", Description = "List roles with their immunity and permissions", Permission = View, ConsoleOnly = true)]
    public void RoleList(CCitadelPlayerController? caller)
    {
        var roles = PermissionManager.Roles;
        if (roles.Count == 0)
        {
            Reply(caller, "No roles are defined.");
            return;
        }

        foreach (var (name, role) in roles.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
        {
            var inherits = role.Inherits.Count > 0 ? $", inherits {string.Join(", ", role.Inherits)}" : "";
            Reply(caller, $"{name} (immunity {role.Immunity ?? 0}{inherits}): {(role.Permissions.Count > 0 ? string.Join(", ", role.Permissions) : "no permissions")}");
        }
    }

    [Command("role_grant", Description = "Give a player a role. Add --temp to keep it for this session only", Permission = Manage, ConsoleOnly = true)]
    public void RoleGrant(CCitadelPlayerController? caller, string player, string role, string temp = "")
        => Change(caller, player, PermissionManager.ChangeKind.GrantRole, role, temp);

    [Command("role_revoke", Description = "Take a role from a player. Add --temp to undo it on restart", Permission = Manage, ConsoleOnly = true)]
    public void RoleRevoke(CCitadelPlayerController? caller, string player, string role, string temp = "")
        => Change(caller, player, PermissionManager.ChangeKind.RevokeRole, role, temp);

    [Command("perm_grant", Description = "Give a player a permission; prefix with - to deny it. Add --temp for this session only", Permission = Manage, ConsoleOnly = true)]
    public void PermGrant(CCitadelPlayerController? caller, string player, string permission, string temp = "")
        => Change(caller, player, PermissionManager.ChangeKind.GrantPermission, permission, temp);

    [Command("perm_revoke", Description = "Remove a permission (or a -deny) from a player's entry. Add --temp for this session only", Permission = Manage, ConsoleOnly = true)]
    public void PermRevoke(CCitadelPlayerController? caller, string player, string permission, string temp = "")
        => Change(caller, player, PermissionManager.ChangeKind.RevokePermission, permission, temp);

    [Command("perm_check", Description = "Show whether a player has a permission, and which grant decided it", Permission = View, ConsoleOnly = true)]
    public void PermCheck(CCitadelPlayerController? caller, string player, string permission)
    {
        var who = ResolvePlayer(caller, player);
        var result = who.Slot >= 0
            ? PermissionManager.ExplainSlot(who.Slot, permission)
            : PermissionManager.Explain(who.SteamId64, permission);
        Reply(caller, $"{who.Display}: {permission.Trim().ToLowerInvariant()} is {result}");
    }

    [Command("perm_list", Description = "Show a player's roles, permissions and immunity", Permission = View, ConsoleOnly = true)]
    public void PermList(CCitadelPlayerController? caller, string player)
    {
        var who = ResolvePlayer(caller, player);
        var subject = PermissionManager.Describe(who.SteamId64);

        Reply(caller, $"{who.Display}:");
        if (who.Slot >= 0)
        {
            PermissionManager.DescribeSlot(who.Slot, out var authenticated);
            if (!authenticated)
                Reply(caller, "  Not validated by Steam yet: only \"default\" applies until then.");
        }
        Reply(caller, $"  Roles: {(subject.AssignedRoles.Count > 0 ? string.Join(", ", subject.AssignedRoles) : "none")}");
        Reply(caller, $"  Permissions come from: {string.Join(", ", subject.EffectiveRoles.Select(r => $"role:{r}").Prepend("player"))}");
        var own = subject.Rules.Where(r => r.FromPlayer).Select(r => r.Grant.Raw).ToList();
        if (own.Count > 0)
            Reply(caller, $"  Player grants: {string.Join(", ", own)}");
        Reply(caller, $"  Immunity: {subject.Immunity}");
        if (PermissionManager.IsTemporary(who.SteamId64))
            Reply(caller, "  Has --temp changes that will be lost on restart.");
    }

    private static void Change(CCitadelPlayerController? caller, string player, PermissionManager.ChangeKind kind, string value, string temp)
    {
        bool temporary = temp.Length > 0;
        if (temporary && !temp.Equals("--temp", StringComparison.OrdinalIgnoreCase) && !temp.Equals("temp", StringComparison.OrdinalIgnoreCase))
            throw new CommandException($"Unknown option '{temp}'. The only option is --temp.");

        var who = ResolvePlayer(caller, player);

        if (caller != null)
        {
            if (!CallerCanTarget(caller, who))
                throw new CommandException($"You can't change {who.Display}: their immunity is higher than yours.");

            // Without this, anyone who can manage permissions could hand themselves or a friend "*".
            if (kind is PermissionManager.ChangeKind.GrantRole or PermissionManager.ChangeKind.GrantPermission)
            {
                var own = PermissionManager.DescribeSlot(caller.Slot, out _);
                if (kind == PermissionManager.ChangeKind.GrantRole
                    && !own.EffectiveRoles.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                    throw new CommandException($"You can only give out roles you hold yourself.");
                if (kind == PermissionManager.ChangeKind.GrantPermission
                    && Grant.TryParse(value, out var grant, out _) && !PermissionEvaluator.CanDelegate(own, grant))
                    throw new CommandException($"You can only give out permissions you hold yourself.");
            }
        }

        var error = PermissionManager.Change(who.SteamId64, kind, value, temporary, who.Name);
        if (error != null)
            throw new CommandException(error);

        var verb = kind switch
        {
            PermissionManager.ChangeKind.GrantRole => $"Gave {who.Display} the role {value}",
            PermissionManager.ChangeKind.RevokeRole => $"Took the role {value} from {who.Display}",
            PermissionManager.ChangeKind.GrantPermission => $"Gave {who.Display} {value}",
            _ => $"Removed {value} from {who.Display}",
        };
        Reply(caller, $"{verb}{(temporary ? " until restart" : "")}.");
    }

    private static bool CallerCanTarget(CCitadelPlayerController caller, ResolvedPlayer who)
    {
        if (who.Slot >= 0)
            return PermissionManager.CanTargetSlots(caller.Slot, who.Slot);

        var callerSubject = PermissionManager.DescribeSlot(caller.Slot, out _);
        return PermissionManager.Describe(who.SteamId64).Immunity <= callerSubject.Immunity;
    }

    private readonly record struct ResolvedPlayer(ulong SteamId64, int Slot, string? Name)
    {
        public string Display => Name != null ? $"{Name} ({SteamId64})" : SteamId64.ToString();
    }

    /// <summary>A SteamID (online or not), or a connected player by <c>#slot</c> or name.</summary>
    private static ResolvedPlayer ResolvePlayer(CCitadelPlayerController? caller, string input)
    {
        if (SteamIds.TryParse(input, out var steamId64))
        {
            var online = Players.GetAll().FirstOrDefault(p => PermissionManager.GetSlotSteamId(p.Slot) == steamId64);
            return new ResolvedPlayer(steamId64, online?.Slot ?? -1, online?.PlayerName);
        }

        if (!TargetResolver.TryResolve(input, caller, enforceImmunity: false, out var target, out var error))
            throw new CommandException(error ?? $"No player matches '{input}'.");
        if (target!.IsGroup)
            throw new CommandException("Name one player: a SteamID, #slot or part of their name.");

        var player = target.Single();
        var id = PermissionManager.GetSlotSteamId(player.Slot);
        if (id == 0)
            throw new CommandException($"{player.PlayerName} is a bot and can't hold permissions.");
        return new ResolvedPlayer(id, player.Slot, player.PlayerName);
    }

    private static void Reply(CCitadelPlayerController? to, string message)
    {
        if (to != null)
            to.PrintToConsole(message);
        else
            Console.WriteLine(message);
    }
}
