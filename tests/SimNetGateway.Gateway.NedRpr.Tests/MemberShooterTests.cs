using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>DD-002 act 5: a member of a NED formation is the subject of a weapon status and the shooter of a fire (ShooterMember).</summary>
public class MemberShooterTests
{
    private sealed class Rig
    {
        public Rig(GatewayHarness h, DispatchProbe probe, ulong targetNedId)
        {
            H = h;
            Probe = probe;
            TargetNedId = targetNedId;
        }

        public GatewayHarness H { get; }

        public DispatchProbe Probe { get; }

        public ulong TargetNedId { get; }

        public EntityRecord Member(int index) => S.NedMember(H, S.ViperId, index);
    }

    /// <summary>Viper with members 0..2 and the RPR platform T-1 (target), all published; member 2 has weapon status 501 / 30.</summary>
    private static Rig Setup(GatewayConfiguration? configuration = null)
    {
        DispatchProbe? probe = null;
        var h = new GatewayHarness(configuration ?? NedRprConfigurations.Showcase, beforeBuild: b =>
        {
            probe = new DispatchProbe(new PivotSchema(b.Attributes, b.EventTypes));
            b.AddModule(probe);
        });
        h.NedStep(S.Viper(0, 1, 2));
        h.NedStep(new NedWeaponStatus(S.ViperId, 501, 30, Member: 2));
        h.Rpr(S.Platform(1, "T-1"));
        h.Steps(4);
        Assert.True(h.NedIds.TryGetKey(S.RprEntity(h, 0x601).Id, out ulong target));
        h.ClearOutput();
        return new Rig(h, probe!, target);
    }

    private static NedWeaponFire MemberFire(Guid id, ulong target, int shooterMember = 2, int quantity = 1) =>
        new(id, S.ViperId, target, 0, 50.01, 14.01, 310, quantity, ShooterMember: shooterMember);

    // ---- weapon status of a member -----------------------------------------------------------------------------------

    [Fact]
    public void A_weapon_status_with_a_member_index_is_stored_on_the_member_and_nowhere_else()
    {
        var r = Setup();
        var h = r.H;

        Assert.Equal(501, r.Member(2).Get(h.Schema.SelectedMunition).AsInt());
        Assert.Equal(30, r.Member(2).Get(h.Schema.Ammo).AsInt());
        Assert.False(r.Member(0).Has(h.Schema.Ammo));
        Assert.False(r.Member(1).Has(h.Schema.Ammo));
        Assert.False(S.NedUnit(h, S.ViperId).Has(h.Schema.Ammo));
        Assert.Equal(5, h.Engine.State.Count); // unit + 3 members + T-1: nothing was created by the status
    }

    [Fact]
    public void A_weapon_status_without_a_member_still_creates_the_individual_entity()
    {
        var h = new GatewayHarness(NedRprConfigurations.Showcase);

        h.NedStep(new NedWeaponStatus(T.Viper, 501, 30));

        Assert.Equal(30, h.EntityOfNed(T.Viper)!.Get(h.Schema.Ammo).AsInt());
    }

    [Fact]
    public void A_weapon_status_for_an_unknown_member_is_ignored_and_creates_nothing()
    {
        var r = Setup();
        string snapshot = r.H.Snapshot();
        int entities = r.H.Engine.State.Count;

        r.H.NedStep(new NedWeaponStatus(S.ViperId, 502, 9, Member: 7));
        r.H.NedStep(new NedWeaponStatus(999, 502, 9, Member: 0));

        Assert.Equal(entities, r.H.Engine.State.Count);
        Assert.Equal(snapshot, r.H.Snapshot());
        Assert.Equal(3, r.H.Host.NedMembers.Count);
    }

    // ---- split fire by a member --------------------------------------------------------------------------------------

    [Fact]
    public void A_member_fires_in_two_parts_and_its_own_ammunition_goes_down()
    {
        var r = Setup();
        var h = r.H;
        var f1 = Guid.NewGuid();

        h.NedStep(MemberFire(f1, r.TargetNedId));
        h.Run(0.12);
        Assert.Equal(0, h.RprOut.Count);
        Assert.Equal(30, r.Member(2).Get(h.Schema.Ammo).AsInt());
        h.NedStep(new NedFireAmmunition(f1, 502, 2));
        h.Steps(2);

        var fire = Assert.Single(h.RprSent<RprWeaponFire>());
        Assert.Equal(h.HandleOf(r.Member(2)), fire.FiringObject);
        Assert.Equal(h.HandleOf(S.RprEntity(h, 0x601)), fire.TargetObject);
        Assert.Equal(T.Dis(502), fire.Munition);
        Assert.Equal(28, r.Member(2).Get(h.Schema.Ammo).AsInt());
        Assert.Equal(28L, Assert.Single(r.Probe.FireSeen).Ammo);
        Assert.False(r.Member(0).Has(h.Schema.Ammo));
        Assert.Empty(h.NedSent<NedWeaponFire>()); // no echo
    }

    [Fact]
    public void A_member_fire_without_the_second_part_uses_the_selected_munition_of_that_member()
    {
        var r = Setup();
        var h = r.H;
        h.Checker.Expect("completed by timeout");

        h.NedStep(MemberFire(Guid.NewGuid(), r.TargetNedId));
        h.Run(1.3);

        var fire = Assert.Single(h.RprSent<RprWeaponFire>());
        Assert.Equal(T.Dis(501), fire.Munition);
        Assert.Equal(h.HandleOf(r.Member(2)), fire.FiringObject);
        Assert.Equal(29, r.Member(2).Get(h.Schema.Ammo).AsInt());
    }

    [Fact]
    public void A_fire_of_an_unknown_shooter_member_aborts()
    {
        var r = Setup();
        r.H.Checker.Expect("unknown shooter");

        r.H.NedStep(MemberFire(Guid.NewGuid(), r.TargetNedId, shooterMember: 9));

        Assert.Equal(0, r.H.Host.NedFires.Count);
        Assert.Equal(1, r.H.Engine.Diagnostics.TransactionFailures);
    }

    [Fact]
    public void The_single_message_fire_of_the_default_configuration_takes_the_ammunition_of_the_member_shooter_too()
    {
        var r = Setup(NedRprConfigurations.Default);
        var h = r.H;
        var id = Guid.NewGuid();

        h.NedStep(new NedWeaponFire(id, S.ViperId, r.TargetNedId, 502, 50, 14, 300, 3, ShooterMember: 2));
        h.Steps(2);

        var fire = Assert.Single(h.RprSent<RprWeaponFire>());
        Assert.Equal(h.HandleOf(r.Member(2)), fire.FiringObject);
        Assert.Equal(27, r.Member(2).Get(h.Schema.Ammo).AsInt());
    }

    // ---- towards NED ---------------------------------------------------------------------------------------------------

    [Fact]
    public void An_RPR_fire_by_an_aggregate_member_platform_reaches_NED_with_the_unit_id_and_ShooterMember()
    {
        var r = Setup();
        var h = r.H;
        h.Rpr(S.Platform(2));
        h.Rpr(S.Platform(3));
        h.Step();
        h.RprStep(S.Aggregate(0x700, "Wolf", 2, 3));
        h.Steps(2);
        Assert.True(h.NedIds.TryGetKey(S.RprEntity(h, 0x700).Id, out ulong wolf));
        h.ClearOutput();

        h.RprStep(new RprWeaponFire(Guid.NewGuid(), S.PlatformHandle(3), null, default, T.Dis(502), 1));
        h.Steps(1);

        var fire = Assert.Single(h.NedSent<NedWeaponFire>());
        Assert.Equal(wolf, fire.Shooter);
        Assert.Equal(1, fire.ShooterMember);
        Assert.Equal(-1, fire.TargetMember);
    }

    [Fact]
    public void A_fire_of_a_NED_formation_member_looped_back_from_RPR_is_ignored_as_an_echo()
    {
        var r = Setup();
        var h = r.H;
        string snapshot = h.Snapshot();

        h.RprStep(new RprWeaponFire(Guid.NewGuid(), h.HandleOf(r.Member(1)), S.PlatformHandle(1), default, T.Dis(502), 1));
        h.Steps(1);

        Assert.Empty(h.NedSent<NedWeaponFire>());
        Assert.Equal(snapshot, h.Snapshot());
    }

    [Fact]
    public void An_RPR_fire_by_an_individual_has_no_shooter_member()
    {
        var r = Setup();
        r.H.Rpr(S.Platform(2, "T-2"));
        r.H.Steps(3);
        r.H.ClearOutput();

        r.H.RprStep(new RprWeaponFire(Guid.NewGuid(), S.PlatformHandle(2), null, default, T.Dis(502), 1));

        Assert.Equal(-1, Assert.Single(r.H.NedSent<NedWeaponFire>()).ShooterMember);
    }
}
