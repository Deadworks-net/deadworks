using System.Text;
using DeadworksManaged;
using DeadworksManaged.Api;
using Xunit;

namespace DeadworksManaged.Tests;

internal sealed class EventRecorder
{
    public int Calls;
    public EventContext? LastContext;
    public object? LastPayload;
}

/// <summary>Wires PluginBus through PluginLoader's dispatcher and tears down between tests.</summary>
internal static class PluginBusFixture
{
    public static void Setup() { PluginLoader.InitializePluginBusForTests(); }
    public static void Teardown() { PluginLoader.ResetPluginBusForTests(); }
}

// Test classes below share static state on PluginLoader/PluginBus; the collection serializes them.
[CollectionDefinition(nameof(PluginBusCollection), DisableParallelization = true)]
public sealed class PluginBusCollection { }

[Collection(nameof(PluginBusCollection))]
public class PluginBusEventDispatchTests
{
    [Fact]
    public void Publish_with_no_subscribers_returns_Continue()
    {
        PluginBusFixture.Setup();
        try { Assert.Equal(HookResult.Continue, PluginBus.Publish("nothing:here")); }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void Subscribe_context_Action_receives_payload_and_name()
    {
        PluginBusFixture.Setup();
        try
        {
            var rec = new EventRecorder();
            PluginBus.Subscribe("x:foo", ctx => { rec.Calls++; rec.LastContext = ctx; });

            PluginBus.Publish("x:foo", "hello");

            Assert.Equal(1, rec.Calls);
            Assert.Equal("x:foo", rec.LastContext!.Name);
            Assert.Equal("hello", rec.LastContext.Payload);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void Subscribe_context_Func_aggregates_HookResult_max_wins()
    {
        PluginBusFixture.Setup();
        try
        {
            PluginBus.Subscribe("x:foo", _ => HookResult.Continue);
            PluginBus.Subscribe("x:foo", _ => HookResult.Stop);
            PluginBus.Subscribe("x:foo", _ => HookResult.Continue);

            var result = PluginBus.Publish("x:foo");
            Assert.Equal(HookResult.Stop, result);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void Subscribe_typed_skips_on_payload_mismatch()
    {
        PluginBusFixture.Setup();
        try
        {
            var rec = new EventRecorder();
            PluginBus.Subscribe<string>("x:foo", s => { rec.Calls++; rec.LastPayload = s; });

            PluginBus.Publish("x:foo", 42);            // int — skip
            PluginBus.Publish("x:foo");                // null — skip
            PluginBus.Publish("x:foo", "hello");       // match

            Assert.Equal(1, rec.Calls);
            Assert.Equal("hello", rec.LastPayload);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void Subscribe_typed_Func_fires_and_returns_HookResult()
    {
        PluginBusFixture.Setup();
        try
        {
            PluginBus.Subscribe<string>("x:foo", _ => HookResult.Handled);
            Assert.Equal(HookResult.Handled, PluginBus.Publish("x:foo", "hi"));
            Assert.Equal(HookResult.Continue, PluginBus.Publish("x:foo", 123));
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void IHandle_Cancel_removes_subscription()
    {
        PluginBusFixture.Setup();
        try
        {
            var rec = new EventRecorder();
            var handle = PluginBus.Subscribe("x:foo", _ => rec.Calls++);

            PluginBus.Publish("x:foo");
            Assert.Equal(1, rec.Calls);

            handle.Cancel();
            PluginBus.Publish("x:foo");
            Assert.Equal(1, rec.Calls);
            Assert.Equal(0, PluginBus.SubscriberCount("x:foo"));
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void HasSubscribers_and_SubscriberCount_reflect_state()
    {
        PluginBusFixture.Setup();
        try
        {
            Assert.False(PluginBus.HasSubscribers("x:foo"));
            Assert.Equal(0, PluginBus.SubscriberCount("x:foo"));

            var h1 = PluginBus.Subscribe("x:foo", _ => { });
            var h2 = PluginBus.Subscribe("x:foo", _ => { });

            Assert.True(PluginBus.HasSubscribers("x:foo"));
            Assert.Equal(2, PluginBus.SubscriberCount("x:foo"));

            h1.Cancel();
            Assert.Equal(1, PluginBus.SubscriberCount("x:foo"));

            h2.Cancel();
            Assert.False(PluginBus.HasSubscribers("x:foo"));
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void Handler_exception_does_not_stop_subsequent_handlers()
    {
        PluginBusFixture.Setup();
        try
        {
            var secondFired = false;
            PluginBus.Subscribe("x:foo", _ => throw new InvalidOperationException("boom"));
            PluginBus.Subscribe("x:foo", _ => secondFired = true);

            PluginBus.Publish("x:foo");
            Assert.True(secondFired);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void PayloadAs_returns_cast_on_match_and_null_on_mismatch()
    {
        PluginBusFixture.Setup();
        try
        {
            EventContext? captured = null;
            PluginBus.Subscribe("x:foo", ctx => { captured = ctx; });

            PluginBus.Publish("x:foo", "str");
            Assert.Equal("str", captured!.PayloadAs<string>());
            Assert.Null(captured.PayloadAs<StringBuilder>());
        }
        finally { PluginBusFixture.Teardown(); }
    }
}

[Collection(nameof(PluginBusCollection))]
public class PluginBusEventAttributeTests
{
    private const string PluginPath = "/test/plugin_a.dll";

    internal sealed class RecordingPlugin : IDeadworksPlugin
    {
        public string Name => "RecordingPlugin";
        public void OnLoad(bool isReload) { }
        public void OnUnload() { }

        public int BareCalls;
        public int ContextCalls;
        public int TypedStringCalls;
        public int VoidBareCalls;
        public EventContext? LastContext;
        public string? LastString;

        [EventHandler("ev:bare")]
        public HookResult OnBare() { BareCalls++; return HookResult.Continue; }

        [EventHandler("ev:ctx")]
        public HookResult OnContext(EventContext ctx) { ContextCalls++; LastContext = ctx; return HookResult.Continue; }

        [EventHandler("ev:typed")]
        public void OnTypedString(string s) { TypedStringCalls++; LastString = s; }

        [EventHandler("ev:void_bare")]
        public void OnVoidBare() { VoidBareCalls++; }
    }

    [Fact]
    public void Attribute_handlers_wire_for_all_three_shapes()
    {
        PluginBusFixture.Setup();
        try
        {
            var plugin = new RecordingPlugin();
            PluginLoader.RegisterPluginBusHandlersForTests(PluginPath, plugin);

            PluginBus.Publish("ev:bare");
            PluginBus.Publish("ev:ctx", "hello");
            PluginBus.Publish("ev:typed", "str");
            PluginBus.Publish("ev:typed", 123);        // wrong type — should skip
            PluginBus.Publish("ev:void_bare");

            Assert.Equal(1, plugin.BareCalls);
            Assert.Equal(1, plugin.ContextCalls);
            Assert.Equal("hello", plugin.LastContext!.Payload);
            Assert.Equal(1, plugin.TypedStringCalls);
            Assert.Equal("str", plugin.LastString);
            Assert.Equal(1, plugin.VoidBareCalls);

            PluginLoader.UnregisterPluginBusHandlersForTests(PluginPath);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void UnregisterPlugin_clears_all_subscriptions_for_that_plugin()
    {
        PluginBusFixture.Setup();
        try
        {
            var plugin = new RecordingPlugin();
            PluginLoader.RegisterPluginBusHandlersForTests(PluginPath, plugin);

            Assert.Equal(1, PluginBus.SubscriberCount("ev:bare"));
            Assert.Equal(1, PluginBus.SubscriberCount("ev:ctx"));

            PluginLoader.UnregisterPluginBusHandlersForTests(PluginPath);

            Assert.Equal(0, PluginBus.SubscriberCount("ev:bare"));
            Assert.Equal(0, PluginBus.SubscriberCount("ev:ctx"));
            Assert.Equal(0, PluginBus.SubscriberCount("ev:typed"));
            Assert.Equal(0, PluginBus.SubscriberCount("ev:void_bare"));
        }
        finally { PluginBusFixture.Teardown(); }
    }

    internal sealed class BadSignaturePlugin : IDeadworksPlugin
    {
        public string Name => "BadSignaturePlugin";
        public void OnLoad(bool isReload) { }
        public void OnUnload() { }

        [EventHandler("ev:bad")]
        public int BadReturn(EventContext ctx) => 0;

        [EventHandler("ev:bad_params")]
        public void BadParams(int a, int b) { }

        [EventHandler("ev:bad_value")]
        public void BadValue(int v) { }
    }

    [Fact]
    public void Malformed_event_attribute_signatures_are_skipped_without_registering()
    {
        PluginBusFixture.Setup();
        try
        {
            var plugin = new BadSignaturePlugin();
            PluginLoader.RegisterPluginBusHandlersForTests(PluginPath, plugin);

            Assert.Equal(0, PluginBus.SubscriberCount("ev:bad"));
            Assert.Equal(0, PluginBus.SubscriberCount("ev:bad_params"));
            Assert.Equal(0, PluginBus.SubscriberCount("ev:bad_value"));

            PluginLoader.UnregisterPluginBusHandlersForTests(PluginPath);
        }
        finally { PluginBusFixture.Teardown(); }
    }
}

[Collection(nameof(PluginBusCollection))]
public class PluginBusQueryDispatchTests
{
    [Fact]
    public void Query_with_no_handlers_returns_empty()
    {
        PluginBusFixture.Setup();
        try
        {
            var result = PluginBus.Query<int>("nothing:here");
            Assert.Empty(result);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void Query_with_single_handler_returns_single_response()
    {
        PluginBusFixture.Setup();
        try
        {
            PluginBus.HandleQuery<int>("q:players", () => 12);
            var result = PluginBus.Query<int>("q:players");

            Assert.Single(result);
            Assert.Equal(12, result[0]);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void Query_collects_all_handlers_same_response_type()
    {
        PluginBusFixture.Setup();
        try
        {
            PluginBus.HandleQuery<int>("q:contrib", () => 1);
            PluginBus.HandleQuery<int>("q:contrib", () => 2);
            PluginBus.HandleQuery<int>("q:contrib", () => 3);

            var result = PluginBus.Query<int>("q:contrib");

            Assert.Equal(3, result.Count);
            Assert.Contains(1, result);
            Assert.Contains(2, result);
            Assert.Contains(3, result);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void Query_skips_handlers_whose_response_type_differs()
    {
        PluginBusFixture.Setup();
        try
        {
            PluginBus.HandleQuery<int>("q:mixed", () => 1);
            PluginBus.HandleQuery<string>("q:mixed", () => "hi");

            var ints = PluginBus.Query<int>("q:mixed");
            var strs = PluginBus.Query<string>("q:mixed");

            Assert.Single(ints);
            Assert.Equal(1, ints[0]);
            Assert.Single(strs);
            Assert.Equal("hi", strs[0]);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    internal sealed class GetThingRequest { public string? Key { get; set; } }

    [Fact]
    public void Query_typed_request_skips_on_mismatch()
    {
        PluginBusFixture.Setup();
        try
        {
            PluginBus.HandleQuery<GetThingRequest, string>("q:thing", req => $"got:{req.Key}");

            // Wrong request type — skipped
            var bad = PluginBus.Query<string>("q:thing", "string-instead-of-object");
            Assert.Empty(bad);

            // Null request — skipped
            var none = PluginBus.Query<string>("q:thing");
            Assert.Empty(none);

            // Correct request type — fires
            var ok = PluginBus.Query<string>("q:thing", new GetThingRequest { Key = "abc" });
            Assert.Single(ok);
            Assert.Equal("got:abc", ok[0]);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void Query_context_overload_receives_name_and_request()
    {
        PluginBusFixture.Setup();
        try
        {
            QueryContext? captured = null;
            PluginBus.HandleQuery<int>("q:ctx", ctx => { captured = ctx; return 7; });

            var result = PluginBus.Query<int>("q:ctx", "payload");

            Assert.Single(result);
            Assert.Equal(7, result[0]);
            Assert.Equal("q:ctx", captured!.Name);
            Assert.Equal("payload", captured.Request);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void Query_handler_exception_is_logged_and_others_still_run()
    {
        PluginBusFixture.Setup();
        try
        {
            PluginBus.HandleQuery<int>("q:boom", () => throw new InvalidOperationException("boom"));
            PluginBus.HandleQuery<int>("q:boom", () => 42);

            var result = PluginBus.Query<int>("q:boom");

            Assert.Single(result);
            Assert.Equal(42, result[0]);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void IHandle_Cancel_removes_query_handler()
    {
        PluginBusFixture.Setup();
        try
        {
            var handle = PluginBus.HandleQuery<int>("q:once", () => 99);
            Assert.Equal(1, PluginBus.QueryHandlerCount("q:once"));

            handle.Cancel();

            Assert.Equal(0, PluginBus.QueryHandlerCount("q:once"));
            Assert.Empty(PluginBus.Query<int>("q:once"));
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void HasQueryHandlers_and_QueryHandlerCount_reflect_state()
    {
        PluginBusFixture.Setup();
        try
        {
            Assert.False(PluginBus.HasQueryHandlers("q:x"));
            Assert.Equal(0, PluginBus.QueryHandlerCount("q:x"));

            var h1 = PluginBus.HandleQuery<int>("q:x", () => 1);
            var h2 = PluginBus.HandleQuery<string>("q:x", () => "s");

            Assert.True(PluginBus.HasQueryHandlers("q:x"));
            Assert.Equal(2, PluginBus.QueryHandlerCount("q:x"));

            h1.Cancel();
            h2.Cancel();
            Assert.False(PluginBus.HasQueryHandlers("q:x"));
        }
        finally { PluginBusFixture.Teardown(); }
    }
}

[Collection(nameof(PluginBusCollection))]
public class PluginBusQueryAttributeTests
{
    private const string PluginPath = "/test/plugin_query.dll";

    internal sealed class GetThingRequest { public string? Key { get; set; } }

    internal sealed class QueryPlugin : IDeadworksPlugin
    {
        public string Name => "QueryPlugin";
        public void OnLoad(bool isReload) { }
        public void OnUnload() { }

        public int BareCalls;
        public int ContextCalls;
        public int TypedCalls;
        public QueryContext? LastContext;
        public string? LastKey;

        [QueryHandler("q:bare")]
        public int Bare() { BareCalls++; return 5; }

        [QueryHandler("q:ctx")]
        public string Ctx(QueryContext ctx) { ContextCalls++; LastContext = ctx; return "ctx-response"; }

        [QueryHandler("q:typed")]
        public string Typed(GetThingRequest req) { TypedCalls++; LastKey = req.Key; return $"typed:{req.Key}"; }
    }

    [Fact]
    public void Attribute_query_handlers_wire_for_all_three_shapes()
    {
        PluginBusFixture.Setup();
        try
        {
            var plugin = new QueryPlugin();
            PluginLoader.RegisterPluginBusHandlersForTests(PluginPath, plugin);

            var bareResult = PluginBus.Query<int>("q:bare");
            var ctxResult = PluginBus.Query<string>("q:ctx", "the-request");
            var typedOk = PluginBus.Query<string>("q:typed", new GetThingRequest { Key = "k" });
            var typedBad = PluginBus.Query<string>("q:typed", "not-the-request-type");

            Assert.Single(bareResult);
            Assert.Equal(5, bareResult[0]);
            Assert.Equal(1, plugin.BareCalls);

            Assert.Single(ctxResult);
            Assert.Equal("ctx-response", ctxResult[0]);
            Assert.Equal("the-request", plugin.LastContext!.Request);

            Assert.Single(typedOk);
            Assert.Equal("typed:k", typedOk[0]);
            Assert.Equal("k", plugin.LastKey);

            Assert.Empty(typedBad);

            PluginLoader.UnregisterPluginBusHandlersForTests(PluginPath);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    [Fact]
    public void UnregisterPlugin_clears_all_query_handlers_for_that_plugin()
    {
        PluginBusFixture.Setup();
        try
        {
            var plugin = new QueryPlugin();
            PluginLoader.RegisterPluginBusHandlersForTests(PluginPath, plugin);

            Assert.Equal(1, PluginBus.QueryHandlerCount("q:bare"));
            Assert.Equal(1, PluginBus.QueryHandlerCount("q:ctx"));
            Assert.Equal(1, PluginBus.QueryHandlerCount("q:typed"));

            PluginLoader.UnregisterPluginBusHandlersForTests(PluginPath);

            Assert.Equal(0, PluginBus.QueryHandlerCount("q:bare"));
            Assert.Equal(0, PluginBus.QueryHandlerCount("q:ctx"));
            Assert.Equal(0, PluginBus.QueryHandlerCount("q:typed"));
        }
        finally { PluginBusFixture.Teardown(); }
    }

    internal sealed class BadQueryPlugin : IDeadworksPlugin
    {
        public string Name => "BadQueryPlugin";
        public void OnLoad(bool isReload) { }
        public void OnUnload() { }

        [QueryHandler("q:bad_void")]
        public void BadVoid() { }

        [QueryHandler("q:bad_params")]
        public int BadParams(int a, int b) => 0;

        [QueryHandler("q:bad_value")]
        public int BadValue(int v) => v;
    }

    [Fact]
    public void Malformed_query_attribute_signatures_are_skipped_without_registering()
    {
        PluginBusFixture.Setup();
        try
        {
            var plugin = new BadQueryPlugin();
            PluginLoader.RegisterPluginBusHandlersForTests(PluginPath, plugin);

            Assert.Equal(0, PluginBus.QueryHandlerCount("q:bad_void"));
            Assert.Equal(0, PluginBus.QueryHandlerCount("q:bad_params"));
            Assert.Equal(0, PluginBus.QueryHandlerCount("q:bad_value"));

            PluginLoader.UnregisterPluginBusHandlersForTests(PluginPath);
        }
        finally { PluginBusFixture.Teardown(); }
    }

    internal sealed class MultiPlugin : IDeadworksPlugin
    {
        public string Name => "MultiPlugin";
        public void OnLoad(bool isReload) { }
        public void OnUnload() { }

        [QueryHandler("q:multi")]
        public int A() => 1;

        [QueryHandler("q:multi")]
        public int B() => 2;
    }

    [Fact]
    public void Multiple_attribute_handlers_on_same_query_all_contribute()
    {
        PluginBusFixture.Setup();
        try
        {
            var plugin = new MultiPlugin();
            PluginLoader.RegisterPluginBusHandlersForTests(PluginPath, plugin);

            var result = PluginBus.Query<int>("q:multi");

            Assert.Equal(2, result.Count);
            Assert.Contains(1, result);
            Assert.Contains(2, result);

            PluginLoader.UnregisterPluginBusHandlersForTests(PluginPath);
        }
        finally { PluginBusFixture.Teardown(); }
    }
}
