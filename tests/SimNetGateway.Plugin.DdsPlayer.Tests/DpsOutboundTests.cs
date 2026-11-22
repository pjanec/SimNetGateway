using System.Diagnostics;
using CycloneDDS.Runtime;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>The dps plugin outbound: HROT's pivot messages to DPS topics, read back by real CycloneDDS readers in the station's partition (in process).</summary>
public class DpsOutboundTests
{
    private static int _domainCounter = Environment.ProcessId % 50;

    // DDS domains of the test assemblies are disjoint (Plugin 1..50, Protocol.DdsPlayer 55..94, DpsClient 100..166): the projects run in parallel and a shared domain lets their participants meet; all stay below domain 167 because DDS uses UDP port 7400 + 250 * domain and Windows excludes or hands out the ports above 49152, where participant creation then fails at random
    internal static uint NewDomain() => (uint)(1 + Interlocked.Increment(ref _domainCounter) % 50);

    internal static bool WaitFor(Func<bool> condition, int timeoutMs = 6000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }

    internal sealed class FakeStation : IPlayerStation
    {
        public List<PlayerOffer> OfferList { get; } = new();

        public PlayerSlotSnapshot Slot => default;

        public IReadOnlyList<PlayerOffer> Offers => OfferList;

        public PlayerUnitStatus Unit => PlayerUnitStatus.None;

        public bool Join(PlayerJoin request) => false;

        public void Control(in PlayerControl frame)
        {
        }

        public void Leave()
        {
        }
    }

    /// <summary>A reader of one topic in a partition: the live samples by key, and the keys that went not-alive.</summary>
    internal sealed class Watch<T> : IDisposable
        where T : struct
    {
        private readonly DdsParticipant _participant;
        private readonly DdsReader<T> _reader;
        private readonly Func<T, string> _key;

        public Watch(uint domain, string partition, Func<T, string> key)
        {
            _key = key;
            _participant = new DdsParticipant(domain, defaultPartition: partition);
            _reader = new DdsReader<T>(_participant);
        }

        public Dictionary<string, T> Alive { get; } = new();

        public List<T> Events { get; } = new();

        public void Pump()
        {
            using var loan = _reader.Take(256);
            foreach (var sample in loan)
            {
                var data = sample.Data;
                if (sample.IsValid)
                {
                    Events.Add(data);
                }

                // a sample that was never taken before its instance was disposed arrives valid but not alive: the instance is gone
                if (sample.IsValid && sample.Info.InstanceState == DdsInstanceState.Alive)
                {
                    Alive[_key(data)] = data;
                }
                else
                {
                    Alive.Remove(_key(data));
                }
            }
        }

        public bool Until(Func<Watch<T>, bool> condition, int timeoutMs = 6000) => WaitFor(() =>
        {
            Pump();
            return condition(this);
        }, timeoutMs);

        public void Dispose()
        {
            _reader.Dispose();
            _participant.Dispose();
        }
    }

    internal sealed class Rig : IDisposable
    {
        private readonly PluginBound _bound;

        public Rig(uint domain, string partition = "station-1", IPlayerStation? station = null, DdsPlayerPlugin? plugin = null, IHostService? service = null,
            Action<string>? log = null, double leaseSeconds = 2.0, IGizmoSource? gizmoSource = null, Func<long>? stationTicks = null,
            int seats = 1, IReadOnlyList<IPlayerStation>? stations = null, int renderSeat = 1, string renderRoot = "", double heartbeatSeconds = 5.0)
        {
            Domain = domain;
            Plugin = plugin ?? new DdsPlayerPlugin();
            Settings = new DpsSettings { Domain = (int)domain, Partition = partition, LeaseSeconds = leaseSeconds, Seats = seats, RenderSeat = renderSeat, RenderRoot = renderRoot, HeartbeatSeconds = heartbeatSeconds };
            Config = (GatewayConfiguration.CreateDefault() with { Dis = null }).WithSide(DpsSide.SideKey, Settings);
            var sides = SideRegistry.Empty.With(Plugin.Side);
            Clock = new ManualClock();
            var builder = HrotDisConfigurations.For(Config, "test", null, null, sides).CreateBuilder(Clock);
            var hrot = builder.RequireInbound("hrot");
            HrotIn = new InMemoryInbound(hrot.Source, "hrot");
            builder.BindInbound("hrot", HrotIn);
            builder.BindOutbound<IGatewayToHrotMessage>("hrot", new InMemoryOutbound<IGatewayToHrotMessage>());
            Service = service ?? Plugin.CreateHostService(Settings, new PluginHostContext(_ => { }));
            _bound = Plugin.Bind(new SideBinding(builder, Config, Settings, Clock, Service, _ => null, log ?? (_ => { }), null, station, gizmoSource, stationTicks, stations));
            Transport = (DpsTransport)_bound.Lease!;
            Engine = builder.Build().Engine;
        }

        public uint Domain { get; }

        public DdsPlayerPlugin Plugin { get; }

        public DpsSettings Settings { get; }

        public GatewayConfiguration Config { get; }

        public ManualClock Clock { get; }

        public InMemoryInbound HrotIn { get; }

        public IHostService? Service { get; }

        public DpsTransport Transport { get; }

        public GatewayEngine Engine { get; }

        public void Send<T>(in T message)
        {
            HrotIn.Enqueue(in message);
            Step();
        }

        public void Step()
        {
            Service?.Pump();   // the host's beforeFrame: the plugin's host service (queued film calls, deadlines)
            _bound.Pump?.Invoke();
            Engine.Tick();
        }

        public void Dispose()
        {
            _bound.Lease?.Dispose();
        }
    }

    private static HrotEntityState State(int id, double simTime = 1.0, HrotVec3? at = null, HrotVec3? velocity = null, long? dis = null, string marking = "T-72", int force = 2,
        double health = 100, HrotStance stance = HrotStance.Unknown, HrotWeaponPosture weapon = HrotWeaponPosture.None, HrotVec3? aim = null, HrotInjury injury = HrotInjury.Ok) =>
        new(id, 103, dis ?? TankDis, marking, force, 0, at ?? new HrotVec3(10, 20, 3), HrotQuat.Identity, velocity ?? HrotVec3.Zero, HrotVec3.Zero, health, 100, simTime, stance, weapon,
            aim ?? HrotVec3.Zero, injury);

    private static readonly long TankDis = new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack();
    private static readonly long PersonDis = new DisTypeCode(3, 1, 225, 1, 1, 0, 0).Pack();

    private static string Id(int id) => id.ToString();

    [Fact]
    public void Entity_appears_with_type_domain_side_and_name()
    {
        uint domain = NewDomain();
        using var watch = new Watch<D.Entity>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);

        rig.Send(State(2001));
        rig.Send(State(1201, dis: PersonDis, marking: "Rifleman 1", force: 1));

        Assert.True(watch.Until(w => w.Alive.Count == 2));
        var tank = watch.Alive["2001"];
        Assert.Equal(103, tank.TypeId);
        Assert.Equal(D.Domain.DomainGround, tank.Domain);
        Assert.Equal((byte)2, tank.Side);
        Assert.Equal("T-72", tank.Name);
        Assert.Equal((byte)1, tank.Dis.Kind);
        Assert.Equal((ushort)225, tank.Dis.Country);
        Assert.Equal((byte)3, tank.Dis.Specific);
        var man = watch.Alive["1201"];
        Assert.Equal(D.Domain.DomainPerson, man.Domain);
        Assert.Equal((byte)1, man.Side);
        Assert.False(man.Playable);
    }

    [Fact]
    public void A_commander_is_an_entity_with_IsUnit_and_gets_no_body_topic_while_men_and_vehicles_are_not_units()
    {
        uint domain = NewDomain();
        long unitDis = new DisTypeCode(11, 1, 225, 1, 2, 1, 0).Pack();   // the host's unit kind: a squad
        using var entities = new Watch<D.Entity>(domain, "station-1", e => Id(e.EntityId));
        using var states = new Watch<D.EntityState>(domain, "station-1", e => Id(e.EntityId));
        using var persons = new Watch<D.PersonState>(domain, "station-1", e => Id(e.EntityId));
        using var looks = new Watch<D.VehicleLook>(domain, "station-1", e => Id(e.EntityId));
        using var appearances = new Watch<D.Appearance>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);

        rig.Send(State(3001, dis: unitDis, marking: "Alpha squad", at: new HrotVec3(50, 60, 0)));
        rig.Send(State(1201, dis: PersonDis, marking: "Rifleman 1"));
        rig.Send(State(2001));

        Assert.True(entities.Until(w => w.Alive.Count == 3));
        Assert.True(entities.Alive["3001"].IsUnit);
        Assert.False(entities.Alive["1201"].IsUnit);
        Assert.False(entities.Alive["2001"].IsUnit);
        Assert.Equal(D.Domain.DomainOther, entities.Alive["3001"].Domain);
        Assert.True(states.Until(w => w.Alive.ContainsKey("3001")));
        Assert.Equal(50.0, states.Alive["3001"].Position.X);   // at its centroid
        persons.Pump();
        looks.Pump();
        appearances.Pump();
        Assert.DoesNotContain("3001", persons.Alive.Keys);   // no body topic for a commander
        Assert.DoesNotContain("3001", looks.Alive.Keys);
        Assert.DoesNotContain("3001", appearances.Alive.Keys);
    }

    [Fact]
    public void Playable_follows_the_player_station_offers()
    {
        uint domain = NewDomain();
        var station = new FakeStation();
        station.OfferList.Add(new PlayerOffer(1201, "Rifleman", 1));
        using var watch = new Watch<D.Entity>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain, station: station);

        rig.Send(State(1201, dis: PersonDis));
        rig.Send(State(1202, dis: PersonDis));
        Assert.True(watch.Until(w => w.Alive.Count == 2));
        Assert.True(watch.Alive["1201"].Playable);
        Assert.False(watch.Alive["1202"].Playable);

        station.OfferList.Clear();
        station.OfferList.Add(new PlayerOffer(1202, "Sniper", 1));
        rig.Step();

        Assert.True(watch.Until(w => !w.Alive["1201"].Playable && w.Alive["1202"].Playable));
    }

    /// <summary>T-053: a role or a spawn area is an entity of the world (its id is a network id) but no unit to take: <c>Entity.Playable</c> is for the placed units of the offers only.</summary>
    [Fact]
    public void Playable_ignores_role_and_area_offers()
    {
        uint domain = NewDomain();
        var station = new FakeStation();
        station.OfferList.Add(new PlayerOffer(1201, "Rifleman", 1));
        station.OfferList.Add(new PlayerOffer(1202, "Role", 1, PlayerOfferKind.Role));
        station.OfferList.Add(new PlayerOffer(1203, "Area", 1, PlayerOfferKind.Area));
        using var watch = new Watch<D.Entity>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain, station: station);

        rig.Send(State(1201, dis: PersonDis));
        rig.Send(State(1202, dis: PersonDis));
        rig.Send(State(1203, dis: PersonDis));
        Assert.True(watch.Until(w => w.Alive.Count == 3));
        Assert.True(watch.Alive["1201"].Playable);
        Assert.False(watch.Alive["1202"].Playable);
        Assert.False(watch.Alive["1203"].Playable);
    }

    [Fact]
    public void Pose_is_sent_on_threshold_and_heartbeat_not_on_noise()
    {
        uint domain = NewDomain();
        using var watch = new Watch<D.EntityState>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        var written = rig.Transport.EntityState;

        rig.Send(State(2001, simTime: 1.0));
        Assert.Equal(1, written.Written);

        rig.Send(State(2001, simTime: 1.1, at: new HrotVec3(10.01, 20, 3)));   // noise: 1 cm
        rig.Send(State(2001, simTime: 1.2, velocity: new HrotVec3(0.02, 0, 0)));   // noise: 2 cm/s
        Assert.Equal(1, written.Written);

        rig.Send(State(2001, simTime: 1.3, at: new HrotVec3(10.5, 20, 3)));   // moved 50 cm
        Assert.Equal(2, written.Written);

        rig.Send(State(2001, simTime: 3.0, at: new HrotVec3(10.5, 20, 3)));   // quiet
        Assert.Equal(2, written.Written);
        rig.Send(State(2001, simTime: 6.4, at: new HrotVec3(10.5, 20, 3)));   // 5.1 s since the last sample: the heartbeat
        Assert.Equal(3, written.Written);

        rig.Send(State(2001, simTime: 6.5, at: new HrotVec3(10.5, 20, 3), health: 40));   // damage is a change
        Assert.Equal(4, written.Written);

        Assert.True(watch.Until(w => w.Alive.TryGetValue("2001", out var s) && s.SimTime == 6.5));
        Assert.Equal(D.PartCondition.PartConditionDamaged, watch.Alive["2001"].Damage);
        Assert.Equal(10.5, watch.Alive["2001"].Position.X);
    }

    [Fact]
    public void Person_detail_maps_to_person_state()
    {
        uint domain = NewDomain();
        using var watch = new Watch<D.PersonState>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        rig.Send(State(1201, simTime: 4.0, dis: PersonDis, stance: HrotStance.Crouched, weapon: HrotWeaponPosture.Aimed, aim: new HrotVec3(30, 40, 1), injury: HrotInjury.Wounded));

        rig.Send(new HrotPersonDetail(1201, HrotStance.Prone, 0.5f, HrotHeldItem.Binoculars, 2, new HrotVec3(7, 8, 9), true, 0.25f, HrotGesture.Waving, HrotAction.OpenDoor, 3001,
            HrotActionState.Running, 3.5, 2100, HrotSeatRole.Gunner, HrotCarryMode.Dragged));

        Assert.True(watch.Until(w => w.Alive.ContainsKey("1201")));
        var p = watch.Alive["1201"];
        Assert.Equal(D.Stance.StanceCrouched, p.Stance);
        Assert.Equal(D.Stance.StanceProne, p.TargetStance);
        Assert.Equal(0.5f, p.StanceProgress);
        Assert.Equal(D.WeaponPosture.WeaponPostureAimed, p.Posture);
        Assert.Equal(new HrotVec3(30, 40, 1), new HrotVec3(p.AimPoint.X, p.AimPoint.Y, p.AimPoint.Z));
        Assert.Equal((byte)2, p.ActiveSlot);
        Assert.Equal(D.HeldItem.HeldItemBinoculars, p.Held);
        Assert.True(p.HasLookTarget);
        Assert.Equal(8.0, p.LookTarget.Y);
        Assert.Equal(0.25f, p.HeadYaw);
        Assert.Equal(D.Gesture.GestureWaving, p.Gesture);
        Assert.Equal(D.Injury.InjuryWounded, p.Injury);
        Assert.Equal(D.ActionKind.ActionKindOpenDoor, p.Action);
        Assert.Equal(3001, p.ActionTargetId);
        Assert.Equal(D.ActionState.ActionStateRunning, p.ActionState);
        Assert.Equal(3.5, p.ActionStartSimTime);
        Assert.Equal(2100, p.SeatVehicleId);
        Assert.Equal(D.SeatRole.SeatRoleGunner, p.SeatRole);
        Assert.Equal(D.CarryMode.CarryModeDragged, p.CarryMode);
    }

    [Fact]
    public void Turret_pose_maps_to_articulation_part()
    {
        uint domain = NewDomain();
        using var watch = new Watch<D.Articulation>(domain, "station-1", a => $"{a.EntityId}/{a.PartId}");
        using var rig = new Rig(domain);
        rig.Send(State(2001));

        rig.Send(new HrotTurretPose(2001, 2000, 0.5, 0.125));
        rig.Send(new HrotTurretPose(2001, 2001, -0.25, 0.0));
        rig.Send(new HrotTurretPose(9999, 2000, 1.0, 1.0));   // unknown entity: ignored

        Assert.True(watch.Until(w => w.Alive.Count == 2));
        var turret = watch.Alive["2001/2000"];
        Assert.Equal("Turret", turret.Kind);
        Assert.Equal(0.5f, turret.Azimuth);
        Assert.Equal(0.125f, turret.Elevation);
        Assert.Equal(-0.25f, watch.Alive["2001/2001"].Azimuth);
    }

    [Fact]
    public void Fire_and_detonation_are_events()
    {
        uint domain = NewDomain();
        using var fires = new Watch<D.Fire>(domain, "station-1", f => "fire");
        using var hits = new Watch<D.Detonation>(domain, "station-1", f => "hit");
        using var rig = new Rig(domain);
        rig.Send(HrotOrigin.Utm(50, 14, 300, 1));
        rig.Send(new HrotSession(ClusterPhase.Live, true, 1.0, 12.5));
        rig.Send(State(2001, simTime: 12.5));   // the shooter and the target are known entities, as HROT always sends them before an event
        rig.Send(State(2002, simTime: 12.5));

        // events are volatile: send until the readers have matched, then once more is fine (a client counts none of them as state)
        Assert.True(fires.Until(w =>
        {
            rig.Send(new HrotFire(2001, 2002, 501, TankDis, new HrotVec3(1, 2, 3), new HrotVec3(800, 0, 0), 1));
            return w.Events.Count > 0;
        }));
        Assert.True(hits.Until(w =>
        {
            rig.Send(new HrotDetonation(2001, 0, 501, TankDis, new HrotVec3(50, 20, 0.4), new HrotVec3(0, 0, -5)));
            return w.Events.Count > 0;
        }));

        var f = fires.Events[0];
        Assert.Equal(2001, f.ShooterId);
        Assert.Equal(2002, f.TargetId);
        Assert.Equal(501, f.MunitionTypeId);
        Assert.Equal((byte)1, f.MunitionDis.Kind);
        Assert.Equal(12.5, f.SimTime);
        Assert.Equal(3.0, f.Origin.Z);
        Assert.Equal(800f, f.Velocity.X);
        var h = hits.Events[0];
        Assert.Equal(0.4, h.Position.Z);
        Assert.Equal(-5f, h.Velocity.Z);
    }

    [Fact]
    public void Effect_maps_every_field()
    {
        uint domain = NewDomain();
        using var watch = new Watch<D.Effect>(domain, "station-1", e => "effect");
        using var rig = new Rig(domain);
        ulong dis = unchecked((ulong)TankDis);

        Assert.True(watch.Until(w =>
        {
            rig.Send(new HrotEffect("Explosion", "Huge", "Building", dis, 1234, new HrotVec3(5, 6, 7), new HrotQuat(0, 0, 0.5, 0.5), 2001, 1, 2.5f, 4f, 9.5));
            rig.Send(new HrotEffect("Tracer", "Small", "Ground", 0, 1, HrotVec3.Zero, HrotQuat.Identity, 0, 0, 1f, 1f));   // a tracer: DPS carries no tracers
            return w.Events.Count > 0;
        }));

        Assert.All(watch.Events, e => Assert.Equal(D.EffectKind.EffectKindExplosion, e.Kind));
        var e = watch.Events[0];
        Assert.Equal(D.EffectSize.EffectSizeLarge, e.SizeClass);
        Assert.Equal(D.EffectSurface.EffectSurfaceBuilding, e.Surface);
        Assert.Equal((byte)1, e.MunitionDis.Kind);
        Assert.Equal(1234, e.EffectType);
        Assert.Equal(new D.Vec3d { X = 5, Y = 6, Z = 7 }, e.Position);
        Assert.Equal(0.5f, e.Rotation.W);
        Assert.Equal(2001, e.AnchorId);
        Assert.Equal((byte)1, e.AnchorWeaponSlot);
        Assert.Equal(2.5f, e.LifetimeSeconds);
        Assert.Equal(4f, e.Size);
        Assert.Equal(9.5, e.SimTime);   // EffectSpec.StartedAt is the effect's time
    }

    [Fact]
    public void Appearance_maps_outfit_colours_and_kinds()
    {
        uint domain = NewDomain();
        using var watch = new Watch<D.Appearance>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        rig.Send(State(1201, dis: PersonDis));

        rig.Send(new HrotPersonAppearance(1201, 1201, 2,
            Top: 0x336699FF, Sleeve: 0x336698FF, Bottom: 0x112233FF, Shin: 0x112234FF, Skin: 0xCC9966FF, Hair: 0x221100FF, Tie: 0xFF0000FF, HeadCloth: 0x445566FF, OverColour: 0x778899FF,
            ExtraColour: 0xAABBCCFF, HairStyle: "Long", Legwear: "Skirt", Camo: "Woodland", TopKind: "Uniform", HeadKind: "Helmet", OverKind: "ChestRig", ExtraKind: "Waistcoat", Beard: "Beard",
            Suit: true, Child: false, Soldier: true, Police: false, Riot: false, Woman: true, Dressed: true, FacePaint: false,
            EyeSpacing: 0.5f, MouthHalfWidth: 0.25f, BrowHalfHeight: 0.125f, EyeColour: 0x010203FF, Lips: 0x040506FF));

        Assert.True(watch.Until(w => w.Alive.ContainsKey("1201")));
        var a = watch.Alive["1201"];
        Assert.Equal(1201u, a.Seed);
        Assert.Equal((byte)2, a.Side);
        Assert.Equal("Woodland", a.Camouflage);
        Assert.Equal("Long", a.Hair);
        Assert.Equal(D.Sex.SexFemale, a.Sex);
        Assert.Equal(D.AgeClass.AgeClassAdult, a.AgeClass);
        Assert.True(a.IsSoldier);
        Assert.True(a.IsWoman);
        Assert.Empty(a.Equipment);
        var slots = a.Slots.ToDictionary(s => s.Slot);
        Assert.Equal(("Uniform", 0x336699FFu), (slots["Top"].Kind, slots["Top"].ColourRgba));
        Assert.Equal(("Skirt", 0x112233FFu), (slots["Legs"].Kind, slots["Legs"].ColourRgba));
        Assert.Equal(("Helmet", 0x445566FFu), (slots["Head"].Kind, slots["Head"].ColourRgba));
        Assert.Equal(("ChestRig", 0x778899FFu), (slots["Over"].Kind, slots["Over"].ColourRgba));
        Assert.Equal(("Waistcoat", 0xAABBCCFFu), (slots["Extra"].Kind, slots["Extra"].ColourRgba));
        Assert.Equal(("Beard", 0x221100FFu), (slots["Beard"].Kind, slots["Beard"].ColourRgba));
        Assert.Equal(0xCC9966FFu, slots["Skin"].ColourRgba);
        Assert.Equal(0xFF0000FFu, slots["Tie"].ColourRgba);
        Assert.Contains("eyeSpacing=0.5", a.FaceTraits);
    }

    [Fact]
    public void Vehicle_look_maps_parts_openings_lights()
    {
        uint domain = NewDomain();
        using var watch = new Watch<D.VehicleLook>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain);
        rig.Send(State(2001));

        rig.Send(new HrotVehicleLook(2001, "Catastrophic", true, true, false, 0b111u, [(1, 2), (4, 1)], [(2, 0.5f), (3, 1f)]));

        Assert.True(watch.Until(w => w.Alive.ContainsKey("2001")));
        var v = watch.Alive["2001"];
        Assert.Equal(D.KillKind.KillKindCatastrophic, v.KillKind);
        Assert.True(v.TurretThrown);
        Assert.True(v.Burning);
        Assert.False(v.Smoking);
        Assert.Equal(0b111u, v.LightMask);
        Assert.Equal(0f, v.RotorSpeed);
        Assert.Equal((byte)0, v.SmokeLaunchers);
        Assert.Equal([(1, D.PartCondition.PartConditionDestroyed), (4, D.PartCondition.PartConditionDamaged)], v.Parts.Select(p => (p.PartId, p.State)).ToArray());
        Assert.Equal([(2, 0.5f), (3, 1f)], v.Openings.Select(o => (o.OpeningId, o.Progress)).ToArray());
    }

    [Fact]
    public void Removed_entity_is_disposed()
    {
        uint domain = NewDomain();
        using var rig = new Rig(domain);
        rig.Send(State(2001));
        rig.Send(State(1201, dis: PersonDis));
        rig.Send(new HrotPersonDetail(1201, HrotStance.Standing, 1f, HrotHeldItem.None, 0, HrotVec3.Zero, false, 0f, HrotGesture.None, HrotAction.None, 0, HrotActionState.None, 0, 0,
            HrotSeatRole.None, HrotCarryMode.None));
        rig.Send(new HrotTurretPose(2001, 2000, 0.1, 0.1));

        rig.Send(new HrotEntityRemoved(2001));

        // a client that joins now sees the person but nothing of the tank
        using var entities = new Watch<D.Entity>(domain, "station-1", e => Id(e.EntityId));
        using var states = new Watch<D.EntityState>(domain, "station-1", e => Id(e.EntityId));
        using var parts = new Watch<D.Articulation>(domain, "station-1", a => $"{a.EntityId}/{a.PartId}");
        Assert.True(entities.Until(w => w.Alive.ContainsKey("1201")));
        Assert.True(states.Until(w => w.Alive.ContainsKey("1201")));
        Thread.Sleep(300);
        entities.Pump();
        states.Pump();
        parts.Pump();
        Assert.DoesNotContain("2001", entities.Alive.Keys);
        Assert.DoesNotContain("2001", states.Alive.Keys);
        Assert.Empty(parts.Alive);
    }

    [Fact]
    public void Session_carries_protocol_version_geo_and_mode()
    {
        uint domain = NewDomain();
        using var watch = new Watch<D.Session>(domain, "station-1", s => "session");
        using var rig = new Rig(domain, partition: "station-1");

        rig.Send(HrotOrigin.Utm(50, 14, 300, 1));
        rig.Send(new HrotSession(ClusterPhase.Live, true, 2.0, 12.5));
        Assert.True(watch.Until(w => w.Alive.TryGetValue("session", out var s) && s.SimTime == 12.5));
        var live = watch.Alive["session"];
        Assert.Equal((D.DpsProtocol.Major, D.DpsProtocol.Minor), (live.ProtocolMajor, live.ProtocolMinor));
        Assert.Equal(D.SessionMode.SessionModeLive, live.Mode);
        Assert.Equal(2.0, live.TimeScale);
        Assert.Equal(33, live.UtmZone);
        Assert.True(live.UtmNorthern);
        Assert.InRange(live.OriginEasting, 400_000, 600_000);
        Assert.InRange(live.OriginNorthing, 5_400_000, 5_700_000);
        Assert.Equal("station-1", live.StationName);
        Assert.Equal(1u, live.WorldEpoch);

        rig.Send(new HrotSession(ClusterPhase.Live, false, 1.0, 13.0));
        Assert.True(watch.Until(w => w.Alive["session"].Mode == D.SessionMode.SessionModePaused));

        rig.Send(new HrotSession(ClusterPhase.Replay, true, 1.0, 14.0));
        Assert.True(watch.Until(w => w.Alive["session"].Mode == D.SessionMode.SessionModeReplay));
    }

    [Fact]
    public void World_boundary_disposes_every_instance()
    {
        uint domain = NewDomain();
        var plugin = new DdsPlayerPlugin();
        var service = plugin.CreateHostService(new DpsSettings(), new PluginHostContext(_ => { }));
        using var entities = new Watch<D.Entity>(domain, "station-1", e => Id(e.EntityId));
        using var sessions = new Watch<D.Session>(domain, "station-1", s => "session");
        var first = new Rig(domain, plugin: plugin, service: service);
        first.Send(new HrotSession(ClusterPhase.Live, true, 1.0, 1.0));
        for (int id = 2001; id <= 2003; id++)
        {
            first.Send(State(id));
        }

        Assert.True(entities.Until(w => w.Alive.Count == 3));
        Assert.True(sessions.Until(w => w.Alive.TryGetValue("session", out var s) && s.WorldEpoch == 1));

        first.Dispose();   // the runtime ends at the world boundary: the lease is disposed

        Assert.True(entities.Until(w => w.Alive.Count == 0), "every entity instance is disposed with the world");
        Assert.True(sessions.Until(w => w.Alive.Count == 0));

        // the next world: a new epoch, and none of the old entities
        using var second = new Rig(domain, plugin: plugin, service: service);
        second.Send(new HrotSession(ClusterPhase.Live, true, 1.0, 1.0));
        Assert.True(sessions.Until(w => w.Alive.TryGetValue("session", out var s) && s.WorldEpoch == 2));
        Assert.Empty(entities.Alive);
    }

    [Fact]
    public void Other_partition_sees_nothing()
    {
        uint domain = NewDomain();
        using var other = new Watch<D.Entity>(domain, "station-2", e => Id(e.EntityId));
        using var own = new Watch<D.Entity>(domain, "station-1", e => Id(e.EntityId));
        using var rig = new Rig(domain, partition: "station-1");

        rig.Send(State(2001));

        Assert.True(own.Until(w => w.Alive.Count == 1));
        Thread.Sleep(300);
        other.Pump();
        Assert.Empty(other.Alive);
    }

    // ---- seats (DD-023): Entity.Playable and Session on the world participant --------------------------------------------------------

    private static DpsInboundTests.RecordingStation Offering(params int[] units)
    {
        var station = new DpsInboundTests.RecordingStation();
        foreach (int u in units)
        {
            station.OfferList.Add(new PlayerOffer(u, null, 1));
        }

        return station;
    }

    /// <summary>(DD-023 e, D-8) Playable is true for EVERY offerable unit - the union of the seats' offers, one list for all of them - on every seat's client, whichever seat is joined.</summary>
    [Fact]
    public void Playable_is_true_for_every_offerable_unit_whichever_seat_is_joined_and_follows_the_offers()
    {
        uint domain = NewDomain();
        var stations = new[] { Offering(1001), Offering(1002), Offering(1003) };
        stations[1].Slot = new PlayerSlotSnapshot(PlayerSlotState.Playing, 1002, 0.0, 2);   // only seat 2 is joined
        using var seatTwo = new Watch<D.Entity>(domain, "station-1-2", e => Id(e.EntityId));
        using var seatThree = new Watch<D.Entity>(domain, "station-1-3", e => Id(e.EntityId));
        using var rig = new Rig(domain, seats: 3, stations: stations);

        foreach (int id in new[] { 1001, 1002, 1003, 2001 })
        {
            rig.Send(State(id));
        }

        foreach (var watch in new[] { seatTwo, seatThree })
        {
            Assert.True(watch.Until(w =>
            {
                rig.Step();
                return w.Alive.Count == 4;
            }));
            Assert.Equal([true, true, true, false], new[] { "1001", "1002", "1003", "2001" }.Select(k => watch.Alive[k].Playable).ToArray());
        }

        stations[2].OfferList.Add(new PlayerOffer(2001, null, 2));   // an offer appears on seat 3 only: still one list for everybody
        stations[0].OfferList.Clear();                               // and 1001 stops being offered
        Assert.True(seatTwo.Until(w =>
        {
            rig.Step();
            return w.Alive["2001"].Playable && !w.Alive["1001"].Playable;
        }));
        Assert.True(seatThree.Until(w =>
        {
            rig.Step();
            return w.Alive["2001"].Playable && !w.Alive["1001"].Playable;
        }));
    }

    /// <summary>(DD-023 e) Session is written by exactly one writer (the world participant): every seat's client gets each sample once, and it names the BASE partition.</summary>
    [Fact]
    public void Session_has_exactly_one_writer_and_names_the_base_partition()
    {
        uint domain = NewDomain();
        using var one = new Watch<D.Session>(domain, "station-1-1", _ => "s");
        using var two = new Watch<D.Session>(domain, "station-1-2", _ => "s");
        using var rig = new Rig(domain, seats: 2);

        rig.Send(new HrotSession(ClusterPhase.Live, true, 1.0, 5.0));

        foreach (var watch in new[] { one, two })
        {
            Assert.True(watch.Until(w => w.Events.Count >= 1));
            Thread.Sleep(200);
            watch.Pump();
            Assert.Single(watch.Events);   // a second writer would show the sample twice
            Assert.All(watch.Events, e => Assert.Equal("station-1", e.StationName));
        }

        Assert.Equal(("station-1-*", "station-1"), (rig.Transport.WorldPartition, rig.Transport.Partition));
        Assert.Equal(["station-1-1", "station-1-2"], rig.Transport.Seats.Select(x => x.Partition).ToArray());
    }

    // ---- T-048: the terrain in Session ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_terrain_name_and_both_hashes_reach_session_and_a_change_sends_it_again()
    {
        uint domain = NewDomain();
        using var watch = new Watch<D.Session>(domain, "station-1", _ => "s");
        using var rig = new Rig(domain);

        rig.Send(new HrotSession(ClusterPhase.Live, true, 1.0, 5.0));
        Assert.True(watch.Until(w => w.Events.Count >= 1));
        Assert.Equal((string.Empty, string.Empty, string.Empty), (watch.Events[^1].Terrain, watch.Events[^1].TerrainBaseHash, watch.Events[^1].TerrainOverlayHash));   // no terrain: empty strings

        rig.Send(new HrotTerrain("city-1", "basehash-aaa", "overlayhash-bbb"));
        Assert.True(watch.Until(w => w.Events[^1].Terrain == "city-1"));
        Assert.Equal(("city-1", "basehash-aaa", "overlayhash-bbb"), (watch.Events[^1].Terrain, watch.Events[^1].TerrainBaseHash, watch.Events[^1].TerrainOverlayHash));

        rig.Send(new HrotTerrain("city-1", "basehash-aaa", "overlayhash-ccc"));   // only the overlay changed
        Assert.True(watch.Until(w => w.Events[^1].TerrainOverlayHash == "overlayhash-ccc"));
        Assert.Equal(("city-1", "basehash-aaa"), (watch.Events[^1].Terrain, watch.Events[^1].TerrainBaseHash));

        rig.Send(new HrotTerrain(string.Empty, string.Empty, string.Empty));   // the terrain went away
        Assert.True(watch.Until(w => w.Events[^1].Terrain.Length == 0));
        Assert.Equal((string.Empty, string.Empty), (watch.Events[^1].TerrainBaseHash, watch.Events[^1].TerrainOverlayHash));
    }

    [Fact]
    public void An_unchanged_terrain_does_not_send_session_again_and_a_terrain_before_the_first_session_waits_for_it()
    {
        uint domain = NewDomain();
        using var rig = new Rig(domain);
        var sessions = new List<D.Session>();
        rig.Transport.Session.Tap = sessions.Add;

        rig.Send(new HrotTerrain("city-1", "b", "o"));
        Assert.Empty(sessions);   // there is no session yet to put it in

        rig.Send(new HrotSession(ClusterPhase.Live, true, 1.0, 5.0));
        Assert.Equal(("city-1", "b", "o"), (sessions[^1].Terrain, sessions[^1].TerrainBaseHash, sessions[^1].TerrainOverlayHash));
        int count = sessions.Count;
        rig.Send(new HrotTerrain("city-1", "b", "o"));
        rig.Send(new HrotTerrain("city-1", "b", "o"));
        Assert.Equal(count, sessions.Count);
    }

    [Fact]
    public void A_placed_unit_offer_has_empty_kits_and_outline()
    {
        uint domain = NewDomain();
        var station = Offering(1001);
        station.Slot = new PlayerSlotSnapshot(PlayerSlotState.Choosing, 0, 0.0, 1);
        using var watch = new Watch<D.PlayerSlot>(domain, "station-1", _ => "slot");
        using var rig = new Rig(domain, station: station);

        Assert.True(watch.Until(w =>
        {
            rig.Step();
            return w.Alive.ContainsKey("slot");
        }));
        var offer = Assert.Single(watch.Alive["slot"].Offers);
        Assert.Equal(D.OfferKind.OfferKindUnit, offer.Kind);
        Assert.Empty(offer.Kits);
        Assert.Empty(offer.Outline);
    }

    /// <summary>(DD-023 g, HOT path) The per-frame Playable computation of three seats allocates nothing in the steady state.</summary>
    [Fact]
    public void The_playable_computation_of_three_seats_allocates_nothing_in_the_steady_state()
    {
        uint domain = NewDomain();
        var stations = new[] { Offering(1001, 1002, 1003), Offering(1002, 1004), Offering(1005) };
        using var rig = new Rig(domain, seats: 3, stations: stations);
        rig.Transport.RefreshPlayable();
        for (int i = 0; i < 50; i++)
        {
            Assert.False(rig.Transport.RefreshPlayable() && i > 0);   // warmed: the same offers are not a change
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool changed = false;
        for (int i = 0; i < 1000; i++)
        {
            changed |= rig.Transport.RefreshPlayable();
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(changed);
        Assert.Equal(0, after - before);
    }

    /// <summary>A seat station whose answer to "is this message for you" is the test's (the host's rule stands behind it; the plugin has none of its own).</summary>
    private sealed class AddressedStation : DpsInboundTests.RecordingStation, IPlayerStation
    {
        public bool Wants { get; set; }

        public List<(string Kind, int Value)> Asked { get; } = new();

        bool IPlayerStation.Addressed(string kind, int value)
        {
            Asked.Add((kind, value));
            return Wants;
        }
    }

    /// <summary>T-054 (DD-023 D-15): a message is written on the participant of each seat whose station says it is addressed to it, with the address kind NAME and value the rule is asked; the others hear nothing. Mutations: written on the world participant; every seat.</summary>
    [Fact]
    public void A_player_message_is_written_only_on_the_seats_whose_station_it_is_addressed_to()
    {
        uint domain = NewDomain();
        var stations = new[] { new AddressedStation { Wants = true }, new AddressedStation { Wants = false }, new AddressedStation { Wants = true } };
        using var one = new Watch<D.PlayerMessage>(domain, "station-1-1", m => "m");
        using var two = new Watch<D.PlayerMessage>(domain, "station-1-2", m => "m");
        using var three = new Watch<D.PlayerMessage>(domain, "station-1-3", m => "m");
        using var rig = new Rig(domain, seats: 3, stations: stations.Cast<IPlayerStation>().ToArray());

        Assert.True(one.Until(w =>
        {
            rig.Send(new HrotPlayerMessage(7, 0, "Station", 65537, "to seat one", 4f, 0, 1.0, 0));
            two.Pump();
            three.Pump();
            return w.Events.Count > 0 && three.Events.Count > 0;
        }));
        Thread.Sleep(200);
        two.Pump();
        Assert.Empty(two.Events);   // seat 2's station said no
        Assert.All(new[] { one, three }, w => Assert.Equal((D.MessageAddress.MessageAddressStation, 65537, "to seat one"), (w.Events[0].To, w.Events[0].ToValue, w.Events[0].Text)));
        Assert.All(stations, st => Assert.Equal(("Station", 65537), st.Asked[0]));
    }

    /// <summary>
    /// T-054 (HOT path): with three seats and no message the message path costs nothing per steady frame: the router is built once and touched only when a message arrives, no station is asked and nothing is written.
    /// (The whole engine tick of a rig is NOT 0 B - about 1.4 KB a frame measured before this task, from the slot / HUD paths, not from messages.)
    /// </summary>
    [Fact]
    public void Three_seats_with_no_messages_allocate_nothing_per_steady_frame_for_the_message_path()
    {
        uint domain = NewDomain();
        var stations = new[] { new AddressedStation(), new AddressedStation(), new AddressedStation() };
        using var rig = new Rig(domain, seats: 3, stations: stations.Cast<IPlayerStation>().ToArray());
        rig.Step();
        var port = rig.Transport.PlayerMessage;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            _ = rig.Transport.PlayerMessage;
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Same(port, rig.Transport.PlayerMessage);
        Assert.All(stations, st => Assert.Empty(st.Asked));
    }

    /// <summary>
    /// T-055 (b), HOT path: a whole steady engine tick of three PLAYING seats (slots, offers, own-unit status with ammo, the inbound reads) allocates nothing of the gateway's own. Measured source of the old ~1.4 KB a frame:
    /// <c>DpsPlayerSlot.Pump</c> (offers compared through <c>SequenceEqual</c>, the own unit rebuilt with LINQ every frame). The 144 B once attributed to the CycloneDDS binding
    /// (<c>DdsReader.Take</c>, 16 B an empty read) were the gateway's own: <c>DpsInbound.HandleLiveliness</c> had a lambda capturing the loop variable, so a 48 B closure was allocated per seat per tick (T-056, measured by bisecting
    /// <c>DpsInbound.Pump</c>; an empty <c>Take</c> in CycloneDDS.NET 1.2.20 is 0 B). Mutations: the own unit built every frame again; the capturing lambda back.
    /// </summary>
    [Fact]
    public void A_steady_engine_tick_of_three_playing_seats_allocates_nothing()
    {
        uint domain = NewDomain();
        var stations = new AddressedStation[3];
        for (int i = 0; i < 3; i++)
        {
            stations[i] = new AddressedStation { Slot = new PlayerSlotSnapshot(PlayerSlotState.Playing, 1201 + i, 0.0, 2, null, 1) };
            stations[i].OfferList.Add(new PlayerOffer(1201 + i, "Rifleman", 1));
            stations[i].OfferList.Add(new PlayerOffer(900, "Role", 1, PlayerOfferKind.Role, 1, 2, true, ["a", "b"]));
            stations[i].Unit = new PlayerUnitStatus(80.0, 100.0, [new PlayerWeapon(0, "RIFLE", "5.56", 5L, 20, 30, -1, -1, false, false, 0), new PlayerWeapon(1, "MG", "7.62", 6L, 100, 200, -1, -1, false, false, 0)], 0, 0);
        }

        using var rig = new Rig(domain, seats: 3, stations: stations.Cast<IPlayerStation>().ToArray());
        for (int i = 0; i < 50; i++)
        {
            rig.Step();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
        {
            rig.Step();
        }

        long perTick = (GC.GetAllocatedBytesForCurrentThread() - before) / 500;
        Assert.Equal(0L, perTick);
    }

    [Fact]
    public void The_side_reads_and_writes_its_section()
    {
        var side = new DpsSide();
        var result = GatewayConfigurationFile.Parse(
            """{ "dps": { "domain": 7, "partition": "p", "leaseSeconds": 3, "heartbeatSeconds": 4, "positionThresholdM": 0.1, "angleThresholdDeg": 2, "velocityThreshold": 0.2 } }""",
            "test", SideRegistry.Empty.With(side));

        Assert.Empty(result.Errors);
        var settings = (DpsSettings)result.Configuration!.SettingsOf("dps")!;
        Assert.Equal(new DpsSettings { Domain = 7, Partition = "p", LeaseSeconds = 3, HeartbeatSeconds = 4, PositionThresholdM = 0.1, AngleThresholdDeg = 2, VelocityThreshold = 0.2 }, settings);
        Assert.True(side.Runs(settings));
        Assert.True(new DdsPlayerPlugin().WantsEventsInReplay(settings));
        Assert.Equal("dps", new DdsPlayerPlugin().Name);
    }

    [Fact]
    public void Look_names_map_to_the_contract_and_an_unknown_name_to_its_unknown_value()
    {
        Assert.Equal(D.KillKind.KillKindNone, LookNames.KillKindOf("None"));
        Assert.Equal(D.KillKind.KillKindKnockedOut, LookNames.KillKindOf("KnockedOut"));
        Assert.Equal(D.KillKind.KillKindBurntOut, LookNames.KillKindOf("BurntOut"));
        Assert.Equal(D.KillKind.KillKindCatastrophic, LookNames.KillKindOf("Catastrophic"));
        Assert.Equal(D.KillKind.KillKindUnknown, LookNames.KillKindOf("Vaporised"));

        Assert.Equal(D.EffectSize.EffectSizeLarge, LookNames.EffectSizeOf("Huge"));
        Assert.Equal(D.EffectSize.EffectSizeUnknown, LookNames.EffectSizeOf("Gigantic"));
        Assert.Equal(D.EffectSurface.EffectSurfaceWater, LookNames.EffectSurfaceOf("Water"));
        Assert.Equal(D.EffectSurface.EffectSurfaceUnknown, LookNames.EffectSurfaceOf("Ice"));

        Assert.True(LookNames.TryEffectKindOf("Decal", out var decal));
        Assert.Equal(D.EffectKind.EffectKindGroundMark, decal);
        Assert.False(LookNames.TryEffectKindOf("Tracer", out _));   // deliberately not carried
        Assert.True(LookNames.TryEffectKindOf("Sparks", out var unknown));   // a kind HROT appends later: sent, as None
        Assert.Equal(D.EffectKind.EffectKindNone, unknown);
    }
}
