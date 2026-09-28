using DeadworksManaged.Api;
using DeadworksManaged.Commands;
using Xunit;

namespace DeadworksManaged.Tests;

/// <summary>
/// Drives <see cref="CommandAttribute"/> dispatch with arguments shaped the way each entry point hands
/// them over - argv the engine already tokenized for <c>dw_</c> console commands, raw text for chat.
/// Either way a double-quoted run reaches the plugin as one argument and nothing else merges.
/// </summary>
public class CommandDispatchTests
{
    private const string PluginPath = "test://CommandDispatchTests";

    private sealed class RecordingPlugin : DeadworksPluginBase
    {
        public override string Name => nameof(RecordingPlugin);
        public List<string> Received { get; } = [];

        [Command("stringtestcommand")]
        public void TestString(string string1) => Received.Add(string1);

        [Command("paramstestcommand")]
        public void TestParams(params string[] args) => Received.AddRange(args);
    }

    /// <summary>
    /// <c>dw_stringtestcommand "one two"</c> arrives as argv <c>["dw_stringtestcommand", "one two"]</c>:
    /// CCommand groups the quoted run and strips the quotes. Re-tokenizing a space-joined copy of that
    /// argv split it back into two tokens, so the single string parameter got the usage line instead.
    /// </summary>
    [Fact]
    public void QuotedConsoleArgumentBindsToSingleStringParameter()
    {
        var plugin = DispatchConsole("dw_stringtestcommand", "one two");
        Assert.Equal("one two", Assert.Single(plugin.Received));
    }

    public static TheoryData<string[]> EngineArgv => new()
    {
        new[] { "foo bar", "baz bing" },       // dw_paramstestcommand "foo bar" "baz bing"
        new[] { "foo", "bar", "baz", "bing" }, // dw_paramstestcommand foo bar baz bing
    };

    [Theory]
    [MemberData(nameof(EngineArgv))]
    public void ConsoleArgumentsBindAsTheEngineTokenizedThem(string[] argv)
    {
        var plugin = DispatchConsole("dw_paramstestcommand", argv);
        Assert.Equal(argv, plugin.Received);
    }

    [Theory]
    [InlineData("/paramstestcommand \"foo bar\" \"baz bing\"", new[] { "foo bar", "baz bing" })]
    [InlineData("/paramstestcommand foo bar baz bing", new[] { "foo", "bar", "baz", "bing" })]
    [InlineData("!paramstestcommand \"foo  bar\"\tbaz", new[] { "foo  bar", "baz" })]
    public void ChatArgumentsGroupOnDoubleQuotes(string chatText, string[] expected)
    {
        Assert.True(PluginLoader.TryParseChatCommand(chatText, out var prefix, out var commandName, out var args));
        Assert.Equal("paramstestcommand", commandName);
        Assert.Equal(expected, args); // exactly what ChatCommandContext.Args exposes

        // Chat always comes from a player, and a test has no player controller to run it as, so bind the
        // tokenized arguments through the console path instead.
        var plugin = DispatchConsole("dw_paramstestcommand", [.. args]);
        Assert.Equal(expected, plugin.Received);
    }

    private static RecordingPlugin DispatchConsole(string command, params string[] args) =>
        WithRegisteredPlugin(_ => ConCommandManager.Dispatch(-1, command, [command, .. args]));

    private static RecordingPlugin WithRegisteredPlugin(
        Action<HandlerRegistry<string, Func<ChatCommandContext, HookResult>>> dispatch)
    {
        var plugin = new RecordingPlugin();
        var chatRegistry = new HandlerRegistry<string, Func<ChatCommandContext, HookResult>>(StringComparer.OrdinalIgnoreCase);
        CommandRegistration.RegisterPluginCommands(PluginPath, [plugin], chatRegistry);
        try
        {
            dispatch(chatRegistry);
        }
        finally
        {
            ConCommandManager.UnregisterPlugin(PluginPath);
            PluginRegistrationTracker.Remove(PluginPath);
        }
        return plugin;
    }
}
