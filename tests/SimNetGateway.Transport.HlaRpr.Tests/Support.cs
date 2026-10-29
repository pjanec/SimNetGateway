using SimNetGateway.Showcase;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Transport.FakeRti;

namespace SimNetGateway.Transport.HlaRpr.Tests;

/// <summary>An engine sink that keeps what the transport hands over, in order.</summary>
public sealed class CollectingSink : IIngressSink
{
    public List<IRprMessage> Messages { get; } = new();

    public void Deliver<T>(in T message, double? receivedAt = null) => Messages.Add((IRprMessage)(object)message!);

    public IEnumerable<T> Of<T>() where T : IRprMessage => Messages.OfType<T>();
}

/// <summary>
/// A started transport on a fake RTI with one scripted peer. <see cref="Round"/> is one host cycle: the transport's pump, the
/// engine's drain, then the peer's evoke.
/// </summary>
public sealed class Rig
{
    public const string Federation = "RPR-Test";

    private long _guidCounter;

    public Rig(RprHlaSettings? settings = null, bool startGateway = true)
    {
        Clock = new ManualClock();
        Hub = new FakeRtiHub();
        Settings = settings ?? new RprHlaSettings { FederationName = Federation, FederateName = "Gateway" };
        Gateway = new RprHlaConnection(Hub.CreateAmbassador(), Settings, Clock, new SourceId(7), "rpr", NextGuid);
        Peer = new ScriptedRprFederate(Hub, Settings.FederationName, "Peer");
        if (startGateway)
        {
            Gateway.Start();
        }
    }

    public ManualClock Clock { get; }

    public FakeRtiHub Hub { get; }

    public RprHlaSettings Settings { get; }

    public RprHlaConnection Gateway { get; }

    public ScriptedRprFederate Peer { get; }

    /// <summary>Everything drained from the transport so far.</summary>
    public CollectingSink Inbox { get; } = new();

    private Guid NextGuid() => new(0, 0, 0, BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(++_guidCounter)));

    public void Send(IRprMessage message) => Gateway.Send(in message);

    /// <summary>The transport's pump, then the engine's drain of everything it produced, then the peer's evoke.</summary>
    public void Round()
    {
        Gateway.Pump();
        Gateway.Drain(Inbox, int.MaxValue);
        Peer.Evoke();
    }

    /// <summary>Rounds until nothing is left to deliver (the request / answer exchanges of discovery take a few).</summary>
    public void Settle(int rounds = 4)
    {
        for (int i = 0; i < rounds; i++)
        {
            Round();
        }
    }

    /// <summary>Registers one entity through the transport and returns its handle; the peer has discovered it afterwards.</summary>
    public RprObjectHandle Register(DisEntityType type, long token = 1)
    {
        int before = Inbox.Messages.Count;
        Send(new RprRegisterObject(token, type));
        Settle();
        var answer = Inbox.Messages.Skip(before).OfType<RprObjectRegistered>().Single(r => r.Token == token);
        return answer.Handle;
    }
}

/// <summary>Seeded random values of the gateway's message types. Floats on the wire are exactly representable as the doubles used here.</summary>
public static class Gen
{
    public static readonly DisEntityType Tank = new(1, 1, 225, 1, 1, 3, 0);
    public static readonly DisEntityType Uav = new(1, 2, 225, 50, 1, 0, 0);

    public static double Coord(Random r) => Math.Round((r.NextDouble() - 0.5) * 2e7, 3);

    /// <summary>A double that survives a float32 round trip.</summary>
    public static double F(Random r) => (double)(float)((r.NextDouble() - 0.5) * 2000);

    public static RprVector Vec(Random r) => new(F(r), F(r), F(r));

    public static WorldLocation Loc(Random r) => new(Coord(r), Coord(r), Coord(r));

    public static RprOrientation Ori(Random r) => new((double)(float)(F(r) / 1000), (double)(float)(F(r) / 1000), (double)(float)(F(r) / 1000));

    public static DisEntityType Type(Random r) =>
        new((byte)r.Next(0, 6), (byte)r.Next(0, 7), (ushort)r.Next(0, 300), (byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256));

    public static DisEntityId Id(Random r) => new((ushort)r.Next(1, 65535), (ushort)r.Next(1, 65535), (ushort)r.Next(1, 65535));

    public static string Marking(Random r)
    {
        int length = r.Next(0, 12);
        return new string(Enumerable.Range(0, length).Select(_ => (char)r.Next('A', 'Z' + 1)).ToArray());
    }

    /// <summary>The DIS algorithms with a FOM alternative (1 .. 9), Other, absent and an invalid one.</summary>
    public static byte? Algorithm(Random r) => r.Next(13) switch
    {
        0 => null,
        1 => 0,
        2 => 200,
        var n => (byte)((n % 9) + 1),
    };

    /// <summary>An update with a random subset of fields (at least one).</summary>
    public static RprObjectUpdate PartialUpdate(Random r, RprObjectHandle handle)
    {
        RprObjectUpdate u;
        do
        {
            u = new RprObjectUpdate(handle)
            {
                Marking = r.Next(4) == 0 ? Marking(r) : null,
                EntityType = r.Next(5) == 0 ? Type(r) : null,
                ForceId = r.Next(5) == 0 ? (RprForceId)r.Next(4) : null,
                EntityIdentifier = r.Next(6) == 0 ? Id(r) : null,
                DamageState = r.Next(5) == 0 ? (RprDamageState)r.Next(4) : null,
                WorldLocation = r.Next(3) == 0 ? Loc(r) : null,
                Orientation = r.Next(3) == 0 ? Ori(r) : null,
                VelocityVector = r.Next(3) == 0 ? Vec(r) : null,
                AccelerationVector = r.Next(4) == 0 ? Vec(r) : null,
                AngularVelocity = r.Next(4) == 0 ? Vec(r) : null,
                DeadReckoningAlgorithm = r.Next(4) == 0 ? Algorithm(r) : null,
            };
        }
        while (!HasAny(u));
        return u;
    }

    public static bool HasAny(RprObjectUpdate u) =>
        u.Marking is not null || u.EntityType is not null || u.ForceId is not null || u.EntityIdentifier is not null ||
        u.DamageState is not null || HasSpatial(u);

    public static bool HasSpatial(RprObjectUpdate u) =>
        u.WorldLocation is not null || u.Orientation is not null || u.VelocityVector is not null ||
        u.AccelerationVector is not null || u.AngularVelocity is not null || u.DeadReckoningAlgorithm is not null;

    /// <summary>A complete update (every field), as the gateway sends after a registration.</summary>
    public static RprObjectUpdate FullUpdate(Random r, RprObjectHandle handle, DisEntityType type) => new(handle)
    {
        Marking = Marking(r),
        EntityType = type,
        ForceId = (RprForceId)r.Next(4),
        EntityIdentifier = Id(r),
        DamageState = (RprDamageState)r.Next(4),
        WorldLocation = Loc(r),
        Orientation = Ori(r),
        VelocityVector = Vec(r),
        AccelerationVector = r.Next(2) == 0 ? Vec(r) : null,
        AngularVelocity = r.Next(2) == 0 ? Vec(r) : null,
        DeadReckoningAlgorithm = Algorithm(r),
    };

    public static EntityTypeStruct ToStruct(DisEntityType t) =>
        new(t.Kind, t.Domain, t.Country, t.Category, t.Subcategory, t.Specific, t.Extra);

    public static DisEntityType ToDis(EntityTypeStruct t) =>
        new(t.EntityKind, t.Domain, t.CountryCode, t.Category, t.Subcategory, t.Specific, t.Extra);

    public static WorldLocationStruct ToStruct(WorldLocation l) => new(l.X, l.Y, l.Z);
}

/// <summary>
/// The reference model of the merged spatial state (success condition a): an independent re-statement of the rule of DD-008
/// section 3.4 - fold every update's spatial fields, then the variant is Static without a velocity and FPW with one when the
/// gateway gave no usable algorithm (DIS 1 .. 9), and only the fields the algorithm carries are present.
/// </summary>
public sealed class SpatialModel
{
    private WorldLocation? _l;
    private RprOrientation? _o;
    private RprVector? _v, _a, _w;
    private byte? _alg;

    public void Merge(RprObjectUpdate u)
    {
        _l = u.WorldLocation ?? _l;
        _o = u.Orientation ?? _o;
        _v = u.VelocityVector ?? _v;
        _a = u.AccelerationVector ?? _a;
        _w = u.AngularVelocity ?? _w;
        _alg = u.DeadReckoningAlgorithm ?? _alg;
    }

    public SpatialVariantStruct? Expected()
    {
        if (_l is not { } l)
        {
            return null;
        }

        var algorithm = _alg is >= 1 and <= 9
            ? (DeadReckoningAlgorithmEnum8)_alg.Value
            : _v is null ? DeadReckoningAlgorithmEnum8.Static : DeadReckoningAlgorithmEnum8.DRM_FPW;
        var probe = new SpatialVariantStruct(algorithm, default, false, default, default, default, default);
        var o = _o ?? default;
        var v = _v ?? default;
        var a = _a ?? default;
        var w = _w ?? default;
        return new SpatialVariantStruct(
            algorithm,
            new WorldLocationStruct(l.X, l.Y, l.Z),
            false,
            new OrientationStruct((float)o.Psi, (float)o.Theta, (float)o.Phi),
            probe.HasVelocity ? new VelocityVectorStruct((float)v.X, (float)v.Y, (float)v.Z) : default,
            probe.HasAcceleration ? new AccelerationVectorStruct((float)a.X, (float)a.Y, (float)a.Z) : default,
            probe.HasAngularVelocity ? new AngularVelocityVectorStruct((float)w.X, (float)w.Y, (float)w.Z) : default);
    }
}
