using DeadworksManaged.Api;
using Xunit;

namespace DeadworksManaged.Tests;

public sealed class ChatMuteTests : IDisposable
{
    public void Dispose()
    {
        ChatMutes.Clear();
    }

    [Fact]
    public void DispatchChatMessage_stops_normal_chat_from_muted_players()
    {
        ChatMutes.SetMuted(new ChatMuteInfo
        {
            SteamId64 = 76561197960287930,
            Reason = "test mute"
        });

        var result = PluginLoader.DispatchChatMessage(new ChatMessage
        {
            SenderSlot = 0,
            SenderSteamId64 = 76561197960287930,
            ChatText = "hello",
            AllChat = true,
            LaneColor = LaneColor.Invalid
        });

        Assert.Equal(HookResult.Stop, result);
    }

    [Fact]
    public void ChatMutes_prunes_expired_entries()
    {
        ChatMutes.SetMuted(new ChatMuteInfo
        {
            SteamId64 = 76561197960287930,
            Reason = "expired",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
        });

        Assert.False(ChatMutes.IsMuted(76561197960287930));
    }
}
