using System.Numerics;
using DeadworksManaged.Api;
using Xunit;

namespace DeadworksManaged.Tests;

public class ZoneTests
{
    [Fact]
    public void Constructor_NormalisesCornerOrder()
    {
        using var zone = new Zone(new Vector3(10, 10, 10), new Vector3(-10, -10, -10));

        Assert.Equal(new Vector3(-10, -10, -10), zone.Mins);
        Assert.Equal(new Vector3(10, 10, 10), zone.Maxs);
        Assert.Equal(Vector3.Zero, zone.Center);
        Assert.Equal(new Vector3(20, 20, 20), zone.Size);
    }

    [Fact]
    public void FromOrigin_AppliesRelativeBounds()
    {
        using var zone = Zone.FromOrigin(new Vector3(100, 200, 300), new Vector3(-64, -64, -8), new Vector3(64, 64, 96));

        Assert.Equal(new Vector3(36, 136, 292), zone.Mins);
        Assert.Equal(new Vector3(164, 264, 396), zone.Maxs);
    }

    [Fact]
    public void Contains_IsInclusiveOnBounds()
    {
        using var zone = new Zone(Vector3.Zero, new Vector3(10, 10, 10));

        Assert.True(zone.Contains(Vector3.Zero));
        Assert.True(zone.Contains(new Vector3(10, 10, 10)));
        Assert.True(zone.Contains(new Vector3(5, 5, 5)));
        Assert.False(zone.Contains(new Vector3(10.01f, 5, 5)));
        Assert.False(zone.Contains(new Vector3(5, -0.01f, 5)));
    }

    [Fact]
    public void Step_ReportsEnterOnceThenLeaveOnce()
    {
        using var zone = new Zone(Vector3.Zero, new Vector3(10, 10, 10));
        var inside = new Vector3(5, 5, 5);
        var outside = new Vector3(50, 5, 5);

        Assert.Equal(ZoneTransition.None, zone.Step(3, outside));
        Assert.Equal(ZoneTransition.Entered, zone.Step(3, inside));
        Assert.Equal(ZoneTransition.None, zone.Step(3, inside));
        Assert.True(zone.IsInside(3));
        Assert.Equal(1, zone.OccupantCount);

        Assert.Equal(ZoneTransition.Left, zone.Step(3, outside));
        Assert.Equal(ZoneTransition.None, zone.Step(3, outside));
        Assert.False(zone.IsInside(3));
        Assert.Equal(0, zone.OccupantCount);
    }

    [Fact]
    public void Step_TracksPlayersIndependently()
    {
        using var zone = new Zone(Vector3.Zero, new Vector3(10, 10, 10));
        var inside = new Vector3(5, 5, 5);

        Assert.Equal(ZoneTransition.Entered, zone.Step(1, inside));
        Assert.Equal(ZoneTransition.Entered, zone.Step(2, inside));
        Assert.Equal(ZoneTransition.Left, zone.Step(1, null));
        Assert.True(zone.IsInside(2));
        Assert.False(zone.IsInside(1));
    }

    [Fact]
    public void Step_MissingPawnCountsAsOutside()
    {
        using var zone = new Zone(Vector3.Zero, new Vector3(10, 10, 10));

        Assert.Equal(ZoneTransition.Entered, zone.Step(0, new Vector3(1, 1, 1)));
        Assert.Equal(ZoneTransition.Left, zone.Step(0, null));
    }

    [Fact]
    public void Disabled_ZoneTreatsEveryoneAsOutside()
    {
        using var zone = new Zone(Vector3.Zero, new Vector3(10, 10, 10));
        var inside = new Vector3(5, 5, 5);

        Assert.Equal(ZoneTransition.Entered, zone.Step(0, inside));
        zone.Enabled = false;
        Assert.Equal(ZoneTransition.Left, zone.Step(0, inside));
        Assert.Equal(ZoneTransition.None, zone.Step(0, inside));
        zone.Enabled = true;
        Assert.Equal(ZoneTransition.Entered, zone.Step(0, inside));
    }

    [Fact]
    public void Forget_DropsOccupantSilently()
    {
        using var zone = new Zone(Vector3.Zero, new Vector3(10, 10, 10));
        var inside = new Vector3(5, 5, 5);

        zone.Step(4, inside);
        zone.Forget(4);

        Assert.False(zone.IsInside(4));
        Assert.Equal(ZoneTransition.Entered, zone.Step(4, inside));
    }

    [Fact]
    public void MoveTo_KeepsSize()
    {
        using var zone = new Zone(Vector3.Zero, new Vector3(10, 20, 30));
        zone.MoveTo(new Vector3(100, 100, 100));

        Assert.Equal(new Vector3(10, 20, 30), zone.Size);
        Assert.Equal(new Vector3(100, 100, 100), zone.Center);
    }

    [Fact]
    public void Dispose_UnregistersAndDisables()
    {
        var zone = new Zone(Vector3.Zero, new Vector3(10, 10, 10));
        zone.Step(0, new Vector3(1, 1, 1));
        int before = ZoneRegistry.Count;

        zone.Dispose();

        Assert.Equal(before - 1, ZoneRegistry.Count);
        Assert.False(zone.Enabled);
        Assert.Equal(0, zone.OccupantCount);
        Assert.Equal(ZoneTransition.None, zone.Step(0, new Vector3(1, 1, 1)));
    }
}
