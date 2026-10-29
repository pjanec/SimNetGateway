using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Showcase;

/// <summary>
/// The comparison of what the gateway SENT to the RPR side (the messages of its outbound contract, as the recorder holds them)
/// with what the scripted federate RECEIVED through the RTI and decoded with <see cref="RprFom2"/> on its own (DD-008 section 12b).
/// The two meet in the invariant G-9 (<see cref="InvariantChecker"/>, every tick) and in the success conditions of the showcase that
/// are about what the RPR side sees. A difference is a defect of the transport or of the encoding, never a rounding: the FOM
/// stores positions as float64 and orientations / velocities as float32, so the comparison is exact against the same cast.
/// </summary>
public static class RtiMirror
{
    /// <summary>The state of an object as the sequence of its partial updates built it: every field of <paramref name="update"/> overrides the state's.</summary>
    public static RprObjectUpdate Merge(RprObjectUpdate? state, RprObjectUpdate update)
    {
        if (state is null)
        {
            return update;
        }

        return state with
        {
            Marking = update.Marking ?? state.Marking,
            EntityType = update.EntityType ?? state.EntityType,
            ForceId = update.ForceId ?? state.ForceId,
            EntityIdentifier = update.EntityIdentifier ?? state.EntityIdentifier,
            WorldLocation = update.WorldLocation ?? state.WorldLocation,
            Orientation = update.Orientation ?? state.Orientation,
            VelocityVector = update.VelocityVector ?? state.VelocityVector,
            AccelerationVector = update.AccelerationVector ?? state.AccelerationVector,
            AngularVelocity = update.AngularVelocity ?? state.AngularVelocity,
            DeadReckoningAlgorithm = update.DeadReckoningAlgorithm ?? state.DeadReckoningAlgorithm,
            DamageState = update.DamageState ?? state.DamageState,
            DamagePercent = update.DamagePercent ?? state.DamagePercent,
        };
    }

    /// <summary>Does the update carry anything an HLA attribute can hold (everything but the damage percentage)?</summary>
    public static bool CarriesAttributes(RprObjectUpdate u) =>
        u.Marking is not null || u.EntityType is not null || u.ForceId is not null || u.EntityIdentifier is not null ||
        u.DamageState is not null || u.WorldLocation is not null || u.Orientation is not null || u.VelocityVector is not null ||
        u.AccelerationVector is not null || u.AngularVelocity is not null || u.DeadReckoningAlgorithm is not null;

    /// <summary>
    /// What differs between the object's state as sent (<paramref name="sent"/>, merged) and what the federate decoded of it; empty
    /// when everything the gateway ever said about the object is what the federate holds. The damage percentage has no attribute
    /// and is not compared.
    /// </summary>
    public static List<string> Differences(RprObjectUpdate sent, PeerObject seen)
    {
        var diffs = new List<string>();
        if (sent.Marking is { } marking)
        {
            string expected = MarkingStruct.FromAscii(marking).AsciiText;
            if (seen.Marking?.AsciiText != expected)
            {
                diffs.Add($"marking '{expected}' arrived as '{seen.Marking?.AsciiText ?? "(nothing)"}'");
            }
        }

        if (sent.EntityType is { } t)
        {
            var expected = new EntityTypeStruct(t.Kind, t.Domain, t.Country, t.Category, t.Subcategory, t.Specific, t.Extra);
            if (seen.EntityType != expected)
            {
                diffs.Add($"entity type {expected} arrived as {seen.EntityType?.ToString() ?? "(nothing)"}");
            }
        }

        if (sent.ForceId is { } force && seen.Force != (ForceIdentifierEnum8)(byte)force)
        {
            diffs.Add($"force {force} arrived as {seen.Force?.ToString() ?? "(nothing)"}");
        }

        if (sent.EntityIdentifier is { } id)
        {
            var expected = new EntityIdentifierStruct(new FederateIdentifierStruct(id.Site, id.Application), id.Entity);
            if (seen.EntityIdentifier != expected)
            {
                diffs.Add($"entity identifier {expected} arrived as {seen.EntityIdentifier?.ToString() ?? "(nothing)"}");
            }
        }

        if (sent.DamageState is { } damage && seen.Damage != (DamageStatusEnum32)(byte)damage)
        {
            diffs.Add($"damage state {damage} arrived as {seen.Damage?.ToString() ?? "(nothing)"}");
        }

        if (sent.WorldLocation is { } location)
        {
            SpatialDifferences(sent, location, seen, diffs);
        }

        return diffs;
    }

    private static void SpatialDifferences(RprObjectUpdate sent, WorldLocation location, PeerObject seen, List<string> diffs)
    {
        if (seen.Spatial is not { } spatial)
        {
            diffs.Add("the spatial state never arrived");
            return;
        }

        if (spatial.WorldLocation != new WorldLocationStruct(location.X, location.Y, location.Z))
        {
            diffs.Add($"location ({location.X}, {location.Y}, {location.Z}) arrived as {spatial.WorldLocation}");
        }

        // the algorithm of the message when the FOM has an alternative for it (DIS 1 .. 9), else Static without a velocity, FPW with one
        byte algorithm = sent.DeadReckoningAlgorithm is >= 1 and <= 9
            ? sent.DeadReckoningAlgorithm.Value
            : sent.VelocityVector is null ? (byte)1 : (byte)2;
        if ((byte)spatial.Algorithm != algorithm)
        {
            diffs.Add($"dead reckoning algorithm {algorithm} arrived as {(byte)spatial.Algorithm}");
        }

        if (sent.Orientation is { } o && spatial.Orientation != new OrientationStruct((float)o.Psi, (float)o.Theta, (float)o.Phi))
        {
            diffs.Add($"orientation ({o.Psi}, {o.Theta}, {o.Phi}) arrived as {spatial.Orientation}");
        }

        if (spatial.HasVelocity && sent.VelocityVector is { } v &&
            spatial.Velocity != new VelocityVectorStruct((float)v.X, (float)v.Y, (float)v.Z))
        {
            diffs.Add($"velocity ({v.X}, {v.Y}, {v.Z}) arrived as {spatial.Velocity}");
        }

        if (spatial.HasAcceleration && sent.AccelerationVector is { } a &&
            spatial.Acceleration != new AccelerationVectorStruct((float)a.X, (float)a.Y, (float)a.Z))
        {
            diffs.Add($"acceleration ({a.X}, {a.Y}, {a.Z}) arrived as {spatial.Acceleration}");
        }

        if (spatial.HasAngularVelocity && sent.AngularVelocity is { } w &&
            spatial.AngularVelocity != new AngularVelocityVectorStruct((float)w.X, (float)w.Y, (float)w.Z))
        {
            diffs.Add($"angular velocity ({w.X}, {w.Y}, {w.Z}) arrived as {spatial.AngularVelocity}");
        }
    }

    /// <summary>What differs between a fire as sent and as the federate decoded it (object references compared as RTI names).</summary>
    public static List<string> Differences(RprWeaponFire sent, PeerFire seen, ScriptedRprFederate peer)
    {
        var diffs = new List<string>();
        Reference("firing object", sent.FiringObject, seen.Firing, peer, diffs);
        Reference("target object", sent.TargetObject, seen.Target, peer, diffs);
        Reference("munition object", sent.MunitionObject, seen.MunitionObject, peer, diffs);
        Same("munition", ToStruct(sent.Munition), seen.Munition, diffs);
        Same("location", new WorldLocationStruct(sent.Location.X, sent.Location.Y, sent.Location.Z), seen.Location, diffs);
        Same("quantity", (ushort)Math.Clamp(sent.Quantity, 0, ushort.MaxValue), seen.Quantity, diffs);
        Same("rate of fire", sent.RateOfFire ?? 0, seen.Rate, diffs);
        Same("fuse", FuseOf(sent.FuseType), seen.Fuse, diffs);
        Same("warhead", WarheadOf(sent.WarheadType), seen.Warhead, diffs);
        return diffs;
    }

    /// <summary>What differs between a detonation as sent and as the federate decoded it.</summary>
    public static List<string> Differences(RprMunitionDetonation sent, PeerDetonation seen, ScriptedRprFederate peer)
    {
        var diffs = new List<string>();
        Reference("firing object", sent.FiringObject, seen.Firing, peer, diffs);
        Reference("target object", sent.TargetObject, seen.Target, peer, diffs);
        Reference("munition object", sent.MunitionObject, seen.MunitionObject, peer, diffs);
        Same("munition", ToStruct(sent.Munition), seen.Munition, diffs);
        Same("location", new WorldLocationStruct(sent.Location.X, sent.Location.Y, sent.Location.Z), seen.Location, diffs);
        Same("result", (DetonationResultCodeEnum8)(byte)sent.Result, seen.Result, diffs);
        Same("fuse", FuseOf(sent.FuseType), seen.Fuse, diffs);
        Same("warhead", WarheadOf(sent.WarheadType), seen.Warhead, diffs);
        return diffs;
    }

    private static void Reference(string what, RprObjectHandle? handle, string seen, ScriptedRprFederate peer, List<string> diffs)
    {
        string expected = handle is { } h ? peer.NameOfHandle(h.Value) ?? string.Empty : string.Empty;
        if (!string.Equals(expected, seen, StringComparison.Ordinal))
        {
            diffs.Add($"{what} '{expected}' arrived as '{seen}'");
        }
    }

    private static void Same<T>(string what, T expected, T seen, List<string> diffs)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, seen))
        {
            diffs.Add($"{what} {expected} arrived as {seen}");
        }
    }

    private static EntityTypeStruct ToStruct(DisEntityType t) =>
        new(t.Kind, t.Domain, t.Country, t.Category, t.Subcategory, t.Specific, t.Extra);

    private static FuseTypeEnum16 FuseOf(ushort? code) =>
        code is { } c && Enum.IsDefined((FuseTypeEnum16)c) ? (FuseTypeEnum16)c : FuseTypeEnum16.Other;

    private static WarheadTypeEnum16 WarheadOf(ushort? code) =>
        code is { } c && Enum.IsDefined((WarheadTypeEnum16)c) ? (WarheadTypeEnum16)c : WarheadTypeEnum16.Other;
}
