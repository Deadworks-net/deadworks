using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

        [Command("throwingtestcommand")]
        public void Throwing() => throw new InvalidOperationException("plugin bug");

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Command("asynctestcommand")]
        public async Task Async(string mode)
        {
            await Gate.Task;
            if (mode == "refuse")
                throw new CommandException("Not now.");
            throw new InvalidOperationException("plugin bug after await");
        }

        [Command("threadtestcommand")]
        public async Task ThreadCheck()
        {
            await Task.Delay(10);
            Received.Add(Environment.CurrentManagedThreadId.ToString());
        }

        [Command("multiawaittestcommand")]
        public async Task MultiAwait()
        {
            await Task.Delay(10);
            Received.Add($"after 1: {Environment.CurrentManagedThreadId}");
            await Task.Delay(10);
            Received.Add($"after 2: {Environment.CurrentManagedThreadId}");
            await Task.Delay(10);
            Received.Add($"after 3: {Environment.CurrentManagedThreadId}");
        }

        [Command("nestedawaittestcommand")]
        public async Task NestedAwait()
        {
            await LoadAsync();
            await SaveAsync();
            Received.Add($"done: {Environment.CurrentManagedThreadId}");
        }

        private async Task LoadAsync()
        {
            await Task.Delay(10);
            Received.Add($"loaded: {Environment.CurrentManagedThreadId}");
        }

        private async Task SaveAsync()
        {
            await Task.Delay(10);
            Received.Add($"saved: {Environment.CurrentManagedThreadId}");
        }

        [Command("valuetasktestcommand")]
        public async ValueTask ValueTaskRefusal()
        {
            await Task.Yield();
            throw new CommandException("Refused after a ValueTask await.");
        }

        [Command("asyncvoidtestcommand")]
        public async void AsyncVoid() => await Task.Yield();

        [Command("typestestcommand")]
        public void Types(ulong id, Colour colour, int? count = null) => Received.Add($"{id} {colour} {count?.ToString() ?? "none"}");

        [Command("coordtestcommand")]
        public void Coords(Coord at) => Received.Add($"{at.X},{at.Y}");

        [Command("playeronlytestcommand")]
        public void PlayerOnly(CCitadelPlayerController player) => Received.Add(player.PlayerName);

        [Command("nullablecallertestcommand")]
        public void NullableCaller(CCitadelPlayerController? player) => Received.Add(player == null ? "console" : player.PlayerName);

        [Command("unparseabletestcommand")]
        public void Unparseable(TimeSpan length) => Received.Add(length.ToString());
    }

    public enum Colour { Red, Blue }

    public readonly record struct Coord(int X, int Y);

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

    [Fact]
    public void A_command_that_throws_tells_the_caller_it_failed_and_logs_why()
    {
        var output = CaptureConsole(() => DispatchConsole("dw_throwingtestcommand"));
        Assert.Contains(CommandRegistration.FailedMessage, output);
        Assert.Contains("RecordingPlugin: command 'dw_throwingtestcommand' threw System.InvalidOperationException: plugin bug", output);
    }

    [Theory]
    [InlineData("refuse", "Not now.")]
    [InlineData("crash", "plugin bug after await")]
    public void An_async_command_is_followed_to_the_end_on_the_game_thread(string mode, string expected)
    {
        var output = CaptureConsole(() => WithRegisteredPlugin(plugin =>
        {
            ConCommandManager.Dispatch(-1, "dw_asynctestcommand", ["dw_asynctestcommand", mode]);
            plugin.Gate.SetResult();
            // The failure is reported on a later tick, never from the thread pool.
            for (var i = 0; i < 200 && !Console.Out.ToString()!.Contains(expected); i++)
            {
                Thread.Sleep(5);
                TimerEngine.OnTick();
            }
        }));
        Assert.Contains(expected, output);
    }

    [Fact]
    public void Code_after_an_await_in_a_command_runs_on_the_game_thread()
    {
        var plugin = WithRegisteredPlugin(p =>
        {
            ConCommandManager.Dispatch(-1, "dw_threadtestcommand", ["dw_threadtestcommand"]);
            for (var i = 0; i < 200 && p.Received.Count == 0; i++)
            {
                Thread.Sleep(5);
                TimerEngine.OnTick(); // this thread plays the game thread
            }
        });
        Assert.Equal(Environment.CurrentManagedThreadId.ToString(), Assert.Single(plugin.Received));
    }

    /// <summary>
    /// Every <c>await</c> in a command must come back on the game thread, not just the first: each resume posts
    /// the next continuation, so the context has to still be current when the continuation runs.
    /// </summary>
    [Fact]
    public void Code_after_every_await_in_a_command_runs_on_the_game_thread()
    {
        var plugin = RunUntil("dw_multiawaittestcommand", 3);
        var game = Environment.CurrentManagedThreadId;
        Assert.Equal([$"after 1: {game}", $"after 2: {game}", $"after 3: {game}"], plugin.Received);
    }

    [Fact]
    public void Awaits_inside_helper_methods_a_command_calls_run_on_the_game_thread()
    {
        var plugin = RunUntil("dw_nestedawaittestcommand", 3);
        var game = Environment.CurrentManagedThreadId;
        Assert.Equal([$"loaded: {game}", $"saved: {game}", $"done: {game}"], plugin.Received);
    }

    private RecordingPlugin RunUntil(string command, int entries) => WithRegisteredPlugin(p =>
    {
        ConCommandManager.Dispatch(-1, command, [command]);
        for (var i = 0; i < 400 && p.Received.Count < entries; i++)
        {
            Thread.Sleep(5);
            TimerEngine.OnTick(); // this thread plays the game thread
        }
    });

    [Fact]
    public void A_value_task_command_is_followed_like_a_task()
    {
        var output = CaptureConsole(() => WithRegisteredPlugin(_ =>
        {
            ConCommandManager.Dispatch(-1, "dw_valuetasktestcommand", ["dw_valuetasktestcommand"]);
            for (var i = 0; i < 200 && !Console.Out.ToString()!.Contains("Refused after"); i++)
            {
                Thread.Sleep(5);
                TimerEngine.OnTick();
            }
        }));
        Assert.Contains("Refused after a ValueTask await.", output);
    }

    [Fact]
    public void Async_void_commands_are_refused_because_they_can_crash_the_server()
    {
        var output = CaptureConsole(() => WithRegisteredPlugin(_ => Assert.False(ConCommandManager.IsRegistered("dw_asyncvoidtestcommand"))));
        Assert.Contains("AsyncVoid is async void", output);
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("de-DE")]
    public void Decimals_parse_the_same_whatever_the_servers_locale(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
        try
        {
            Assert.Equal(1.5f, ConCommandManager.ConvertValue("1.5", typeof(float)));
            Assert.Equal(2.25, ConCommandManager.ConvertValue("2.25", typeof(double)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Steam_ids_enums_and_optional_numbers_bind()
    {
        var plugin = DispatchConsole("dw_typestestcommand", "76561197960287930", "blue", "3");
        Assert.Equal("76561197960287930 Blue 3", Assert.Single(plugin.Received));
        Assert.Equal("76561197960287930 Red none", Assert.Single(DispatchConsole("dw_typestestcommand", "76561197960287930", "red").Received));
    }

    [Fact]
    public void An_enum_number_it_doesnt_have_is_refused()
    {
        string? output = null;
        var plugin = WithRegisteredPlugin(_ => output = CaptureConsole(() =>
            ConCommandManager.Dispatch(-1, "dw_typestestcommand", ["dw_typestestcommand", "1", "99"])));
        Assert.Empty(plugin.Received);
        Assert.Contains("Usage: dw_typestestcommand <id> <red|blue> [count]", output);
    }

    [Fact]
    public void A_converter_can_explain_what_is_wrong()
    {
        CommandConverters.Register<Coord>(s => s.Split(',') is [var x, var y] && int.TryParse(x, out var xi) && int.TryParse(y, out var yi)
            ? new Coord(xi, yi)
            : throw new CommandException($"'{s}' isn't x,y."));
        try
        {
            Assert.Equal("3,4", Assert.Single(DispatchConsole("dw_coordtestcommand", "3,4").Received));
            string? output = null;
            WithRegisteredPlugin(_ => output = CaptureConsole(() => ConCommandManager.Dispatch(-1, "dw_coordtestcommand", ["dw_coordtestcommand", "nope"])));
            Assert.Contains("'nope' isn't x,y.", output);
        }
        finally
        {
            CommandConverters.Unregister<Coord>();
        }
    }

    [Fact]
    public void A_converter_for_a_type_Deadworks_parses_is_refused()
    {
        Assert.Throws<ArgumentException>(() => CommandConverters.Register<int>(int.Parse));
        Assert.Throws<ArgumentException>(() => CommandConverters.Register<Caller>(_ => Caller.Console));
        Assert.Throws<ArgumentException>(() => CommandConverters.Register<PenaltyType?>(_ => null));
    }

    [Fact]
    public void The_console_is_told_when_a_command_is_for_players_only()
    {
        string? output = null;
        var plugin = WithRegisteredPlugin(_ => output = CaptureConsole(() => ConCommandManager.Dispatch(-1, "dw_playeronlytestcommand", ["dw_playeronlytestcommand"])));
        Assert.Empty(plugin.Received);
        Assert.Contains("Only players can run this command.", output);
    }

    [Fact]
    public void A_nullable_controller_caller_is_flagged_at_load()
        => Assert.Contains("NullableCaller take a CCitadelPlayerController? caller", CaptureConsole(() => WithRegisteredPlugin(_ => { })));

    [Fact]
    public void A_parameter_type_nothing_can_parse_is_reported_at_load()
    {
        var output = CaptureConsole(() => WithRegisteredPlugin(_ => { }));
        Assert.Contains("Unparseable: parameter 'length' is a TimeSpan, which Deadworks can't parse", output);
    }

    private static string CaptureConsole(Action action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }

    /// <summary>The block result comes back immediately; the method itself runs next tick.</summary>
    [Theory]
    [InlineData("/stringtestcommand hello", HookResult.Handled)]
    [InlineData("!stringtestcommand hello", HookResult.Continue)]
    public unsafe void ChatCommandBlocksImmediatelyAndRunsNextTick(string chatText, HookResult expected)
    {
        Assert.True(PluginLoader.TryParseChatCommand(chatText, out var prefix, out var commandName, out var args));
        var message = new ChatMessage { SenderSlot = FakePlayerSlot, ChatText = chatText, AllChat = true, LaneColor = default };
        var ctx = new ChatCommandContext(message, commandName, args, prefix);

        // A chat command with no player behind it is refused, so the sender has to resolve to a controller. These two
        // callbacks are all a controller needs to exist; nothing on this path reads through it, since the command
        // needs no permission and takes no caller.
        var callbacks = default(NativeCallbacks);
        callbacks.GetPlayerController = (nint)(delegate* unmanaged[Cdecl]<int, void*>)&FakeGetPlayerController;
        callbacks.GetEntityHandle = (nint)(delegate* unmanaged[Cdecl]<void*, uint>)&FakeGetEntityHandle;
        NativeInterop.Bind(&callbacks);
        try
        {
            var results = new List<HookResult>();
            var plugin = WithRegisteredPlugin((_, chatRegistry) =>
            {
                foreach (var handler in chatRegistry.Snapshot(ctx.Command) ?? [])
                    results.Add(handler(ctx));
            });

            Assert.Equal(expected, Assert.Single(results));
            Assert.Empty(plugin.Received);

            TimerEngine.OnTick();
            Assert.Equal("hello", Assert.Single(plugin.Received));
        }
        finally
        {
            var none = default(NativeCallbacks);
            NativeInterop.Bind(&none);
        }
    }

    [Fact]
    public void A_chat_command_with_no_player_behind_it_is_refused()
    {
        Assert.True(PluginLoader.TryParseChatCommand("!stringtestcommand hello", out var prefix, out var commandName, out var args));
        var message = new ChatMessage { SenderSlot = -1, ChatText = "!stringtestcommand hello", AllChat = true, LaneColor = default };
        var ctx = new ChatCommandContext(message, commandName, args, prefix);

        var results = new List<HookResult>();
        var plugin = WithRegisteredPlugin((_, chatRegistry) =>
        {
            foreach (var handler in chatRegistry.Snapshot(ctx.Command) ?? [])
                results.Add(handler(ctx));
            TimerEngine.OnTick();
        });

        Assert.Equal(HookResult.Handled, Assert.Single(results)); // never echoed to chat
        Assert.Empty(plugin.Received);
    }

    private const int FakePlayerSlot = 3;

    // Never dereferenced: it only has to be non-null, and FakeGetEntityHandle answers for it.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void* FakeGetPlayerController(int slot) => slot == FakePlayerSlot ? (void*)0x1000 : null;

    // A controller's entity index is its slot plus one.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint FakeGetEntityHandle(void* entity) => FakePlayerSlot + 1;

    private static RecordingPlugin DispatchConsole(string command, params string[] args) =>
        WithRegisteredPlugin(_ => ConCommandManager.Dispatch(-1, command, [command, .. args]));

    private static RecordingPlugin WithRegisteredPlugin(Action<RecordingPlugin> dispatch)
        => WithRegisteredPlugin((plugin, _) => dispatch(plugin));

    private static RecordingPlugin WithRegisteredPlugin(
        Action<RecordingPlugin, HandlerRegistry<string, Func<ChatCommandContext, HookResult>>> dispatch)
    {
        var plugin = new RecordingPlugin();
        var chatRegistry = new HandlerRegistry<string, Func<ChatCommandContext, HookResult>>(StringComparer.OrdinalIgnoreCase);
        CommandRegistration.RegisterPluginCommands(PluginPath, [plugin], chatRegistry);
        try
        {
            dispatch(plugin, chatRegistry);
        }
        finally
        {
            ConCommandManager.UnregisterPlugin(PluginPath);
            PluginRegistrationTracker.Remove(PluginPath);
        }
        return plugin;
    }
}
