using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Dis7;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// Plain conversions between the DIS wire classes (<c>DISnet</c>) and the values of the pivot (DD-006 section 3.1): packed entity
/// types, entity and event identifiers, vectors, and the damage / deactivated bits of the entity appearance. No state; the DIS
/// ingress and egress modules share them so that both directions agree by construction.
/// </summary>
public static class DisConversions
{
    /// <summary>The "all" value of site, application and entity number (IEEE 1278.1: ALL_SITES, ALL_APPLIC, ALL_ENTITIES).</summary>
    public const ushort All = 0xFFFF;

    // ---- entity type --------------------------------------------------------------------------------------------------

    public static long PackType(EntityType type) =>
        new DisTypeCode(type.EntityKind, type.Domain, type.Country, type.Category, type.Subcategory, type.Specific, type.Extra).Pack();

    public static EntityType ToEntityType(long packed)
    {
        var code = DisTypeCode.Unpack(packed);
        return new EntityType
        {
            EntityKind = code.Kind,
            Domain = code.Domain,
            Country = code.Country,
            Category = code.Category,
            Subcategory = code.Subcategory,
            Specific = code.Specific,
            Extra = code.Extra,
        };
    }

    // ---- identifiers --------------------------------------------------------------------------------------------------

    public static DisEntityKey KeyOf(EntityID id) =>
        new(id.SimulationAddress.Site, id.SimulationAddress.Application, id.EntityNumber);

    public static EntityID ToEntityId(DisEntityKey key) => new()
    {
        SimulationAddress = new SimulationAddress { Site = key.Site, Application = key.Application },
        EntityNumber = key.Number,
    };

    /// <summary>The "no entity" identifier (0, 0, 0): a Fire without a target, a Detonation without an exploding entity.</summary>
    public static EntityID NoEntity() => new();

    /// <summary>The identifier of a PDU that goes to every simulation application: all sites, all applications, all entities.</summary>
    public static EntityID AllEntities() => new()
    {
        SimulationAddress = new SimulationAddress { Site = All, Application = All },
        EntityNumber = All,
    };

    public static bool IsNoEntity(EntityID id) =>
        id.SimulationAddress.Site == 0 && id.SimulationAddress.Application == 0 && id.EntityNumber == 0;

    public static DisEventId EventIdOf(EventIdentifier id) =>
        new(id.SimulationAddress.Site, id.SimulationAddress.Application, id.EventNumber);

    public static EventIdentifier ToEventIdentifier(DisEventId id) => new()
    {
        SimulationAddress = new SimulationAddress { Site = id.Site, Application = id.Application },
        EventNumber = id.Event,
    };

    // ---- vectors ------------------------------------------------------------------------------------------------------

    public static Vec3 ToVec(Vector3Float v) => new(v.X, v.Y, v.Z);

    public static Vec3 ToVec(Vector3Double v) => new(v.X, v.Y, v.Z);

    public static Vec3 ToVec(EulerAngles a) => new(a.Psi, a.Theta, a.Phi);

    public static Vector3Float ToFloat(Vec3 v) => new() { X = (float)v.X, Y = (float)v.Y, Z = (float)v.Z };

    public static Vector3Double ToDouble(Vec3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    public static EulerAngles ToEuler(Vec3 psiThetaPhi) =>
        new() { Psi = (float)psiThetaPhi.X, Theta = (float)psiThetaPhi.Y, Phi = (float)psiThetaPhi.Z };

    public static bool IsFinite(Vector3Float v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public static bool IsFinite(Vector3Double v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    public static bool IsFinite(EulerAngles a) => float.IsFinite(a.Psi) && float.IsFinite(a.Theta) && float.IsFinite(a.Phi);

    // ---- appearance ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The appearance word of an Entity State PDU for a damage state (0 none .. 3 destroyed) and the deactivated flag. The platform /
    /// lifeform layout of <see cref="EntityAppearance"/> is used for every entity kind (the other bits stay 0); a PoC simplification
    /// recorded in DD-006 section 14.
    /// </summary>
    public static uint AppearanceOf(int damage, bool deactivated)
    {
        uint appearance = EntityAppearance.SetDamage(0u, (DamageState)Math.Clamp(damage, 0, 3));
        return EntityAppearance.SetDeactivated(appearance, deactivated);
    }

    /// <summary>The damage state (0 .. 3) in an appearance word.</summary>
    public static int DamageOf(uint appearance) => (int)EntityAppearance.GetDamage(appearance);

    /// <summary>
    /// A dead-reckoning algorithm the gateway can predict with: 1 .. 9. "Other" (0) and unknown numbers have no predictor and are
    /// read as Static, so a replica never moves on a guess.
    /// </summary>
    public static int NormalizeAlgorithm(int algorithm) => algorithm is >= 1 and <= 9 ? algorithm : DrAlgorithms.Static;

    /// <summary>A DIS clock time (hour and time past the hour in the DIS timestamp encoding) of a time in seconds.</summary>
    public static ClockTime ClockOf(double seconds)
    {
        double safe = double.IsFinite(seconds) && seconds >= 0.0 ? seconds : 0.0;
        return new ClockTime { Hour = (uint)(safe / 3600.0), TimePastHour = DisTimestamp.EncodeAbsolute(safe) };
    }
}
