using SimNetGateway.Engine;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Showcase;

/// <summary>Which side a simulated vehicle belongs to (and therefore which protocol it speaks to the gateway).</summary>
public enum VehicleSide
{
    Ned,
    Rpr,
}

/// <summary>
/// A simulated sender on one side of the gateway: it keeps the true kinematic state of a vehicle and the state it last sent,
/// and sends an update only when its own constant-velocity prediction (FPW) of the last sent state is more than the
/// threshold away from the truth - dead-reckoned thinning on the sender side (R-005). An RPR vehicle also sends at least
/// every <see cref="HeartbeatSeconds"/> (the remote heartbeat expectation). Used by the demo and the benchmark.
/// </summary>
public sealed class SimulatedVehicle
{
    /// <summary>An RPR vehicle sends at least this often (simulated seconds).</summary>
    public const double HeartbeatSeconds = 5.0;

    private const double EarthRadius = 6_371_000.0;
    private const double RadToDeg = 180.0 / Math.PI;
    private static readonly TypeCatalog s_catalog = new();

    private readonly DeterministicRandom _random;
    private readonly bool _autoTurn;
    private double _nextTurnChange;
    private Vec3 _sentPosition;
    private Vec3 _sentVelocity;
    private double _sentTime;

    public SimulatedVehicle(VehicleSide side, int index, string name, int tkbType, NedForce force,
        double latitudeDeg, double longitudeDeg, double altitude, double headingDeg, double speed,
        bool isStatic, DeterministicRandom random, bool autoTurn = true)
    {
        Side = side;
        Index = index;
        Name = name;
        TkbType = tkbType;
        Force = force;
        Latitude = latitudeDeg;
        Longitude = longitudeDeg;
        Altitude = altitude;
        Heading = headingDeg;
        IsStatic = isStatic;
        Speed = isStatic ? 0.0 : speed;
        _random = random;
        _autoTurn = autoTurn && !isStatic;
        _nextTurnChange = 0;
        NedId = (ulong)index + 1UL;
        Handle = new RprObjectHandle(1_000_000u + (uint)index);
        EntityIdentifier = new DisEntityId(9, 9, (ushort)(index + 1));
    }

    public VehicleSide Side { get; }

    public int Index { get; }

    public string Name { get; }

    public int TkbType { get; }

    public NedForce Force { get; }

    public bool IsStatic { get; }

    /// <summary>The NED wire id of a NED vehicle.</summary>
    public ulong NedId { get; }

    /// <summary>The RTI handle of an RPR vehicle.</summary>
    public RprObjectHandle Handle { get; }

    /// <summary>The DIS entity identifier of an RPR vehicle.</summary>
    public DisEntityId EntityIdentifier { get; }

    public double Latitude { get; private set; }

    public double Longitude { get; private set; }

    public double Altitude { get; private set; }

    public double Heading { get; private set; }

    public double Speed { get; private set; }

    /// <summary>Heading change in degrees per second.</summary>
    public double TurnRate { get; set; }

    /// <summary>Messages this vehicle has sent to the gateway.</summary>
    public long MessagesSent { get; private set; }

    /// <summary>The ECEF position of the true state.</summary>
    public Vec3 TruePosition => NedRprConversions.GeodeticToEcef(Latitude, Longitude, Altitude);

    /// <summary>The position the vehicle's own prediction of its last sent state gives at <paramref name="t"/>.</summary>
    public Vec3 PredictedPosition(double t)
    {
        double dt = t - _sentTime;
        return new Vec3(_sentPosition.X + _sentVelocity.X * dt, _sentPosition.Y + _sentVelocity.Y * dt,
            _sentPosition.Z + _sentVelocity.Z * dt);
    }

    /// <summary>Sends the first message(s) of the vehicle (NED: master and geo-spatial; RPR: one full update); returns the count.</summary>
    public int SendInitial(NedRprHost host, double t)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (Side == VehicleSide.Ned)
        {
            var master = new NedEntityMaster(NedId, Name, TkbType, Force);
            host.NedIn.Enqueue(in master);
            MessagesSent++;
            SendNed(host, t);
            return 2;
        }

        SendRpr(host, t, full: true);
        return 1;
    }

    /// <summary>
    /// Advances the true state by <paramref name="dt"/> to time <paramref name="t"/>, then sends an update when the
    /// prediction error exceeds <paramref name="threshold"/> (or, for RPR, the heartbeat is due). Returns the number of
    /// messages sent (0 or 1).
    /// </summary>
    public int Update(NedRprHost host, double t, double dt, double threshold)
    {
        ArgumentNullException.ThrowIfNull(host);
        bool send = false;
        if (!IsStatic)
        {
            Advance(t, dt);
            var truth = TruePosition;
            var predicted = PredictedPosition(t);
            double dx = truth.X - predicted.X, dy = truth.Y - predicted.Y, dz = truth.Z - predicted.Z;
            send = Math.Sqrt(dx * dx + dy * dy + dz * dz) > threshold;
        }

        if (Side == VehicleSide.Rpr && t - _sentTime >= HeartbeatSeconds)
        {
            send = true;
        }

        if (!send)
        {
            return 0;
        }

        if (Side == VehicleSide.Ned)
        {
            SendNed(host, t);
        }
        else
        {
            SendRpr(host, t, full: false);
        }

        return 1;
    }

    /// <summary>The state as a NED geo-spatial message.</summary>
    public NedGeoSpatial ToGeoSpatial() => new(NedId, Latitude, Longitude, Altitude, Heading, 0.0, 0.0, Speed);

    private void Advance(double t, double dt)
    {
        if (_autoTurn && t >= _nextTurnChange)
        {
            TurnRate = _random.NextDouble(-3.0, 3.0);
            _nextTurnChange = t + _random.NextDouble(5.0, 15.0);
        }

        Heading = NedRprConversions.NormalizeHeadingDeg(Heading + TurnRate * dt);
        double headingRad = Heading / RadToDeg;
        double north = Speed * Math.Cos(headingRad) * dt;
        double east = Speed * Math.Sin(headingRad) * dt;
        Latitude += north / EarthRadius * RadToDeg;
        Longitude += east / (EarthRadius * Math.Cos(Latitude / RadToDeg)) * RadToDeg;
    }

    private void SendNed(NedRprHost host, double t)
    {
        var message = ToGeoSpatial();
        host.NedIn.Enqueue(in message);
        NedRprConversions.GeoToPivot(in message, out var position, out _, out var velocity);
        Remember(position, velocity, t);
    }

    private void SendRpr(NedRprHost host, double t, bool full)
    {
        NedRprConversions.GeoToPivot(Latitude, Longitude, Altitude, Heading, 0, 0, Speed, out var position, out var orientation,
            out var velocity);
        var update = new RprObjectUpdate(Handle)
        {
            WorldLocation = NedRprConversions.ToWorldLocation(position),
            Orientation = NedRprConversions.ToRprOrientation(orientation),
            VelocityVector = NedRprConversions.ToRprVector(velocity),
            DeadReckoningAlgorithm = (byte)(Speed > 0 ? 2 : 1),
        };
        if (full)
        {
            if (!s_catalog.TryToDis(TkbType, out var dis))
            {
                throw new InvalidOperationException($"TKB type {TkbType} has no DIS type.");
            }

            NedRprConversions.TryForceToRpr((int)Force, out var rprForce);
            update = update with
            {
                Marking = Name,
                EntityType = dis,
                ForceId = rprForce,
                EntityIdentifier = EntityIdentifier,
            };
        }

        host.SendRpr(update);
        Remember(position, velocity, t);
    }

    private void Remember(Vec3 position, Vec3 velocity, double t)
    {
        _sentPosition = position;
        _sentVelocity = velocity;
        _sentTime = t;
        MessagesSent++;
    }
}
