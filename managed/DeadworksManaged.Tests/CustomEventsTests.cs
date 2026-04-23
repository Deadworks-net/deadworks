using System.Text;
using DeadworksManaged;
using DeadworksManaged.Api;
using Xunit;

namespace DeadworksManaged.Tests;

internal sealed class HandlerRecorder
{
    public int Calls;
    public CustomEventContext? LastContext;
    public object? LastPayload;
}

/// <summary>Wires CustomEvents through PluginLoader's dispatcher and tears down between tests.</summary>
internal static class CustomEventsFixture
{
    public static void Setup() { PluginLoader.InitializeCustomEventsForTests(); }
    public static void Teardown() { PluginLoader.ResetCustomEventsForTests(); }
}

public class CustomEventsDispatchTests
{
    [Fact]
    public void Publish_with_no_subscribers_returns_Continue()
    {
        CustomEventsFixture.Setup();
        try { Assert.Equal(HookResult.Continue, CustomEvents.Publish("nothing:here")); }
        finally { CustomEventsFixture.Teardown(); }
    }

    [Fact]
    public void Subscribe_context_Action_receives_payload_and_name()
    {
        CustomEventsFixture.Setup();
        try
        {
            var rec = new HandlerRecorder();
            CustomEvents.Subscribe("x:foo", ctx => { rec.Calls++; rec.LastContext = ctx; });

            CustomEvents.Publish("x:foo", "hello");

            Assert.Equal(1, rec.Calls);
            Assert.Equal("x:foo", rec.LastContext!.Name);
            Assert.Equal("hello", rec.LastContext.Payload);
        }
        finally { CustomEventsFixture.Teardown(); }
    }

    [Fact]
    public void Subscribe_context_Func_aggregates_HookResult_max_wins()
    {
        CustomEventsFixture.Setup();
        try
        {
            CustomEvents.Subscribe("x:foo", _ => HookResult.Continue);
            CustomEvents.Subscribe("x:foo", _ => HookResult.Stop);
            CustomEvents.Subscribe("x:foo", _ => HookResult.Continue);

            var result = CustomEvents.Publish("x:foo");
            Assert.Equal(HookResult.Stop, result);
        }
        finally { CustomEventsFixture.Teardown(); }
    }

    [Fact]
    public void Subscribe_typed_skips_on_payload_mismatch()
    {
        CustomEventsFixture.Setup();
        try
        {
            var rec = new HandlerRecorder();
            CustomEvents.Subscribe<string>("x:foo", s => { rec.Calls++; rec.LastPayload = s; });

            CustomEvents.Publish("x:foo", 42);            // int — skip
            CustomEvents.Publish("x:foo");                // null — skip
            CustomEvents.Publish("x:foo", "hello");       // match

            Assert.Equal(1, rec.Calls);
            Assert.Equal("hello", rec.LastPayload);
        }
        finally { CustomEventsFixture.Teardown(); }
    }

    [Fact]
    public void Subscribe_typed_Func_fires_and_returns_HookResult()
    {
        CustomEventsFixture.Setup();
        try
        {
            CustomEvents.Subscribe<string>("x:foo", _ => HookResult.Handled);
            Assert.Equal(HookResult.Handled, CustomEvents.Publish("x:foo", "hi"));
            Assert.Equal(HookResult.Continue, CustomEvents.Publish("x:foo", 123));
        }
        finally { CustomEventsFixture.Teardown(); }
    }

    [Fact]
    public void IHandle_Cancel_removes_subscription()
    {
        CustomEventsFixture.Setup();
        try
        {
            var rec = new HandlerRecorder();
            var handle = CustomEvents.Subscribe("x:foo", _ => rec.Calls++);

            CustomEvents.Publish("x:foo");
            Assert.Equal(1, rec.Calls);

            handle.Cancel();
            CustomEvents.Publish("x:foo");
            Assert.Equal(1, rec.Calls);
            Assert.Equal(0, CustomEvents.SubscriberCount("x:foo"));
        }
        finally { CustomEventsFixture.Teardown(); }
    }

    [Fact]
    public void Unsubscribe_by_original_delegate_removes_subscription()
    {
        CustomEventsFixture.Setup();
        try
        {
            var rec = new HandlerRecorder();
            Action<CustomEventContext> handler = _ => rec.Calls++;
            CustomEvents.Subscribe("x:foo", handler);

            CustomEvents.Unsubscribe("x:foo", handler);
            CustomEvents.Publish("x:foo");
            Assert.Equal(0, rec.Calls);
        }
        finally { CustomEventsFixture.Teardown(); }
    }

    [Fact]
    public void HasSubscribers_and_SubscriberCount_reflect_state()
    {
        CustomEventsFixture.Setup();
        try
        {
            Assert.False(CustomEvents.HasSubscribers("x:foo"));
            Assert.Equal(0, CustomEvents.SubscriberCount("x:foo"));

            var h1 = CustomEvents.Subscribe("x:foo", _ => { });
            var h2 = CustomEvents.Subscribe("x:foo", _ => { });

            Assert.True(CustomEvents.HasSubscribers("x:foo"));
            Assert.Equal(2, CustomEvents.SubscriberCount("x:foo"));

            h1.Cancel();
            Assert.Equal(1, CustomEvents.SubscriberCount("x:foo"));

            h2.Cancel();
            Assert.False(CustomEvents.HasSubscribers("x:foo"));
        }
        finally { CustomEventsFixture.Teardown(); }
    }

    [Fact]
    public void Handler_exception_does_not_stop_subsequent_handlers()
    {
        CustomEventsFixture.Setup();
        try
        {
            var secondFired = false;
            CustomEvents.Subscribe("x:foo", _ => throw new InvalidOperationException("boom"));
            CustomEvents.Subscribe("x:foo", _ => secondFired = true);

            CustomEvents.Publish("x:foo");
            Assert.True(secondFired);
        }
        finally { CustomEventsFixture.Teardown(); }
    }

    [Fact]
    public void PayloadAs_returns_cast_on_match_and_null_on_mismatch()
    {
        CustomEventsFixture.Setup();
        try
        {
            CustomEventContext? captured = null;
            CustomEvents.Subscribe("x:foo", ctx => { captured = ctx; });

            CustomEvents.Publish("x:foo", "str");
            Assert.Equal("str", captured!.PayloadAs<string>());
            Assert.Null(captured.PayloadAs<StringBuilder>());
        }
        finally { CustomEventsFixture.Teardown(); }
    }
}

public class CustomEventsAttributeTests
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
        public CustomEventContext? LastContext;
        public string? LastString;

        [CustomEventHandler("ce:bare")]
        public HookResult OnBare() { BareCalls++; return HookResult.Continue; }

        [CustomEventHandler("ce:ctx")]
        public HookResult OnContext(CustomEventContext ctx) { ContextCalls++; LastContext = ctx; return HookResult.Continue; }

        [CustomEventHandler("ce:typed")]
        public void OnTypedString(string s) { TypedStringCalls++; LastString = s; }

        [CustomEventHandler("ce:void_bare")]
        public void OnVoidBare() { VoidBareCalls++; }
    }

    [Fact]
    public void Attribute_handlers_wire_for_all_three_shapes()
    {
        CustomEventsFixture.Setup();
        try
        {
            var plugin = new RecordingPlugin();
            PluginLoader.RegisterPluginCustomEventHandlersForTests(PluginPath, plugin);

            CustomEvents.Publish("ce:bare");
            CustomEvents.Publish("ce:ctx", "hello");
            CustomEvents.Publish("ce:typed", "str");
            CustomEvents.Publish("ce:typed", 123);        // wrong type — should skip
            CustomEvents.Publish("ce:void_bare");

            Assert.Equal(1, plugin.BareCalls);
            Assert.Equal(1, plugin.ContextCalls);
            Assert.Equal("hello", plugin.LastContext!.Payload);
            Assert.Equal(1, plugin.TypedStringCalls);
            Assert.Equal("str", plugin.LastString);
            Assert.Equal(1, plugin.VoidBareCalls);

            PluginLoader.UnregisterPluginCustomEventsForTests(PluginPath);
        }
        finally { CustomEventsFixture.Teardown(); }
    }

    [Fact]
    public void UnregisterPlugin_clears_all_subscriptions_for_that_plugin()
    {
        CustomEventsFixture.Setup();
        try
        {
            var plugin = new RecordingPlugin();
            PluginLoader.RegisterPluginCustomEventHandlersForTests(PluginPath, plugin);

            Assert.Equal(1, CustomEvents.SubscriberCount("ce:bare"));
            Assert.Equal(1, CustomEvents.SubscriberCount("ce:ctx"));

            PluginLoader.UnregisterPluginCustomEventsForTests(PluginPath);

            Assert.Equal(0, CustomEvents.SubscriberCount("ce:bare"));
            Assert.Equal(0, CustomEvents.SubscriberCount("ce:ctx"));
            Assert.Equal(0, CustomEvents.SubscriberCount("ce:typed"));
            Assert.Equal(0, CustomEvents.SubscriberCount("ce:void_bare"));
        }
        finally { CustomEventsFixture.Teardown(); }
    }

    internal sealed class BadSignaturePlugin : IDeadworksPlugin
    {
        public string Name => "BadSignaturePlugin";
        public void OnLoad(bool isReload) { }
        public void OnUnload() { }

        [CustomEventHandler("ce:bad")]
        public int BadReturn(CustomEventContext ctx) => 0;

        [CustomEventHandler("ce:bad_params")]
        public void BadParams(int a, int b) { }

        [CustomEventHandler("ce:bad_value")]
        public void BadValue(int v) { }
    }

    [Fact]
    public void Malformed_attribute_signatures_are_skipped_without_registering()
    {
        CustomEventsFixture.Setup();
        try
        {
            var plugin = new BadSignaturePlugin();
            PluginLoader.RegisterPluginCustomEventHandlersForTests(PluginPath, plugin);

            Assert.Equal(0, CustomEvents.SubscriberCount("ce:bad"));
            Assert.Equal(0, CustomEvents.SubscriberCount("ce:bad_params"));
            Assert.Equal(0, CustomEvents.SubscriberCount("ce:bad_value"));

            PluginLoader.UnregisterPluginCustomEventsForTests(PluginPath);
        }
        finally { CustomEventsFixture.Teardown(); }
    }
}
