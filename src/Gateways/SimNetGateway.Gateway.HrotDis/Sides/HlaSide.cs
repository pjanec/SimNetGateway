using System.Text.Json;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// The HLA side (DD-009, DD-011 section 3a): the "hla" section, the ports "rpr" (inbound, outbound <see cref="IRprMessage"/>), the
/// shared entity numbers (one number per HROT entity on DIS and HLA) and the modules: ingress of object updates, removals, registration
/// answers, update requests, fire and detonation; egress of entities, fire and detonation; its own stale reaper. The RTI binding is the host's.
/// </summary>
public sealed class HlaSide : IProtocolSide
{
    /// <summary>"hla".</summary>
    public const string SideKey = "hla";

    /// <inheritdoc />
    public string Key => SideKey;

    /// <inheritdoc />
    public object? Parse(ConfigSection section) => ReadHla(section);

    /// <inheritdoc />
    public void Write(Utf8JsonWriter w, object settings)
    {
        var hla = (HlaConfiguration)settings;
        w.WriteBoolean("enabled", hla.Enabled);
        w.WriteString("rti", hla.Rti);
        w.WriteString("federation", hla.Federation);
        w.WriteString("federateName", hla.FederateName);
        w.WriteString("federateType", hla.FederateType);
        w.WriteStartArray("fomModules");
        foreach (var module in hla.FomModules)
        {
            w.WriteStringValue(module);
        }

        w.WriteEndArray();
        w.WriteBoolean("createFederation", hla.CreateFederation);
        w.WriteNumber("positionThresholdM", hla.PositionThresholdM);
        w.WriteNumber("orientationThresholdDeg", hla.OrientationThresholdDeg);
        w.WriteNumber("heartbeatS", hla.HeartbeatS);
        w.WriteNumber("timeoutS", hla.TimeoutS);
    }

    /// <inheritdoc />
    public bool Runs(object settings) => ((HlaConfiguration)settings).Enabled;

    /// <inheritdoc />
    public void Declare(SideDeclaration d, object settings)
    {
        d.Origin = d.Builder.RequireInbound("rpr").Source;
        d.Builder.RequireOutbound<IRprMessage>("rpr");
        // one entity number per hrot entity for both sides: the dis.entity map and pool serve DIS and HLA
        d.UsesEntityNumbers = true;
        d.IsRprSide = true;
    }

    /// <inheritdoc />
    public void Register(SideContext c, object settings)
    {
        var builder = c.Builder;
        var config = c.Config;
        var schema = c.Schema;
        var maps = c.Maps;
        var sources = c.Sources;
        var services = c.Services;
        var rprOut = builder.RequireOutbound<IRprMessage>("rpr");

        // RPR ingress (HLA -> pivot, origin rpr)
        var publication = new RprPublication(rprOut, schema, maps, sources, services);
        builder.AddModule(new RprObjectUpdateIngress(schema, maps));
        builder.AddModule(new RprObjectRemovedIngress(maps));
        builder.AddModule(new RprObjectRegisteredIngress(maps, publication));
        builder.AddModule(new RprUpdateRequestedResponder(maps, publication));
        builder.AddModule(new RprFireIngress(schema, maps));
        builder.AddModule(new RprDetonationIngress(schema, maps));

        // RPR egress (the entity rules register before the sweep looks at the same tick)
        builder.AddModule(new RprEntityEgress(schema, publication, sources));
        builder.AddModule(new RprFireEgress(schema, maps, sources, services, rprOut));
        builder.AddModule(new RprDetonationEgress(schema, maps, sources, services, rprOut));

        // Processors
        var timeout = TimeSpan.FromSeconds(config.Hla!.TimeoutS);
        var period = TimeSpan.FromSeconds(Math.Min(HrotDisProfile.ReaperCheckPeriod.TotalSeconds, config.Hla.TimeoutS / 4.0));
        builder.AddModule(new StaleEntityReaper("RprStaleReaper", sources.Rpr, timeout, period, builder.Sources.Register("proc.reaper.rpr")));
    }

    private static HlaConfiguration? ReadHla(ConfigSection? s)
    {
        if (s is null)
        {
            return null;
        }

        var d = new HlaConfiguration { Federation = "" };
        var result = new HlaConfiguration
        {
            Enabled = s.Boolean("enabled", d.Enabled),
            Rti = s.NonEmptyString("rti", d.Rti),
            Federation = s.RequiredNonEmptyString("federation"),
            FederateName = s.NonEmptyString("federateName", d.FederateName),
            FederateType = s.NonEmptyString("federateType", d.FederateType),
            FomModules = s.StringList("fomModules", d.FomModules),
            CreateFederation = s.Boolean("createFederation", d.CreateFederation),
            PositionThresholdM = s.Number("positionThresholdM", d.PositionThresholdM, min: 0),
            OrientationThresholdDeg = s.Number("orientationThresholdDeg", d.OrientationThresholdDeg, min: 0),
            HeartbeatS = s.Number("heartbeatS", d.HeartbeatS, min: 0, minExclusive: true),
            TimeoutS = s.Number("timeoutS", d.TimeoutS, min: 0, minExclusive: true),
        };
        s.Finish();
        return result;
    }}
