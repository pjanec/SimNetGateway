using System.Numerics;
using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Combat.Contracts;
using Fdp.Toolkit.Combat.Events;
using Fdp.Toolkit.Perception.Signatures;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Tkb.Domain;
using Fdp.Toolkit.Time;
using Hrot.Map.Common.Components;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// DPS round 3 in the host (DD-019 3.1, 3.2, 3.4, 3.7): the suggested-view seam, impacts of rounds on the static world, a round's launch time and motor burn, and effects in
/// replay derived through HROT's per-event rules, on a staged HROT world with HROT's own TKB.
/// </summary>
public class RoundThreeGatherTests
{
    private const long MissileType = 0x7A01, PlainRoundType = 0x7A02, ShooterTkb = Hrot.Map.Common.TkbEntityTypes.Tank_T72;

    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);

        public List<T> Of<T>() => All.OfType<T>().ToList();
    }

    private sealed class Stage : PackHarness
    {
        private double _now = 1000;

        public Stage()
            : base(dis: true)
        {
            foreach (var template in Hrot.Map.Common.HrotEnvironment.CreateTkb().GetAll())
            {
                TestTkb.Register(template);
            }

            var missile = new TkbTemplate("test missile", MissileType) { DisType = new DISEntityType { Kind = 2, Domain = 1, Country = 225, Category = 2 } };
            missile.AddDescriptor(new MissileDto { BurnSeconds = 5f });
            TestTkb.Register(missile);
            TestTkb.Register(new TkbTemplate("test round", PlainRoundType) { DisType = new DISEntityType { Kind = 2, Domain = 1, Country = 225, Category = 2 } });
            World.SetSingletonManaged<ITkbDatabase>(TestTkb);
            foreach (var register in new Action[]
            {
                () => World.RegisterComponent<LightweightRound>(), () => World.RegisterComponent<BallisticProjectile>(), () => World.RegisterComponent<NetworkVelocity>(),
            })
            {
                register();
            }

            Steps(3);
        }

        public Capture Gather(double simTime)
        {
            var inbound = new InMemoryInbound(new SourceId(99), "test");
            _now += 0.1;
            Adapter.GatherEntities(World, inbound, _now, simTime);
            var sink = new Capture();
            inbound.Drain(sink, int.MaxValue);
            return sink;
        }

        public Capture GatherEvents()
        {
            var inbound = new InMemoryInbound(new SourceId(99), "test");
            Adapter.GatherEvents(World, inbound);
            var sink = new Capture();
            inbound.Drain(sink, int.MaxValue);
            return sink;
        }

        public Capture GatherReplay(double simNow)
        {
            var inbound = new InMemoryInbound(new SourceId(99), "test");
            Adapter.GatherReplayEffects(World, inbound, simNow);
            var sink = new Capture();
            inbound.Drain(sink, int.MaxValue);
            return sink;
        }

        public Entity AddRound(int id, long munition, Vector3 at, Vector3 velocity, bool combat, uint spawnTick = 0, double launchedAt = 0.0)
        {
            var e = AddHrotEntity(id, at, name: "round", tkb: munition);
            World.SetComponent(e, new SimVelocity { Linear = velocity });
            World.AddComponent(e, new LightweightRound { LaunchedAt = (SimInstant)launchedAt });
            if (combat)
            {
                World.AddComponent(e, new BallisticProjectile { Muzzle = new WorldPoint(at.X, at.Y, at.Z), Ammo = munition, SpawnTick = spawnTick });
            }

            return e;
        }

        public void SetClock(double total, float delta, long frame) => World.SetSingleton(new GlobalTime { TotalTime = total, DeltaTime = delta, TimeScale = 1f, FrameNumber = frame });

        public long MunitionWithEffects() => (long)TestTkb.GetAll().First(t => t.DisType.Kind == 2 && (long)t.TkbType != MissileType && (long)t.TkbType != PlainRoundType).TkbType;
    }

    // ---- the suggested view (3.1): a seam, HROT's own CameraSuggestion by default ------------------------------------------------------------

    private sealed class FakeSource : ISuggestedViewSource
    {
        public HrotView? Current;

        public bool TryRead(EntityRepository world, out HrotView view)
        {
            view = Current.GetValueOrDefault();
            return Current is not null;
        }
    }

    private static HrotView View(string shot) =>
        new(HrotViewSource.Director, HrotViewMode.Fixed, 0, 0f, 0f, new HrotVec3(1, 2, 3), HrotQuat.Identity, 50f, shot, 7.0);

    [Fact]
    public void The_host_sends_no_view_while_HROT_suggests_none_the_world_has_no_camera_suggestion()
    {
        using var h = new Stage();
        Assert.Empty(h.Gather(1.0).Of<HrotView>());
        Assert.Empty(h.Gather(2.0).Of<HrotView>());
    }

    [Fact]
    public void A_wired_source_is_read_every_gather_and_its_end_is_one_sample_with_source_none()
    {
        var gather = new ViewGather();
        var source = new FakeSource();
        var world = new EntityRepository();
        var inbound = new InMemoryInbound(new SourceId(99), "test");
        var sink = new Capture();

        gather.Gather(world, source, inbound, 1.0);   // answers nothing yet: nothing is sent, and no "None" for a source that never answered
        source.Current = View("A");
        gather.Gather(world, source, inbound, 2.0);
        gather.Gather(world, source, inbound, 3.0);
        source.Current = null;
        gather.Gather(world, source, inbound, 4.0);
        gather.Gather(world, source, inbound, 5.0);
        gather.Gather(world, null, inbound, 6.0);
        inbound.Drain(sink, int.MaxValue);

        var views = sink.Of<HrotView>();
        Assert.Equal(3, views.Count);
        Assert.Equal(["A", "A", string.Empty], views.Select(v => v.Shot).ToArray());
        Assert.Equal((HrotViewSource.None, 4.0), (views[2].Source, views[2].SimTime));
    }

    // ---- impacts (3.2) ----------------------------------------------------------------------------------------------------------------------

    private static RoundImpactNotification Hit(Entity shooter, long ammo, ImpactKind kind, float x = 5, float y = 6, float z = 0.1f, ImpactMaterial material = ImpactMaterial.Unknown) =>
        new() { Shooter = shooter, X = x, Y = y, Z = z, NormalZ = 1f, Ammo = ammo, Kind = (byte)kind, Material = (byte)material };

    /// <summary>
    /// T-051 (HROT R-11007, CE-11024): an impact is read from the EFFECT ENTITY HROT's spawner made of it - one <c>Impact</c> per entity with a non-zero <c>EffectCause.Impact</c>, kind = Impact - 1, the effect's own position and
    /// birth time, the shooter mapped to its network id, the surface / material / munition as the cause says; the normal is not on the entity (0 = unknown). Mutations: kind off by one; shooter unmapped.
    /// </summary>
    [Fact]
    public void A_round_impact_is_one_Impact_from_its_effect_entity_with_kind_shooter_surface_and_position()
    {
        using var h = new Stage();
        var shooter = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
        long ammo = h.MunitionWithEffects();
        h.GatherEffects(10.0);   // the baseline
        var stop = Hit(shooter, ammo, ImpactKind.Stop);
        var entry = Hit(shooter, ammo, ImpactKind.Entry, 7, 8, 5f, ImpactMaterial.Concrete);
        var exit = Hit(Entity.Null, ammo, ImpactKind.Exit, 9, 8, 5f);
        foreach (var hit in new[] { stop, entry, exit })
        {
            var captured = hit;
            h.SpawnEffects(10.1, specs => Hrot.Look.Effects.EffectRules.ForImpact(in captured, h.World, h.TestTkb, specs));
        }

        var effects = h.GatherEffects(10.2);
        var impacts = h.GatheredImpacts;

        Assert.NotEmpty(effects);   // the derived puff and mark still arrive as Effect
        Assert.Equal(3, impacts.Count);
        var byKind = impacts.ToDictionary(i => i.Kind);
        Assert.Equal((HrotEffectSurface.Ground, 2001, ammo, new HrotVec3(5, 6, 0.1), 10.1), (byKind[HrotImpactKind.Stopped].Surface, byKind[HrotImpactKind.Stopped].ShooterId, byKind[HrotImpactKind.Stopped].MunitionTkb, byKind[HrotImpactKind.Stopped].Position with { Z = 0.1 }, byKind[HrotImpactKind.Stopped].SimTime));   // the puff, just off the face; HROT stamps ONLY the puff, the mark carries Impact 0 (CE-11030)
        Assert.Equal((HrotEffectSurface.Building, "Concrete"), (byKind[HrotImpactKind.Entered].Surface, byKind[HrotImpactKind.Entered].Material));
        Assert.Equal(string.Empty, byKind[HrotImpactKind.Stopped].Material);
        Assert.Equal((0, new HrotVec3(0, 0, 1)), (byKind[HrotImpactKind.Left].ShooterId, byKind[HrotImpactKind.Left].Normal));   // no shooter; the normal is the notification's, through the cause (T-057)
        bool mark = false;
        foreach (var e in h.World.Query().With<Hrot.Map.Common.Components.EffectCause>().Build())
        {
            mark |= h.World.GetComponentRO<Hrot.Map.Common.Components.EffectCause>(e).Impact == 0;
        }

        Assert.True(mark, "HROT's marks exist (Impact 0) and make no Impact");
        h.GatherEffects(10.3);
        Assert.Empty(h.GatheredImpacts);   // never sent again
    }

    /// <summary>T-057: no fold - one Impact per effect entity with a non-zero Impact, whatever its neighbours. (HROT stamps one per round impact since CE-11030; an entity stamped twice would be two impacts.) Mutation: the fold reintroduced.</summary>
    [Fact]
    public void Every_effect_entity_with_an_Impact_makes_one_Impact_and_none_is_folded()
    {
        using var h = new Stage();
        var shooter = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
        long ammo = h.MunitionWithEffects();
        h.GatherEffects(10.0);
        var hit = Hit(shooter, ammo, ImpactKind.Stop);
        var specs = new List<Hrot.Look.Effects.EffectSpec>();
        Hrot.Look.Effects.EffectRules.ForImpact(in hit, h.World, h.TestTkb, specs);
        Assert.True(specs.Count >= 2);
        h.SpawnEffects(10.1, list => { foreach (var sp in specs) { var c = sp; c.Cause = c.Cause!.Value with { Impact = 3 }; list.Add(c); } });   // both stamped, as an older recording or a rule that stamped both would
        h.GatherEffects(10.2);
        Assert.Equal(specs.Count, h.GatheredImpacts.Count);
    }

    /// <summary>A recording shown (or a state restored) holds the same effect entities: the same Impact comes out, once, in a replay.</summary>
    [Fact]
    public void A_restored_effect_entity_in_a_replay_gives_the_same_Impact_as_live()
    {
        long ammo;
        HrotImpact live, replayed;
        using (var h = new Stage())
        {
            var shooter = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
            ammo = h.MunitionWithEffects();
            h.GatherEffects(10.0);
            var hit = Hit(shooter, ammo, ImpactKind.Stop);
            h.SpawnEffects(10.1, specs => Hrot.Look.Effects.EffectRules.ForImpact(in hit, h.World, h.TestTkb, specs));
            h.GatherEffects(10.2);
            live = Assert.Single(h.GatheredImpacts);
        }

        using (var h = new Stage())
        {
            var shooter = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
            h.MunitionWithEffects();
            h.GatherEffects(10.0, replaying: true);   // entering the replay: what is there is "already sent"
            var hit = Hit(shooter, ammo, ImpactKind.Stop);
            h.SpawnEffects(10.1, specs => Hrot.Look.Effects.EffectRules.ForImpact(in hit, h.World, h.TestTkb, specs));   // the recording's next effect entity appears
            h.GatherEffects(10.2, replaying: true);
            replayed = Assert.Single(h.GatheredImpacts);
        }

        Assert.Equal(live, replayed);
    }

    /// <summary>A detonation's effect carries Impact 0 (not a round's impact): it makes an Effect and NO Impact.</summary>
    [Fact]
    public void A_detonations_effect_gives_no_Impact()
    {
        using var h = new Stage();
        var shooter = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
        h.GatherEffects(10.0);
        var burst = new DetonationNotification { Shooter = shooter, Target = Entity.Null, Ammo = h.MunitionWithEffects(), HitX = 5, HitY = 6, HitZ = 0.1f, Damage = 10f };
        h.SpawnEffects(10.1, specs => Hrot.Look.Effects.EffectRules.ForDetonation(in burst, h.World, h.TestTkb, specs));

        Assert.NotEmpty(h.GatherEffects(10.2));
        Assert.Empty(h.GatheredImpacts);
    }

    /// <summary>ONE path: the effect entity. HROT's round-impact EVENT is no longer read, so an impact that has both an event and an entity is sent once (a replay re-sends no event, an entity is what both live and replay hold). Mutation: the event path read again.</summary>
    [Fact]
    public void An_impact_with_both_an_event_and_an_entity_is_sent_once()
    {
        using var h = new Stage();
        var shooter = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
        h.GatherEffects(10.0);
        var hit = Hit(shooter, h.MunitionWithEffects(), ImpactKind.Stop);
        h.SpawnEffects(10.1, specs => Hrot.Look.Effects.EffectRules.ForImpact(in hit, h.World, h.TestTkb, specs));
        h.PublishForNextFrame(hit);

        int fromEvents = h.GatherEvents().Of<HrotImpact>().Count;
        h.GatherEffects(10.2);

        Assert.Equal(1, fromEvents + h.GatheredImpacts.Count);
    }

    /// <summary>HOT path: with effect entities alive and already sent, a steady frame of the effect gather (Impact included) allocates nothing.</summary>
    [Fact]
    public void A_steady_effect_gather_with_impact_entities_allocates_nothing()
    {
        using var h = new Stage();
        var shooter = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
        long ammo = h.MunitionWithEffects();
        h.GatherEffects(10.0);
        var hit = Hit(shooter, ammo, ImpactKind.Stop);
        h.SpawnEffects(10.1, specs => Hrot.Look.Effects.EffectRules.ForImpact(in hit, h.World, h.TestTkb, specs));
        Assert.Single(h.GatheredImpacts.Concat(Collect(h, 10.2)));
        var inbound = new SimNetGateway.Engine.InMemory.InMemoryInbound(new SimNetGateway.Engine.SourceId(99), "test");
        double t = 10.3;
        for (int i = 0; i < 20; i++, t += 0.016)
        {
            h.Adapter.GatherEffects(h.World, inbound, t, false);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++, t += 0.016)
        {
            h.Adapter.GatherEffects(h.World, inbound, t, false);
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static List<HrotImpact> Collect(Stage h, double t)
    {
        h.GatherEffects(t);
        return h.GatheredImpacts;
    }

    // ---- a round's launch time and motor (3.4) ----------------------------------------------------------------------------------------

    [Fact]
    public void A_round_carries_the_launch_time_HROT_stamped_on_it_and_its_motor_burn_from_the_tkb()
    {
        using var h = new Stage();
        h.SetClock(100.0, 0.05f, 50);
        h.AddRound(3001, MissileType, new Vector3(1, 1, 1), new Vector3(300, 0, 0), combat: true, spawnTick: 47, launchedAt: 99.7);   // the spawn tick says 99.85: HROT's stamp wins

        var round = Assert.Single(h.Gather(100.0).Of<HrotRound>());
        Assert.Equal(5f, round.BurnSeconds);
        Assert.Equal(99.7, round.LaunchedAt, 6);
        Assert.Equal(99.7, round.LaunchedAt, 6);
    }

    [Fact]
    public void A_round_with_no_motor_data_has_no_burn_and_one_with_no_stamped_launch_is_launched_when_first_seen()
    {
        using var h = new Stage();
        h.SetClock(100.0, 0.05f, 50);
        h.AddRound(3002, PlainRoundType, new Vector3(1, 1, 1), new Vector3(300, 0, 0), combat: true, spawnTick: 50);

        var round = Assert.Single(h.Gather(100.0).Of<HrotRound>());

        Assert.Equal((0f, 100.0), (round.BurnSeconds, round.LaunchedAt));
    }

    [Fact]
    public void A_replicated_round_with_no_ballistic_record_keeps_its_stamped_launch_and_a_later_change_keeps_it()
    {
        using var h = new Stage();
        h.SetClock(100.0, 0.05f, 50);
        var round = h.AddRound(3003, MissileType, new Vector3(1, 1, 1), new Vector3(300, 0, 0), combat: false, launchedAt: 98.25);

        var first = Assert.Single(h.Gather(100.0).Of<HrotRound>());
        Assert.Equal(98.25, first.LaunchedAt);

        h.World.SetComponent(round, new SimVelocity { Linear = Vector3.Zero });   // it lies now: a new sample, the same launch time
        var later = Assert.Single(h.Gather(107.0).Of<HrotRound>());
        Assert.True(later.Lying);
        Assert.Equal((98.25, 5f), (later.LaunchedAt, later.BurnSeconds));
    }

    // ---- effects in replay (3.7): the same state path as live (Round 6) ---------------------------------------------------------------

    [Fact]
    public void In_replay_a_recorded_burst_and_a_recorded_muzzle_flash_give_effects_from_the_recorded_effect_entities_and_fire_and_detonation_still_flow()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
        long ammo = h.MunitionWithEffects();
        Assert.Empty(h.GatherReplay(1.0).All);   // entering a replay: only a baseline
        Assert.Empty(h.GatherEffects(1.0, replaying: true));

        // what HROT recorded of a burst and of a shot: its memory entry, the effect entities of both
        RecentDetonations.Add(h.World, new Vector3(50, 20, 0.4f), 1.05, tank, ammo);
        var burst = new DetonationNotification { Shooter = tank, Ammo = ammo, HitX = 50, HitY = 20, HitZ = 0.4f };
        var shot = new WeaponFireNotification { Shooter = tank, WeaponIndex = 0, Quantity = 1 };
        h.SpawnEffects(1.05, specs => Hrot.Look.Effects.EffectRules.ForDetonation(in burst, h.World, h.TestTkb, specs));
        h.SpawnEffects(1.05, specs => Hrot.Look.Effects.EffectRules.ForShot(in shot, h.World, h.TestTkb, specs));
        var got = h.GatherReplay(1.1);
        var effects = h.GatherEffects(1.1, replaying: true);

        var boom = effects.Where(e => e.Kind == HrotEffectKind.Explosion).ToList();
        Assert.NotEmpty(boom);
        Assert.Equal(HrotEffectSurface.Ground, boom[0].Surface);
        Assert.Equal(new HrotVec3(50, 20, 0.4f), boom[0].At);
        Assert.Equal(1.05, boom[0].StartedAt);
        var muzzle = Assert.Single(effects, e => e.Kind == HrotEffectKind.MuzzleFlash);
        Assert.Equal((2001, HrotEffectSurface.Air), (muzzle.AnchorEntity, muzzle.Surface));
        Assert.Single(got.Of<HrotDetonation>());   // the replay's fire and detonation still flow, from the recorded memory and flash
        Assert.Single(got.Of<HrotFire>());
        Assert.Empty(got.Of<HrotEffect>());   // and the replay path of fire and detonation makes no effect of its own

        Assert.Empty(h.GatherReplay(1.2).All);   // each recorded entity once
        Assert.Empty(h.GatherEffects(1.2, replaying: true));
    }

    [Fact]
    public void After_a_seek_or_a_change_between_live_and_replay_the_recorded_past_makes_no_effects()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
        long ammo = h.MunitionWithEffects();
        h.GatherEffects(1.0, replaying: true);
        var burst = new DetonationNotification { Shooter = tank, Ammo = ammo, HitX = 50, HitY = 20, HitZ = 0.4f };
        h.SpawnEffects(1.05, specs => Hrot.Look.Effects.EffectRules.ForDetonation(in burst, h.World, h.TestTkb, specs));

        Assert.Empty(h.GatherEffects(40.0, replaying: true));   // a jump of more than 2 s: what lies behind it is only taken as already sent
        h.SpawnEffects(40.5, specs => Hrot.Look.Effects.EffectRules.ForDetonation(in burst, h.World, h.TestTkb, specs));
        Assert.NotEmpty(h.GatherEffects(40.6, replaying: true));   // what is recorded after the seek flows

        h.SpawnEffects(40.65, specs => Hrot.Look.Effects.EffectRules.ForDetonation(in burst, h.World, h.TestTkb, specs));
        Assert.Empty(h.GatherEffects(40.7, replaying: false));   // leaving the replay: a new baseline
        h.SpawnEffects(40.8, specs => Hrot.Look.Effects.EffectRules.ForDetonation(in burst, h.World, h.TestTkb, specs));
        Assert.NotEmpty(h.GatherEffects(40.9, replaying: false));   // live is the same path
    }

    // ---- round 5 (DD-019 section 9, Round 5): HROT's own suggested camera, effect cause, airborne ---------------------------------------------

    private static CameraSuggestion Suggestion(Entity bound, int boundNetId) => new()
    {
        Source = CameraSource.Director,
        Mode = CameraMode.FixedKey,
        ShoulderSide = -1,
        Bound = bound,
        BoundNetId = boundNetId,
        Distance = 2.2f,
        Position = new Vector3(10, 20, 30),
        LookAt = new Vector3(0, 0, 0),
        Orientation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
        FovyDegrees = 55f,
        Cut = 4,
        Take = new FixedString64("Take 3"),
    };

    [Fact]
    public void The_view_is_HROTs_CameraSuggestion_singleton_with_its_names_target_pose_fov_cut_and_take()
    {
        using var h = new Stage();
        var unit = h.AddHrotEntity(2001, Vector3.Zero, tkb: ShooterTkb);
        h.SetClock(33.5, 0.05f, 10);
        h.World.SetSingleton(Suggestion(unit, 0));

        var view = Assert.Single(h.Gather(33.5).Of<HrotView>());

        Assert.Equal((HrotViewSource.Director, HrotViewMode.Fixed, 2001, 2.2f, -1f, 55f, "Take 3", 4, 33.5), (view.Source, view.Mode, view.TargetId, view.Distance, view.Side, view.FovDeg, view.Shot, view.Cut, view.SimTime));
        Assert.Equal(new HrotVec3(10, 20, 30), view.Position);
        Assert.Equal(new HrotQuat(0.1f, 0.2f, 0.3f, 0.9f), view.Orientation);
    }

    [Fact]
    public void The_directors_follow_unit_is_named_by_its_network_id_before_the_bound_entity()
    {
        var source = new HrotCameraViewSource();
        using var world = new EntityRepository();
        Assert.False(source.TryRead(world, out _));   // a node with no map has no suggestion

        world.SetSingleton(Suggestion(Entity.Null, 77) with { Source = CameraSource.Follow, Mode = CameraMode.Shoulder });
        Assert.True(source.TryRead(world, out var view));
        Assert.Equal((HrotViewSource.Follow, HrotViewMode.Shoulder, 77), (view.Source, view.Mode, view.TargetId));
    }


    [Fact]
    public void An_effect_entity_that_struck_a_unit_is_a_unit_effect_by_its_recorded_cause_live_and_in_replay()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
        var target = h.AddHrotEntity(2002, new Vector3(50, 20, 0.4f), tkb: ShooterTkb);
        long ammo = h.MunitionWithEffects();
        h.GatherEffects(1.0);

        // HROT's spawner made the effect of a burst that struck the unit: the cause says what and how hard
        var burst = new DetonationNotification { Shooter = tank, Target = target, Ammo = ammo, HitX = 50, HitY = 20, HitZ = 0.4f, Damage = 80f };
        h.SpawnEffects(1.05, specs => Hrot.Look.Effects.EffectRules.ForDetonation(in burst, h.World, h.TestTkb, specs));
        var effects = h.GatherEffects(1.1);

        Assert.NotEmpty(effects);
        Assert.All(effects, e => Assert.Equal(HrotEffectSurface.Vehicle, e.Surface));   // not the ground mark a burst with no known target gets
        Assert.Equal((ulong)h.TestTkb.GetAll().First(t => (long)t.TkbType == ammo).DisType.Value, effects[0].MunitionDis);   // the munition comes from the cause's ammo
    }

    [Fact]
    public void A_world_with_no_effect_entities_sends_no_effect_and_a_non_effect_entity_with_a_lifespan_is_ignored()
    {
        using var h = new Stage();
        h.GatherEffects(1.0);
        var other = h.World.CreateEntity();   // a timed entity that is no effect of the type database
        h.World.AddComponent(other, new SimTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity });
        h.World.AddComponent(other, new TkbIdentity { TkbType = (Fdp.Core.TkbTypeId)0x5EED });
        h.World.AddComponent(other, Lifespan.Starting(1.0, 5f));

        Assert.Empty(h.GatherEffects(1.1));
    }
}
