using System.Net;
using System.Text.Json;
using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// The DIS side (DD-011 section 3a): the "dis" section, the ports "dis" (inbound, outbound <see cref="Pdu"/>), the shared entity numbers
/// and the modules of H2b: ingress of Entity State, Fire, Detonation, Remove Entity, Start/Resume and Stop/Freeze PDUs, egress of Entity
/// State, Fire, Detonation and the simulation management PDUs, and the stale reaper.
/// </summary>
public sealed class DisSide : IProtocolSide
{
    /// <summary>"dis".</summary>
    public const string SideKey = "dis";

    /// <inheritdoc />
    public string Key => SideKey;

    /// <inheritdoc />
    public object? Parse(ConfigSection section) => ReadDis(section);

    /// <inheritdoc />
    public void Write(Utf8JsonWriter w, object settings)
    {
        var dis = (DisConfiguration)settings;
        w.WriteBoolean("enabled", dis.Enabled);
        w.WriteNumber("exerciseId", dis.ExerciseId);
        w.WriteNumber("port", dis.Port);
        w.WriteString("address", dis.Address);
        w.WriteNumber("positionThresholdM", dis.PositionThresholdM);
        w.WriteNumber("orientationThresholdDeg", dis.OrientationThresholdDeg);
        w.WriteNumber("heartbeatS", dis.HeartbeatS);
        w.WriteNumber("timeoutS", dis.TimeoutS);
        w.WriteBoolean("hrotControlsDis", dis.HrotControlsDis);
        w.WriteBoolean("disControlsHrot", dis.DisControlsHrot);
    }

    /// <inheritdoc />
    public bool Runs(object settings) => ((DisConfiguration)settings).Enabled;

    /// <inheritdoc />
    public void Declare(SideDeclaration d, object settings)
    {
        d.Origin = d.Builder.RequireInbound("dis").Source;
        d.Builder.RequireOutbound<Pdu>("dis");
        d.UsesEntityNumbers = true;
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
        var disOut = builder.RequireOutbound<Pdu>("dis");

        // DIS ingress
        builder.AddModule(new DisEntityStateIngress(schema, maps, sources, services));
        builder.AddModule(new DisFireIngress(schema, maps, services));
        builder.AddModule(new DisDetonationIngress(schema, maps, services));
        builder.AddModule(new DisRemoveEntityIngress(maps, sources, services));
        builder.AddModule(new DisStartResumeIngress(services));
        builder.AddModule(new DisStopFreezeIngress(services));

        // DIS egress (the entity rules send before the heartbeat rule looks at the same tick)
        var publication = new DisPublication(disOut, schema, maps, sources, services);
        builder.AddModule(new DisEntityStateEgress(schema, publication, sources));
        builder.AddModule(new DisHeartbeatEgress(publication));
        builder.AddModule(new DisFireEgress(schema, maps, sources, services, disOut));
        builder.AddModule(new DisDetonationEgress(schema, maps, sources, services, disOut));
        builder.AddModule(new DisSessionEgress(sources, services, disOut));

        // Processors
        var timeout = TimeSpan.FromSeconds(config.Dis!.TimeoutS);
        var period = TimeSpan.FromSeconds(Math.Min(HrotDisProfile.ReaperCheckPeriod.TotalSeconds, config.Dis.TimeoutS / 4.0));
        builder.AddModule(new StaleEntityReaper("DisStaleReaper", sources.Dis, timeout, period, builder.Sources.Register("proc.reaper.dis")));
    }

    private static DisConfiguration? ReadDis(ConfigSection? s)
    {
        if (s is null)
        {
            return null;
        }

        var d = new DisConfiguration { ExerciseId = 0 };
        bool enabled = s.Boolean("enabled", d.Enabled);
        int exercise = (int)s.RequiredInteger("exerciseId", 0, 255);
        int port = (int)s.Integer("port", d.Port, 1, 65535);
        string address = s.String("address", d.Address);
        if (!string.IsNullOrEmpty(address) && !IPAddress.TryParse(address, out _))
        {
            s.Error("address", $"must be an IP address, got \"{address}\"");
        }

        var result = new DisConfiguration
        {
            Enabled = enabled,
            ExerciseId = exercise,
            Port = port,
            Address = address,
            PositionThresholdM = s.Number("positionThresholdM", d.PositionThresholdM, min: 0),
            OrientationThresholdDeg = s.Number("orientationThresholdDeg", d.OrientationThresholdDeg, min: 0),
            HeartbeatS = s.Number("heartbeatS", d.HeartbeatS, min: 0, minExclusive: true),
            TimeoutS = s.Number("timeoutS", d.TimeoutS, min: 0, minExclusive: true),
            HrotControlsDis = s.Boolean("hrotControlsDis", d.HrotControlsDis),
            DisControlsHrot = s.Boolean("disControlsHrot", d.DisControlsHrot),
        };
        s.Finish();
        return result;
    }}
