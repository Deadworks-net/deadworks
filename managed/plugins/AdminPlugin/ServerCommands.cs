using DeadworksManaged.Api;

namespace DeadworksAdmin;

public sealed partial class AdminPlugin
{
    [Command("map", Description = "Change map: map <name>, or map to list them", Permission = Perm.Map, SuppressChat = true)]
    public void CmdMap(Caller caller, string map = "")
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
    public void CmdRcon(Caller caller, params string[] command)
    {
        if (command.Length == 0)
            throw new CommandException("Give a command to run.");

        // The arguments arrive already split; quote the ones that had spaces so the command reads the same. A single
        // argument is a whole command someone quoted (dw_rcon "sv_cheats 1"), so it runs as typed.
        var line = command.Length == 1
            ? command[0]
            : string.Join(' ', command.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        AdminActivity.Log(caller, $"ran rcon: {Redact(line)}");

        if (caller.Player is not { } player)
        {
            Server.ExecuteCommand(line);
            return;
        }

        var slot = player.Slot;
        var id = caller.SteamId64;
        Server.ExecuteCommand(line, output =>
        {
            // Only answer if the same player is still in that slot.
            if (Players.FromSlot(slot) is { } still && Permissions.GetSteamId(slot) == id)
                ReplyLines(Caller.Of(still), output.Length > 0 ? output.Split('\n') : ["(no output)"]);
        });
    }

    [Command("cvar", Description = "Show or change a server setting: cvar <name> [value]", Permission = Perm.Cvar, SuppressChat = true)]
    public void CmdCvar(Caller caller, string name, params string[] value)
    {
        var cvar = FindCvar(name);
        if (value.Length == 0)
        {
            caller.Reply($"{cvar.Name} = \"{cvar.Value}\" (default \"{cvar.DefaultValue}\")");
            return;
        }
        SetCvar(caller, cvar, string.Join(' ', value));
    }

    [Command("resetcvar", Description = "Put a server setting back to its default: resetcvar <name>", Permission = Perm.Cvar, SuppressChat = true)]
    public void CmdResetCvar(Caller caller, string name)
    {
        var cvar = FindCvar(name);
        SetCvar(caller, cvar, cvar.DefaultValue);
    }

    [Command("execcfg", Description = "Run a config file from cfg/: execcfg <file>", Permission = Perm.Config, SuppressChat = true)]
    public void CmdExecCfg(Caller caller, string file)
    {
        if (!IsSafeConfigName(file))
            throw new CommandException("Give a config file name inside cfg/, like server.cfg or events/lan.cfg.");
        var name = file.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) ? file : file + ".cfg";
        if (!File.Exists(Path.Combine(ConfigDir, name)))
            throw new CommandException($"There's no cfg/{name}.");
        Server.ExecuteCommand($"exec {file}");
        AdminActivity.Show(caller, $"ran the config {file}");
    }

    /// <summary><c>game/citadel/cfg</c>, found from the managed folder (<c>game/bin/win64/managed</c>).</summary>
    private static string ConfigDir => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(Server).Assembly.Location) ?? ".", "..", "..", "..", "citadel", "cfg"));

    internal static bool IsSafeConfigName(string file)
        => file.Length is > 0 and <= 128
           && !file.Contains("..", StringComparison.Ordinal)
           && !file.StartsWith('/')
           && file.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/');

    private const string RconPassword = "rcon_password";

    /// <summary>The cvar, unless it's rcon_password, which would hand out the whole server.</summary>
    private static ConVarEntry FindCvar(string name)
    {
        var cvar = Server.EnumerateConVars().Find(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new CommandException($"There's no cvar called '{name}'.");

        if (cvar.Name.Equals(RconPassword, StringComparison.OrdinalIgnoreCase))
            throw new CommandException($"{RconPassword} can't be read or changed here.");
        return cvar;
    }

    private static void SetCvar(Caller caller, ConVarEntry cvar, string value)
    {
        var handle = ConVar.Find(cvar.Name) ?? throw new CommandException($"There's no cvar called '{cvar.Name}'.");
        if (!handle.SetString(value))
            throw new CommandException($"{cvar.Name} didn't accept \"{value}\".");

        // Password cvars are logged without the value and not announced, so they don't end up in everyone's chat.
        // Otherwise the announcement is the caller's answer too.
        if (IsProtected(cvar))
        {
            AdminActivity.Log(caller, $"changed the protected cvar {cvar.Name}");
            caller.Reply($"{cvar.Name} is now \"(hidden)\".");
        }
        else
        {
            AdminActivity.Show(caller, $"set {cvar.Name} to \"{value}\"");
        }
    }

    private static bool IsProtected(ConVarEntry cvar) => (cvar.Flags & (ulong)FCVar.Protected) != 0;

    /// <summary>
    /// The rcon line for the admin log, without the value if it sets a password cvar: the log is a file and is often
    /// forwarded to Discord.
    /// </summary>
    internal static string Redact(string line, Func<string, bool>? isSecret = null)
    {
        isSecret ??= n => n.Equals(RconPassword, StringComparison.OrdinalIgnoreCase)
                          || Server.EnumerateConVars().Find(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } cvar && IsProtected(cvar);

        // The console runs each ;-separated command, so each one is checked.
        return string.Join(';', line.Split(';').Select(command =>
        {
            var name = command.TrimStart().Split(' ', 2)[0].Trim('"');
            return name.Length > 0 && command.Trim().Length > name.Length && isSecret(name)
                ? $"{(command.StartsWith(' ') ? " " : "")}{name} (value hidden)"
                : command;
        }));
    }
}
