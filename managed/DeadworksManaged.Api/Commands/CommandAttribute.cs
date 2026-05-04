namespace DeadworksManaged.Api;

/// <summary>Registers a method as <c>/name</c>, <c>!name</c>, and <c>dw_name</c> with typed parameter binding.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CommandAttribute : Attribute
{
    public string[] Names { get; }
    public string Description { get; set; } = "";

    /// <summary>Permission required for player callers. Server console callers bypass this check.</summary>
    public string Permission { get; set; } = "";

    /// <summary>Refuses execution from any player caller.</summary>
    public bool ServerOnly { get; set; }

    /// <summary>Skip the <c>dw_name</c> console command.</summary>
    public bool ChatOnly { get; set; }

    /// <summary>Skip the <c>/name</c> and <c>!name</c> chat commands.</summary>
    public bool ConsoleOnly { get; set; }

    /// <summary>Force the <c>!name</c> invocation to be hidden from chat broadcast.</summary>
    public bool SuppressChat { get; set; }

    /// <summary>Exclude from the <c>dw_help</c> listing.</summary>
    public bool Hidden { get; set; }

    public CommandAttribute(string name, params string[] aliases)
    {
        Names = [name, .. aliases];
    }
}
