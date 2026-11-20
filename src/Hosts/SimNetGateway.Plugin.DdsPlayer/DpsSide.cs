using System.Text.Json;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The dps side (DD-017): the "dps" section, the outbound ports of the published topics (no inbound port and no origin: it introduces no entity into the pivot)
/// and five modules, <see cref="DpsOutbound"/>, <see cref="DpsRoundFlameOutbound"/>, <see cref="DpsHudOutbound"/>, <see cref="DpsTypeCatalog"/> and <see cref="DpsBuildingOutbound"/>, that map the messages HROT sends to DPS samples.
/// </summary>
public sealed class DpsSide : IProtocolSide
{
    /// <summary>"dps".</summary>
    public const string SideKey = "dps";

    /// <inheritdoc />
    public string Key => SideKey;

    /// <inheritdoc />
    public object? Parse(ConfigSection section)
    {
        if (section is null)
        {
            return null;
        }

        var d = new DpsSettings();
        var result = new DpsSettings
        {
            Enabled = section.Boolean("enabled", d.Enabled),
            Domain = (int)section.Integer("domain", d.Domain, 0, 232),
            Partition = section.String("partition", d.Partition),
            LeaseSeconds = section.Number("leaseSeconds", d.LeaseSeconds, min: 0, minExclusive: true),
            HeartbeatSeconds = section.Number("heartbeatSeconds", d.HeartbeatSeconds, min: 0, minExclusive: true),
            PositionThresholdM = section.Number("positionThresholdM", d.PositionThresholdM, min: 0),
            AngleThresholdDeg = section.Number("angleThresholdDeg", d.AngleThresholdDeg, min: 0),
            VelocityThreshold = section.Number("velocityThreshold", d.VelocityThreshold, min: 0),
        };
        section.Finish();
        return result;
    }

    /// <inheritdoc />
    public void Write(Utf8JsonWriter w, object settings)
    {
        var s = (DpsSettings)settings;
        w.WriteBoolean("enabled", s.Enabled);
        w.WriteNumber("domain", s.Domain);
        w.WriteString("partition", s.Partition);
        w.WriteNumber("leaseSeconds", s.LeaseSeconds);
        w.WriteNumber("heartbeatSeconds", s.HeartbeatSeconds);
        w.WriteNumber("positionThresholdM", s.PositionThresholdM);
        w.WriteNumber("angleThresholdDeg", s.AngleThresholdDeg);
        w.WriteNumber("velocityThreshold", s.VelocityThreshold);
    }

    /// <inheritdoc />
    public bool Runs(object settings) => ((DpsSettings)settings).Enabled;

    /// <inheritdoc />
    public void Declare(SideDeclaration d, object settings) => DpsPorts.Require(d.Builder);

    /// <inheritdoc />
    public void Register(SideContext c, object settings)
    {
        var ports = DpsPorts.Require(c.Builder);
        c.Builder.AddModule(new DpsOutbound((DpsSettings)settings, ports));
        c.Builder.AddModule(new DpsRoundFlameOutbound(ports));
        c.Builder.AddModule(new DpsHudOutbound(ports));
        c.Builder.AddModule(new DpsTypeCatalog(ports));
        c.Builder.AddModule(new DpsBuildingOutbound(ports));
    }
}
