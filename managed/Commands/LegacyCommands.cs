using System.Reflection;
using DeadworksManaged.Api;

namespace DeadworksManaged.Commands;

/// <summary>
/// [ChatCommand] and [ConCommand] are compile errors now. A plugin built against an older Deadworks can still carry
/// them; their commands would skip permission checks and overrides, so they aren't registered, and the console says so.
/// </summary>
internal static class LegacyCommands
{
    private static readonly string[] AttributeNames =
    [
        "DeadworksManaged.Api.ChatCommandAttribute",
        "DeadworksManaged.Api.ConCommandAttribute",
    ];

    public static void Report(IEnumerable<IDeadworksPlugin> plugins)
    {
        foreach (var plugin in plugins)
        {
            var methods = plugin.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var method in methods)
            {
                // Matched by name: the attribute types can't be referenced once they're compile errors.
                foreach (var attr in method.GetCustomAttributesData())
                {
                    if (!AttributeNames.Contains(attr.AttributeType.FullName))
                        continue;
                    var command = attr.ConstructorArguments.FirstOrDefault().Value as string ?? method.Name;
                    Console.WriteLine($"[PluginLoader] ERROR: {plugin.Name}.{method.Name} uses [{attr.AttributeType.Name.Replace("Attribute", "")}(\"{command}\")], "
                                      + "which is no longer supported, so the command was not registered. Rebuild the plugin with [Command].");
                }
            }
        }
    }
}
