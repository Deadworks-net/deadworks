using Xunit;

namespace DeadworksManaged.Tests;

public class KickCommandTests
{
    [Theory]
    [InlineData(new[] { "dw_kick", "0" }, 0)]
    [InlineData(new[] { "dw_kick", "30" }, 30)]
    [InlineData(new[] { "dw_kick", "7", "being", "rude" }, 7)]
    public void ValidSlotParses(string[] argv, int expected)
    {
        Assert.True(KickCommand.TryParseSlot(argv, out var slot, out var error));
        Assert.Equal(expected, slot);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData(new object[] { new[] { "dw_kick" } })]
    [InlineData(new object[] { new[] { "dw_kick", "abc" } })]
    [InlineData(new object[] { new[] { "dw_kick", "-1" } })]
    [InlineData(new object[] { new[] { "dw_kick", "31" } })]
    public void BadSlotIsRejected(string[] argv)
    {
        Assert.False(KickCommand.TryParseSlot(argv, out var slot, out var error));
        Assert.Equal(-1, slot);
        Assert.NotEmpty(error);
    }
}
