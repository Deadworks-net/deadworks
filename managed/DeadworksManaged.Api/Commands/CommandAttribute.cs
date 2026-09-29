namespace DeadworksManaged.Api;

/// <summary>Registers a method as <c>/name</c>, <c>!name</c>, and <c>dw_name</c> with typed parameter binding.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CommandAttribute : Attribute
{
    /// <summary>The command's name followed by its aliases, without a prefix.</summary>
    public string[] Names { get; }

    /// <summary>One line for <c>dw_help</c> and the generated permission files, e.g. "Kick a player: kick &lt;player&gt; [reason]".</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Permission a player needs to run this command, e.g. <c>admin.moderation.ban</c>. Empty means anyone.
    /// The server console always may. Server owners can change it in <c>configs/permissions/overrides.jsonc</c>.
    /// </summary>
    public string Permission { get; set; } = "";

    /// <summary>Whether <see cref="Target"/> parameters leave out players the caller can't target.</summary>
    public TargetImmunity TargetImmunity { get; set; } = TargetImmunity.Auto;

    /// <summary>Lets only the server console (and RCON) run the command, whatever permissions a player has.</summary>
    public bool ServerOnly { get; set; }

    /// <summary>Creates only <c>/name</c> and <c>!name</c>. Not even the server console can run it then.</summary>
    public bool ChatOnly { get; set; }

    /// <summary>
    /// Creates only <c>dw_name</c>. Players can still run it from their own game console, so use
    /// <see cref="Permission"/> or <see cref="ServerOnly"/> to keep it from them.
    /// </summary>
    public bool ConsoleOnly { get; set; }

    /// <summary>Hides <c>!name</c> from chat, like <c>/name</c>.</summary>
    public bool SuppressChat { get; set; }

    /// <summary>Leaves the command out of <c>dw_help</c>.</summary>
    public bool Hidden { get; set; }

    /// <param name="name">The command's name, without a prefix: <c>"kick"</c> creates <c>/kick</c>, <c>!kick</c> and <c>dw_kick</c>.</param>
    /// <param name="aliases">Other names for the same command, created the same way.</param>
    public CommandAttribute(string name, params string[] aliases)
    {
        Names = [name, .. aliases];
    }
}
