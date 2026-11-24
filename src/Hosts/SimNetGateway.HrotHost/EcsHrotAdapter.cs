using System.Numerics;
using Fdp.Core;
using Fdp.Interfaces;
using Fdp.Toolkit.Tkb.Domain;
using Fdp.Toolkit.Time;
using Hrot.Map.Common.Components;
using Hrot.MuscleCharacter.Animation.Components;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Combat.Contracts;
using Fdp.Toolkit.Combat.Events;
using Fdp.Toolkit.Physics;
using Fdp.Toolkit.Physics.Components;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Scenario;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The only ECS to <c>Protocol.Hrot</c> mapping: <see cref="Gather"/> turns HROT entities and events into
/// messages for the gateway, <see cref="Apply"/> turns the gateway's messages into replicas and bus events. Replicas follow
/// <c>TrafficReplicas</c> plus <c>SetDisType</c>: <c>NetworkIdentity</c> (int), <c>NetworkAuthority</c> owned by another node (so
/// <c>HealthOwnership.AppliesHere</c> is false and HROT never damages them), <c>ReplicationRoute.ExternalGateway</c>, <c>ScenarioIgnoreTag</c>,
/// registered in the <see cref="Fdp.Toolkit.Replication.Services.NetworkEntityMap"/>. Main thread only.
/// </summary>
public sealed class EcsHrotAdapter
{
    private const double AimEpsilonM = 0.5;
    private const int LifeFormKind = 3;   // DIS entity kind "life form"
    private const int PlatformKind = 1;   // DIS entity kind "platform" (vehicles)
    private const double PositionEpsilonM = 0.01, OrientationEpsilonDeg = 0.5, VelocityEpsilon = 0.05, ResendS = 1.0;
    private const float DefaultReplicaHealth = 100f;

    private sealed class SentPose
    {
        public double Azimuth, Elevation, At;
        public bool Seen;
    }

    private sealed class Sent
    {
        public HrotEntityState State;
        public double At;
        public bool Seen;
    }

    private readonly HrotServices _services;
    private readonly GatewayConfiguration _config;
    private readonly Dictionary<int, DisReplica> _replicas = new();
    private readonly Dictionary<int, Sent> _sent = new();
    private readonly PersonDetailGather _details = new();
    private readonly EntityStatusGather _status = new();
    private readonly VoiceGather _voice = new();
    private readonly AmbienceGather _ambience = new();
    private readonly WeatherGather _weather = new();
    private readonly Func<int, bool> _isReplica;
    private readonly EffectGather _effects = new();
    private readonly AppearanceGather _appearances = new();
    private readonly VehicleLookGather _vehicleLooks = new();
    private readonly RoundGather _rounds = new();
    private readonly MunitionLookGather _munitions = new();
    private readonly FlameJetGather _jets = new();
    private readonly FireSpotGather _fireSpots = new();
    private readonly GroundMarkGather _groundMarks = new();
    private readonly HudGather _hud = new();
    private readonly BuildingDamageGather _buildings = new();
    private readonly GroundDamageGather _craters = new();
    private readonly TrafficGather _traffic = new();
    private readonly TkbGather _types = new();
    private readonly AnimalGather _animals = new();
    private readonly ViewGather _view = new();
    private readonly BankTracker _banks = new();
    private readonly List<int> _scratch = new();
    private readonly List<(int Parent, int Part)> _scratchPoses = new();
    private EntityQuery? _hrotQuery;
    private EntityQuery? _routeQuery;
    private EntityQuery? _turretQuery;
    private readonly Dictionary<(int Parent, int Part), SentPose> _poses = new();
    private EntityRepository? _queryWorld;
    private bool _warnedOwnership;

    public EcsHrotAdapter(HrotServices services, GatewayConfiguration config)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _isReplica = _replicas.ContainsKey;
    }

    public PackCounters Counters { get; } = new();

    /// <summary>The live replicas by their network id.</summary>
    public IReadOnlyDictionary<int, DisReplica> Replicas => _replicas;

    /// <summary>The ids of the HROT entities currently published to the gateway.</summary>
    public IEnumerable<int> PublishedIds => _sent.Keys;

    // ---- world preparation --------------------------------------------------------------------------------------------------

    /// <summary>Registers the component and event types the pack writes, when the world does not know them yet (a bare or minimal world).</summary>
    public static void EnsureRegistered(EntityRepository world)
    {
        Ensure<SimTransform>(world);
        Ensure<SimVelocity>(world);
        Ensure<NetworkIdentity>(world);
        Ensure<NetworkAuthority>(world);
        Ensure<TkbIdentity>(world);
        Ensure<ReplicationRoute>(world);
        Ensure<ScenarioIgnoreTag>(world);
        Ensure<EntityInfo>(world);
        Ensure<PhysicsCollider>(world);
        Ensure<Health>(world);
        Ensure<EffectAnchor>(world);
        Ensure<Lifespan>(world);
        Ensure<StanceIntent>(world);
        Ensure<WeaponPosture>(world);
        Ensure<InjuryThresholds>(world);
        PersonDetailGather.EnsureRegistered(world);
        Ensure<Fdp.Toolkit.Terrain.BuildingDamage>(world);
        Ensure<Fdp.Toolkit.Terrain.GroundDamage>(world);
        if (!world.Bus.IsRegistered<WeaponFireNotification>())
        {
            world.RegisterEvent<WeaponFireNotification>();
        }

        if (!world.Bus.IsRegistered<DetonationNotification>())
        {
            world.RegisterEvent<DetonationNotification>();
        }

        if (!world.Bus.IsRegistered<RoundImpactNotification>())
        {
            world.RegisterEvent<RoundImpactNotification>();
        }

        if (!world.Bus.IsRegistered<ManDownedEvent>())
        {
            world.RegisterEvent<ManDownedEvent>();
        }

        if (!world.Bus.IsRegisteredManaged<Fdp.Toolkit.ScenarioRules.PlayerMessageEvent>())
        {
            world.RegisterManagedEvent<Fdp.Toolkit.ScenarioRules.PlayerMessageEvent>();
        }
    }

    private static void Ensure<T>(EntityRepository world)
        where T : unmanaged
    {
        if (!world.IsComponentTypeRegistered<T>())
        {
            world.RegisterComponent<T>();
        }
    }

    // ---- HROT -> gateway ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Publishes the HROT entities that changed beyond 1 cm / 0.5 deg / 5 cm/s (or are due once per second) and the removal of those that went away.
    /// Selected: <c>SimTransform</c> + <c>TkbIdentity</c> + <c>NetworkIdentity</c> and no <c>ReplicationRoute</c> (replicas are skipped: echo).
    /// </summary>
    public void GatherEntities(EntityRepository world, InMemoryInbound inbound, double now, double simTime)
    {
        BindQueries(world);
        foreach (var s in _sent.Values)
        {
            s.Seen = false;
        }

        var tkb = _services.Tkb();
        foreach (var e in _hrotQuery!)
        {
            int id = world.GetComponentRO<NetworkIdentity>(e).Value;
            if (id == 0 || _replicas.ContainsKey(id))
            {
                continue;
            }

            var state = StateOf(world, e, id, tkb, simTime);
            bool sendState;
            bool isNew = !_sent.ContainsKey(id);
            if (_sent.TryGetValue(id, out var previous))
            {
                previous.Seen = true;
                sendState = now - previous.At >= ResendS || Changed(in previous.State, in state);
                if (sendState)
                {
                    previous.State = state;
                    previous.At = now;
                }
            }
            else
            {
                _sent[id] = new Sent { State = state, At = now, Seen = true };
                sendState = true;
            }

            if (sendState)
            {
                inbound.Enqueue(in state);
                Counters.StatesSent++;
            }

            _types.Reference(state.TkbType);
            ReferenceLoadedAmmo(world, e, isNew);

            // a round with a look: its munition look (once per type) and its round state, after the state so the gateway knows the entity
            if (RoundGather.IsRound(world, e))
            {
                if (_munitions.TryGather(tkb, state.TkbType, out var munitionLook))
                {
                    inbound.Enqueue(in munitionLook);
                }

                if (_rounds.TryGather(world, tkb, e, id, state.TkbType, simTime, out var round))
                {
                    inbound.Enqueue(in round);
                }
            }

            // after the state, so the gateway knows the entity; a person is sent again only when something about him changed
            if (state.DisType != 0 && DisTypeCode.Unpack(state.DisType).Kind == LifeFormKind && AnimalGather.IsAnimal(world, e))
            {
                if (_animals.TryGather(world, e, id, out var animal))
                {
                    inbound.Enqueue(in animal);
                }
            }
            else if (state.DisType != 0 && DisTypeCode.Unpack(state.DisType).Kind == LifeFormKind)
            {
                if (_details.TryGather(world, tkb, e, id, simTime, out var detail))
                {
                    inbound.Enqueue(in detail);
                }

                if (_appearances.TryGather(world, tkb, e, id, out var appearance))
                {
                    inbound.Enqueue(in appearance);
                }
            }
            else if (state.DisType != 0 && DisTypeCode.Unpack(state.DisType).Kind == PlatformKind && _vehicleLooks.TryGather(world, tkb, e, id, simTime, out var look))
            {
                inbound.Enqueue(in look);
            }

            // what the client draws the entity's card from (DD-025), after the state, for the entities HROT would give a card
            _status.Gather(world, tkb, _services.Behaviors(), e, id, state.TkbType, inbound);
        }

        _scratch.Clear();
        foreach (var (id, s) in _sent)
        {
            if (!s.Seen)
            {
                _scratch.Add(id);
            }
        }

        foreach (int id in _scratch)
        {
            _sent.Remove(id);
            _details.Forget(id);
            _appearances.Forget(id);
            _rounds.Forget(id);
            _animals.Forget(id);
            _banks.Forget(id);
            _vehicleLooks.Forget(id);
            _status.Forget(id);
            inbound.Enqueue(new HrotEntityRemoved(id));
            Counters.RemovalsSent++;
        }

        // flames, burning spots and ground marks follow HROT's state of this frame
        _jets.Gather(world, inbound, simTime);
        _fireSpots.Gather(world, inbound, simTime);
        _groundMarks.Gather(world, inbound);
        _hud.GatherState(world, inbound);
        _buildings.Gather(world, inbound);
        _craters.Gather(world, inbound);
        _traffic.Gather(world, inbound, simTime, _types);
        _view.Gather(world, _services.SuggestedView, inbound, simTime);
        _voice.Gather(world, inbound, simTime, _services.Behaviors(), _isReplica);   // what the men shout, report and order (DD-026)
        _ambience.Gather(world, inbound);   // the ambience layers (DD-028)
        _weather.Gather(world, inbound);   // the global weather and the weather regions (DD-028)
        _types.Flush(tkb, inbound);
    }

    /// <summary>
    /// Publishes the turret poses of HROT's own entities (the <c>PartMetadata</c> + <c>TurretPose</c> children, part id 2000 + index) that changed by more than
    /// 0.5 degrees, or are due once per second. Call after <see cref="GatherEntities"/> so the entity is known to the gateway.
    /// </summary>
    public void GatherTurrets(EntityRepository world, InMemoryInbound inbound, double now)
    {
        if (!TurretParts.HostsTurrets(world))
        {
            return;
        }

        BindQueries(world);
        foreach (var p in _poses.Values)
        {
            p.Seen = false;
        }

        foreach (var part in _turretQuery!)
        {
            var meta = world.GetComponentRO<PartMetadata>(part);
            if (TurretParts.IndexOf(meta.InstanceId) < 0 || !TryHrotId(world, meta.ParentEntity, out int id) || _replicas.ContainsKey(id) || !_sent.ContainsKey(id))
            {
                continue;
            }

            var pose = world.GetComponentRO<TurretPose>(part);
            double az = pose.Azimuth.Radians, el = pose.Elevation.Radians;
            var key = (id, meta.InstanceId);
            if (_poses.TryGetValue(key, out var previous))
            {
                previous.Seen = true;
                if (now - previous.At < ResendS && AngleDeg(az, previous.Azimuth) <= OrientationEpsilonDeg && AngleDeg(el, previous.Elevation) <= OrientationEpsilonDeg)
                {
                    continue;
                }

                previous.Azimuth = az;
                previous.Elevation = el;
                previous.At = now;
            }
            else
            {
                _poses[key] = new SentPose { Azimuth = az, Elevation = el, At = now, Seen = true };
            }

            inbound.Enqueue(new HrotTurretPose(id, meta.InstanceId, az, el));
            Counters.TurretPosesSent++;
        }

        _scratchPoses.Clear();
        foreach (var (key, p) in _poses)
        {
            if (!p.Seen)
            {
                _scratchPoses.Add(key);
            }
        }

        foreach (var key in _scratchPoses)
        {
            _poses.Remove(key);
        }
    }

    private static double AngleDeg(double a, double b)
    {
        double d = Math.Abs(a - b) % (2.0 * Math.PI);
        return Math.Min(d, 2.0 * Math.PI - d) * (180.0 / Math.PI);
    }

    /// <summary>HROT stamps a round's place in its burst 1-based, DPS wants 0-based (DD-027): hrot - 1; 0 when the round is not part of a planned burst.</summary>
    internal static byte BurstIndexOf(uint burstId, byte hrotIndex) => burstId == 0 || hrotIndex == 0 ? (byte)0 : (byte)(hrotIndex - 1);

    /// <summary>Publishes last frame's fire and detonation events of HROT entities (not <c>IsRemote</c>, not by a replica).</summary>
    public void GatherEvents(EntityRepository world, InMemoryInbound inbound)
    {
        var tkb = _services.Tkb();
        _hud.GatherEvents(world, inbound, Fdp.Toolkit.Time.SimClock.TotalTimeOf(world));
        var fires = world.ReadEvents<WeaponFireNotification>();
        for (int i = 0; i < fires.Length; i++)
        {
            ref readonly var f = ref fires[i];
            if (f.IsRemote || !f.Fired || !TryHrotId(world, f.Shooter, out int shooter))
            {
                continue;
            }

            // A flamethrower's fuel shot (~10/s) is a stream, not a gunshot: HROT's look rules give it no flash or blast, so no HrotFire.
            if (Hrot.Look.Effects.EffectRules.IsFuelShot(CombatTkb.MountOf(world, f.Shooter, f.WeaponIndex)))
            {
                continue;
            }

            TryHrotId(world, f.Target, out int target);
            var origin = f.Origin == Vector3.Zero && world.HasComponent<SimTransform>(f.Shooter) ? (Vector3)world.GetComponentRO<SimTransform>(f.Shooter).Position : f.Origin;
            if (_munitions.TryGather(tkb, f.AmmoGuid, out var munitionLook))
            {
                inbound.Enqueue(in munitionLook);
            }

            _types.Reference(f.AmmoGuid);
            string category = CombatTkb.MountOf(world, f.Shooter, f.WeaponIndex)?.Category.ToString() ?? string.Empty;   // HROT's weapon category by name
            inbound.Enqueue(new HrotFire(shooter, target, f.AmmoGuid, MunitionDis(tkb, f.AmmoGuid), V(origin), V(f.Velocity), Math.Max(1, f.Quantity), category,
                f.BurstId, f.BurstRounds, BurstIndexOf(f.BurstId, f.BurstIndex)));
            Counters.FiresSent++;
        }

        var hits = world.ReadEvents<DetonationNotification>();
        for (int i = 0; i < hits.Length; i++)
        {
            ref readonly var d = ref hits[i];
            if (d.IsRemote || !TryHrotId(world, d.Shooter, out int shooter))
            {
                continue;
            }

            _types.Reference(d.Ammo);
            TryHrotId(world, d.Target, out int target);
            inbound.Enqueue(new HrotDetonation(shooter, target, d.Ammo, MunitionDis(tkb, d.Ammo), new HrotVec3(d.HitX, d.HitY, d.HitZ),
                new HrotVec3(d.TravelX, d.TravelY, d.TravelZ)));
            Counters.DetonationsSent++;
        }

        _types.Flush(tkb, inbound);
    }

    /// <summary>
    /// The munition types an entity's weapons have loaded (a mount with no loadout fires the mount's own round, so that one is asked once, when the entity is first seen).
    /// </summary>
    private void ReferenceLoadedAmmo(EntityRepository world, Entity e, bool first)
    {
        if (!world.IsComponentTypeRegistered<WeaponAmmo>() || !world.HasComponent<WeaponAmmo>(e))
        {
            return;
        }

        ref readonly var ammo = ref world.GetComponentRO<WeaponAmmo>(e);
        for (int i = 0; i < ammo.Count && i < WeaponAmmo.MaxMounts; i++)
        {
            long loaded = ammo.LoadedOf(i);
            if (loaded != 0)
            {
                _types.Reference(loaded);
            }
            else if (first && CombatTkb.MountOf(world, e, i) is { } mount)
            {
                _types.Reference(unchecked((long)mount.AmmoGuid));
            }
        }
    }

    private bool TryHrotId(EntityRepository world, Entity e, out int id)
    {
        id = 0;
        if (e == Entity.Null || !world.IsAlive(e) || !world.HasComponent<NetworkIdentity>(e) || world.HasComponent<ReplicationRoute>(e))
        {
            return false;
        }

        id = world.GetComponentRO<NetworkIdentity>(e).Value;
        return id != 0;
    }

    private HrotEntityState StateOf(EntityRepository world, Entity e, int id, ITkbDatabase? tkb, double simTime)
    {
        var tf = world.GetComponentRO<SimTransform>(e);
        long tkbType = world.GetComponentRO<TkbIdentity>(e).TkbType.Value;
        long dis = 0;
        if (tkb is not null && tkb.TryGetByType(tkbType, out var template))
        {
            dis = (long)template.DisType.Value;
        }

        string marking = string.Empty;
        int force = 3;
        if (world.HasComponent<EntityInfo>(e))
        {
            var info = world.GetComponentRO<EntityInfo>(e);
            marking = info.Name.ToString();
            force = info.ForceId switch { ForceId.Friend => 1, ForceId.Hostile => 2, _ => 3 };
        }

        var velocity = HrotVec3.Zero;
        var angular = HrotVec3.Zero;
        if (world.HasComponent<SimVelocity>(e))
        {
            var v = world.GetComponentRO<SimVelocity>(e);
            velocity = V(v.Linear);
            angular = V(v.Angular);
        }

        double max = _config.Shared.EntityMaxHealth, current = max;
        if (world.HasComponent<Health>(e))
        {
            var h = world.GetComponentRO<Health>(e);
            current = h.Current;
            max = h.Max > 0 ? h.Max : max;
        }

        var (stance, weapon, aim, injury, aimTarget) = LifeFormOf(world, e, dis);
        double bank = _banks.Update(world, e, id, tkbType, dis, simTime);
        return new HrotEntityState(id, tkbType, dis, marking, force, 0, V(tf.Position), new HrotQuat(tf.Rotation.X, tf.Rotation.Y, tf.Rotation.Z, tf.Rotation.W),
            velocity, angular, current, max, simTime, stance, weapon, aim, injury, bank, aimTarget);
    }

    /// <summary>
    /// Stance, weapon posture, aim point and injury of a life form; a life form is an entity whose DIS kind is 3 (HROT's tanks carry a weapon posture too, and a spawned
    /// soldier has no stance intent until a stance is ordered) - anything else keeps Unknown / None / Ok. The aim point is the aim target's current position, else the aimed point; with neither the weapon is just ready.
    /// </summary>
    private static (HrotStance, HrotWeaponPosture, HrotVec3, HrotInjury, int) LifeFormOf(EntityRepository world, Entity e, long disType)
    {
        bool hasPosture = world.HasComponent<WeaponPosture>(e);
        if (disType == 0 || DisTypeCode.Unpack(disType).Kind != LifeFormKind)
        {
            return (HrotStance.Unknown, HrotWeaponPosture.None, HrotVec3.Zero, HrotInjury.Ok, 0);
        }

        var injury = world.HasComponent<Health>(e)
            ? Injury.StateOf(world, e) switch
            {
                InjuryState.Wounded => HrotInjury.Wounded,
                InjuryState.Incapacitated => HrotInjury.Incapacitated,
                InjuryState.Dead => HrotInjury.Dead,
                _ => HrotInjury.Ok,
            }
            : HrotInjury.Ok;
        var stance = LogicalStance.Of(world, e) switch
        {
            StanceId.Crouched => HrotStance.Crouched,
            StanceId.Prone => HrotStance.Prone,
            _ => HrotStance.Standing,
        };

        var weapon = HrotWeaponPosture.None;
        var aim = HrotVec3.Zero;
        int aimTarget = 0;
        if (hasPosture)
        {
            var posture = world.GetComponentRO<WeaponPosture>(e);
            // HeldItem None means his own weapon (HROT draws it); only a phone or binoculars in hand take the weapon away
            if (posture.HeldItem is not (HeldItemKind.Phone or HeldItemKind.Binoculars))
            {
                weapon = posture.Kind switch
                {
                    WeaponPostureKind.AtEase => HrotWeaponPosture.AtEase,
                    WeaponPostureKind.Aimed => HrotWeaponPosture.Aimed,
                    _ => HrotWeaponPosture.Ready,
                };
                if (weapon == HrotWeaponPosture.Aimed)
                {
                    // HROT's one reading of the posture's aim: the target's chest (1 m above its position) or the aimed point
                    if (WeaponPostureOf.TryAimPoint(world, e, out var at))
                    {
                        aim = V(at);
                        // the intent (DD-020 3.2): the aimed entity, whose chest the client derives itself; the point stays as the fallback
                        aimTarget = LookIds.NetworkIdOf(world, posture.Target);
                    }
                    else
                    {
                        weapon = HrotWeaponPosture.Ready;   // the target is gone and there is no point: straight ahead
                    }
                }
            }
        }

        return (stance, weapon, aim, injury, aimTarget);
    }

    private static bool Changed(in HrotEntityState a, in HrotEntityState b)
    {
        if (a.Marking != b.Marking || a.Force != b.Force || a.TkbType != b.TkbType || a.HealthCurrent != b.HealthCurrent || a.HealthMax != b.HealthMax
            || a.Stance != b.Stance || a.Weapon != b.Weapon || a.Injury != b.Injury || Math.Abs(a.Bank - b.Bank) * (180.0 / Math.PI) > OrientationEpsilonDeg || a.AimTargetId != b.AimTargetId || (b.Weapon == HrotWeaponPosture.Aimed && Dist(a.AimPoint, b.AimPoint) > AimEpsilonM))
        {
            return true;
        }

        if (Dist(a.Position, b.Position) > PositionEpsilonM || Dist(a.Velocity, b.Velocity) > VelocityEpsilon || Dist(a.AngularVelocity, b.AngularVelocity) > VelocityEpsilon)
        {
            return true;
        }

        double dot = Math.Abs(a.Rotation.X * b.Rotation.X + a.Rotation.Y * b.Rotation.Y + a.Rotation.Z * b.Rotation.Z + a.Rotation.W * b.Rotation.W);
        return 2.0 * Math.Acos(Math.Min(1.0, dot)) * (180.0 / Math.PI) > OrientationEpsilonDeg;
    }

    private static double Dist(HrotVec3 a, HrotVec3 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static long MunitionDis(ITkbDatabase? tkb, long munitionTkb) =>
        tkb is not null && munitionTkb != 0 && tkb.TryGetByType(munitionTkb, out var t) ? (long)t.DisType.Value : 0;

    private static HrotVec3 V(Vector3 v) => new(v.X, v.Y, v.Z);

    private void BindQueries(EntityRepository world)
    {
        if (ReferenceEquals(_queryWorld, world))
        {
            return;
        }

        _queryWorld = world;
        // record entities (building breaches, craters) carry a transform and a network id but are not units to show; nor are a terrain's
        // DOORS (T-073): a door is part of its house, which a client builds from the terrain - ~4,430 on Stechovice were gathered every tick,
        // re-ingested every second and sent to DPS as entities (the Unreal lane's measurement: the gateway halved headless HROT's tick rate)
        _hrotQuery = world.Query().With<SimTransform>().With<TkbIdentity>().With<NetworkIdentity>().Without<ReplicationRoute>().Without<Fdp.Toolkit.Terrain.BuildingDamage>().Without<Fdp.Toolkit.Terrain.GroundDamage>().Without<Fdp.Toolkit.Terrain.DoorState>().Build();
        _routeQuery = world.Query().With<ReplicationRoute>().Build();
        _turretQuery = world.Query().With<PartMetadata>().With<TurretPose>().Build();
    }

    // ---- gateway -> HROT ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Applies what the gateway sent this tick. With <paramref name="ingress"/> false (replay, review: the recording owns the world) everything that
    /// would enter the world is dropped and counted.
    /// </summary>
    public void Apply(EntityRepository world, QueueOutbound<IGatewayToHrotMessage> outbound, bool ingress)
    {
        while (outbound.TryDequeue(out var message))
        {
            if (!ingress && message is ReplicaState or ReplicaRemoved or InjectFire or InjectDetonation)
            {
                Counters.DroppedInReplay++;
                continue;
            }

            switch (message)
            {
                case ReplicaState s:
                    Upsert(world, in s);
                    break;
                case ReplicaRemoved r:
                    RemoveReplica(world, r.Id);
                    break;
                case InjectFire f:
                    InjectFire_(world, in f);
                    break;
                case InjectDetonation d:
                    InjectDetonation_(world, in d);
                    break;
                case TimeRequest:
                    Counters.TimeRequestsIgnored++;   // disControlsHrot is refused at composition; a request should not arrive
                    break;
                case HrotDescriptorOwnership or ExternalState:
                    Counters.OwnershipMessagesIgnored++;   // iteration 1 produces none and applies none
                    if (!_warnedOwnership)
                    {
                        _warnedOwnership = true;
                        Console.Error.WriteLine("[SimNetGateway] a descriptor ownership message arrived; ownership messages are not applied in this iteration (counted).");
                    }

                    break;
            }
        }
    }

    private void Upsert(EntityRepository world, in ReplicaState s)
    {
        if (s.Id == 0)
        {
            return;
        }

        if (_replicas.TryGetValue(s.Id, out var existing) && world.IsAlive(existing.Entity))
        {
            existing.State = s.State;
            if (world.HasComponent<EntityInfo>(existing.Entity))
            {
                world.SetComponent(existing.Entity, InfoOf(in s));
            }

            if (world.HasComponent<Health>(existing.Entity))
            {
                world.SetComponent(existing.Entity, HealthOf(in s));
            }

            return;
        }

        if (existing is not null)
        {
            _replicas.Remove(s.Id);   // its entity is gone (a restore replaced it): create anew below
        }

        _replicas[s.Id] = Create(world, in s);
        Counters.ReplicasCreated++;
    }

    private DisReplica Create(EntityRepository world, in ReplicaState s)
    {
        var e = world.CreateEntity();
        var map = _services.EntityMap;
        world.AddComponent(e, new NetworkIdentity(s.Id));
        world.AddComponent(e, new NetworkAuthority(primaryOwnerId: _config.Hrot.ReplicaOwnerNodeId, localNodeId: _services.LocalNodeId));
        var tkb = _services.Tkb();
        if (s.TkbType != 0 && (tkb is null || tkb.TryGetByType(s.TkbType, out _)))
        {
            world.AddComponent(e, new TkbIdentity { TkbType = new TkbTypeId(s.TkbType) });
        }

        world.AddComponent(e, new ReplicationRoute(ReplicationRoute.ExternalGateway));
        world.AddComponent(e, new ScenarioIgnoreTag());
        world.AddComponent(e, InfoOf(in s));
        var dis = new DISEntityType { Value = (ulong)s.DisType };
        world.SetDisType(e, dis);
        world.AddComponent(e, new PhysicsCollider { Radius = RadiusOf(dis), Height = 2f, CollisionLayer = PhysicsConstants.EntityCollisionLayer });
        var p = s.State;
        world.AddComponent(e, new SimTransform
        {
            Position = new WorldPoint((float)p.Position.X, (float)p.Position.Y, (float)p.Position.Z),
            Rotation = new WorldRotation((float)p.Rotation.X, (float)p.Rotation.Y, (float)p.Rotation.Z, (float)p.Rotation.W),
        });
        world.AddComponent(e, new SimVelocity { Linear = new Vector3((float)p.Velocity.X, (float)p.Velocity.Y, (float)p.Velocity.Z) });
        world.AddComponent(e, HealthOf(in s));
        map?.Register(s.Id, e);
        return new DisReplica(s.Id, e, in p);
    }

    private static float RadiusOf(DISEntityType t) => t.Kind == 1 ? t.Domain switch { 1 => 2.5f, 2 => 6f, 3 => 8f, _ => 3f } : 1f;

    private static EntityInfo InfoOf(in ReplicaState s) => new()
    {
        Name = new FixedString64(s.Marking ?? string.Empty),
        ForceId = s.Force switch { 1 => ForceId.Friend, 2 => ForceId.Hostile, _ => ForceId.Neutral },
    };

    private static Health HealthOf(in ReplicaState s) => new() { Current = (float)s.HealthCurrent, Max = s.HealthMax > 0 ? (float)s.HealthMax : DefaultReplicaHealth };

    private void RemoveReplica(EntityRepository world, int id)
    {
        if (_replicas.Remove(id, out var replica))
        {
            Destroy(world, replica.Entity, id);
            Counters.ReplicasRemoved++;
        }
    }

    private void Destroy(EntityRepository world, Entity e, int id)
    {
        if (!world.IsAlive(e))
        {
            return;
        }

        var map = _services.EntityMap;
        if (map is not null && map.TryGetEntity(id, out var mapped) && mapped == e)
        {
            uint frame = world.HasSingleton<GlobalTime>() ? (uint)world.GetSingleton<GlobalTime>().FrameNumber : 0u;
            map.Unregister(id, frame);
        }

        world.DestroyEntity(e);
    }

    /// <summary>Destroys every replica (replay entered, the wire went away).</summary>
    public void RemoveAllReplicas(EntityRepository world)
    {
        foreach (var (id, replica) in _replicas.ToArray())
        {
            Destroy(world, replica.Entity, id);
            Counters.ReplicasRemoved++;
        }

        _replicas.Clear();
        SweepRoutedEntities(world);
    }

    /// <summary>
    /// The world was put back (a load, a checkpoint restore, a recording restored into it): forget both books and destroy any entity on the gateway
    /// route that the restore brought back - replicas are never restored. The gateway engine is rebuilt by the caller.
    /// </summary>
    // ---- replay effects ---------------------------------------------------------------------------------------

    private const double ReplayJumpS = 2.0;
    private readonly HashSet<(Entity Entity, double BornAt)> _flashesSeen = new();
    private readonly HashSet<(Entity Entity, double BornAt)> _flashesNow = new();
    private EntityQuery? _flashQuery;
    private EntityRepository? _flashWorld;
    private long _lastSerial;
    private double _effectsTime;
    private bool _effectsSynced;
    private bool _warnedNoMemory;

    /// <summary>
    /// A replay shows HROT's recorded WORLD, not its events (the recording is played without them): fire and detonations are read from what the world remembers.
    /// Detonations: the entries of the recorded <c>RecentDetonations</c> singleton with a serial above the last one sent. Fire: muzzle effect entities (an
    /// <c>EffectAnchor</c> of kind Muzzle) not seen before, with the munition loaded on the shooter's weapon of that index. After entering a replay, a seek, a jump or time going
    /// backwards the newest serial and the live flashes are only taken as "already sent": nothing is sent for what was skipped. Call every frame while replaying.
    /// </summary>
    public void GatherReplayEffects(EntityRepository world, InMemoryInbound inbound, double simNow)
    {
        bool hasMemory = world.HasSingletonUnmanaged<RecentDetonations>();
        if (!hasMemory && !_warnedNoMemory)
        {
            _warnedNoMemory = true;
            Console.Error.WriteLine("[SimNetGateway] this node keeps no recent detonations: detonations are not sent in a replay (fire still is).");
        }

        long newest = hasMemory ? RecentDetonations.CountOf(world) : 0;
        if (!_effectsSynced || simNow < _effectsTime - 1e-6 || simNow - _effectsTime > ReplayJumpS || newest < _lastSerial)
        {
            ResyncEffects(world, newest);
            _effectsTime = simNow;
            return;
        }

        _effectsTime = simNow;
        var tkb = _services.Tkb();
        if (hasMemory && newest > _lastSerial)
        {
            var memory = world.GetSingletonUnmanagedRO<RecentDetonations>();
            for (long serial = Math.Max(_lastSerial + 1, newest - RecentDetonations.Capacity + 1); serial <= newest; serial++)
            {
                var burst = memory.Slots[(int)((serial - 1) % RecentDetonations.Capacity)];
                TryHrotId(world, burst.Shooter, out int shooter);
                inbound.Enqueue(new HrotDetonation(shooter, 0, burst.Ammo, MunitionDis(tkb, burst.Ammo), V(burst.At), HrotVec3.Zero));
                Counters.DetonationsSent++;
            }

            _lastSerial = newest;
        }

        BindFlashQuery(world);
        _flashesNow.Clear();
        foreach (var e in _flashQuery!)
        {
            var anchor = world.GetComponentRO<EffectAnchor>(e);
            if (anchor.Kind != EffectAnchorKind.Muzzle)
            {
                continue;
            }

            var key = (e, (double)world.GetComponentRO<Lifespan>(e).BornAtSimSeconds);
            _flashesNow.Add(key);
            if (!_flashesSeen.Add(key) || !TryHrotId(world, anchor.Shooter, out int shooterId))
            {
                continue;
            }

            var mount = CombatTkb.MountOf(world, anchor.Shooter, anchor.WeaponIndex);
            long ammo = mount is null ? 0L : unchecked((long)mount.AmmoGuid);
            var at = world.HasComponent<SimTransform>(anchor.Shooter) ? V(world.GetComponentRO<SimTransform>(anchor.Shooter).Position) : HrotVec3.Zero;
            inbound.Enqueue(new HrotFire(shooterId, 0, ammo, MunitionDis(tkb, ammo), at, HrotVec3.Zero, 1));
            Counters.FiresSent++;
        }

        _flashesSeen.IntersectWith(_flashesNow);
    }

    /// <summary>
    /// Effects from state, live and in a replay (the one path, R-11007): the effect entities HROT's spawner made or a recording holds, each sent once. Call every frame the gateway
    /// gathers events. A node that runs no effect spawner has no such entities: it warns once when shots were sent and no effect ever followed.
    /// </summary>
    public void GatherEffects(EntityRepository world, InMemoryInbound inbound, double simNow, bool replaying)
    {
        _effects.Gather(world, _services.Tkb(), inbound, simNow, replaying, ReplayJumpS, _types);
        if (_effects.Sent > 0 || _warnedNoSpawner || Counters.FiresSent == 0)
        {
            return;
        }

        if (_firstFireAt < 0.0)
        {
            _firstFireAt = simNow;
        }
        else if (simNow - _firstFireAt > NoSpawnerWarnS)
        {
            _warnedNoSpawner = true;
            Console.Error.WriteLine("[SimNetGateway] shots were sent but this node made no effect entity: Effect needs HROT's effect spawner (a map host) on the gateway's node.");
        }
    }

    private const double NoSpawnerWarnS = 3.0;
    private bool _warnedNoSpawner;
    private double _firstFireAt = -1.0;

    /// <summary>Leaving a replay (or the world moving): the next replay starts from a fresh baseline.</summary>
    public void ForgetReplayEffects()
    {
        _effectsSynced = false;
        _flashesSeen.Clear();
    }

    private void ResyncEffects(EntityRepository world, long newest)
    {
        _lastSerial = newest;
        BindFlashQuery(world);
        _flashesSeen.Clear();
        foreach (var e in _flashQuery!)
        {
            if (world.GetComponentRO<EffectAnchor>(e).Kind == EffectAnchorKind.Muzzle)
            {
                _flashesSeen.Add((e, (double)world.GetComponentRO<Lifespan>(e).BornAtSimSeconds));
            }
        }

        _effectsSynced = true;
    }

    private void BindFlashQuery(EntityRepository world)
    {
        if (!ReferenceEquals(_flashWorld, world))
        {
            _flashWorld = world;
            _flashQuery = world.Query().With<EffectAnchor>().With<Lifespan>().Build();
        }
    }

    public void Reset(EntityRepository world)
    {
        _replicas.Clear();
        _sent.Clear();
        _details.Clear();
        _status.Clear();
        _voice.Clear();   // a world boundary forgets: nothing is replayed (DD-026)
        _ambience.Clear();   // and a new world states every layer and weather again (DD-028)
        _weather.Clear();
        _appearances.Clear();
        _vehicleLooks.Clear();
        _rounds.Clear();
        _munitions.Clear();
        _jets.Clear();
        _fireSpots.Clear();
        _groundMarks.Clear();
        _hud.Clear();
        _buildings.Clear();
        _craters.Clear();
        _traffic.Clear();
        _types.Clear();
        _animals.Clear();
        _view.Clear();
        _banks.Clear();
        ForgetReplayEffects();
        _effects.Clear();
        _warnedNoSpawner = false;
        _firstFireAt = -1.0;
        SweepRoutedEntities(world);
        _queryWorld = null;
    }

    private void SweepRoutedEntities(EntityRepository world)
    {
        BindQueries(world);
        _scratch.Clear();
        var doomed = new List<(Entity Entity, int Id)>();
        foreach (var e in _routeQuery!)
        {
            if (world.GetComponentRO<ReplicationRoute>(e).RouteId == ReplicationRoute.ExternalGateway)
            {
                doomed.Add((e, world.HasComponent<NetworkIdentity>(e) ? world.GetComponentRO<NetworkIdentity>(e).Value : 0));
            }
        }

        foreach (var (e, id) in doomed)
        {
            Destroy(world, e, id);
        }
    }

    private void InjectFire_(EntityRepository world, in InjectFire f)
    {
        world.Bus.Publish(new WeaponFireNotification
        {
            Shooter = EntityOf(world, f.ShooterId),
            Target = EntityOf(world, f.TargetId),
            IsRemote = true,   // effects, sound, shot ledger - never a new shot
            AmmoGuid = f.MunitionTkb,
            Origin = new Vector3((float)f.Origin.X, (float)f.Origin.Y, (float)f.Origin.Z),
            Velocity = new Vector3((float)f.Velocity.X, (float)f.Velocity.Y, (float)f.Velocity.Z),
            Quantity = Math.Max(1, f.Quantity),
        });
        Counters.FiresInjected++;
    }

    private void InjectDetonation_(EntityRepository world, in InjectDetonation d)
    {
        var target = EntityOf(world, d.TargetId);
        // a target simulated by HROT (not one of our replicas) is damaged by HROT's own DamageCalculationSystem (DIS receiver adjudication);
        // anything else gets the effects only
        bool hrotTarget = d.TargetIsHrot && target != Entity.Null && !_replicas.ContainsKey(d.TargetId);
        world.Bus.Publish(new DetonationNotification
        {
            Shooter = EntityOf(world, d.ShooterId),
            Target = target,
            HitX = (float)d.Location.X,
            HitY = (float)d.Location.Y,
            HitZ = (float)d.Location.Z,
            IsRemote = !hrotTarget,
            Ammo = d.MunitionTkb,
            TravelX = (float)d.Velocity.X,
            TravelY = (float)d.Velocity.Y,
            TravelZ = (float)d.Velocity.Z,
        });
        Counters.DetonationsInjected++;
    }

    private Entity EntityOf(EntityRepository world, int id)
    {
        if (id == 0)
        {
            return Entity.Null;
        }

        if (_replicas.TryGetValue(id, out var replica) && world.IsAlive(replica.Entity))
        {
            return replica.Entity;
        }

        var map = _services.EntityMap;
        if (map is not null && map.TryGetEntity(id, out var e) && world.IsAlive(e))
        {
            return e;
        }

        // no map: find the HROT entity by its network id (a world without a node map)
        BindQueries(world);
        foreach (var candidate in _hrotQuery!)
        {
            if (world.GetComponentRO<NetworkIdentity>(candidate).Value == id)
            {
                return candidate;
            }
        }

        return Entity.Null;
    }
}
