using DeadworksManaged.Api;

namespace DeadworksAdmin;

public sealed partial class AdminPlugin
{
    [Command("map", Description = "Change map: map <name>, or map to list them", Permission = Perm.Map, SuppressChat = true)]
    public void CmdMap(CCitadelPlayerController? caller, string map = "")
    {
        if (map.Length == 0)
        {
            ReplyLines(caller, Server.GetMapList().Select(m => $"  {m}").Prepend("Maps:"));
            return;
        }
        if (!Server.IsMapValid(map))
            throw new CommandException($"There's no map called '{map}'. Run map with no name to list them.");

        AdminActivity.Show(caller, $"is changing the map to {map} in {Config.MapChangeDelaySeconds}s");
        if (Config.MapChangeDelaySeconds == 0)
            Server.ChangeMap(map);
        else
            Timer.Once(Config.MapChangeDelaySeconds.Seconds(), () => Server.ChangeMap(map));
    }

    [Command("rcon", Description = "Run a server console command and see its output: rcon <command...>", Permission = Perm.Rcon, SuppressChat = true)]
    public void CmdRcon(CCitadelPlayerController? caller, params string[] command)
    {
        if (command.Length == 0)
            throw new CommandException("Give a command to run.");

        // The arguments arrive already split; quote the ones that had spaces so the command reads the same.
        var line = string.Join(' ', command.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        AdminActivity.Log(caller, $"ran rcon: {line}");

        if (caller == null)
        {
            Server.ExecuteCommand(line);
            return;
        }

        var slot = caller.Slot;
        var id = Permissions.GetSteamId(slot);
        Server.ExecuteCommand(line, output =>
        {
            // Only answer if the same player is still in that slot.
            if (Players.FromSlot(slot) is { } still && Permissions.GetSteamId(slot) == id)
                ReplyLines(still, output.Length > 0 ? output.Split('\n') : ["(no output)"]);
        });
    }

    [Command("cvar", Description = "Show or change a server setting: cvar <name> [value]", Permission = Perm.Cvar, SuppressChat = true)]
    public void CmdCvar(CCitadelPlayerController? caller, string name, params string[] value)
    {
        var cvar = FindCvar(caller, name);
        if (value.Length == 0)
        {
            Reply(caller, $"{cvar.Name} = \"{cvar.Value}\" (default \"{cvar.DefaultValue}\")");
            return;
        }
        SetCvar(caller, cvar, string.Join(' ', value));
    }

    [Command("resetcvar", Description = "Put a server setting back to its default: resetcvar <name>", Permission = Perm.Cvar, SuppressChat = true)]
    public void CmdResetCvar(CCitadelPlayerController? caller, string name)
    {
        var cvar = FindCvar(caller, name);
        SetCvar(caller, cvar, cvar.DefaultValue);
    }

    [Command("execcfg", Description = "Run a config file from cfg/: execcfg <file>", Permission = Perm.Config, SuppressChat = true)]
    public void CmdExecCfg(CCitadelPlayerController? caller, string file)
    {
        if (!IsSafeConfigName(file))
            throw new CommandException("Give a config file name inside cfg/, like server.cfg or events/lan.cfg.");
        Server.ExecuteCommand($"exec {file}");
        AdminActivity.Show(caller, $"ran the config {file}");
    }

    internal static bool IsSafeConfigName(string file)
        => file.Length is > 0 and <= 128
           && !file.Contains("..", StringComparison.Ordinal)
           && !file.StartsWith('/')
           && file.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/');

    private const string RconPassword = "rcon_password";

    /// <summary>The cvar, if the caller may touch it at all. The extra permissions are checked here too, so reading is gated like writing.</summary>
    private static ConVarEntry FindCvar(CCitadelPlayerController? caller, string name)
    {
        var cvar = Server.EnumerateConVars().Find(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new CommandException($"There's no cvar called '{name}'.");

        if (cvar.Name.Equals(RconPassword, StringComparison.OrdinalIgnoreCase))
            throw new CommandException($"{RconPassword} can't be read or changed here.");
        if (IsProtected(cvar) && !caller.HasPermission(Perm.CvarProtected))
            throw new CommandException($"{cvar.Name} is protected. You need {Perm.CvarProtected}.");
        return cvar;
    }

    private static void SetCvar(CCitadelPlayerController? caller, ConVarEntry cvar, string value)
    {
        if (cvar.Name.Equals("sv_cheats", StringComparison.OrdinalIgnoreCase) && !caller.HasPermission(Perm.CvarCheats))
            throw new CommandException($"Changing sv_cheats needs {Perm.CvarCheats}.");

        var handle = ConVar.Find(cvar.Name) ?? throw new CommandException($"There's no cvar called '{cvar.Name}'.");
        if (!handle.SetString(value))
            throw new CommandException($"{cvar.Name} didn't accept \"{value}\".");

        if (IsProtected(cvar))
            AdminActivity.Log(caller, $"changed the protected cvar {cvar.Name}");
        else
            AdminActivity.Show(caller, $"set {cvar.Name} to \"{value}\"");
        Reply(caller, $"{cvar.Name} is now \"{(IsProtected(cvar) ? "(hidden)" : value)}\".");
    }

    private static bool IsProtected(ConVarEntry cvar) => (cvar.Flags & (ulong)FCVar.Protected) != 0;
}
