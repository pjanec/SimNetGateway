using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.HlaRpr;

// The gateway -> RTI direction (DD-008 sections 3.4, 3.5, 5.2): synchronous RTI calls inside Send.
public sealed partial class RprHlaConnection
{
    /// <inheritdoc />
    public void Send(in IRprMessage message)
    {
        if (!IsStarted)
        {
            RejectedOutbound++;
            return;
        }

        try
        {
            switch (message)
            {
                case RprRegisterObject m: Register(m); break;
                case RprObjectUpdate m: SendUpdate(m); break;
                case RprObjectRemoved m: Delete(m); break;
                case RprWeaponFire m: SendFire(m); break;
                case RprMunitionDetonation m: SendDetonation(m); break;
                case RprSimulationControl:
                    DroppedSimulationControl++;
                    WarnOnce("simulation-control",
                        "RprSimulationControl is not carried over HLA (RPR FOM 2.0 has no simulation management interaction, R-107); dropped.");
                    break;
                default:
                    RejectedOutbound++;
                    WarnOnce("outbound:" + message.GetType().Name, $"{message.GetType().Name} is not an outbound RPR message; dropped.");
                    break;
            }
        }
        catch (RtiException)
        {
            SendErrors++;
        }
    }

    private void Register(RprRegisterObject m)
    {
        var cls = _h.PublishedClasses[RprHlaClasses.NameFor(m.EntityType)];
        var instance = _rti.RegisterObjectInstance(cls);
        var name = _rti.GetObjectInstanceName(instance);
        var handle = new RprObjectHandle((uint)instance.Value);
        _own[handle.Value] = new OwnObject(instance, name);
        _handleToName[handle.Value] = name;
        _nameToHandle[name] = handle.Value;
        // the gateway models registration as asynchronous: the answer waits for the next Pump
        _answers.Add(new RprObjectRegistered(m.Token, handle));
    }

    private void Delete(RprObjectRemoved m)
    {
        if (!_own.Remove(m.Handle.Value, out var o))
        {
            RejectedOutbound++;
            return;
        }

        _handleToName.Remove(m.Handle.Value);
        _nameToHandle.Remove(o.Name);
        _rti.DeleteObjectInstance(o.Instance, NoTag);
    }

    // ---- attribute updates ---------------------------------------------------------------------------------------------

    private void SendUpdate(RprObjectUpdate u)
    {
        if (!_own.TryGetValue(u.Handle.Value, out var o))
        {
            RejectedOutbound++;
            return;
        }

        if (u.DamagePercent is not null)
        {
            DroppedDamagePercent++;
            WarnOnce("damage-percent", "RPR FOM 2.0 has no damage percentage attribute; DamagePercent is dropped (R-105).");
        }

        var values = new AttributeHandleValueMap();
        if (u.EntityType is { } t)
        {
            Put(values, _h.EntityType, () => RprFom2.EncodeEntityType(
                new EntityTypeStruct(t.Kind, t.Domain, t.Country, t.Category, t.Subcategory, t.Specific, t.Extra)));
        }

        if (u.EntityIdentifier is { } id)
        {
            Put(values, _h.EntityIdentifier, () => RprFom2.EncodeEntityIdentifier(
                new EntityIdentifierStruct(new FederateIdentifierStruct(id.Site, id.Application), id.Entity)));
        }

        if (u.Marking is { } marking)
        {
            Put(values, _h.Marking, () => RprFom2.EncodeMarking(MarkingStruct.FromAscii(marking)));
        }

        if (u.ForceId is { } force)
        {
            Put(values, _h.Force, () => RprFom2.EncodeForceIdentifier((ForceIdentifierEnum8)(byte)force));
        }

        if (u.DamageState is { } damage)
        {
            Put(values, _h.Damage, () => RprFom2.EncodeDamageStatus((DamageStatusEnum32)(byte)damage));
        }

        // one variant record: any spatial field that changed re-encodes the whole variant from the merged cache
        if (o.Spatial.Merge(u) && o.Spatial.TryBuild(out var spatial))
        {
            Put(values, _h.Spatial, () => RprFom2.EncodeSpatial(spatial));
        }

        if (!values.IsEmpty)
        {
            _rti.UpdateAttributeValues(o.Instance, values, NoTag);
        }
    }

    private void Put<THandle>(HandleValueMap<THandle> map, THandle handle, Func<byte[]> encode)
        where THandle : struct, IHandle
    {
        try
        {
            map.Set(handle, encode());
        }
        catch (ArgumentException)
        {
            EncodeErrors++; // the FOM cannot hold this value: the attribute or parameter is left out
        }
    }

    // ---- interactions ---------------------------------------------------------------------------------------------------

    private void SendFire(RprWeaponFire m)
    {
        var p = _h.FireParameters;
        var values = new ParameterHandleValueMap();
        var firing = NameOf(m.FiringObject);
        var key = _correlation.ForOutbound(m.EventId, firing, _clock.Now);
        Put(values, p[RprFom2.WeaponFireParameters.EventIdentifier],
            () => RprFom2.EncodeEventIdentifier(new EventIdentifierStruct(key.Count, key.IssuingObject)));
        Put(values, p[RprFom2.WeaponFireParameters.FireControlSolutionRange],
            () => RprFom2.EncodeLengthMeterFloat32(m.FireControlSolutionRange ?? 0f));
        Put(values, p[RprFom2.WeaponFireParameters.FireMissionIndex], () => RprFom2.EncodeUnsignedInteger32(0));
        Put(values, p[RprFom2.WeaponFireParameters.FiringLocation], () => RprFom2.EncodeWorldLocation(ToStruct(m.Location)));
        Put(values, p[RprFom2.WeaponFireParameters.FiringObjectIdentifier], () => RprFom2.EncodeRtiObjectId(firing));
        Put(values, p[RprFom2.WeaponFireParameters.FuseType], () => RprFom2.EncodeFuseType(FuseOf(m.FuseType)));
        Put(values, p[RprFom2.WeaponFireParameters.InitialVelocityVector], () => RprFom2.EncodeVelocityVector(ToVelocity(m.InitialVelocity)));
        Put(values, p[RprFom2.WeaponFireParameters.MunitionObjectIdentifier], () => RprFom2.EncodeRtiObjectId(NameOf(m.MunitionObject)));
        Put(values, p[RprFom2.WeaponFireParameters.MunitionType], () => RprFom2.EncodeEntityType(ToStruct(m.Munition)));
        Put(values, p[RprFom2.WeaponFireParameters.QuantityFired], () => RprFom2.EncodeUnsignedInteger16(ClampQuantity(m.Quantity)));
        Put(values, p[RprFom2.WeaponFireParameters.RateOfFire], () => RprFom2.EncodeUnsignedInteger16(m.RateOfFire ?? 0));
        Put(values, p[RprFom2.WeaponFireParameters.TargetObjectIdentifier], () => RprFom2.EncodeRtiObjectId(NameOf(m.TargetObject)));
        Put(values, p[RprFom2.WeaponFireParameters.WarheadType], () => RprFom2.EncodeWarheadType(WarheadOf(m.WarheadType)));
        _rti.SendInteraction(_h.WeaponFire, values, NoTag);
    }

    private void SendDetonation(RprMunitionDetonation m)
    {
        var p = _h.DetonationParameters;
        var values = new ParameterHandleValueMap();
        var firing = NameOf(m.FiringObject);
        var key = _correlation.ForOutbound(m.EventId, firing, _clock.Now);
        Put(values, p[RprFom2.MunitionDetonationParameters.ArticulatedPartData], () => RprFom2.EncodeArticulatedParameters([]));
        Put(values, p[RprFom2.MunitionDetonationParameters.DetonationLocation], () => RprFom2.EncodeWorldLocation(ToStruct(m.Location)));
        Put(values, p[RprFom2.MunitionDetonationParameters.DetonationResultCode],
            () => RprFom2.EncodeDetonationResultCode((DetonationResultCodeEnum8)(byte)m.Result));
        Put(values, p[RprFom2.MunitionDetonationParameters.EventIdentifier],
            () => RprFom2.EncodeEventIdentifier(new EventIdentifierStruct(key.Count, key.IssuingObject)));
        Put(values, p[RprFom2.MunitionDetonationParameters.FiringObjectIdentifier], () => RprFom2.EncodeRtiObjectId(firing));
        Put(values, p[RprFom2.MunitionDetonationParameters.FinalVelocityVector], () => RprFom2.EncodeVelocityVector(ToVelocity(m.FinalVelocity)));
        Put(values, p[RprFom2.MunitionDetonationParameters.FuseType], () => RprFom2.EncodeFuseType(FuseOf(m.FuseType)));
        Put(values, p[RprFom2.MunitionDetonationParameters.MunitionObjectIdentifier], () => RprFom2.EncodeRtiObjectId(NameOf(m.MunitionObject)));
        Put(values, p[RprFom2.MunitionDetonationParameters.MunitionType], () => RprFom2.EncodeEntityType(ToStruct(m.Munition)));
        Put(values, p[RprFom2.MunitionDetonationParameters.QuantityFired], () => RprFom2.EncodeUnsignedInteger16(1));
        Put(values, p[RprFom2.MunitionDetonationParameters.RateOfFire], () => RprFom2.EncodeUnsignedInteger16(m.RateOfFire ?? 0));
        Put(values, p[RprFom2.MunitionDetonationParameters.RelativeDetonationLocation],
            () => RprFom2.EncodeRelativePosition(ToRelative(m.RelativeDetonationLocation)));
        Put(values, p[RprFom2.MunitionDetonationParameters.TargetObjectIdentifier], () => RprFom2.EncodeRtiObjectId(NameOf(m.TargetObject)));
        Put(values, p[RprFom2.MunitionDetonationParameters.WarheadType], () => RprFom2.EncodeWarheadType(WarheadOf(m.WarheadType)));
        _rti.SendInteraction(_h.Detonation, values, NoTag);
    }

    /// <summary>The RTI name an object reference is sent as: empty means "no object" (also for a handle this transport does not know).</summary>
    private string NameOf(RprObjectHandle? handle) =>
        handle is { } h && _handleToName.TryGetValue(h.Value, out var name) ? name : string.Empty;

    private static WorldLocationStruct ToStruct(WorldLocation l) => new(l.X, l.Y, l.Z);

    private static EntityTypeStruct ToStruct(DisEntityType t) =>
        new(t.Kind, t.Domain, t.Country, t.Category, t.Subcategory, t.Specific, t.Extra);

    private static VelocityVectorStruct ToVelocity(RprVector? v) =>
        v is { } x ? new VelocityVectorStruct((float)x.X, (float)x.Y, (float)x.Z) : default;

    private static RelativePositionStruct ToRelative(RprVector? v) =>
        v is { } x ? new RelativePositionStruct((float)x.X, (float)x.Y, (float)x.Z) : default;

    private static ushort ClampQuantity(int quantity) => (ushort)Math.Clamp(quantity, 0, ushort.MaxValue);

    private static FuseTypeEnum16 FuseOf(ushort? code) =>
        code is { } c && Enum.IsDefined((FuseTypeEnum16)c) ? (FuseTypeEnum16)c : FuseTypeEnum16.Other;

    private static WarheadTypeEnum16 WarheadOf(ushort? code) =>
        code is { } c && Enum.IsDefined((WarheadTypeEnum16)c) ? (WarheadTypeEnum16)c : WarheadTypeEnum16.Other;
}
