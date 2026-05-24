using DeadworksManaged.Api;

namespace DeadworksManaged;

/// <summary>
/// Retrofits FCVAR_CHEAT onto server-side ConCommands that ship without it but enable
/// cheats / sandbox state at runtime. The engine itself then refuses to dispatch them
/// when sv_cheats == 0, on every invocation path (client cmd, server console, rcon,
/// point_servercommand, .cfg exec).
///
/// Target list:
///  - spawn_hero_testing_controller   (CCitadel_Hud_HeroTest entity / hero-testing sandbox)
///  - spawn_citadel_tutorial_controller (CInfoTutorialController / tutorial-sandbox)
///
/// Both are released without FCVAR_CHEAT, gated only by CCitadelServerGCSystem's
/// "is a Valve GC lobby bound?" check — which fails on every community-hosted dedi,
/// allowing the spawned entity to flip the server into sandbox mode and enable cheats.
/// </summary>
internal static class CheatCommandGate
{
    private const ulong FCVAR_CHEAT = 0x4000;

    private static readonly string[] _commands =
    {
        "spawn_hero_testing_controller",
        "spawn_citadel_tutorial_controller",
    };

    public static unsafe void ApplyCheatFlag()
    {
        if (NativeInterop.AddConCommandFlags == null)
        {
            Console.WriteLine("[CheatCommandGate] AddConCommandFlags not bound; skipping");
            return;
        }

        foreach (var name in _commands)
        {
            Span<byte> utf8 = Utf8.Encode(name, stackalloc byte[Utf8.Size(name)]);
            byte ok;
            fixed (byte* ptr = utf8)
                ok = NativeInterop.AddConCommandFlags(ptr, FCVAR_CHEAT);

            Console.WriteLine(ok != 0
                ? $"[CheatCommandGate] FCVAR_CHEAT applied to '{name}'"
                : $"[CheatCommandGate] '{name}' not registered, nothing to gate");
        }
    }
}
