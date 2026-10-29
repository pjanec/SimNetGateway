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
    /// <summary>How often a held RPR event (R-047) is checked for its timeout.</summary>
    public static readonly TimeSpan HoldReleasePeriod = TimeSpan.FromMilliseconds(100);

    /// <summary>How often a split fire that waits for its ammunition part is checked for its timeout.</summary>
    public static readonly TimeSpan FireAssemblyCheckPeriod = TimeSpan.FromMilliseconds(100);

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
            builder.Sources.Register("proc.reaper"),
            builder.Sources.Register("proc.aggregate"),
            builder.Sources.Register("proc.fire"));
        var nedOut = builder.RequireOutbound<INedMessage>("ned");
        var rprOut = builder.RequireOutbound<IRprMessage>("rpr");
        var displayOut = builder.RequireOutbound<IDisplayMessage>("display");

        var maps = NedRprMaps.Create(builder.Identities, sources, settings, builder.Clock);
        var catalog = new TypeCatalog();
        var predictors = PredictorRegistry.CreateDis();
        var publication = new RprPublication(rprOut, schema, maps, catalog, predictors);
        var hold = new RprEventHold(maps, publication, rprOut, settings.EventHoldTimeout);

        // NED ingress
        builder.AddModule(new NedEntityMasterIngress(schema, maps));
        builder.AddModule(new NedGeoSpatialIngress(schema, maps));
        builder.AddModule(new NedDamageIngress(schema, maps));
        builder.AddModule(new NedWeaponStatusIngress(schema, maps));
        builder.AddModule(new NedDisposeIngress(maps));
        builder.AddModule(new NedFormationIngress(schema, maps));
        if (settings.SplitFireAssembly)
        {
            builder.AddModule(new NedSplitFireIngress(schema, maps));
        }
        else
        {
            builder.AddModule(new NedWeaponFireIngress(schema, maps));
        }

        builder.AddModule(new NedDetonationIngress(schema, maps));
        builder.AddModule(new NedControlIngress());

        // RPR ingress
        builder.AddModule(new RprObjectUpdateIngress(schema, maps, catalog));
        builder.AddModule(new RprAggregateEntityIngress(schema, maps, catalog));
        builder.AddModule(new RprObjectRemovedIngress(maps));
        builder.AddModule(new RprWeaponFireIngress(schema, maps, catalog));
        builder.AddModule(new RprObjectRegisteredIngress(maps, publication));
        builder.AddModule(new RprUpdateRequestedResponder(maps, publication));

        // NED egress (the identity rule introduces an entity before the spatial and damage rules look for its id)
        var introductions = new NedIntroductions();
        builder.AddModule(new NedIdentityEgress(schema, maps, sources, introductions, nedOut));
        builder.AddModule(new NedSpatialEgress(schema, maps, sources, introductions, nedOut));
        builder.AddModule(new NedDamageEgress(schema, maps, sources, introductions, nedOut));
        builder.AddModule(new NedDamageNotificationEgress(schema, maps, sources, introductions, nedOut));
        builder.AddModule(new NedLifecycleEgress(schema, maps, sources, nedOut));
        builder.AddModule(new NedFormationEgress(schema, maps, sources, nedOut));
        builder.AddModule(new NedWeaponFireEgress(schema, maps, sources, nedOut));

        // RPR egress
        builder.AddModule(new RprEntityEgress(schema, publication, sources));
        builder.AddModule(new CustomerDamagePercent(schema, publication, sources));
        builder.AddModule(new RprHeartbeatEgress(publication, settings.HeartbeatPeriod, settings.HeartbeatCheckPeriod));
        builder.AddModule(new RprWeaponFireEgress(schema, maps, catalog, sources, rprOut, hold, HoldReleasePeriod));
        builder.AddModule(new RprDetonationEgress(schema, maps, catalog, sources, rprOut, hold, HoldReleasePeriod));
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
        builder.AddModule(new RprAggregateLinker(schema, maps, sources));
        if (settings.SplitFireAssembly)
        {
            builder.AddModule(new FireAssemblerProcessor(schema, sources, settings.FireAssemblyTimeout, FireAssemblyCheckPeriod));
        }
    }
}

/// <summary>The named configurations of the NED-RPR gateway (R-052, R-054): static registries in code, no files.</summary>
public static class NedRprConfigurations
{
    /// <summary>The PoC gateway with its default settings.</summary>
    public static GatewayConfiguration Default { get; } = new("ned-rpr/default", new NedRprProfile(), new NedRprSettings());

    /// <summary>
    /// The first NED id of the gateway's range and the size of the range in the showcase: 6 ids, of which exactly 4 are free
    /// when act 7's reinforcements arrive (the RPR-origin entities that the script has published to NED by then use the other two).
    /// </summary>
    public static NedIdRange ShowcaseNedIdRange { get; } = new(1_000_000, 1_000_005);

    /// <summary>
    /// The showcase "Convoy Ambush" (DD-002): the default gateway with split fire assembly (C8), damage notifications to NED
    /// (C6, the rule is enabled), the small NED id range of act 7 and a short DIS quarantine.
    /// </summary>
    public static GatewayConfiguration Showcase { get; } = new("ned-rpr/showcase", new NedRprProfile(), new NedRprSettings
    {
        SplitFireAssembly = true,
        NedIdRange = ShowcaseNedIdRange,
        DisQuarantineSeconds = 10,
        EnabledRules = ["NedDamageNotificationEgress/Notify"],
    });

    /// <summary>Every configuration of this gateway.</summary>
    public static IReadOnlyList<GatewayConfiguration> All { get; } = new[] { Default, Showcase };
}
