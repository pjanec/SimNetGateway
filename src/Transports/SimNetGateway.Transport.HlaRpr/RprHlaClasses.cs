using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Transport.HlaRpr;

/// <summary>The object class an entity is registered as, from its DIS entity type (DD-008 section 3.5).</summary>
public static class RprHlaClasses
{
    /// <summary>DIS entity kinds.</summary>
    private const byte KindPlatform = 1;
    private const byte KindMunition = 2;
    private const byte KindLifeform = 3;

    /// <summary>The classes an entity can be registered as, most specific first; all are published.</summary>
    public static readonly IReadOnlyList<string> Published =
    [
        RprFom2.ObjectClasses.GroundVehicle,
        RprFom2.ObjectClasses.Aircraft,
        RprFom2.ObjectClasses.SurfaceVessel,
        RprFom2.ObjectClasses.SubmersibleVessel,
        RprFom2.ObjectClasses.Spacecraft,
        RprFom2.ObjectClasses.Platform,
        RprFom2.ObjectClasses.Human,
        RprFom2.ObjectClasses.Munition,
        RprFom2.ObjectClasses.PhysicalEntity,
    ];

    /// <summary>Fully qualified FOM class name for an entity type: platform by domain (land, air, surface, subsurface, space, else Platform), lifeform = Human, munition = Munition, anything else PhysicalEntity.</summary>
    public static string NameFor(DisEntityType type) => type.Kind switch
    {
        KindPlatform => type.Domain switch
        {
            1 => RprFom2.ObjectClasses.GroundVehicle,
            2 => RprFom2.ObjectClasses.Aircraft,
            3 => RprFom2.ObjectClasses.SurfaceVessel,
            4 => RprFom2.ObjectClasses.SubmersibleVessel,
            5 => RprFom2.ObjectClasses.Spacecraft,
            _ => RprFom2.ObjectClasses.Platform,
        },
        KindLifeform => RprFom2.ObjectClasses.Human,
        KindMunition => RprFom2.ObjectClasses.Munition,
        _ => RprFom2.ObjectClasses.PhysicalEntity,
    };
}
