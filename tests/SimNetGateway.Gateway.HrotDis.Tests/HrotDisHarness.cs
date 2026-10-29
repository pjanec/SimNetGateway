using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using SimNetGateway.Protocol.Rpr;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>The state of a DIS entity as the (future) DIS ingress writes it into the pivot: ECEF values, DIS enumerations.</summary>
public sealed record DisState
{
    public long EntityType { get; init; } = new DisTypeCode(1, 1, 225, 1, 1, 3, 0).Pack();
    public string Marking { get; init; } = "DIS-1";
    public int Force { get; init; } = 2;
    public Vec3 Position { get; init; }
    public Vec3 Orientation { get; init; }
    public Vec3 Velocity { get; init; }
    public Vec3 Acceleration { get; init; }
    public Vec3 AngularVelocity { get; init; }
    public int DrAlgorithm { get; init; } = DrAlgorithms.Fpw;
    public double StateTime { get; init; } = 1.0;
    public int Damage { get; init; }
    public bool Deactivated { get; init; }
}

/// <summary>
/// The end-to-end harness of the HROT DIS gateway (H2a): a manual clock, the gateway built from a configuration with the HROT id
/// allocator port injected, in-memory channels (HROT inbound, recorders for HROT and DIS outbound) and the <see cref="InvariantChecker"/>
/// that runs after every step. The DIS side has no modules yet (H2b): the harness writes DIS-origin entities and events into
/// the pivot as the DIS source, exactly as the DIS ingress will.
/// </summary>
public sealed class HrotDisHarness
{
    public const double Dt = 1.0 / 60.0;

    private bool _stimulus;
    private double _stateTime = 1.0;

    public HrotDisHarness(GatewayConfiguration? config = null, Action<GatewayBuilder>? beforeBuild = null,
        long idFirst = 5_000_000, long idLast = 5_000_999, bool installChecker = true, DisNumbering? numbering = null)
    {
        Config = config ?? GatewayConfiguration.CreateDefault();
        Clock = new ManualClock();
        Ids = new HrotIdRangeAllocator(idFirst, idLast);
        var options = new GatewayOptions
        {
            JournalCapacity = 16384,
            DiagnosticsCapacity = 8192,
            EventLogCapacity = 4096,
        };
        var configuration = HrotDisConfigurations.For(Config, "test", Ids, numbering);
        Profile = (HrotDisProfile)configuration.Profile;
        Builder = configuration.CreateBuilder(Clock, options);
        Numbering = numbering ?? new DisNumbering();
        var hrot = Builder.RequireInbound("hrot");
        HrotIn = new InMemoryInbound(hrot.Source, "hrot");
        Builder.BindInbound("hrot", HrotIn);
        HrotOut = new InMemoryOutbound<IGatewayToHrotMessage>();
        Builder.BindOutbound<IGatewayToHrotMessage>("hrot", HrotOut);
        if (Config.DisRuns)
        {
            var dis = Builder.RequireInbound("dis");
            DisIn = new InMemoryInbound(dis.Source, "dis");
            Builder.BindInbound("dis", DisIn);
            DisOut = new InMemoryOutbound<Pdu>();
            Builder.BindOutbound<Pdu>("dis", DisOut);
        }

        if (Config.HlaRuns)
        {
            var rpr = Builder.RequireInbound("rpr");
            RprIn = new InMemoryInbound(rpr.Source, "rpr");
            Builder.BindInbound("rpr", RprIn);
            RprOut = new InMemoryOutbound<IRprMessage>();
            Builder.BindOutbound<IRprMessage>("rpr", RprOut);
        }

        beforeBuild?.Invoke(Builder);
        Binding = Builder.Build();
        Engine = Binding.Engine;
        HrotOut.Context = Engine;
        Schema = new PivotSchema(Engine.Attributes, Engine.EventTypes);
        HrotSource = hrot.Source;
        DisSource = Engine.Sources.Register("dis");
        HrotIds = (IdentityMap<long>)Engine.Identities.Find("hrot.entity")!;
        if (Config.HlaRuns)
        {
            RprOut!.Context = Engine;
            RprSource = Engine.Sources.Register("rpr");
            RprHandles = (IdentityMap<RprObjectHandle>)Engine.Identities.Find("rpr.handle")!;
        }

        DisIds = (IdentityMap<DisEntityKey>?)Engine.Identities.Find("dis.entity");

        Checker = new InvariantChecker(this);
        if (installChecker)
        {
            HrotOut.Tap = Checker.OnSent;
        }

        if (Config.HlaRuns && installChecker)
        {
            RprChecker = new RprInvariantChecker(this);
            RprOut!.Tap = RprChecker.OnSent;
        }

        if (Config.DisRuns)
        {
            DisOut!.Context = Engine;
            DisChecker = new DisInvariantChecker(this);
            if (installChecker)
            {
                DisOut.Tap = DisChecker.OnSent;
            }
        }
    }

    /// <summary>The profile that configured the gateway under test.</summary>
    public HrotDisProfile Profile { get; }

    /// <summary>The services of the gateway under test (what the DIS ingress ignored, ...).</summary>
    public HrotDisServices Services => Profile.Services!;

    /// <summary>The numbering of the DIS entity numbers the gateway was built with.</summary>
    public DisNumbering Numbering { get; }

    /// <summary>The "dis.entity" identity map: the one entity number space of DIS and HLA (null when neither side runs).</summary>
    public IdentityMap<DisEntityKey>? DisIds { get; }

    /// <summary>The invariants of the DIS half (null when the DIS side does not run); checked after every step.</summary>
    public DisInvariantChecker? DisChecker { get; }

    /// <summary>When false the harness does not check the DIS invariants after a step (a test that provokes a violation on purpose).</summary>
    public bool CheckDis { get; set; } = true;

    public GatewayConfiguration Config { get; }

    public ManualClock Clock { get; }

    public HrotIdRangeAllocator Ids { get; }

    public GatewayBuilder Builder { get; }

    public GatewayHostBinding Binding { get; }

    public GatewayEngine Engine { get; }

    public PivotSchema Schema { get; }

    public SourceId HrotSource { get; }

    public SourceId DisSource { get; }

    /// <summary>The source of the HLA side (<see cref="SourceId.None"/> when the HLA side does not run).</summary>
    public SourceId RprSource { get; }

    /// <summary>True for the origin of a replica of a peer simulation (dis or rpr).</summary>
    public bool IsReplica(SourceId origin) => origin == DisSource || (RprSource != SourceId.None && origin == RprSource);

    public InMemoryInbound? RprIn { get; }

    public InMemoryOutbound<IRprMessage>? RprOut { get; }

    /// <summary>"rpr.handle" (null when the HLA side does not run).</summary>
    public IdentityMap<RprObjectHandle>? RprHandles { get; }

    /// <summary>The invariants of the HLA half (null when the HLA side does not run or the checker is not installed); checked after every step.</summary>
    public RprInvariantChecker? RprChecker { get; }

    /// <summary>When false the harness does not check the RPR invariants after a step.</summary>
    public bool CheckRpr { get; set; } = true;

    /// <summary>
    /// The fake RTI of the in-memory tests: when true (default), every registration the gateway sent is answered with a fresh handle at the
    /// start of the next tick (asynchronous, like the transport's pump), unless <see cref="DeliverRti"/> is false.
    /// </summary>
    public bool AutoRegister { get; set; } = true;

    /// <summary>When false no registration is answered (the handles stay pending).</summary>
    public bool DeliverRti { get; set; } = true;

    /// <summary>The handles this RTI gave to the gateway's registrations, by token.</summary>
    public Dictionary<long, RprObjectHandle> Answered { get; } = new();

    /// <summary>The handle value the next answered registration gets.</summary>
    public uint NextHandle { get; set; } = 0x1000;

    private int _rtiCursor;

    public InMemoryInbound HrotIn { get; }

    public InMemoryInbound? DisIn { get; }

    public InMemoryOutbound<IGatewayToHrotMessage> HrotOut { get; }

    public InMemoryOutbound<Pdu>? DisOut { get; }

    public IdentityMap<long> HrotIds { get; }

    public InvariantChecker Checker { get; }

    // ---- stepping ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// One tick: advance the clock, tick the engine, check the invariants. A tick with no new input sends nothing to HROT, except
    /// the removal of a DIS entity by the stale reaper (a timer, not an input).
    /// </summary>
    public void Step()
    {
        int before = HrotOut.Messages.Count(m => m.Message is not ReplicaRemoved);
        bool quiet = !_stimulus;
        _stimulus = false;
        Clock.Advance(Dt);
        PumpRti();
        Engine.Tick();
        if (quiet)
        {
            int sent = HrotOut.Messages.Count(m => m.Message is not ReplicaRemoved) - before;
            Assert.True(sent == 0, $"a tick without new input sent {sent} message(s) to HROT");
        }

        Checker.Check();
        if (CheckRpr)
        {
            RprChecker?.Check();
        }

        if (CheckDis)
        {
            DisChecker?.Check();
        }
    }

    /// <summary>Answers the registrations the gateway sent since the last pump (the fake RTI), asynchronously: they reach the engine in this tick.</summary>
    public void PumpRti()
    {
        if (RprOut is null || !AutoRegister)
        {
            return;
        }

        var messages = RprOut.Messages;
        for (; _rtiCursor < messages.Count; _rtiCursor++)
        {
            if (!DeliverRti)
            {
                return;
            }

            if (messages[_rtiCursor].Message is RprRegisterObject register)
            {
                var handle = new RprObjectHandle(NextHandle++);
                Answered[register.Token] = handle;
                RprIn!.Enqueue(new RprObjectRegistered(register.Token, handle));
            }
        }
    }

    /// <summary>Queues a message of the HLA side (a federate's object update, interaction, ...).</summary>
    public void Rpr<T>(T message, double? receivedAt = null)
        where T : IRprMessage
    {
        _stimulus = true;
        RprIn!.Enqueue(in message, receivedAt);
    }

    public void RprStep<T>(T message)
        where T : IRprMessage
    {
        Rpr(message);
        Step();
    }

    public void Steps(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Step();
        }
    }

    public void Run(double seconds) => Steps((int)Math.Round(seconds / Dt));

    // ---- HROT side ---------------------------------------------------------------------------------------------------

    public void Hrot<T>(T message)
        where T : IHrotToGatewayMessage
    {
        _stimulus = true;
        HrotIn.Enqueue(in message);
    }

    public void HrotStep<T>(T message)
        where T : IHrotToGatewayMessage
    {
        Hrot(message);
        Step();
    }

    // ---- DIS side as PDUs (the DIS modules of H2b) -------------------------------------------------------------------

    /// <summary>Queues a PDU on the DIS inbound channel (the type parameter must be the PDU's concrete class: that is what the ingress routes on).</summary>
    public void Dis<T>(T pdu, double? receivedAt = null)
        where T : Pdu
    {
        _stimulus = true;
        DisIn!.Enqueue(in pdu, receivedAt);
    }

    public void DisStep<T>(T pdu, double? receivedAt = null)
        where T : Pdu
    {
        Dis(pdu, receivedAt);
        Step();
    }

    /// <summary>The DIS entity key of a pivot entity (null when it has none).</summary>
    public DisEntityKey? DisKeyOf(EntityId id) => DisIds!.TryGetKey(id, out var key) ? key : null;

    public void Origin(double lat, double lon, double alt, int generation = 1)
    {
        SendOrigin(lat, lon, alt, generation);
        Step();
    }

    /// <summary>Queues a valid origin (and updates the tests' own oracle frame) without ticking.</summary>
    public void SendOrigin(double lat, double lon, double alt, int generation = 1)
    {
        Frame = new LocalTangentFrame(Geodetic.FromDegrees(lat, lon, alt));
        Hrot(new HrotOrigin(lat, lon, alt, generation));
    }

    public void Session(ClusterPhase phase, bool advancing = true) =>
        HrotStep(new HrotSession(phase, advancing, 1.0, 0.0));

    /// <summary>Origin (50 N, 14 E, 300 m) and a Live, advancing session: replicas and effects are open.</summary>
    public void GoLive(double lat = 50.0, double lon = 14.0, double alt = 300.0)
    {
        Origin(lat, lon, alt);
        Session(ClusterPhase.Live);
    }

    public static HrotEntityState State(long id, HrotVec3 position = default, long tkb = 100, string marking = "T1", int force = 1,
        double health = 100, double healthMax = 100, double simTime = 1.0, HrotQuat? rotation = null, HrotVec3 velocity = default,
        HrotVec3 angularVelocity = default, long commander = 0, long disType = 0) =>
        new(id, tkb, disType, marking, force, commander, position, rotation ?? HrotQuat.Identity, velocity, angularVelocity, health, healthMax, simTime);

    // ---- DIS side (writes into the pivot as the DIS source) -------------------------------------------------------------

    /// <summary>
    /// The independent oracle of the tests: the tangent frame of the last origin built straight from Common (not through the
    /// gateway's <see cref="GeoFrame"/>), so that a defect in the gateway's use of the frame is not mirrored in the expectation.
    /// </summary>
    public LocalTangentFrame? Frame { get; private set; }

    /// <summary>A DIS state at a point of the origin's ENU frame (so the replica is where the test expects it).</summary>
    public DisState DisStateAt(HrotVec3 enu, HrotVec3 enuVelocity = default, int damage = 0, double? stateTime = null)
    {
        var frame = Frame ?? throw new InvalidOperationException("No origin was sent yet.");
        return new DisState
        {
            Position = frame.PointToEcef(new Vec3(enu.X, enu.Y, enu.Z)),
            Velocity = frame.VectorToEcef(new Vec3(enuVelocity.X, enuVelocity.Y, enuVelocity.Z)),
            Orientation = LocalToGeocentricOrientation.ToDisEuler(Quaternion.Identity, frame),
            Damage = damage,
            StateTime = stateTime ?? NextStateTime(),
        };
    }

    public double NextStateTime() => _stateTime += 0.05;

    /// <summary>Creates a DIS-origin entity with the given state in one change set (a "new DIS state").</summary>
    public EntityId DisCreate(DisState state)
    {
        var changes = Engine.BeginChange(DisSource);
        var id = changes.CreateEntity();
        Write(changes, id, state);
        changes.Commit();
        return id;
    }

    /// <summary>Writes a new DIS state of an existing entity.</summary>
    public void DisUpdate(EntityId id, DisState state)
    {
        var changes = Engine.BeginChange(DisSource);
        Write(changes, id, state);
        changes.Commit();
    }

    public void DisRemove(EntityId id)
    {
        var changes = Engine.BeginChange(DisSource);
        changes.DestroyEntity(id);
        changes.Commit();
    }

    private void Write(ChangeSet changes, EntityId id, DisState s)
    {
        var sc = Schema;
        changes.Set(id, sc.EntityType, AttrValue.From(s.EntityType));
        changes.Set(id, sc.Marking, AttrValue.From(s.Marking));
        changes.Set(id, sc.Force, AttrValue.From((long)s.Force));
        changes.Set(id, sc.Position, AttrValue.From(s.Position));
        changes.Set(id, sc.Orientation, AttrValue.From(s.Orientation));
        changes.Set(id, sc.Velocity, AttrValue.From(s.Velocity));
        changes.Set(id, sc.Acceleration, AttrValue.From(s.Acceleration));
        changes.Set(id, sc.AngularVelocity, AttrValue.From(s.AngularVelocity));
        changes.Set(id, sc.DrAlgorithm, AttrValue.From((long)s.DrAlgorithm));
        changes.Set(id, sc.StateTime, AttrValue.From(s.StateTime));
        changes.Set(id, sc.Damage, AttrValue.From((long)s.Damage));
        changes.Set(id, sc.Deactivated, AttrValue.From(s.Deactivated));
    }

    /// <summary>Publishes a DIS-side event (a Fire or Detonation PDU after the DIS ingress) as the DIS source.</summary>
    public void DisEvent(EventTypeId type, Action<AttributeBag> fill)
    {
        var bag = Engine.RentPayload();
        fill(bag);
        Engine.Publish(DisSource, type, bag);
    }

    // ---- inspection ---------------------------------------------------------------------------------------------------

    public List<T> Sent<T>()
        where T : IGatewayToHrotMessage => HrotOut.OfType<T>().ToList();

    public EntityRecord? EntityOfHrot(long id) => HrotIds.TryResolve(id, out var e) ? Engine.State.Find(e) : null;

    public IEnumerable<EntityRecord> Entities(SourceId origin)
    {
        foreach (var entity in Engine.State.Entities)
        {
            if (entity.Origin == origin)
            {
                yield return entity;
            }
        }
    }

    public int WarningCount(string moduleName)
    {
        int n = 0;
        foreach (var entry in Engine.Diagnostics.Recent)
        {
            if (entry.Severity == DiagnosticSeverity.Warning && entry.Origin == moduleName)
            {
                n++;
            }
        }

        return n;
    }
}
