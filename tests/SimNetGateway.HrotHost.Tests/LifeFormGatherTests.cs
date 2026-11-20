using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Physics.Components;
using Fdp.Toolkit.Tkb.Domain;
using Hrot.MuscleCharacter.Animation.Components;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>the adapter fills stance, weapon posture, aim point and injury of HROT life forms (pivot level, through the real gateway).</summary>
public class LifeFormGatherTests
{
    private static PackHarness Live()
    {
        var h = new PackHarness(dis: true);
        h.RegisterTemplate(100, 1);   // a tank
        h.RegisterTemplate(300, 3);   // a soldier (DIS kind 3: life form)
        h.Steps(3);
        return h;
    }

    private static Entity Soldier(PackHarness h, int id, StanceId stance = StanceId.Standing, WeaponPostureKind kind = WeaponPostureKind.AtEase, Vector3? at = null, float health = 100f, bool stanceOrdered = true, HeldItemKind held = HeldItemKind.Weapon)
    {
        var e = h.AddHrotEntity(id, at ?? new Vector3(0, 0, 0), tkb: 300, health: health);
        if (stanceOrdered)
        {
            h.World.AddComponent(e, new StanceIntent { TargetStance = stance });
        }

        h.World.AddComponent(e, new WeaponPosture { Kind = kind, HeldItem = held });
        h.World.AddComponent(e, new InjuryThresholds { WoundedBelow = 0.7f, IncapacitatedBelow = 0.3f });
        return e;
    }

    private static (long Stance, long Weapon, Vec3 Aim, long Injury) Pivot(PackHarness h, int hrotId)
    {
        var ids = (IdentityMap<int>)h.Engine.Identities.Find("hrot.entity")!;
        var schema = new PivotSchema(h.Engine.Attributes, h.Engine.EventTypes);
        Assert.True(ids.TryResolve(hrotId, out var id));
        var record = h.Engine.State.Find(id)!;
        return (record.Get(schema.Stance).AsInt(), record.Get(schema.WeaponPosture).AsInt(), record.TryGet(schema.AimPoint, out var a) ? a.AsVec3() : default, record.Get(schema.Injury).AsInt());
    }

    [Fact]
    public void ADeadMan_WhoseStanceSaysStanding_IsProneAndDead()
    {
        using var h = Live();
        Soldier(h, 2001, StanceId.Standing, health: 0f);
        h.Steps(5);
        var p = Pivot(h, 2001);
        Assert.Equal((long)HrotStance.Prone, p.Stance);
        Assert.Equal((long)HrotInjury.Dead, p.Injury);
    }

    [Fact]
    public void Aimed_AtALiveTarget_TheAimPointIsTheTargetsChest_OneMetreAboveItsPosition()
    {
        using var h = Live();
        var target = h.AddHrotEntity(2002, new Vector3(40, 10, 0));
        var shooter = Soldier(h, 2001, kind: WeaponPostureKind.Aimed);
        h.World.SetComponent(shooter, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Target = target, HeldItem = HeldItemKind.Weapon });
        h.Steps(5);
        var p = Pivot(h, 2001);
        Assert.Equal((long)HrotWeaponPosture.Aimed, p.Weapon);
        var expected = h.Frame.PointToEcef(new Vec3(40, 10, 1));
        Assert.Equal(expected.X, p.Aim.X, 1);
        Assert.Equal(expected.Y, p.Aim.Y, 1);
        Assert.Equal(expected.Z, p.Aim.Z, 1);

        // the target moves: the pivot's aim point follows its chest (entity sides publish it); only DPS uses the target id instead
        h.Move(target, new Vector3(60, 10, 0));
        h.Steps(5);
        var follows = h.Frame.PointToEcef(new Vec3(60, 10, 1));
        var now = Pivot(h, 2001).Aim;
        Assert.Equal(follows.X, now.X, 1);
        Assert.Equal(follows.Y, now.Y, 1);
        Assert.Equal(follows.Z, now.Z, 1);
    }

    [Fact]
    public void Aimed_AtAPoint_UsesThePoint_AtADestroyedTargetWithoutPoint_IsReady()
    {
        using var h = Live();
        var shooter = Soldier(h, 2001);
        h.World.SetComponent(shooter, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Point = new WorldPoint(30, 5, 1), HeldItem = HeldItemKind.Weapon });
        h.Steps(5);
        Assert.Equal((long)HrotWeaponPosture.Aimed, Pivot(h, 2001).Weapon);

        var doomed = h.AddHrotEntity(2002, new Vector3(40, 0, 0));
        h.World.SetComponent(shooter, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Target = doomed, HeldItem = HeldItemKind.Weapon });
        h.Steps(5);
        Assert.Equal((long)HrotWeaponPosture.Aimed, Pivot(h, 2001).Weapon);
        h.RemoveHrotEntity(doomed);   // the target vanishes: no crash, straight ahead
        h.Steps(5);
        Assert.Equal((long)HrotWeaponPosture.Ready, Pivot(h, 2001).Weapon);
    }

    [Fact]
    public void ANoWeaponInHand_IsNone()
    {
        using var h = Live();
        var e = Soldier(h, 2001, kind: WeaponPostureKind.Aimed);
        h.World.SetComponent(e, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Point = new WorldPoint(3, 3, 0), HeldItem = HeldItemKind.Phone });
        h.Steps(5);
        Assert.Equal((long)HrotWeaponPosture.None, Pivot(h, 2001).Weapon);
    }

    [Fact]
    public void ATank_IsUnknownNoneOk()
    {
        using var h = Live();
        h.AddHrotEntity(2001, new Vector3(1, 1, 0));
        h.Steps(5);
        Assert.Equal((0L, 0L, 0L), (Pivot(h, 2001).Stance, Pivot(h, 2001).Weapon, Pivot(h, 2001).Injury));
    }

    [Fact]
    public void AStanceChangeAlone_MarksTheEntityChanged()
    {
        using var h = Live();
        var e = Soldier(h, 2001);
        h.Steps(5);
        Assert.Equal((long)HrotStance.Standing, Pivot(h, 2001).Stance);
        int sent = h.Adapter.Counters.StatesSent;
        h.World.SetComponent(e, new StanceIntent { TargetStance = StanceId.Crouched });
        h.Steps(2);   // well under the one-second resend: only the change can send
        Assert.True(h.Adapter.Counters.StatesSent > sent);
        Assert.Equal((long)HrotStance.Crouched, Pivot(h, 2001).Stance);
    }

    [Fact]
    public void ATankWithAWeaponPosture_IsNotALifeForm()
    {
        using var h = Live();
        var tank = h.AddHrotEntity(2001, new Vector3(1, 1, 0));   // template 100: DIS kind 1
        h.World.AddComponent(tank, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Point = new WorldPoint(9, 9, 0), HeldItem = HeldItemKind.None });
        h.Steps(5);
        Assert.Equal((0L, 0L, 0L), (Pivot(h, 2001).Stance, Pivot(h, 2001).Weapon, Pivot(h, 2001).Injury));
    }

    [Fact]
    public void ASpawnedSoldier_WithAPostureButNoStanceIntent_Stands()
    {
        using var h = Live();
        Soldier(h, 2001, kind: WeaponPostureKind.Ready, stanceOrdered: false);
        h.Steps(5);
        var p = Pivot(h, 2001);
        Assert.Equal((long)HrotStance.Standing, p.Stance);
        Assert.Equal((long)HrotWeaponPosture.Ready, p.Weapon);
    }

    [Theory]
    [InlineData(HeldItemKind.None, HrotWeaponPosture.Ready)]       // nothing in hand means his own weapon
    [InlineData(HeldItemKind.Weapon, HrotWeaponPosture.Ready)]
    [InlineData(HeldItemKind.Throwable, HrotWeaponPosture.Ready)]
    [InlineData(HeldItemKind.Phone, HrotWeaponPosture.None)]
    [InlineData(HeldItemKind.Binoculars, HrotWeaponPosture.None)]
    public void TheHeldItem_DecidesWhetherTheWeaponIsInHand(HeldItemKind held, HrotWeaponPosture expected)
    {
        using var h = Live();
        Soldier(h, 2001, kind: WeaponPostureKind.Ready, held: held);
        h.Steps(5);
        Assert.Equal((long)expected, Pivot(h, 2001).Weapon);
    }
}
