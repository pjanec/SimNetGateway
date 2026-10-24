using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// The NED-RPR gateway (R-051): declares the ports "ned" and "rpr" (inbound, one multi-type channel each) and the outbound
/// ports "ned" (<see cref="INedMessage"/>), "rpr" (<see cref="IRprMessage"/>) and "display" (<see cref="IDisplayMessage"/>), and
/// registers the pivot schema, the sources, the identity maps, the services and every module of DD-001 section 9.
/// Module order is the order in which same-trigger rules fire.
/// </summary>
public sealed class NedRprProfile : IGatewayProfile
{
    public string Name => "ned-rpr";

    public void Configure(GatewayBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var settings = builder.SettingsAs<NedRprSettings>();

        var schema = new PivotSchema(builder.Attributes, builder.EventTypes);
        var sources = new NedRprSources(
            builder.RequireInbound("ned").Source,
            builder.RequireInbound("rpr").Source,
            builder.Sources.Register("display"),
            builder.Sources.Register("proc.dr"),
            builder.Sources.Register("proc.reaper"));
        var nedOut = builder.RequireOutbound<INedMessage>("ned");
        var rprOut = builder.RequireOutbound<IRprMessage>("rpr");
        var displayOut = builder.RequireOutbound<IDisplayMessage>("display");

        var maps = NedRprMaps.Create(builder.Identities, sources, settings, builder.Clock);
        var catalog = new TypeCatalog();
        var predictors = PredictorRegistry.CreateDis();
        var publication = new RprPublication(rprOut, schema, maps, catalog, predictors);

        // NED ingress
        builder.AddModule(new NedEntityMasterIngress(schema, maps));
        builder.AddModule(new NedGeoSpatialIngress(schema, maps));
        builder.AddModule(new NedDamageIngress(schema, maps));
        builder.AddModule(new NedWeaponStatusIngress(schema, maps));
        builder.AddModule(new NedDisposeIngress(maps));
        builder.AddModule(new NedWeaponFireIngress(schema, maps));
        builder.AddModule(new NedDetonationIngress(schema, maps));
        builder.AddModule(new NedControlIngress());

        // RPR ingress
        builder.AddModule(new RprObjectUpdateIngress(schema, maps, catalog));
        builder.AddModule(new RprObjectRemovedIngress(maps));
        builder.AddModule(new RprWeaponFireIngress(schema, maps, catalog));
        builder.AddModule(new RprObjectRegisteredIngress(maps, publication));

        // NED egress (the identity rule introduces an entity before the spatial and damage rules look for its id)
        var introductions = new NedIntroductions();
        builder.AddModule(new NedIdentityEgress(schema, maps, sources, introductions, nedOut));
        builder.AddModule(new NedSpatialEgress(schema, maps, sources, introductions, nedOut));
        builder.AddModule(new NedDamageEgress(schema, maps, sources, introductions, nedOut));
        builder.AddModule(new NedLifecycleEgress(maps, sources, nedOut));
        builder.AddModule(new NedWeaponFireEgress(schema, maps, sources, nedOut));

        // RPR egress
        builder.AddModule(new RprEntityEgress(schema, publication, sources));
        builder.AddModule(new RprHeartbeatEgress(publication, settings.HeartbeatPeriod, settings.HeartbeatCheckPeriod));
        builder.AddModule(new RprWeaponFireEgress(schema, maps, catalog, sources, rprOut));
        builder.AddModule(new RprDetonationEgress(schema, maps, catalog, sources, rprOut));
        builder.AddModule(new RprControlEgress(sources, rprOut));

        // Display and processors
        builder.AddModule(new DisplayPositionEgress(schema, maps, displayOut));
        var nameId = schema.Name;
        builder.AddModule(new DrExtrapolationProcessor(
            "DrExtrapolation",
            sources.ProcDr,
            settings.DrProcessorPeriod,
            new DrAttributeSet(schema.Position, schema.Velocity, schema.Acceleration, schema.Orientation,
                schema.AngularVelocity, schema.DrAlgorithm, schema.StateTime),
            schema.PredictedPosition,
            schema.PredictedAt,
            predictors,
            select: entity => entity.Has(nameId), // the entities the display can show
            slices: settings.DrSlices));
        builder.AddModule(new StaleEntityReaper(
            "RprStaleReaper", sources.Rpr, settings.StaleTimeout, settings.ReaperCheckPeriod, sources.ProcReaper));
    }
}

/// <summary>The named configurations of the NED-RPR gateway (R-052, R-054): static registries in code, no files.</summary>
public static class NedRprConfigurations
{
    /// <summary>The PoC gateway with its default settings.</summary>
    public static GatewayConfiguration Default { get; } = new("ned-rpr/default", new NedRprProfile(), new NedRprSettings());

    /// <summary>Every configuration of this gateway (the Showcase variants come in stage 7).</summary>
    public static IReadOnlyList<GatewayConfiguration> All { get; } = new[] { Default };
}
