using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using SimNetGateway.Protocol.Rpr;
using EngineConfiguration = SimNetGateway.Engine.GatewayConfiguration;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>Settings of the HROT DIS gateway: the configuration file's content (R-094) plus the operator rule overrides of the base.</summary>
public sealed record HrotDisSettings(GatewayConfiguration Config) : GatewaySettings;

/// <summary>
/// The HROT DIS gateway (R-051, DD-006 section 0, DD-007, DD-009): declares the ports "hrot" (inbound, one multi-type channel of
/// <see cref="IHrotToGatewayMessage"/>; outbound <see cref="IGatewayToHrotMessage"/>), when the DIS side runs "dis"
/// (inbound, outbound <see cref="Pdu"/>) and when the HLA side runs "rpr" (inbound, outbound <see cref="IRprMessage"/>), and registers the pivot schema, the sources, the identity map, the services and the
/// modules of the HROT side and, when the DIS side runs, of the DIS side (H2b: ingress of Entity State, Fire, Detonation, Remove
/// Entity, Start/Resume and Stop/Freeze PDUs, egress of Entity State, Fire, Detonation and the simulation management PDUs, and the
/// stale reaper) and, when the HLA side runs, of the HLA side (DD-009: ingress of object updates, removals, registration answers,
/// update requests, fire and detonation; egress of entities, fire and detonation; its own stale reaper). Module order is the order
/// in which same-trigger rules fire.
/// </summary>
public sealed class HrotDisProfile : IGatewayProfile
{
    private readonly IIdAllocator<long>? _hrotIds;
    private readonly DisNumbering _numbering;
    private readonly SideRegistry _sides;

    /// <param name="hrotIds">
    /// The HROT entity id allocator port (DD-007 section 3 item 3): the pack passes an allocator over
    /// <c>HrotNodeContext.IdAllocator</c>; null = a built-in range (<see cref="DefaultIdFirst"/> .. <see cref="DefaultIdLast"/>).
    /// </param>
    /// <param name="numbering">The range and quarantine of the DIS entity numbers the gateway hands out; null = 1 .. 65534, 60 s.</param>
    /// <param name="sides">The sides the gateway can run; null = <see cref="SideRegistry.Builtin"/> (DIS and HLA).</param>
    public HrotDisProfile(IIdAllocator<long>? hrotIds = null, DisNumbering? numbering = null, SideRegistry? sides = null)
    {
        _sides = sides ?? SideRegistry.Builtin;
        _hrotIds = hrotIds;
        _numbering = numbering ?? new DisNumbering();
        _numbering.Validate();
    }

    /// <summary>How often the stale reaper looks for silent DIS entities, at most (a quarter of the timeout for short timeouts).</summary>
    public static readonly TimeSpan ReaperCheckPeriod = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The services of the gateway this profile configured last (null before <see cref="Configure"/>): for hosts, diagnostics and tests
    /// that read what the DIS ingress ignored. A profile configures one gateway; the registry's static default must not be used for this.
    /// </summary>
    public HrotDisServices? Services { get; private set; }

    /// <summary>First id of the built-in allocator range.</summary>
    public const long DefaultIdFirst = 1_000_000;

    /// <summary>Last id of the built-in allocator range.</summary>
    public const long DefaultIdLast = 1_999_999;

    public string Name => "hrot-dis";

    public void Configure(GatewayBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var config = builder.SettingsAs<HrotDisSettings>().Config;
        if (!config.AnySideEnabledIn(_sides))
        {
            throw new InvalidOperationException("No protocol side is enabled in the gateway configuration: the gateway is not instantiated (DD-007 section 3a).");
        }

        var schema = new PivotSchema(builder.Attributes, builder.EventTypes);
        var hrotPort = builder.RequireInbound("hrot");
        var disSource = builder.Sources.Register("dis");

        // phase 1: every running side declares (registry order): ports, origin, entity numbers, maps
        var running = new List<(IProtocolSide Side, object Settings, SideDeclaration Declaration)>();
        foreach (var side in _sides.Sides)
        {
            if (config.SettingsOf(side.Key) is { } settings && side.Runs(settings))
            {
                var declaration = new SideDeclaration(builder, side.Key);
                side.Declare(declaration, settings);
                running.Add((side, settings, declaration));
            }
        }

        var rprSource = SourceId.None;
        var otherOrigins = new List<SourceId>();
        foreach (var (_, _, declaration) in running)
        {
            if (declaration.IsRprSide)
            {
                rprSource = declaration.Origin;
            }
            else if (declaration.Origin != SourceId.None && declaration.Origin != disSource && !otherOrigins.Contains(declaration.Origin))
            {
                otherOrigins.Add(declaration.Origin);
            }
        }

        var sources = new HrotDisSources(hrotPort.Source, disSource, rprSource) { Others = otherOrigins.ToArray() };

        var hrotOut = builder.RequireOutbound<IGatewayToHrotMessage>("hrot");
        // one entity number per hrot entity for both sides: the dis.entity map and pool serve DIS and HLA (an instantiated gateway runs at least one)
        var disIdentities = running.Any(r => r.Declaration.UsesEntityNumbers)
            ? new DisIdentityOptions(builder.Clock, (ushort)config.Shared.Site, (ushort)config.Shared.Application, _numbering)
            : null;
        var maps = HrotDisMaps.Create(builder.Identities, sources, _hrotIds ?? new HrotIdRangeAllocator(DefaultIdFirst, DefaultIdLast),
            disIdentities, rprSource != SourceId.None);
        foreach (var (_, _, declaration) in running)
        {
            foreach (var request in declaration.MapRequests)
            {
                request(builder.Identities);
            }
        }

        var services = new HrotDisServices(config);
        Services = services;
        var publisher = new ReplicaPublisher(schema, maps, sources, services, hrotOut, builder.Engine);

        // HROT ingress
        builder.AddModule(new HrotOriginIngress(schema, services));
        builder.AddModule(new HrotSessionIngress());
        builder.AddModule(new HrotEntityStateIngress(schema, maps, services));
        builder.AddModule(new HrotEntityRemovedIngress(maps));
        builder.AddModule(new HrotTurretPoseIngress(schema, maps));
        builder.AddModule(new HrotFireIngress(schema, maps, services));
        builder.AddModule(new HrotDetonationIngress(schema, maps, services));

        // HROT egress
        builder.AddModule(new ReplicaStateEgress(schema, sources, publisher));
        builder.AddModule(new ReplicaRemovedEgress(sources, publisher));
        builder.AddModule(new InjectFireEgress(schema, maps, sources, services, hrotOut));
        builder.AddModule(new InjectDetonationEgress(schema, maps, sources, services, hrotOut));
        builder.AddModule(new TimeRequestEgress(sources, services, hrotOut));

        // phase 2: every running side registers its modules (registry order)
        var context = new SideContext(builder, config, schema, maps, sources, services, hrotOut);
        foreach (var (side, settings, _) in running)
        {
            side.Register(context, settings);
        }
    }
}

/// <summary>The named configurations of the HROT DIS gateway (R-052): static registries in code that build the profile from a <see cref="GatewayConfiguration"/>.</summary>
public static class HrotDisConfigurations
{
    /// <summary>All defaults with a DIS side for exercise 1 and the built-in HROT id range.</summary>
    public static EngineConfiguration Default { get; } = For(GatewayConfiguration.CreateDefault(), "hrot-dis/default");

    /// <summary>The profile for a configuration read from the file; <paramref name="hrotIds"/> is the HROT id allocator port, <paramref name="numbering"/> the DIS entity numbers, <paramref name="sides"/> the sides that can run (null = built-in DIS and HLA).</summary>
    public static EngineConfiguration For(GatewayConfiguration config, string name = "hrot-dis/file", IIdAllocator<long>? hrotIds = null,
        DisNumbering? numbering = null, SideRegistry? sides = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new EngineConfiguration(name, new HrotDisProfile(hrotIds, numbering, sides), new HrotDisSettings(config));
    }

    /// <summary>Every fixed configuration of this gateway.</summary>
    public static IReadOnlyList<EngineConfiguration> All { get; } = new[] { Default };
}
