using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeadworksManaged.Api;
using Xunit;

namespace DeadworksManaged.Tests;

// Layouts and values the game reads directly: RnCollisionAttr_t is copied out of the collision property and
// out of every trace result, so a shifted field silently reads the neighbouring byte.
public class CollisionLayoutTests
{
    [Fact]
    public void RnCollisionAttr_MatchesGameLayout()
    {
        Assert.Equal(0x28, Unsafe.SizeOf<RnCollisionAttr_t>());
        Assert.Equal(0x00, Offset<RnCollisionAttr_t>(nameof(RnCollisionAttr_t.InteractsAs)));
        Assert.Equal(0x08, Offset<RnCollisionAttr_t>(nameof(RnCollisionAttr_t.InteractsWith)));
        Assert.Equal(0x10, Offset<RnCollisionAttr_t>(nameof(RnCollisionAttr_t.InteractsExclude)));
        Assert.Equal(0x18, Offset<RnCollisionAttr_t>(nameof(RnCollisionAttr_t.EntityId)));
        Assert.Equal(0x1C, Offset<RnCollisionAttr_t>(nameof(RnCollisionAttr_t.OwnerId)));
        Assert.Equal(0x20, Offset<RnCollisionAttr_t>(nameof(RnCollisionAttr_t.HierarchyId)));
        Assert.Equal(0x26, Offset<RnCollisionAttr_t>(nameof(RnCollisionAttr_t.CollisionGroup)));
        Assert.Equal(0x27, Offset<RnCollisionAttr_t>(nameof(RnCollisionAttr_t.CollisionFunctionMask)));
    }

    [Fact]
    public void GameTrace_ShapeAttributesSitBetweenTransformAndStartPos()
    {
        Assert.Equal(192, Unsafe.SizeOf<CGameTrace>());
        Assert.Equal(0x50, Offset<CGameTrace>(nameof(CGameTrace.ShapeAttributes)));
        Assert.Equal(0x78, Offset<CGameTrace>(nameof(CGameTrace.StartPos)));
    }

    [Fact]
    public void InteractionLayer_IsOneBitPerLayer()
    {
        var bits = Enum.GetValues<InteractionLayer>().Where(l => l != InteractionLayer.None).Select(l => (ulong)l).ToList();
        Assert.All(bits, b => Assert.True(ulong.IsPow2(b)));
        Assert.Equal(64, bits.Distinct().Count());
        Assert.Equal(1UL << 31, (ulong)InteractionLayer.CitadelTeamAmber);
        Assert.Equal(1UL << 34, (ulong)InteractionLayer.CitadelAbility);
        Assert.Equal(1UL << 35, (ulong)InteractionLayer.CitadelBullet);
        Assert.Equal(1UL << 63, (ulong)InteractionLayer.CitadelPortalEnvironment);
    }

    [Fact]
    public void SolidFlags_UseDeadlockBits()
    {
        Assert.Equal(0x1, (int)SolidFlags.NotStandable);
        Assert.Equal(0x2, (int)SolidFlags.UseTriggerBounds);
        Assert.Equal(0x4, (int)SolidFlags.NotSolid);
    }

    [Fact]
    public void SolidType_MatchesSchemaEnum()
    {
        Assert.Equal(6, (int)SolidType.VPhysics);
        Assert.Equal(8, (int)SolidType.Cylinder);
    }

    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);
}
