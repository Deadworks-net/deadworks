using DeadworksManaged.Api;

namespace DeadworksManaged.AdminSystem;

/// <summary>Core console commands for penalties. Adding and lifting them is the Admin plugin's job.</summary>
internal sealed class PenaltyCommands : DeadworksPluginBase
{
    public override string Name => "Deadworks";

    [Command("penalties_reload", Description = "Reload bans, gags and mutes from their store", Permission = "deadworks.penalties.reload", ConsoleOnly = true)]
    public void PenaltiesReload(CCitadelPlayerController? caller)
    {
        var message = PenaltyManager.Reload()
            ? "Reloaded penalties."
            : "Failed to reload penalties; the server console has details. The previous ones are still in force.";
        if (caller != null)
            caller.PrintToConsole(message);
        else
            Console.WriteLine(message);
    }
}
