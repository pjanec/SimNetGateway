using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>Builders of the messages the HLA side (another federate, through the transport) sends to the gateway, and the configurations of the HLA tests.</summary>
public static class RprMessages
{
    /// <summary>Handle values of the federation's own objects; the fake RTI of the harness gives the gateway's objects 0x1000 and up.</summary>
    public const uint PeerBase = 0x9000;

    public static readonly DisEntityType Tank = new(1, 1, 225, 1, 1, 3, 0);
    public static readonly DisEntityType MunitionKind = new(2, 1, 225, 1, 1, 0, 0);

    public static RprObjectHandle Peer(uint n) => new(PeerBase + n);

    /// <summary>Both sides with their defaults.</summary>
    public static GatewayConfiguration Both(Func<HlaConfiguration, HlaConfiguration>? hla = null, Func<DisConfiguration, DisConfiguration>? dis = null,
        SharedConfiguration? shared = null) =>
        GatewayConfiguration.CreateDefault() with
        {
            Shared = shared ?? new SharedConfiguration(),
            Dis = (dis ?? (d => d))(new DisConfiguration { ExerciseId = 1 }),
            Hla = (hla ?? (x => x))(new HlaConfiguration { Federation = "F" }),
        };

    /// <summary>Only the HLA side.</summary>
    public static GatewayConfiguration HlaOnly(Func<HlaConfiguration, HlaConfiguration>? hla = null, SharedConfiguration? shared = null) =>
        Both(hla, shared: shared) with { Dis = null };

    public static HrotDisHarness Live(GatewayConfiguration? config = null, bool checker = true)
    {
        var h = new HrotDisHarness(config ?? Both(), installChecker: checker);
        h.GoLive();
        return h;
    }

    /// <summary>A federate's full object update at a point of the origin's ENU frame.</summary>
    public static RprObjectUpdate Update(HrotDisHarness h, RprObjectHandle handle, Vec3 enu, string marking = "PEER", DisEntityType? type = null,
        RprForceId force = RprForceId.Opposing, RprDamageState damage = RprDamageState.NoDamage, Vec3 enuVelocity = default, byte algorithm = 2,
        Vec3 euler = default) =>
        Update(h.Frame ?? throw new InvalidOperationException("No origin was sent yet."), handle, enu, marking, type, force, damage, enuVelocity, algorithm, euler);

    /// <summary>A federate's full object update at a point of the ENU frame of <paramref name="frame"/>.</summary>
    public static RprObjectUpdate Update(LocalTangentFrame frame, RprObjectHandle handle, Vec3 enu, string marking = "PEER", DisEntityType? type = null,
        RprForceId force = RprForceId.Opposing, RprDamageState damage = RprDamageState.NoDamage, Vec3 enuVelocity = default, byte algorithm = 2,
        Vec3 euler = default)
    {
        return new RprObjectUpdate(handle)
        {
            Marking = marking,
            EntityType = type ?? Tank,
            ForceId = force,
            WorldLocation = RprConversions.ToLocation(frame.PointToEcef(enu)),
            Orientation = RprConversions.ToOrientation(euler),
            VelocityVector = RprConversions.ToVector(frame.VectorToEcef(enuVelocity)),
            AccelerationVector = default(RprVector),
            AngularVelocity = default(RprVector),
            DeadReckoningAlgorithm = algorithm,
            DamageState = damage,
        };
    }

    /// <summary>The handle the fake RTI of the harness gave to the registration of a hrot entity (null while pending).</summary>
    public static RprObjectHandle? HandleOf(HrotDisHarness h, long hrotId) =>
        h.HrotIds.TryResolve(hrotId, out var e) && h.RprHandles!.TryGetKey(e, out var handle) ? handle : null;

    public static void Settle(HrotDisHarness h, int ticks = 3) => h.Steps(ticks);
}
