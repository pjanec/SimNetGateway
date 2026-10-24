using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>DD-002 C8 (R-044): a fire split over NedWeaponFire + NedFireAmmunition, assembled by the FireAssemblerProcessor.</summary>
public class SplitFireTests
{
    private const double FireLat = 50.01;
    private const double FireLon = 14.01;
    private const double FireAlt = 310.0;

    private sealed class Rig
    {
        public Rig(GatewayHarness harness, DispatchProbe probe, ulong targetNedId)
        {
            H = harness;
            Probe = probe;
            TargetNedId = targetNedId;
        }

        public GatewayHarness H { get; }

        public DispatchProbe Probe { get; }

        public ulong TargetNedId { get; }

        public EntityRecord Shooter => H.EntityOfNed(T.Hawk)!;

        public EntityRecord Target => S.RprEntity(H, 0x601);
    }

    /// <summary>Hawk (NED shooter, selected munition 501, ammo 30) and the RPR platform T-1 (target), both published, output cleared.</summary>
    private static Rig Setup(GatewayConfiguration? configuration = null, int ammo = 30)
    {
        DispatchProbe? probe = null;
        var h = new GatewayHarness(configuration ?? NedRprConfigurations.Showcase, beforeBuild: b =>
        {
            probe = new DispatchProbe(new PivotSchema(b.Attributes, b.EventTypes));
            b.AddModule(probe);
        });
        T.PublishNed(h, T.Hawk, "Hawk", 200);
        h.NedStep(new NedWeaponStatus(T.Hawk, 501, ammo));
        h.Rpr(S.Platform(1, "T-1"));
        h.Steps(3);
        var target = S.RprEntity(h, 0x601);
        Assert.True(h.NedIds.TryGetKey(target.Id, out ulong targetNedId));
        h.ClearOutput();
        return new Rig(h, probe!, targetNedId);
    }

    private static NedWeaponFire Fire(Guid id, ulong shooter, ulong target, int munition = 0, int quantity = 1, int targetMember = -1) =>
        new(id, shooter, target, munition, FireLat, FireLon, FireAlt, quantity, targetMember);

    private static int EventCount(GatewayHarness h, EventTypeId type)
    {
        int count = 0;
        foreach (var ev in h.Engine.State.Journal.RecentEvents)
        {
            if (ev.Type == type)
            {
                count++;
            }
        }

        return count;
    }

    // ---- two parts ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_first_part_alone_publishes_nothing_and_leaves_the_ammunition_alone()
    {
        var r = Setup();
        var f1 = Guid.NewGuid();
        int entities = r.H.Engine.State.Count;

        r.H.NedStep(Fire(f1, T.Hawk, r.TargetNedId));
        r.H.Run(0.5);

        Assert.Equal(0, r.H.RprOut.Count);
        Assert.Equal(0, r.H.NedOut.Count);
        Assert.Equal(30, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
        Assert.Empty(r.Probe.FireSeen);
        Assert.Equal(entities + 1, r.H.Engine.State.Count); // the helper entity
        Assert.True(r.H.Host.NedFires.TryResolve(f1, out var helper));
        var record = r.H.Engine.State.Find(helper)!;
        Assert.True(record.Get(r.H.Schema.FirePending).AsBool());
        Assert.Equal(r.Shooter.Id, record.Get(r.H.Schema.FireShooter).AsEntity());
        Assert.Equal(r.Target.Id, record.Get(r.H.Schema.FireTarget).AsEntity());
        Assert.False(record.Has(r.H.Schema.Position)); // the helper has no position or identity: no egress publishes it
        Assert.False(record.Has(r.H.Schema.Name));
        Assert.Equal(0, EventCount(r.H, r.H.Schema.WeaponFire));
    }

    [Fact]
    public void Two_parts_give_exactly_one_RPR_fire_after_the_second_with_the_given_munition_and_quantity()
    {
        var r = Setup();
        var f1 = Guid.NewGuid();
        var shooterHandle = r.H.HandleOf(r.Shooter);
        var targetHandle = r.H.HandleOf(r.Target);
        int entities = r.H.Engine.State.Count;

        r.H.NedStep(Fire(f1, T.Hawk, r.TargetNedId));
        r.H.Run(0.12); // 120 ms later
        Assert.Equal(0, r.H.RprOut.Count);
        r.H.NedStep(new NedFireAmmunition(f1, 502, 2));
        r.H.Steps(2);

        var fire = Assert.Single(r.H.RprSent<RprWeaponFire>());
        Assert.Equal(1, r.H.RprOut.Count);
        Assert.Equal(f1, fire.EventId);
        Assert.Equal(T.Dis(502), fire.Munition);
        Assert.Equal(2, fire.Quantity);
        Assert.Equal(shooterHandle, fire.FiringObject);
        Assert.Equal(targetHandle, fire.TargetObject);
        Assert.Equal(NedRprConversions.ToWorldLocation(NedRprConversions.GeodeticToEcef(FireLat, FireLon, FireAlt)), fire.Location);
        // the ammunition went down by the quantity and was visible when the event was dispatched
        Assert.Equal(28, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
        var seen = Assert.Single(r.Probe.FireSeen);
        Assert.Equal(28L, seen.Ammo);
        // the helper is destroyed and its fire id released
        Assert.Equal(entities, r.H.Engine.State.Count);
        Assert.False(r.H.Host.NedFires.TryResolve(f1, out _));
        Assert.Equal(0, r.H.Host.NedFires.Count);
        // the fire came from NED: no echo
        Assert.Empty(r.H.NedSent<NedWeaponFire>());
        Assert.Equal(0, r.H.NedOut.Count);
        Assert.Equal(1, EventCount(r.H, r.H.Schema.WeaponFire));
    }

    [Fact]
    public void The_assembled_event_carries_the_source_ned_so_echo_suppression_keeps_it_away_from_NED()
    {
        var r = Setup();
        var f1 = Guid.NewGuid();

        r.H.NedStep(Fire(f1, T.Hawk, r.TargetNedId));
        r.H.NedStep(new NedFireAmmunition(f1, 502, 2));
        r.H.Steps(2);

        SimEvent? fire = null;
        foreach (var ev in r.H.Engine.State.Journal.RecentEvents)
        {
            if (ev.Type == r.H.Schema.WeaponFire)
            {
                fire = ev;
            }
        }

        Assert.NotNull(fire);
        Assert.Equal(r.H.NedSource, fire!.Source);
        Assert.Equal(f1, fire.Payload.Get(r.H.Schema.EventId).AsObject<Guid>());
        Assert.Equal(502, fire.Payload.Get(r.H.Schema.EventMunitionTkb).AsInt());
        Assert.Equal(2, fire.Payload.Get(r.H.Schema.EventQuantity).AsInt());
    }

    [Fact]
    public void The_ammunition_cannot_go_below_zero()
    {
        var r = Setup(ammo: 1);
        var f1 = Guid.NewGuid();

        r.H.NedStep(Fire(f1, T.Hawk, r.TargetNedId));
        r.H.NedStep(new NedFireAmmunition(f1, 502, 5));
        r.H.Steps(2);

        Assert.Equal(0, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
        Assert.Equal(5, Assert.Single(r.H.RprSent<RprWeaponFire>()).Quantity);
    }

    [Fact]
    public void A_shooter_without_a_stored_ammunition_fires_without_touching_any()
    {
        var r = Setup();
        r.H.NedStep(T.Master(40, "Eagle", 200));
        r.H.NedStep(T.Geo(40));
        r.H.Steps(3);
        r.H.ClearOutput();
        var f = Guid.NewGuid();

        r.H.NedStep(Fire(f, 40, r.TargetNedId));
        r.H.NedStep(new NedFireAmmunition(f, 502, 3));
        r.H.Steps(2);

        Assert.Single(r.H.RprSent<RprWeaponFire>());
        Assert.False(r.H.EntityOfNed(40)!.Has(r.H.Schema.Ammo));
    }

    [Fact]
    public void Two_fires_assemble_independently_and_in_any_order_of_their_parts()
    {
        var r = Setup();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        r.H.NedStep(Fire(a, T.Hawk, r.TargetNedId));
        r.H.NedStep(Fire(b, T.Hawk, 0));
        Assert.Equal(2, r.H.Host.NedFires.Count);
        r.H.NedStep(new NedFireAmmunition(b, 501, 4));
        r.H.NedStep(new NedFireAmmunition(a, 502, 2));
        r.H.Steps(2);

        var fires = r.H.RprSent<RprWeaponFire>();
        Assert.Equal(new[] { b, a }, fires.Select(f => f.EventId)); // in the order they completed
        Assert.Equal(24, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
        Assert.Null(fires[0].TargetObject);
        Assert.Equal(0, r.H.Host.NedFires.Count);
    }

    [Fact]
    public void A_repeated_first_part_is_ignored_with_a_warning()
    {
        var r = Setup();
        var f1 = Guid.NewGuid();
        r.H.Checker.Expect("already being assembled");

        r.H.NedStep(Fire(f1, T.Hawk, r.TargetNedId));
        r.H.NedStep(Fire(f1, T.Hawk, r.TargetNedId));
        r.H.NedStep(new NedFireAmmunition(f1, 502, 2));
        r.H.Steps(2);

        Assert.Single(r.H.RprSent<RprWeaponFire>());
        Assert.Equal(1, r.H.Checker.WarningsContaining("already being assembled"));
        Assert.Equal(28, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
    }

    [Fact]
    public void The_first_part_with_an_unknown_shooter_aborts()
    {
        var r = Setup();
        r.H.Checker.Expect("unknown shooter");

        r.H.NedStep(Fire(Guid.NewGuid(), 999, 0));

        Assert.Equal(0, r.H.Host.NedFires.Count);
        Assert.Equal(1, r.H.Engine.Diagnostics.TransactionFailures);
    }

    // ---- the second part never comes ---------------------------------------------------------------------------------

    [Fact]
    public void Without_a_second_part_one_RPR_fire_follows_after_one_second_with_the_selected_munition_and_a_warning()
    {
        var r = Setup();
        var f2 = Guid.NewGuid();
        r.H.Checker.Expect("completed by timeout");
        r.H.NedStep(Fire(f2, T.Hawk, r.TargetNedId, quantity: 1));
        double started = r.H.WallTime;

        r.H.Run(0.9);
        Assert.Equal(0, r.H.RprOut.Count); // not before the timeout
        Assert.Equal(1, r.H.Host.NedFires.Count);
        r.H.RunUntil(() => r.H.RprOut.Count > 0, maxSeconds: 0.5, what: "the fire was not completed by the timeout");
        r.H.Steps(2);

        var fire = Assert.Single(r.H.RprSent<RprWeaponFire>());
        Assert.Equal(f2, fire.EventId);
        Assert.Equal(T.Dis(501), fire.Munition); // the shooter's selected munition
        Assert.Equal(1, fire.Quantity);
        Assert.Equal(r.H.HandleOf(r.Target), fire.TargetObject);
        double sentAt = r.H.RprOut.Messages[0].WallTime;
        Assert.InRange(sentAt - started, 1.0, 1.25);
        Assert.Equal(1, r.H.Checker.WarningsContaining($"fire {f2} completed by timeout"));
        Assert.Equal(29, r.Shooter.Get(r.H.Schema.Ammo).AsInt()); // the quantity is taken off, too
        Assert.Equal(0, r.H.Host.NedFires.Count); // helper destroyed, fire id released
        Assert.Equal(0, EventCount(r.H, r.H.Schema.Detonation));
        Assert.Equal(1, EventCount(r.H, r.H.Schema.WeaponFire));
        Assert.Empty(r.H.NedSent<NedWeaponFire>());
    }

    [Fact]
    public void The_timeout_is_applied_once_and_the_late_second_part_is_a_warning_and_nothing_else()
    {
        var r = Setup();
        var f2 = Guid.NewGuid();
        r.H.Checker.Expect("completed by timeout");
        r.H.Checker.Expect("unknown fire");
        r.H.NedStep(Fire(f2, T.Hawk, r.TargetNedId));
        r.H.Run(2.0);
        Assert.Single(r.H.RprSent<RprWeaponFire>());

        r.H.NedStep(new NedFireAmmunition(f2, 502, 2));
        r.H.Run(2.0);

        Assert.Single(r.H.RprSent<RprWeaponFire>()); // still one
        Assert.Equal(29, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
        Assert.Equal(1, r.H.Checker.WarningsContaining("unknown fire"));
        Assert.Equal(1, r.H.Checker.WarningsContaining("completed by timeout"));
    }

    [Fact]
    public void A_timeout_without_a_selected_munition_sends_the_default_type_with_the_munition_warning()
    {
        var r = Setup();
        r.H.NedStep(T.Master(40, "Eagle", 200));
        r.H.NedStep(T.Geo(40));
        r.H.NedStep(new NedWeaponStatus(40, 0, 9));
        r.H.Steps(3);
        r.H.ClearOutput();
        r.H.Checker.Expect("completed by timeout");
        r.H.Checker.Expect("No munition given");

        r.H.NedStep(Fire(Guid.NewGuid(), 40, 0));
        r.H.Run(1.3);

        var fire = Assert.Single(r.H.RprSent<RprWeaponFire>());
        Assert.Equal(TypeCatalog.DefaultDis, fire.Munition);
        Assert.Equal(8, r.H.EntityOfNed(40)!.Get(r.H.Schema.Ammo).AsInt());
    }

    // ---- unknown second parts and the default profile ------------------------------------------------------------------

    [Fact]
    public void An_unknown_fire_id_in_the_ammunition_part_is_a_warning_and_nothing_else()
    {
        var r = Setup();
        r.H.Checker.Expect("unknown fire");
        r.H.Run(0.2);
        string snapshot = r.H.Snapshot();

        r.H.NedStep(new NedFireAmmunition(Guid.NewGuid(), 502, 2));
        r.H.Run(0.3);

        Assert.Equal(1, r.H.Checker.WarningsContaining("unknown fire"));
        Assert.Equal(0, r.H.RprOut.Count);
        Assert.Equal(0, r.H.NedOut.Count);
        Assert.Equal(snapshot, r.H.Snapshot());
        Assert.Equal(30, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
    }

    [Fact]
    public void The_default_configuration_still_fires_on_the_single_message_and_does_not_know_the_ammunition_part()
    {
        var r = Setup(NedRprConfigurations.Default);
        var f = Guid.NewGuid();

        r.H.NedStep(Fire(f, T.Hawk, r.TargetNedId, munition: 502, quantity: 2));
        r.H.Steps(2);

        var fire = Assert.Single(r.H.RprSent<RprWeaponFire>());
        Assert.Equal(T.Dis(502), fire.Munition);
        Assert.Equal(28, r.Shooter.Get(r.H.Schema.Ammo).AsInt());
        Assert.Equal(0, r.H.Host.NedFires.Count);

        r.H.NedStep(new NedFireAmmunition(f, 501, 9));
        Assert.Equal(1, r.H.Engine.Diagnostics.UnhandledMessages);
        Assert.Null(r.H.Engine.FindRule("FireAssemblerProcessor/Timeout"));
        Assert.Null(r.H.Engine.FindRule("FireAssemblerProcessor/Complete"));
    }

    [Fact]
    public void The_showcase_configuration_has_the_assembler_rules_and_no_single_message_fire()
    {
        var r = Setup();

        Assert.NotNull(r.H.Engine.FindRule("FireAssemblerProcessor/Timeout"));
        Assert.NotNull(r.H.Engine.FindRule("FireAssemblerProcessor/Complete"));
        r.H.NedStep(Fire(Guid.NewGuid(), T.Hawk, r.TargetNedId, munition: 502, quantity: 2));
        r.H.Steps(2);

        Assert.Equal(0, r.H.RprOut.Count); // a first part alone is not a fire in the showcase
    }

    [Fact]
    public void A_fire_part_for_a_member_target_resolves_the_member()
    {
        var r = Setup();
        r.H.NedStep(S.Viper(0, 1, 2));
        r.H.Steps(3);
        var member = S.NedMember(r.H, S.ViperId, 2);
        r.H.ClearOutput();
        var f = Guid.NewGuid();

        r.H.NedStep(Fire(f, T.Hawk, S.ViperId, targetMember: 2));
        r.H.NedStep(new NedFireAmmunition(f, 501, 1));
        r.H.Steps(2);

        Assert.Equal(r.H.HandleOf(member), Assert.Single(r.H.RprSent<RprWeaponFire>()).TargetObject);
    }
}
