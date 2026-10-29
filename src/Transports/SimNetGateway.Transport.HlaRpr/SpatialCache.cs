using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Transport.HlaRpr;

/// <summary>
/// The spatial state of one published object, merged from the partial updates the gateway sends (DD-008 section 3.4): the HLA
/// attribute <c>Spatial</c> is ONE variant record, so any spatial field that changes re-encodes the whole variant from this cache -
/// a field that is not part of the update is never zeroed or stale. The dead reckoning algorithm of the variant is the
/// gateway's one when it has an alternative in the FOM (DIS 1 .. 9); otherwise (absent, or Other) it is Static while no velocity is
/// known and FPW afterwards, so <c>Other</c> is never sent.
/// </summary>
internal sealed class SpatialCache
{
    private WorldLocation? _location;
    private RprOrientation? _orientation;
    private RprVector? _velocity;
    private RprVector? _acceleration;
    private RprVector? _angularVelocity;
    private byte? _algorithm;

    /// <summary>Merges the spatial fields an update carries; true when it carries any (the attribute has to be sent).</summary>
    public bool Merge(RprObjectUpdate update)
    {
        bool any = false;
        if (update.WorldLocation is { } location) { _location = location; any = true; }
        if (update.Orientation is { } orientation) { _orientation = orientation; any = true; }
        if (update.VelocityVector is { } velocity) { _velocity = velocity; any = true; }
        if (update.AccelerationVector is { } acceleration) { _acceleration = acceleration; any = true; }
        if (update.AngularVelocity is { } angular) { _angularVelocity = angular; any = true; }
        if (update.DeadReckoningAlgorithm is { } algorithm) { _algorithm = algorithm; any = true; }
        return any;
    }

    /// <summary>
    /// The full variant; false while the position is unknown (a variant needs one). Unknown orientation or vectors are zero (the
    /// gateway always sends a full update first, so this only matters for a hostile sequence).
    /// </summary>
    public bool TryBuild(out SpatialVariantStruct spatial)
    {
        spatial = default;
        if (_location is not { } location)
        {
            return false;
        }

        var algorithm = ChooseAlgorithm();
        var orientation = _orientation ?? default;
        var velocity = _velocity ?? default;
        var acceleration = _acceleration ?? default;
        var angular = _angularVelocity ?? default;
        var probe = new SpatialVariantStruct(algorithm, default, false, default, default, default, default);
        // Fields the algorithm does not carry are zero, as they are after a decode (so the cache equals what a peer decodes).
        spatial = new SpatialVariantStruct(
            algorithm,
            new WorldLocationStruct(location.X, location.Y, location.Z),
            false,
            new OrientationStruct((float)orientation.Psi, (float)orientation.Theta, (float)orientation.Phi),
            probe.HasVelocity ? new VelocityVectorStruct((float)velocity.X, (float)velocity.Y, (float)velocity.Z) : default,
            probe.HasAcceleration ? new AccelerationVectorStruct((float)acceleration.X, (float)acceleration.Y, (float)acceleration.Z) : default,
            probe.HasAngularVelocity ? new AngularVelocityVectorStruct((float)angular.X, (float)angular.Y, (float)angular.Z) : default);
        return true;
    }

    private DeadReckoningAlgorithmEnum8 ChooseAlgorithm()
    {
        if (_algorithm is >= 1 and <= 9)
        {
            return (DeadReckoningAlgorithmEnum8)_algorithm.Value;
        }

        return _velocity is null ? DeadReckoningAlgorithmEnum8.Static : DeadReckoningAlgorithmEnum8.DRM_FPW;
    }
}
