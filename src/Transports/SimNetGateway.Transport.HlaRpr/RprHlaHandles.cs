using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.HlaRpr;

/// <summary>Every RTI handle the transport uses, resolved once after joining (DD-008 section 5.1).</summary>
internal sealed class RprHlaHandles
{
    public ObjectClassHandle PhysicalEntity { get; private set; }

    public ObjectClassHandle AggregateEntity { get; private set; }

    /// <summary>Published object classes by fully qualified name (<see cref="RprHlaClasses.Published"/>).</summary>
    public Dictionary<string, ObjectClassHandle> PublishedClasses { get; } = new(StringComparer.Ordinal);

    // PhysicalEntity attributes (inherited ones keep the declaring class's handle, so these serve every subclass)
    public AttributeHandle EntityType { get; private set; }
    public AttributeHandle EntityIdentifier { get; private set; }
    public AttributeHandle Spatial { get; private set; }
    public AttributeHandle Marking { get; private set; }
    public AttributeHandle Force { get; private set; }
    public AttributeHandle Damage { get; private set; }

    // AggregateEntity attributes (EntityType is BaseEntity's, shared with the physical set)
    public AttributeHandle AggregateMarking { get; private set; }
    public AttributeHandle AggregateForce { get; private set; }
    public AttributeHandle AggregateEntityIdentifiers { get; private set; }

    public AttributeHandleSet PhysicalSet { get; private set; } = new();

    public AttributeHandleSet AggregateSet { get; private set; } = new();

    public InteractionClassHandle WeaponFire { get; private set; }

    public InteractionClassHandle Detonation { get; private set; }

    public Dictionary<string, ParameterHandle> FireParameters { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, ParameterHandle> DetonationParameters { get; } = new(StringComparer.Ordinal);

    public void Resolve(IRtiAmbassador rti)
    {
        PhysicalEntity = rti.GetObjectClassHandle(RprFom2.ObjectClasses.PhysicalEntity);
        AggregateEntity = rti.GetObjectClassHandle(RprFom2.ObjectClasses.AggregateEntity);
        foreach (var name in RprHlaClasses.Published)
        {
            PublishedClasses[name] = rti.GetObjectClassHandle(name);
        }

        EntityType = rti.GetAttributeHandle(PhysicalEntity, RprFom2.BaseEntityAttributes.EntityType);
        EntityIdentifier = rti.GetAttributeHandle(PhysicalEntity, RprFom2.BaseEntityAttributes.EntityIdentifier);
        Spatial = rti.GetAttributeHandle(PhysicalEntity, RprFom2.BaseEntityAttributes.Spatial);
        Marking = rti.GetAttributeHandle(PhysicalEntity, RprFom2.PhysicalEntityAttributes.Marking);
        Force = rti.GetAttributeHandle(PhysicalEntity, RprFom2.PhysicalEntityAttributes.ForceIdentifier);
        Damage = rti.GetAttributeHandle(PhysicalEntity, RprFom2.PhysicalEntityAttributes.DamageState);
        PhysicalSet = new AttributeHandleSet([EntityType, EntityIdentifier, Spatial, Marking, Force, Damage]);

        AggregateMarking = rti.GetAttributeHandle(AggregateEntity, RprFom2.AggregateEntityAttributes.AggregateMarking);
        AggregateForce = rti.GetAttributeHandle(AggregateEntity, RprFom2.AggregateEntityAttributes.ForceIdentifier);
        AggregateEntityIdentifiers = rti.GetAttributeHandle(AggregateEntity, RprFom2.AggregateEntityAttributes.EntityIdentifiers);
        AggregateSet = new AttributeHandleSet([EntityType, AggregateMarking, AggregateForce, AggregateEntityIdentifiers]);

        WeaponFire = rti.GetInteractionClassHandle(RprFom2.InteractionClasses.WeaponFire);
        Detonation = rti.GetInteractionClassHandle(RprFom2.InteractionClasses.MunitionDetonation);
        foreach (var name in FireParameterNames)
        {
            FireParameters[name] = rti.GetParameterHandle(WeaponFire, name);
        }

        foreach (var name in DetonationParameterNames)
        {
            DetonationParameters[name] = rti.GetParameterHandle(Detonation, name);
        }
    }

    private static readonly string[] FireParameterNames =
    [
        RprFom2.WeaponFireParameters.EventIdentifier,
        RprFom2.WeaponFireParameters.FireControlSolutionRange,
        RprFom2.WeaponFireParameters.FireMissionIndex,
        RprFom2.WeaponFireParameters.FiringLocation,
        RprFom2.WeaponFireParameters.FiringObjectIdentifier,
        RprFom2.WeaponFireParameters.FuseType,
        RprFom2.WeaponFireParameters.InitialVelocityVector,
        RprFom2.WeaponFireParameters.MunitionObjectIdentifier,
        RprFom2.WeaponFireParameters.MunitionType,
        RprFom2.WeaponFireParameters.QuantityFired,
        RprFom2.WeaponFireParameters.RateOfFire,
        RprFom2.WeaponFireParameters.TargetObjectIdentifier,
        RprFom2.WeaponFireParameters.WarheadType,
    ];

    private static readonly string[] DetonationParameterNames =
    [
        RprFom2.MunitionDetonationParameters.ArticulatedPartData,
        RprFom2.MunitionDetonationParameters.DetonationLocation,
        RprFom2.MunitionDetonationParameters.DetonationResultCode,
        RprFom2.MunitionDetonationParameters.EventIdentifier,
        RprFom2.MunitionDetonationParameters.FiringObjectIdentifier,
        RprFom2.MunitionDetonationParameters.FinalVelocityVector,
        RprFom2.MunitionDetonationParameters.FuseType,
        RprFom2.MunitionDetonationParameters.MunitionObjectIdentifier,
        RprFom2.MunitionDetonationParameters.MunitionType,
        RprFom2.MunitionDetonationParameters.QuantityFired,
        RprFom2.MunitionDetonationParameters.RateOfFire,
        RprFom2.MunitionDetonationParameters.RelativeDetonationLocation,
        RprFom2.MunitionDetonationParameters.TargetObjectIdentifier,
        RprFom2.MunitionDetonationParameters.WarheadType,
    ];
}
