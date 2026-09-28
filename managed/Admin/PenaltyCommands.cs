using DeadworksManaged.Api;

namespace DeadworksManaged.AdminSystem;

/// <summary>Core console commands for penalties. Adding and lifting them is the Admin plugin's job.</summary>
internal sealed class PenaltyCommands : DeadworksPluginBase
{
    public override string Name => "Deadworks";

    [Command("penalties_reload", Description = "Reload bans, gags and mutes from their store", Permission = "deadworks.penalties.reload", ConsoleOnly = true)]
    public void PenaltiesReload(Caller caller)
    {
        var ok = PenaltyManager.Reload();
        AdminActivity.Log(caller, ok ? "reloaded penalties" : "tried to reload penalties, which failed");
        caller.PrintToConsole(ok
            ? "Reloaded penalties."
            : $"Failed to reload penalties: {PenaltyManager.LastLoadError?.TrimEnd('.') ?? "the server console has details"}. The previous ones are still in force.");
    }
}
