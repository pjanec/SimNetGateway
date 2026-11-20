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

        public Entity AddRound(int id, long munition, Vector3 at, Vector3 velocity, bool combat, uint spawnTick = 0)
        {
            var e = AddHrotEntity(id, at, name: "round", tkb: munition);
            World.SetComponent(e, new SimVelocity { Linear = velocity });
            World.AddComponent(e, default(LightweightRound));
            if (combat)
            {
                World.AddComponent(e, new BallisticProjectile { Muzzle = new WorldPoint(at.X, at.Y, at.Z), Ammo = munition, SpawnTick = spawnTick });
            }

            return e;
        }

        public void SetClock(double total, float delta, long frame) => World.SetSingleton(new GlobalTime { TotalTime = total, DeltaTime = delta, TimeScale = 1f, FrameNumber = frame });

        public long MunitionWithEffects() => (long)TestTkb.GetAll().First(t => t.DisType.Kind == 2 && (long)t.TkbType != MissileType && (long)t.TkbType != PlainRoundType).TkbType;
    }

    // ---- the suggested view (3.1): a seam with no implementation ------------------------------------------------------------------------

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
        new("Director", "Fixed", 0, 0f, 0f, new HrotVec3(1, 2, 3), HrotQuat.Identity, 50f, shot, 7.0);

    [Fact]
    public void The_host_sends_no_view_while_no_source_is_wired()
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
        Assert.Equal(("None", 4.0), (views[2].Source, views[2].SimTime));
    }

    // ---- impacts (3.2) ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_round_impact_is_published_with_its_kind_surface_and_position()
    {
        using var h = new Stage();
        var shooter = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
        long ammo = h.MunitionWithEffects();
        h.SetClock(12.5, 0.05f, 100);
        h.World.Bus.Publish(new RoundImpactNotification { Shooter = shooter, X = 5, Y = 6, Z = 0.1f, NormalZ = 1f, Ammo = ammo, Kind = (byte)ImpactKind.Stop });
        h.World.Bus.Publish(new RoundImpactNotification { Shooter = shooter, X = 7, Y = 8, Z = 5f, NormalX = -1f, Ammo = ammo, Kind = (byte)ImpactKind.Entry });
        h.World.Bus.Publish(new RoundImpactNotification { Shooter = Entity.Null, X = 9, Y = 8, Z = 5f, Ammo = ammo, Kind = (byte)ImpactKind.Exit });
        h.World.Bus.SwapBuffers();

        var impacts = h.GatherEvents().Of<HrotImpact>();

        Assert.Equal(3, impacts.Count);
        Assert.Equal(("Stop", "Ground", 2001, ammo, new HrotVec3(5, 6, 0.1f), new HrotVec3(0, 0, 1), 12.5), (impacts[0].Kind, impacts[0].Surface, impacts[0].ShooterId, impacts[0].MunitionTkb, impacts[0].Position, impacts[0].Normal, impacts[0].SimTime));
        Assert.Equal(("Entry", "Building", string.Empty), (impacts[1].Kind, impacts[1].Surface, impacts[1].Material));   // HROT's event carries no material
        Assert.Equal(("Exit", 0, new HrotVec3(0, 0, 0)), (impacts[2].Kind, impacts[2].ShooterId, impacts[2].Normal));   // no shooter, no normal: unknown
        h.World.Bus.SwapBuffers();
        Assert.Empty(h.GatherEvents().Of<HrotImpact>());   // the next frame has none
    }

    [Fact]
    public void An_impact_is_also_an_effect_through_the_same_event()
    {
        using var h = new Stage();
        h.PublishForNextFrame(new RoundImpactNotification { X = 5, Y = 6, Z = 0.1f, NormalZ = 1f, Ammo = h.MunitionWithEffects(), Kind = (byte)ImpactKind.Stop });

        var got = h.GatherEvents();

        Assert.Single(got.Of<HrotImpact>());
        Assert.NotEmpty(got.Of<HrotEffect>());   // the derived puff and mark still arrive as Effect (HROT's ForFrame)
    }

    // ---- a round's launch time and motor (3.4) ----------------------------------------------------------------------------------------

    [Fact]
    public void A_round_carries_its_launch_time_from_the_spawn_frame_and_its_motor_burn_from_the_tkb()
    {
        using var h = new Stage();
        h.SetClock(100.0, 0.05f, 50);
        h.AddRound(3001, MissileType, new Vector3(1, 1, 1), new Vector3(300, 0, 0), combat: true, spawnTick: 47);   // spawned three frames ago

        var round = Assert.Single(h.Gather(100.0).Of<HrotRound>());

        Assert.Equal(5f, round.BurnSeconds);
        Assert.Equal(100.0 - 3 * 0.05, round.LaunchedAt, 6);
    }

    [Fact]
    public void A_round_with_no_motor_data_has_no_burn_and_a_launch_in_the_same_frame_is_the_gather_time()
    {
        using var h = new Stage();
        h.SetClock(100.0, 0.05f, 50);
        h.AddRound(3002, PlainRoundType, new Vector3(1, 1, 1), new Vector3(300, 0, 0), combat: true, spawnTick: 50);

        var round = Assert.Single(h.Gather(100.0).Of<HrotRound>());

        Assert.Equal((0f, 100.0), (round.BurnSeconds, round.LaunchedAt));
    }

    [Fact]
    public void A_replicated_round_with_no_ballistic_record_is_launched_when_first_seen_and_a_later_change_keeps_it()
    {
        using var h = new Stage();
        h.SetClock(100.0, 0.05f, 50);
        var round = h.AddRound(3003, MissileType, new Vector3(1, 1, 1), new Vector3(300, 0, 0), combat: false);

        var first = Assert.Single(h.Gather(100.0).Of<HrotRound>());
        Assert.Equal(100.0, first.LaunchedAt);

        h.World.SetComponent(round, new SimVelocity { Linear = Vector3.Zero });   // it lies now: a new sample, the same launch time
        var later = Assert.Single(h.Gather(107.0).Of<HrotRound>());
        Assert.True(later.Lying);
        Assert.Equal((100.0, 5f), (later.LaunchedAt, later.BurnSeconds));
    }

    // ---- effects in replay (3.7) ------------------------------------------------------------------------------------------------------

    [Fact]
    public void In_replay_a_recorded_burst_and_a_recorded_muzzle_flash_give_effects_through_HROTs_per_event_rules()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
        long ammo = h.MunitionWithEffects();
        Assert.Empty(h.GatherReplay(1.0).All);   // entering a replay: only a baseline

        RecentDetonations.Add(h.World, new Vector3(50, 20, 0.4f), 1.05, tank, ammo);
        var flash = h.World.CreateEntity();
        h.World.AddComponent(flash, new EffectAnchor { Shooter = tank, WeaponIndex = 0, Kind = EffectAnchorKind.Muzzle });
        h.World.AddComponent(flash, Lifespan.Starting(1.05, 0.1f));
        var got = h.GatherReplay(1.1);

        var boom = got.Of<HrotEffect>().Where(e => e.Kind == nameof(Fdp.Toolkit.Tkb.Domain.EffectKind.Explosion)).ToList();
        Assert.NotEmpty(boom);
        Assert.Equal(nameof(Hrot.Look.Effects.EffectSurface.Ground), boom[0].Surface);
        Assert.Equal(new HrotVec3(50, 20, 0.4f), boom[0].At);
        var muzzle = Assert.Single(got.Of<HrotEffect>(), e => e.Kind == nameof(Fdp.Toolkit.Tkb.Domain.EffectKind.MuzzleFlash));
        Assert.Equal((2001, nameof(Hrot.Look.Effects.EffectSurface.Air)), (muzzle.AnchorEntity, muzzle.Surface));
        Assert.Single(got.Of<HrotDetonation>());   // the replay's fire and detonation still flow, one effect path beside them
        Assert.Single(got.Of<HrotFire>());

        Assert.Empty(h.GatherReplay(1.2).Of<HrotEffect>());   // each recorded event once
    }

    [Fact]
    public void After_a_seek_the_recorded_past_makes_no_effects()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(2001, new Vector3(0, 0, 3), tkb: ShooterTkb);
        h.GatherReplay(1.0);
        RecentDetonations.Add(h.World, new Vector3(50, 20, 0.4f), 1.05, tank, h.MunitionWithEffects());

        Assert.Empty(h.GatherReplay(40.0).All);   // a jump of more than 2 s: what lies behind it is only taken as already sent
    }
}
