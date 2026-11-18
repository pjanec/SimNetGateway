using System.Numerics;
using Fdp.Core;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The only per-frame writer of replica poses: extrapolates every replica's dead-reckoning snapshot to the gateway clock
/// with the Common predictor registry and writes <c>SimTransform</c> / <c>SimVelocity</c>. The caller invokes it only while the HROT clock advances
/// and nothing is replayed. Nothing is posed before a valid origin.
/// </summary>
public sealed class ReplicaPoser
{
    private readonly PredictorRegistry _predictors = PredictorRegistry.CreateDis();
    private readonly GeoFrame _geo;

    public ReplicaPoser(bool allowZeroOrigin) => _geo = new GeoFrame(allowZeroOrigin);

    /// <summary>The origin generation the poser last applied (diagnostics).</summary>
    public int OriginGeneration => _geo.Generation;

    /// <summary>Poses every replica of <paramref name="replicas"/>; returns how many were written.</summary>
    public int Pose(EntityRepository world, IEnumerable<DisReplica> replicas, HrotOrigin? origin, double now)
    {
        if (origin is { } o)
        {
            _geo.TryApply(in o, out _);
        }

        if (!_geo.IsValid)
        {
            return 0;
        }

        int written = 0;
        foreach (var replica in replicas)
        {
            if (!world.IsAlive(replica.Entity) || !TryPredict(replica.State, now, out var position, out var velocity, out var rotation))
            {
                continue;
            }

            world.SetComponent(replica.Entity, new SimTransform
            {
                Position = new WorldPoint((float)position.X, (float)position.Y, (float)position.Z),
                Rotation = new WorldRotation((float)rotation.X, (float)rotation.Y, (float)rotation.Z, (float)rotation.W),
            });
            if (world.HasComponent<SimVelocity>(replica.Entity))
            {
                var omega = replica.State.AngularVelocity;
                world.SetComponent(replica.Entity, new SimVelocity
                {
                    Linear = new Vector3((float)velocity.X, (float)velocity.Y, (float)velocity.Z),
                    Angular = new Vector3((float)omega.X, (float)omega.Y, (float)omega.Z),
                });
            }

            written++;
        }

        return written;
    }

    /// <summary>The state of a snapshot at the gateway clock <paramref name="now"/>, in local ENU (the pure part, tested without a world).</summary>
    public bool TryPredict(in HrotDrSnapshot s, double now, out HrotVec3 position, out HrotVec3 velocity, out HrotQuat rotation)
    {
        position = s.Position;
        velocity = s.Velocity;
        rotation = s.Rotation;
        if (!_geo.IsValid || !_predictors.TryGet(s.DrAlgorithm, out var predictor))
        {
            return false;
        }

        bool body = s.DrAlgorithm is >= (int)DisDeadReckoning.Fpb and <= (int)DisDeadReckoning.Fvb;
        var v = body ? GeoFrame.ToVec(s.Velocity) : _geo.VectorToEcef(s.Position, s.Velocity);
        var a = body ? GeoFrame.ToVec(s.Acceleration) : _geo.VectorToEcef(s.Position, s.Acceleration);
        var basis = new PredictionBasis(_geo.PointToEcef(s.Position), v, a, _geo.ToEuler(s.Position, s.Rotation),
            _geo.AngularVelocityToBody(s.Position, s.Rotation, s.AngularVelocity), s.StateTime);
        var predicted = predictor.Predict(in basis, now);
        position = _geo.PointFromEcef(predicted.Position);
        velocity = _geo.VectorFromEcef(position, predicted.Velocity);
        rotation = _geo.FromEuler(position, predicted.Orientation);
        return true;
    }
}
