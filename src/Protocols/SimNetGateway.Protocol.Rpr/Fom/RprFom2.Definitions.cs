// Generated once from the RPR FOM 2.0 modules RPR-Base / RPR-Physical / RPR-Aggregate / RPR-Warfare (SISO-STD-001.1-2015 Annex A):
// the subset of classes the gateway uses, with the attributes and parameters in module order.
namespace SimNetGateway.Protocol.Rpr.Fom;

public static partial class RprFom2
{
    /// <summary>Fully qualified object class names of the RPR FOM 2.0 subset.</summary>
    public static class ObjectClasses
    {
        public const string BaseEntity = "HLAobjectRoot.BaseEntity";
        public const string PhysicalEntity = "HLAobjectRoot.BaseEntity.PhysicalEntity";
        public const string Platform = "HLAobjectRoot.BaseEntity.PhysicalEntity.Platform";
        public const string Aircraft = "HLAobjectRoot.BaseEntity.PhysicalEntity.Platform.Aircraft";
        public const string AmphibiousVehicle = "HLAobjectRoot.BaseEntity.PhysicalEntity.Platform.AmphibiousVehicle";
        public const string GroundVehicle = "HLAobjectRoot.BaseEntity.PhysicalEntity.Platform.GroundVehicle";
        public const string MultiDomainPlatform = "HLAobjectRoot.BaseEntity.PhysicalEntity.Platform.MultiDomainPlatform";
        public const string Spacecraft = "HLAobjectRoot.BaseEntity.PhysicalEntity.Platform.Spacecraft";
        public const string SubmersibleVessel = "HLAobjectRoot.BaseEntity.PhysicalEntity.Platform.SubmersibleVessel";
        public const string SurfaceVessel = "HLAobjectRoot.BaseEntity.PhysicalEntity.Platform.SurfaceVessel";
        public const string Lifeform = "HLAobjectRoot.BaseEntity.PhysicalEntity.Lifeform";
        public const string Human = "HLAobjectRoot.BaseEntity.PhysicalEntity.Lifeform.Human";
        public const string NonHuman = "HLAobjectRoot.BaseEntity.PhysicalEntity.Lifeform.NonHuman";
        public const string Munition = "HLAobjectRoot.BaseEntity.PhysicalEntity.Munition";
        public const string AggregateEntity = "HLAobjectRoot.BaseEntity.AggregateEntity";
    }

    /// <summary>Fully qualified interaction class names of the RPR FOM 2.0 subset.</summary>
    public static class InteractionClasses
    {
        public const string WeaponFire = "HLAinteractionRoot.WeaponFire";
        public const string MunitionDetonation = "HLAinteractionRoot.MunitionDetonation";
    }

    /// <summary>Attributes declared by BaseEntity.</summary>
    public static class BaseEntityAttributes
    {
        public const string EntityType = "EntityType";
        public const string EntityIdentifier = "EntityIdentifier";
        public const string IsPartOf = "IsPartOf";
        public const string Spatial = "Spatial";
        public const string RelativeSpatial = "RelativeSpatial";
    }

    /// <summary>Attributes declared by PhysicalEntity.</summary>
    public static class PhysicalEntityAttributes
    {
        public const string AcousticSignatureIndex = "AcousticSignatureIndex";
        public const string AlternateEntityType = "AlternateEntityType";
        public const string ArticulatedParametersArray = "ArticulatedParametersArray";
        public const string CamouflageType = "CamouflageType";
        public const string DamageState = "DamageState";
        public const string EngineSmokeOn = "EngineSmokeOn";
        public const string FirePowerDisabled = "FirePowerDisabled";
        public const string FlamesPresent = "FlamesPresent";
        public const string ForceIdentifier = "ForceIdentifier";
        public const string HasAmmunitionSupplyCap = "HasAmmunitionSupplyCap";
        public const string HasFuelSupplyCap = "HasFuelSupplyCap";
        public const string HasRecoveryCap = "HasRecoveryCap";
        public const string HasRepairCap = "HasRepairCap";
        public const string Immobilized = "Immobilized";
        public const string InfraredSignatureIndex = "InfraredSignatureIndex";
        public const string IsConcealed = "IsConcealed";
        public const string LiveEntityMeasuredSpeed = "LiveEntityMeasuredSpeed";
        public const string Marking = "Marking";
        public const string PowerPlantOn = "PowerPlantOn";
        public const string PropulsionSystemsData = "PropulsionSystemsData";
        public const string RadarCrossSectionSignatureIndex = "RadarCrossSectionSignatureIndex";
        public const string SmokePlumePresent = "SmokePlumePresent";
        public const string TentDeployed = "TentDeployed";
        public const string TrailingEffectsCode = "TrailingEffectsCode";
        public const string VectoringNozzleSystemData = "VectoringNozzleSystemData";
    }

    /// <summary>Attributes declared by Platform.</summary>
    public static class PlatformAttributes
    {
        public const string AfterburnerOn = "AfterburnerOn";
        public const string AntiCollisionLightsOn = "AntiCollisionLightsOn";
        public const string BlackOutBrakeLightsOn = "BlackOutBrakeLightsOn";
        public const string BlackOutLightsOn = "BlackOutLightsOn";
        public const string BrakeLightsOn = "BrakeLightsOn";
        public const string FormationLightsOn = "FormationLightsOn";
        public const string HatchState = "HatchState";
        public const string HeadLightsOn = "HeadLightsOn";
        public const string InteriorLightsOn = "InteriorLightsOn";
        public const string LandingLightsOn = "LandingLightsOn";
        public const string LauncherRaised = "LauncherRaised";
        public const string NavigationLightsOn = "NavigationLightsOn";
        public const string RampDeployed = "RampDeployed";
        public const string RunningLightsOn = "RunningLightsOn";
        public const string SpotLightsOn = "SpotLightsOn";
        public const string TailLightsOn = "TailLightsOn";
    }

    /// <summary>Attributes declared by Lifeform.</summary>
    public static class LifeformAttributes
    {
        public const string FlashLightsOn = "FlashLightsOn";
        public const string StanceCode = "StanceCode";
        public const string PrimaryWeaponState = "PrimaryWeaponState";
        public const string SecondaryWeaponState = "SecondaryWeaponState";
        public const string ComplianceState = "ComplianceState";
    }

    /// <summary>Attributes declared by Munition.</summary>
    public static class MunitionAttributes
    {
        public const string LauncherFlashPresent = "LauncherFlashPresent";
    }

    /// <summary>Attributes declared by AggregateEntity.</summary>
    public static class AggregateEntityAttributes
    {
        public const string AggregateMarking = "AggregateMarking";
        public const string AggregateState = "AggregateState";
        public const string Dimensions = "Dimensions";
        public const string EntityIdentifiers = "EntityIdentifiers";
        public const string ForceIdentifier = "ForceIdentifier";
        public const string Formation = "Formation";
        public const string NumberOfSilentEntities = "NumberOfSilentEntities";
        public const string NumberOfVariableDatums = "NumberOfVariableDatums";
        public const string SilentAggregates = "SilentAggregates";
        public const string SilentEntities = "SilentEntities";
        public const string SubAggregateIdentifiers = "SubAggregateIdentifiers";
        public const string VariableDatums = "VariableDatums";
    }

    /// <summary>Parameters of WeaponFire.</summary>
    public static class WeaponFireParameters
    {
        public const string EventIdentifier = "EventIdentifier";
        public const string FireControlSolutionRange = "FireControlSolutionRange";
        public const string FireMissionIndex = "FireMissionIndex";
        public const string FiringLocation = "FiringLocation";
        public const string FiringObjectIdentifier = "FiringObjectIdentifier";
        public const string FuseType = "FuseType";
        public const string InitialVelocityVector = "InitialVelocityVector";
        public const string MunitionObjectIdentifier = "MunitionObjectIdentifier";
        public const string MunitionType = "MunitionType";
        public const string QuantityFired = "QuantityFired";
        public const string RateOfFire = "RateOfFire";
        public const string TargetObjectIdentifier = "TargetObjectIdentifier";
        public const string WarheadType = "WarheadType";
    }

    /// <summary>Parameters of MunitionDetonation.</summary>
    public static class MunitionDetonationParameters
    {
        public const string ArticulatedPartData = "ArticulatedPartData";
        public const string DetonationLocation = "DetonationLocation";
        public const string DetonationResultCode = "DetonationResultCode";
        public const string EventIdentifier = "EventIdentifier";
        public const string FiringObjectIdentifier = "FiringObjectIdentifier";
        public const string FinalVelocityVector = "FinalVelocityVector";
        public const string FuseType = "FuseType";
        public const string MunitionObjectIdentifier = "MunitionObjectIdentifier";
        public const string MunitionType = "MunitionType";
        public const string QuantityFired = "QuantityFired";
        public const string RateOfFire = "RateOfFire";
        public const string RelativeDetonationLocation = "RelativeDetonationLocation";
        public const string TargetObjectIdentifier = "TargetObjectIdentifier";
        public const string WarheadType = "WarheadType";
    }

    private static FomModel BuildModel()
    {
        var builder = new FomModelBuilder();
        builder.ObjectClass(ObjectClasses.BaseEntity,
            ("EntityType", "EntityTypeStruct"),
            ("EntityIdentifier", "EntityIdentifierStruct"),
            ("IsPartOf", "IsPartOfStruct"),
            ("Spatial", "SpatialVariantStruct"),
            ("RelativeSpatial", "SpatialVariantStruct"));
        builder.ObjectClass(ObjectClasses.PhysicalEntity,
            ("AcousticSignatureIndex", "Integer16"),
            ("AlternateEntityType", "EntityTypeStruct"),
            ("ArticulatedParametersArray", "ArticulatedParameterStructLengthlessArray"),
            ("CamouflageType", "CamouflageEnum32"),
            ("DamageState", "DamageStatusEnum32"),
            ("EngineSmokeOn", "RPRboolean"),
            ("FirePowerDisabled", "RPRboolean"),
            ("FlamesPresent", "RPRboolean"),
            ("ForceIdentifier", "ForceIdentifierEnum8"),
            ("HasAmmunitionSupplyCap", "RPRboolean"),
            ("HasFuelSupplyCap", "RPRboolean"),
            ("HasRecoveryCap", "RPRboolean"),
            ("HasRepairCap", "RPRboolean"),
            ("Immobilized", "RPRboolean"),
            ("InfraredSignatureIndex", "Integer16"),
            ("IsConcealed", "RPRboolean"),
            ("LiveEntityMeasuredSpeed", "VelocityDecimeterPerSecondInteger16"),
            ("Marking", "MarkingStruct"),
            ("PowerPlantOn", "RPRboolean"),
            ("PropulsionSystemsData", "PropulsionSystemDataStructLengthlessArray"),
            ("RadarCrossSectionSignatureIndex", "Integer16"),
            ("SmokePlumePresent", "RPRboolean"),
            ("TentDeployed", "RPRboolean"),
            ("TrailingEffectsCode", "TrailingEffectsCodeEnum32"),
            ("VectoringNozzleSystemData", "VectoringNozzleSystemDataStructLengthlessArray"));
        builder.ObjectClass(ObjectClasses.Platform,
            ("AfterburnerOn", "RPRboolean"),
            ("AntiCollisionLightsOn", "RPRboolean"),
            ("BlackOutBrakeLightsOn", "RPRboolean"),
            ("BlackOutLightsOn", "RPRboolean"),
            ("BrakeLightsOn", "RPRboolean"),
            ("FormationLightsOn", "RPRboolean"),
            ("HatchState", "HatchStateEnum32"),
            ("HeadLightsOn", "RPRboolean"),
            ("InteriorLightsOn", "RPRboolean"),
            ("LandingLightsOn", "RPRboolean"),
            ("LauncherRaised", "RPRboolean"),
            ("NavigationLightsOn", "RPRboolean"),
            ("RampDeployed", "RPRboolean"),
            ("RunningLightsOn", "RPRboolean"),
            ("SpotLightsOn", "RPRboolean"),
            ("TailLightsOn", "RPRboolean"));
        builder.ObjectClass(ObjectClasses.Aircraft);
        builder.ObjectClass(ObjectClasses.AmphibiousVehicle);
        builder.ObjectClass(ObjectClasses.GroundVehicle);
        builder.ObjectClass(ObjectClasses.MultiDomainPlatform);
        builder.ObjectClass(ObjectClasses.Spacecraft);
        builder.ObjectClass(ObjectClasses.SubmersibleVessel);
        builder.ObjectClass(ObjectClasses.SurfaceVessel);
        builder.ObjectClass(ObjectClasses.Lifeform,
            ("FlashLightsOn", "RPRboolean"),
            ("StanceCode", "StanceCodeEnum32"),
            ("PrimaryWeaponState", "WeaponStateEnum32"),
            ("SecondaryWeaponState", "WeaponStateEnum32"),
            ("ComplianceState", "ComplianceStateEnum32"));
        builder.ObjectClass(ObjectClasses.Human);
        builder.ObjectClass(ObjectClasses.NonHuman);
        builder.ObjectClass(ObjectClasses.Munition,
            ("LauncherFlashPresent", "RPRboolean"));
        builder.ObjectClass(ObjectClasses.AggregateEntity,
            ("AggregateMarking", "AggregateMarkingStruct"),
            ("AggregateState", "AggregateStateEnum8"),
            ("Dimensions", "DimensionStruct"),
            ("EntityIdentifiers", "RTIobjectIdArray"),
            ("ForceIdentifier", "ForceIdentifierEnum8"),
            ("Formation", "FormationEnum32"),
            ("NumberOfSilentEntities", "Integer16"),
            ("NumberOfVariableDatums", "UnsignedInteger32"),
            ("SilentAggregates", "SilentAggregateStructLengthlessArray"),
            ("SilentEntities", "SilentEntityStructLengthlessArray"),
            ("SubAggregateIdentifiers", "RTIobjectIdArray"),
            ("VariableDatums", "VariableDatumStructLengthlessArray"));
        builder.InteractionClass(InteractionClasses.WeaponFire,
            ("EventIdentifier", "EventIdentifierStruct"),
            ("FireControlSolutionRange", "LengthMeterFloat32"),
            ("FireMissionIndex", "UnsignedInteger32"),
            ("FiringLocation", "WorldLocationStruct"),
            ("FiringObjectIdentifier", "RTIobjectId"),
            ("FuseType", "FuseTypeEnum16"),
            ("InitialVelocityVector", "VelocityVectorStruct"),
            ("MunitionObjectIdentifier", "RTIobjectId"),
            ("MunitionType", "EntityTypeStruct"),
            ("QuantityFired", "UnsignedInteger16"),
            ("RateOfFire", "UnsignedInteger16"),
            ("TargetObjectIdentifier", "RTIobjectId"),
            ("WarheadType", "WarheadTypeEnum16"));
        builder.InteractionClass(InteractionClasses.MunitionDetonation,
            ("ArticulatedPartData", "ArticulatedParameterStructLengthlessArray"),
            ("DetonationLocation", "WorldLocationStruct"),
            ("DetonationResultCode", "DetonationResultCodeEnum8"),
            ("EventIdentifier", "EventIdentifierStruct"),
            ("FiringObjectIdentifier", "RTIobjectId"),
            ("FinalVelocityVector", "VelocityVectorStruct"),
            ("FuseType", "FuseTypeEnum16"),
            ("MunitionObjectIdentifier", "RTIobjectId"),
            ("MunitionType", "EntityTypeStruct"),
            ("QuantityFired", "UnsignedInteger16"),
            ("RateOfFire", "UnsignedInteger16"),
            ("RelativeDetonationLocation", "RelativePositionStruct"),
            ("TargetObjectIdentifier", "RTIobjectId"),
            ("WarheadType", "WarheadTypeEnum16"));
        return builder.Build();
    }
}
