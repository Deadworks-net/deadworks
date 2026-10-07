using DeadworksManaged.Api;
using DeadworksManaged.Game;

namespace DeadworksManaged;

/// <summary>
/// <c>dw_schema_verify</c>: looks every generated schema field up in the running game and
/// lists the ones it does not have. Run it after a game update to see what
/// DeadworksManaged.Game, generated from an earlier build, can no longer reach.
/// </summary>
internal static class SchemaCommands
{
    private const int MaxListed = 200;

    public static void Initialize()
    {
        ConCommandManager.RegisterBuiltInCommand(
            name: "dw_schema_verify",
            description: "Check every generated schema field against the running game.",
            serverOnly: true,
            handler: OnVerify);
    }

    private static void OnVerify(ConCommandContext ctx)
    {
        var report = SchemaVerifier.Run();
        Console.WriteLine($"[Schema] Generated from build {GameBuild.Version}: {report.Classes} classes, {report.Fields} fields, {report.Missing.Count} missing in the running game.");
        foreach (var field in report.Missing.Take(MaxListed))
            Console.WriteLine($"[Schema]   missing {field}");
        if (report.Missing.Count > MaxListed)
            Console.WriteLine($"[Schema]   ... and {report.Missing.Count - MaxListed} more");
    }
}
