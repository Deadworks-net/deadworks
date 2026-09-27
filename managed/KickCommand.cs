using DeadworksManaged.Api;

namespace DeadworksManaged;

/// <summary>
/// Backs <c>dw_kick &lt;slot&gt; [reason...]</c> for the launcher. Prints one <c>DWKICK ok|error</c> line.
/// The reason is accepted but not sent: the engine disconnect only carries an enum, not text.
/// </summary>
internal static class KickCommand
{
    public static void OnCommand(ConCommandContext ctx)
    {
        if (!TryParseSlot(ctx.Args, out var slot, out var error))
        {
            Console.WriteLine($"DWKICK error {(ctx.Args.Length > 1 ? ctx.Args[1] : "-")} {error}");
            return;
        }

        if (!Players.IsConnected(slot) || Players.FromSlot(slot) == null)
        {
            Console.WriteLine($"DWKICK error {slot} no player in slot");
            return;
        }

        Server.Kick(slot, ENetworkDisconnectionReason.NetworkDisconnectKicked);
        Console.WriteLine($"DWKICK ok {slot}");
    }

    /// <summary>Reads the slot from argv (<c>["dw_kick", slot, ...]</c>). Anything after it is the ignored reason.</summary>
    internal static bool TryParseSlot(string[] args, out int slot, out string error)
    {
        slot = -1;
        if (args.Length < 2)
        {
            error = "usage: dw_kick <slot> [reason]";
            return false;
        }
        if (!int.TryParse(args[1], out slot) || slot < 0 || slot >= Players.MaxSlot)
        {
            slot = -1;
            error = $"slot must be 0-{Players.MaxSlot - 1}";
            return false;
        }
        error = "";
        return true;
    }
}
