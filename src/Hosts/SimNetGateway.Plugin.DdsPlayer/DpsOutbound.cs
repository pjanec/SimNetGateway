using SimNetGateway.Common.Geo;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Maps what HROT sends (the pivot messages of <c>Protocol.Hrot</c>) to DPS samples. It reads the message stream, not the pivot state: the person, effect,
/// appearance and vehicle-look messages are not stored by the DIS side (their ingresses there do nothing), so this module has its own handlers for every
/// message it publishes, and DIS output is unchanged. State instances go out on first sight, on a change beyond the thresholds and on the heartbeat (simulation
/// time); events go out as they come; a removed entity is disposed so that a late joiner never sees it. One instance per gateway runtime.
/// </summary>
public sealed class DpsOutbound : IGatewayModule
{
    private sealed class Book
    {
        public HrotEntityState State;
        public double StateSentAt = double.NegativeInfinity;
        public HrotVec3 SentPosition, SentVelocity;
        public HrotQuat SentRotation;
        public D.PartCondition SentDamage;
        public double SentBank;
        public bool IsPerson;
        public HrotPersonDetail Detail;
        public bool HasDetail;
        public D.PersonState SentPerson;
        public double PersonSentAt = double.NegativeInfinity;
        public bool HasSentPerson;
        public D.PersonAim SentAim;
        public double AimSentAt = double.NegativeInfinity;
        public bool HasSentAim;
        public readonly Dictionary<int, (double Azimuth, double Elevation)> Parts = new();
        public bool HasAppearance, HasVehicleLook;
    }

    // the producer's part ids for turrets: 2000 + the turret index (the contract's Articulation says kind and index, PartId stays the opaque key)
    private const int FirstTurretPartId = 2000, MaxTurrets = 16;

    /// <summary>The producer vocabulary of the free strings this module writes, and the terrain export format the building keys refer to (DD-020 3.3).</summary>
    internal const string Vocabulary = "hrot/1", TerrainFormat = "hrot-terrain/1";

    private readonly DpsSettings _settings;
    private readonly DpsPorts _ports;
    private readonly Dictionary<int, Book> _books = new();
    private HrotOrigin? _origin;
    private HrotSession? _session;
    private HrotTerrain _terrain;
    private double _simTime;

    internal DpsOutbound(DpsSettings settings, DpsPorts ports)
    {
        _settings = settings;
        _ports = ports;
    }

    public string Name => nameof(DpsOutbound);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotOrigin>(OnOrigin);
        setup.OnMessage<HrotSession>(OnSession);
        setup.OnMessage<HrotTerrain>(OnTerrain);
        setup.OnMessage<HrotEntityState>(OnState);
        setup.OnMessage<HrotEntityRemoved>(OnRemoved);
        setup.OnMessage<HrotTurretPose>(OnTurret);
        setup.OnMessage<HrotPersonDetail>(OnPersonDetail);
        setup.OnMessage<HrotFire>(OnFire);
        setup.OnMessage<HrotDetonation>(OnDetonation);
        setup.OnMessage<HrotEffect>(OnEffect);
        setup.OnMessage<HrotPersonAppearance>(OnAppearance);
        setup.OnMessage<HrotVehicleLook>(OnVehicleLook);
    }

    // ---- session ----------------------------------------------------------------------------------------------------

    private void OnOrigin(in HrotOrigin o, IngressContext ctx)
    {
        _origin = o;
        PublishSession();
    }

    private void OnSession(in HrotSession s, IngressContext ctx)
    {
        _session = s;
        if (double.IsFinite(s.SimTime))
        {
            _simTime = s.SimTime;
        }

        PublishSession();
    }

    private void OnTerrain(in HrotTerrain t, IngressContext ctx)
    {
        if (t == _terrain)
        {
            return;
        }

        _terrain = t;
        PublishSession();   // a changed terrain is a changed Session
    }

    private void PublishSession()
    {
        if (_session is not { } s)
        {
            return;
        }

        var mode = s.Phase == ClusterPhase.Replay ? D.SessionMode.SessionModeReplay : s.Advancing ? D.SessionMode.SessionModeLive : D.SessionMode.SessionModePaused;
        var session = new D.Session
        {
            ProtocolMajor = D.DpsProtocol.Major,
            ProtocolMinor = D.DpsProtocol.Minor,
            Mode = mode,
            SimTime = s.SimTime,
            TimeScale = s.TimeScale,
            Terrain = Truncate(_terrain.Name ?? string.Empty, 64),
            TerrainBaseHash = Truncate(_terrain.BaseHash ?? string.Empty, 64),
            TerrainOverlayHash = Truncate(_terrain.OverlayHash ?? string.Empty, 64),
            StationName = string.Empty,
            Vocabulary = Vocabulary,
            TerrainFormat = TerrainFormat,
        };
        if (_origin is { Frame: HrotOrigin.FrameUtm } o)
        {
            var utm = UtmFrame.AtOrigin(o.LatitudeDeg, o.LongitudeDeg, o.AltitudeM, o.UtmZoneOverride, o.UtmSouthOverride);
            session.UtmZone = utm.Zone;
            session.UtmNorthern = !utm.South;
            session.OriginEasting = utm.E0;
            session.OriginNorthing = utm.N0;
        }

        _ports.Session.Send(new DpsItem<D.Session>(session));
    }

    // ---- entities ---------------------------------------------------------------------------------------------------

    private void OnState(in HrotEntityState s, IngressContext ctx)
    {
        if (!Finite(s.Position) || !Finite(s.Velocity) || !double.IsFinite(s.SimTime))
        {
            return;
        }

        _simTime = Math.Max(_simTime, s.SimTime);
        bool known = _books.TryGetValue(s.Id, out var book);
        book ??= _books[s.Id] = new Book();
        var previous = book.State;
        book.State = s;
        var dis = DisTypeCode.Unpack(s.DisType);
        book.IsPerson = dis.Kind == 3 && !IsAnimal(dis);

        if (!known || previous.TkbType != s.TkbType || previous.DisType != s.DisType || previous.Force != s.Force || previous.Marking != s.Marking)
        {
            _ports.Entity.Send(new DpsItem<D.Entity>(new D.Entity
            {
                EntityId = s.Id,
                TypeId = s.TkbType,
                Domain = DomainOf(dis),
                Side = (byte)Math.Clamp(s.Force, 0, 255),
                Name = Truncate(s.Marking, 64),
                Dis = ToDis(dis),
                IsUnit = dis.Kind == UnitKind,
            }));
        }

        var damage = s.HealthCurrent <= 0.0 ? D.PartCondition.PartConditionDestroyed
            : s.HealthCurrent < s.HealthMax ? D.PartCondition.PartConditionDamaged : D.PartCondition.PartConditionIntact;
        if (!known || StateDue(book, in s, damage))
        {
            book.StateSentAt = s.SimTime;
            book.SentPosition = s.Position;
            book.SentVelocity = s.Velocity;
            book.SentRotation = s.Rotation;
            book.SentDamage = damage;
            book.SentBank = s.Bank;
            _ports.EntityState.Send(new DpsItem<D.EntityState>(new D.EntityState
            {
                EntityId = s.Id,
                SimTime = s.SimTime,
                Position = V3d(s.Position),
                Orientation = Q(s.Rotation),
                LinearVelocity = V3f(s.Velocity),
                AngularVelocity = V3f(s.AngularVelocity),
                Damage = damage,
                Bank = (float)s.Bank,
            }));
        }

        if (book.IsPerson && book.HasDetail)
        {
            PublishPerson(s.Id, book);
            PublishAim(s.Id, book);
        }
    }

    private bool StateDue(Book b, in HrotEntityState s, D.PartCondition damage) =>
        s.SimTime - b.StateSentAt >= _settings.HeartbeatSeconds
        || damage != b.SentDamage
        || Math.Abs(s.Bank - b.SentBank) * (180.0 / Math.PI) > _settings.AngleThresholdDeg
        || Dist(b.SentPosition, s.Position) > _settings.PositionThresholdM
        || Dist(b.SentVelocity, s.Velocity) > _settings.VelocityThreshold
        || AngleDeg(b.SentRotation, s.Rotation) > _settings.AngleThresholdDeg;

    private void OnRemoved(in HrotEntityRemoved m, IngressContext ctx)
    {
        if (!_books.Remove(m.Id, out var book))
        {
            return;
        }

        _ports.Entity.Send(new DpsItem<D.Entity>(new D.Entity { EntityId = m.Id, Name = string.Empty }, true));
        _ports.EntityState.Send(new DpsItem<D.EntityState>(new D.EntityState { EntityId = m.Id }, true));
        if (book.HasSentAim)
        {
            _ports.PersonAim.Send(new DpsItem<D.PersonAim>(new D.PersonAim { EntityId = m.Id }, true));
        }

        if (book.HasSentPerson)
        {
            _ports.PersonState.Send(new DpsItem<D.PersonState>(new D.PersonState { EntityId = m.Id }, true));
        }

        if (book.HasAppearance)
        {
            _ports.Appearance.Send(new DpsItem<D.Appearance>(new D.Appearance { EntityId = m.Id, Slots = [], Camouflage = string.Empty, Hair = string.Empty, FaceTraits = string.Empty, Equipment = [] }, true));
        }

        if (book.HasVehicleLook)
        {
            _ports.VehicleLook.Send(new DpsItem<D.VehicleLook>(new D.VehicleLook { EntityId = m.Id, Parts = [], Openings = [] }, true));
        }

        foreach (int part in book.Parts.Keys)
        {
            _ports.Articulation.Send(new DpsItem<D.Articulation>(new D.Articulation { EntityId = m.Id, PartId = part, Kind = string.Empty }, true));
        }
    }

    private void OnTurret(in HrotTurretPose m, IngressContext ctx)
    {
        if (!double.IsFinite(m.Azimuth) || !double.IsFinite(m.Elevation) || !_books.TryGetValue(m.Id, out var book))
        {
            return;
        }

        if (book.Parts.TryGetValue(m.PartId, out var last) && last == (m.Azimuth, m.Elevation))
        {
            return;
        }

        book.Parts[m.PartId] = (m.Azimuth, m.Elevation);
        int turretIndex = m.PartId is >= FirstTurretPartId and < FirstTurretPartId + MaxTurrets ? m.PartId - FirstTurretPartId : -1;
        _ports.Articulation.Send(new DpsItem<D.Articulation>(new D.Articulation
        {
            EntityId = m.Id,
            PartId = m.PartId,
            Kind = "Turret",
            PartKind = turretIndex >= 0 ? D.ArticulationKind.ArticulationKindTurret : D.ArticulationKind.ArticulationKindUnknown,
            Index = turretIndex >= 0 ? turretIndex : m.PartId,
            SimTime = _simTime,
            Azimuth = (float)m.Azimuth,
            Elevation = (float)m.Elevation,
        }));
    }

    // ---- people -----------------------------------------------------------------------------------------------------

    private void OnPersonDetail(in HrotPersonDetail m, IngressContext ctx)
    {
        if (!_books.TryGetValue(m.Id, out var book) || !book.IsPerson)
        {
            return;
        }

        book.Detail = m;
        book.HasDetail = true;
        PublishPerson(m.Id, book);
        PublishAim(m.Id, book);
    }

    private void PublishPerson(int id, Book b)
    {
        var s = b.State;
        var d = b.Detail;
        var person = new D.PersonState
        {
            EntityId = id,
            SimTime = s.SimTime,
            Stance = StanceOf(s.Stance),
            TargetStance = StanceOf(d.TargetStance),
            StanceProgress = d.StanceProgress,
            StanceChangedAt = d.StanceChangedAt,
            StanceSeconds = d.StanceSeconds,
            StanceFrom = d.StanceFrom == HrotStance.Unknown ? StanceOf(d.TargetStance) : StanceOf(d.StanceFrom),
            Posture = s.Weapon switch
            {
                HrotWeaponPosture.Aimed => D.WeaponPosture.WeaponPostureAimed,
                HrotWeaponPosture.Ready => D.WeaponPosture.WeaponPostureReady,
                _ => D.WeaponPosture.WeaponPostureAtEase,
            },
            AimPoint = V3d(s.AimPoint),
            ActiveSlot = d.ActiveMount,
            Held = (D.HeldItem)(byte)d.HeldItem,
            HasLookTarget = d.HasLookTarget,
            LookTarget = V3d(d.LookTarget),
            HeadYaw = d.HeadYaw,
            Gesture = (D.Gesture)(byte)d.Gesture,
            Injury = (D.Injury)(byte)s.Injury,
            Action = (D.ActionKind)(byte)d.Action,
            ActionTargetId = d.ActionTarget,
            ActionState = (D.ActionState)(byte)d.ActionState,
            ActionStartSimTime = d.ActionStartedAt,
            SeatVehicleId = d.SeatVehicle,
            SeatRole = (D.SeatRole)(byte)d.SeatRole,
            CarryMode = d.Carry switch
            {
                HrotCarryMode.Carried => D.CarryMode.CarryModeCarried,
                HrotCarryMode.Dragged => D.CarryMode.CarryModeDragged,
                HrotCarryMode.Stretcher => D.CarryMode.CarryModeStretcher,
                _ => D.CarryMode.CarryModeNone,
            },
            CarryOtherId = d.CarryOther,
            SeatIndex = d.SeatIndex,
            SeatExposed = d.SeatExposed,
            SeatPose = LookNames.SeatPoseOf(d.SeatPose),
            CarryRole = (D.CarryRole)(byte)d.CarryRole,
            PartnerId = d.Partner,
            Burning = d.Burning,
            Fear = d.Fear,
            Anger = d.Anger,
            CivilianMode = LookNames.CivilianModeOf(d.CivilianMode),
            HeldLook = (D.HeldLook)(byte)d.HeldLook,   // the pivot enums mirror the contract's in order (a rail pins it)
            SlungLook = (D.HeldLook)(byte)d.SlungLook,
            HeldMarking = (D.HeldMarking)(byte)d.HeldMarking,
        };
        if (b.HasSentPerson && !PersonDue(b, in person))
        {
            return;
        }

        b.SentPerson = person;
        b.HasSentPerson = true;
        b.PersonSentAt = s.SimTime;
        _ports.PersonState.Send(new DpsItem<D.PersonState>(person));
    }

    private bool PersonDue(Book b, in D.PersonState p)
    {
        var o = b.SentPerson;
        return p.SimTime - b.PersonSentAt >= _settings.HeartbeatSeconds
            || p.Stance != o.Stance || p.TargetStance != o.TargetStance || p.Posture != o.Posture || p.ActiveSlot != o.ActiveSlot || p.Held != o.Held
            || p.Gesture != o.Gesture || p.Injury != o.Injury || p.Action != o.Action || p.ActionTargetId != o.ActionTargetId
            || p.ActionState != o.ActionState || p.ActionStartSimTime != o.ActionStartSimTime || p.SeatVehicleId != o.SeatVehicleId || p.SeatRole != o.SeatRole
            || p.CarryMode != o.CarryMode || p.CarryOtherId != o.CarryOtherId || p.CarryRole != o.CarryRole || p.PartnerId != o.PartnerId
            || p.SeatIndex != o.SeatIndex || p.SeatExposed != o.SeatExposed || p.SeatPose != o.SeatPose || p.StanceChangedAt != o.StanceChangedAt || p.StanceSeconds != o.StanceSeconds || p.StanceFrom != o.StanceFrom
            || p.Burning != o.Burning || p.Fear != o.Fear || p.Anger != o.Anger || p.CivilianMode != o.CivilianMode
            || p.HeldLook != o.HeldLook || p.SlungLook != o.SlungLook || p.HeldMarking != o.HeldMarking;
    }

    // ---- aim and look intent (DD-020 3.2) -----------------------------------------------------------------------------

    /// <summary>A head turn smaller than this is "straight on the body" (about 1 degree).</summary>
    private const float HeadOffRadians = 0.02f;

    /// <summary>
    /// The person's aim and look as intent: a target entity (the client derives the point from its pose) or a point. Written when the target, the point (a point aim or
    /// look only) or the head's on/off state changes, and as a heartbeat while it is not empty; nothing is sent for a man who never aimed or looked.
    /// </summary>
    private void PublishAim(int id, Book b)
    {
        if (!b.IsPerson || !b.HasDetail)
        {
            return;
        }

        var s = b.State;
        var d = b.Detail;
        bool aimed = s.Weapon == HrotWeaponPosture.Aimed;
        var aim = new D.PersonAim
        {
            EntityId = id,
            SimTime = s.SimTime,
            AimTargetId = aimed ? s.AimTargetId : 0,
            AimPoint = aimed ? V3d(s.AimPoint) : default,
            LookTargetId = d.HasLookTarget ? d.LookTargetId : 0,
            HasLookTarget = d.HasLookTarget,
            LookTarget = d.HasLookTarget ? V3d(d.LookTarget) : default,
            HeadYaw = float.IsFinite(d.HeadYaw) ? d.HeadYaw : 0f,
        };
        bool empty = aim.AimTargetId == 0 && aim.AimPoint.X == 0 && aim.AimPoint.Y == 0 && aim.AimPoint.Z == 0 && aim.LookTargetId == 0 && !aim.HasLookTarget && aim.HeadYaw == 0f;
        if (b.HasSentAim ? !AimDue(b, in aim, empty) : empty)
        {
            return;
        }

        b.SentAim = aim;
        b.HasSentAim = true;
        b.AimSentAt = s.SimTime;
        _ports.PersonAim.Send(new DpsItem<D.PersonAim>(aim));
    }

    private bool AimDue(Book b, in D.PersonAim p, bool empty)
    {
        var o = b.SentAim;
        return (!empty && p.SimTime - b.AimSentAt >= _settings.HeartbeatSeconds)
            || p.AimTargetId != o.AimTargetId || p.LookTargetId != o.LookTargetId || p.HasLookTarget != o.HasLookTarget
            || (p.AimTargetId == 0 && Dist(p.AimPoint, o.AimPoint) > _settings.PositionThresholdM)
            || (p.LookTargetId == 0 && p.HasLookTarget && Dist(p.LookTarget, o.LookTarget) > _settings.PositionThresholdM)
            || (MathF.Abs(p.HeadYaw) >= HeadOffRadians) != (MathF.Abs(o.HeadYaw) >= HeadOffRadians);
    }

    private void OnAppearance(in HrotPersonAppearance m, IngressContext ctx)
    {
        if (!_books.TryGetValue(m.Id, out var book))
        {
            return;
        }

        book.HasAppearance = true;
        _ports.Appearance.Send(new DpsItem<D.Appearance>(OutfitMapping.ToAppearance(in m)));
    }

    // ---- vehicles ---------------------------------------------------------------------------------------------------

    private void OnVehicleLook(in HrotVehicleLook m, IngressContext ctx)
    {
        if (!_books.TryGetValue(m.Id, out var book))
        {
            return;
        }

        book.HasVehicleLook = true;
        var crew = m.CrewExposed;
        var motions = m.OpeningMotions;
        var look = new D.VehicleLook
        {
            EntityId = m.Id,
            KillKind = LookNames.KillKindOf(m.Destruction),
            TurretThrown = m.TurretThrown,
            Burning = m.Burning,
            Smoking = m.Smoking,
            LightMask = m.Lights,
            FlailActive = m.FlailActive,
            Parts = [.. m.Parts.Take(32).Select(p => new D.PartLook { PartId = p.Part, State = (D.PartCondition)Math.Min(p.State, (byte)2) })],
            RotorSpeed = float.IsFinite(m.RotorSpeed) ? Math.Clamp(m.RotorSpeed, 0f, 1f) : 0f,
            RotorTarget = Unit(m.RotorTarget),
            RotorFrom = Unit(m.RotorFrom),
            RotorChangedAt = double.IsFinite(m.RotorChangedAt) ? m.RotorChangedAt : 0.0,
            RotorSpoolSeconds = float.IsFinite(m.RotorSpoolSeconds) ? Math.Max(0f, m.RotorSpoolSeconds) : 0f,
            Openings = [.. m.Openings.Take(16).Select(o => OpeningOf(o, crew, motions))],
        };
        _ports.VehicleLook.Send(new DpsItem<D.VehicleLook>(look));
    }

    // ---- events -----------------------------------------------------------------------------------------------------

    private void OnFire(in HrotFire m, IngressContext ctx) =>
        _ports.Fire.Send(new DpsItem<D.Fire>(new D.Fire
        {
            SimTime = _simTime,
            ShooterId = m.ShooterId,
            TargetId = m.TargetId,
            MunitionTypeId = m.MunitionTkb,
            MunitionDis = ToDis(DisTypeCode.Unpack(m.MunitionDisType)),
            WeaponSlot = 0,
            Origin = V3d(m.Origin),
            Velocity = V3f(m.Velocity),
            WeaponCategory = LookNames.WeaponCategoryOf(m.WeaponCategory),
            BurstId = m.BurstId,
            BurstRounds = m.BurstId == 0 ? (byte)0 : m.BurstRounds,
            BurstIndex = m.BurstId == 0 ? (byte)0 : m.BurstIndex,
        }));

    private void OnDetonation(in HrotDetonation m, IngressContext ctx) =>
        _ports.Detonation.Send(new DpsItem<D.Detonation>(new D.Detonation
        {
            SimTime = _simTime,
            ShooterId = m.ShooterId,
            TargetId = m.TargetId,
            MunitionTypeId = m.MunitionTkb,
            MunitionDis = ToDis(DisTypeCode.Unpack(m.MunitionDisType)),
            Position = V3d(m.Location),
            Velocity = V3f(m.Velocity),
            Result = 0,
        }));

    private void OnEffect(in HrotEffect m, IngressContext ctx)
    {
        if (!LookNames.TryEffectKindOf(m.Kind, out var kind))
        {
            return;   // a kind DPS deliberately does not carry
        }

        _ports.Effect.Send(new DpsItem<D.Effect>(new D.Effect
        {
            SimTime = m.StartedAt > 0.0 ? m.StartedAt : _simTime,
            Kind = kind,
            SizeClass = LookNames.EffectSizeOf(m.SizeClass),
            Surface = LookNames.EffectSurfaceOf(m.Surface),
            MunitionDis = ToDis(DisTypeCode.Unpack(unchecked((long)m.MunitionDis))),
            EffectType = unchecked((int)m.EffectType),
            Position = V3d(m.At),
            Rotation = Q(m.Rotation),
            AnchorId = m.AnchorEntity,
            AnchorWeaponSlot = m.AnchorMount,
            LifetimeSeconds = m.Lifetime,
            Size = m.Size,
        }));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>The DIS entity kind of a body-less unit (a commander): the gateway's <c>DrWire.UnitKind</c>, the host's own kind for an organisation.</summary>
    private const int UnitKind = 11;

    private static D.Domain DomainOf(DisTypeCode t) => t.Kind switch
    {
        3 when IsAnimal(t) => D.Domain.DomainAnimal,
        3 => D.Domain.DomainPerson,
        2 => D.Domain.DomainMunition,
        1 => t.Domain switch
        {
            1 => D.Domain.DomainGround,
            2 => D.Domain.DomainAir,
            3 => D.Domain.DomainSurface,
            4 => D.Domain.DomainSubsurface,
            _ => D.Domain.DomainOther,
        },
        _ => D.Domain.DomainOther,
    };

    /// <summary>A DIS life form in the animal categories 200 .. 209 (HROT's <c>Animals.IsAnimalDis</c>).</summary>
    private static bool IsAnimal(DisTypeCode t) => t.Kind == 3 && t.Category is >= 200 and <= 209;

    private static D.DisType ToDis(DisTypeCode t) =>
        new() { Kind = t.Kind, Domain = t.Domain, Country = t.Country, Category = t.Category, Subcategory = t.Subcategory, Specific = t.Specific, Extra = t.Extra };

    private static D.Stance StanceOf(HrotStance s) => s switch
    {
        HrotStance.Crouched => D.Stance.StanceCrouched,
        HrotStance.Prone => D.Stance.StanceProne,
        _ => D.Stance.StanceStanding,
    };

    private static float Unit(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;

    private static D.OpeningLook OpeningOf((byte Opening, float Progress) o, IReadOnlyList<byte>? crew, IReadOnlyList<HrotOpeningMotion>? motions)
    {
        var look = new D.OpeningLook { OpeningId = o.Opening, Progress = o.Progress, CrewExposed = crew?.Contains(o.Opening) ?? false, TargetProgress = o.Progress >= 0.5f ? 1f : 0f };
        if (motions is not null)
        {
            foreach (var motion in motions)
            {
                if (motion.Opening == o.Opening)
                {
                    look.TargetProgress = Unit(motion.Target);
                    look.ChangedAt = double.IsFinite(motion.ChangedAt) ? motion.ChangedAt : 0.0;
                    look.Seconds = float.IsFinite(motion.Seconds) ? Math.Max(0f, motion.Seconds) : 0f;
                }
            }
        }

        return look;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static bool Finite(HrotVec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    private static D.Vec3d V3d(HrotVec3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    private static D.Vec3f V3f(HrotVec3 v) => new() { X = (float)v.X, Y = (float)v.Y, Z = (float)v.Z };

    private static D.Quat Q(HrotQuat q) => new() { X = (float)q.X, Y = (float)q.Y, Z = (float)q.Z, W = (float)q.W };

    private static double Dist(HrotVec3 a, HrotVec3 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double Dist(D.Vec3d a, D.Vec3d b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double AngleDeg(HrotQuat a, HrotQuat b)
    {
        double dot = Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
        return 2.0 * Math.Acos(Math.Min(1.0, dot)) * (180.0 / Math.PI);
    }
}
