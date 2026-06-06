using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace DeadworksManaged;

/// <summary>
/// Host-side bridge for the UI subsystem (<see cref="DeadworksManaged.Api.UI"/>).
/// Two responsibilities, both invisible to plugin authors:
///
/// <list type="bullet">
///   <item>Register the <c>dw_ui</c> console command, which is the
///     client-&gt;server return channel for UI events (button clicks,
///     <c>DW.send</c> calls from Panorama).</item>
///   <item>Drive <see cref="UI.Tick"/> once per game frame from the host's
///     dispatch loop — see <see cref="PluginLoader.DispatchGameFrame"/>.</item>
/// </list>
///
/// Plugins just use the <see cref="DeadworksManaged.Api.UI"/> namespace —
/// no init, no manual tick, no ConCommand boilerplate.
/// </summary>
internal static class UIBootstrap
{
    public static void Initialize()
    {
        ConCommandManager.RegisterBuiltInCommand(
            name: "dw_ui",
            description: "UI return channel (Panorama → server events). Not for direct use.",
            serverOnly: false,
            handler: OnDwUi);
    }

    // Wire format (after the command name):
    //   <panel_id>|<event_name>|<arg0>|<arg1>...
    // Pipe is used (not \x1f) because console-command arg strings don't preserve
    // non-printable bytes reliably.
    private static void OnDwUi(ConCommandContext ctx)
    {
        if (ctx.Controller == null) return;
        var arg = ctx.ArgString;
        if (string.IsNullOrEmpty(arg)) return;
        var parts = arg.Split('|');
        if (parts.Length < 2) return;
        var panelId = parts[0];
        var eventName = parts[1];
        var args = parts.Length > 2 ? parts[2..] : Array.Empty<string>();
        UI.Panel(panelId).TryDispatch(new UIEvent(ctx.Controller, panelId, eventName, args));
    }
}
