using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Protocol.Rpr.Tests;

/// <summary>One codec of RprFom2 as data, so the same properties can be checked for every type.</summary>
internal sealed record CodecCase(
    string Name,
    Func<Random, object> Random,
    Func<object, byte[]> Encode,
    Func<byte[], (bool Ok, object? Value)> Decode,
    bool Lengthless = false)
{
    public override string ToString() => Name;

    public static bool SameValue(object? a, object? b) =>
        a is Array x && b is Array y
            ? x.Length == y.Length && x.Cast<object>().Zip(y.Cast<object>()).All(p => Equals(p.First, p.Second))
            : Equals(a, b);
}

internal static class Codecs
{
    private static CodecCase Make<T>(string name, Func<Random, T> random, Func<T, byte[]> encode, TryDecode<T> decode, bool lengthless = false)
        where T : notnull =>
        new(name, r => random(r), v => encode((T)v), bytes => decode(bytes, out var value) ? (true, value) : (false, null), lengthless);

    private delegate bool TryDecode<T>(ReadOnlySpan<byte> data, out T value);

    public static readonly CodecCase[] All =
    [
        Make("Boolean", r => r.Next(2) == 1, RprFom2.EncodeBoolean, RprFom2.TryDecodeBoolean),
        Make("ForceIdentifier", Gen.PickEnum<ForceIdentifierEnum8>, RprFom2.EncodeForceIdentifier, RprFom2.TryDecodeForceIdentifier),
        Make("DamageStatus", Gen.PickEnum<DamageStatusEnum32>, RprFom2.EncodeDamageStatus, RprFom2.TryDecodeDamageStatus),
        Make("DetonationResultCode", Gen.PickEnum<DetonationResultCodeEnum8>, RprFom2.EncodeDetonationResultCode, RprFom2.TryDecodeDetonationResultCode),
        Make("FuseType", Gen.PickEnum<FuseTypeEnum16>, RprFom2.EncodeFuseType, RprFom2.TryDecodeFuseType),
        Make("WarheadType", Gen.PickEnum<WarheadTypeEnum16>, RprFom2.EncodeWarheadType, RprFom2.TryDecodeWarheadType),
        Make("MarkingEncoding", Gen.PickEnum<MarkingEncodingEnum8>, RprFom2.EncodeMarkingEncoding, RprFom2.TryDecodeMarkingEncoding),
        Make("DeadReckoningAlgorithm", Gen.PickEnum<DeadReckoningAlgorithmEnum8>, RprFom2.EncodeDeadReckoningAlgorithm, RprFom2.TryDecodeDeadReckoningAlgorithm),
        Make("FederateIdentifier", Gen.Federate, RprFom2.EncodeFederateIdentifier, RprFom2.TryDecodeFederateIdentifier),
        Make("EntityIdentifier", Gen.EntityId, RprFom2.EncodeEntityIdentifier, RprFom2.TryDecodeEntityIdentifier),
        Make("EntityType", Gen.EntityType, RprFom2.EncodeEntityType, RprFom2.TryDecodeEntityType),
        Make("Marking", Gen.Marking, RprFom2.EncodeMarking, RprFom2.TryDecodeMarking),
        Make("WorldLocation", Gen.World, RprFom2.EncodeWorldLocation, RprFom2.TryDecodeWorldLocation),
        Make("Orientation", Gen.Orientation, RprFom2.EncodeOrientation, RprFom2.TryDecodeOrientation),
        Make("VelocityVector", Gen.Velocity, RprFom2.EncodeVelocityVector, RprFom2.TryDecodeVelocityVector),
        Make("AccelerationVector", Gen.Acceleration, RprFom2.EncodeAccelerationVector, RprFom2.TryDecodeAccelerationVector),
        Make("AngularVelocityVector", Gen.AngularVelocity, RprFom2.EncodeAngularVelocityVector, RprFom2.TryDecodeAngularVelocityVector),
        Make("RelativePosition", Gen.Relative, RprFom2.EncodeRelativePosition, RprFom2.TryDecodeRelativePosition),
        Make("Spatial", r => Gen.Spatial(r), RprFom2.EncodeSpatial, RprFom2.TryDecodeSpatial),
        Make("RtiObjectId", Gen.ObjectName, RprFom2.EncodeRtiObjectId, RprFom2.TryDecodeRtiObjectId),
        Make("EventIdentifier", Gen.Event, RprFom2.EncodeEventIdentifier, RprFom2.TryDecodeEventIdentifier),
        Make("ArticulatedParameter", Gen.Articulated, RprFom2.EncodeArticulatedParameter, RprFom2.TryDecodeArticulatedParameter),
        Make(
            "ArticulatedParameterLengthlessArray",
            r => Enumerable.Range(0, r.Next(0, 6)).Select(_ => Gen.Articulated(r)).ToArray(),
            RprFom2.EncodeArticulatedParameters,
            RprFom2.TryDecodeArticulatedParameters,
            lengthless: true),
    ];

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var c in All)
        {
            data.Add(c.Name);
        }
        return data;
    }

    public static CodecCase Get(string name) => All.Single(c => c.Name == name);
}
