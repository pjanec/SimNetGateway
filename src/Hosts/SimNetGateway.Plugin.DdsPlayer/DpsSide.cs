using System.Text.Json;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The dps side (DD-017): the "dps" section, the outbound ports of the published topics (no inbound port and no origin: it introduces no entity into the pivot)
/// and fifteen modules, <see cref="DpsOutbound"/>, <see cref="DpsRoundFlameOutbound"/>, <see cref="DpsHudOutbound"/>, <see cref="DpsTypeCatalog"/>, <see cref="DpsBuildingOutbound"/>, <see cref="DpsGroundOutbound"/>, <see cref="DpsCarriedOutbound"/>, <see cref="DpsCloudOutbound"/>, <see cref="DpsTrafficOutbound"/>, <see cref="DpsViewOutbound"/>, <see cref="DpsStatusOutbound"/>, <see cref="DpsVoiceOutbound"/>, <see cref="DpsAmbienceOutbound"/>, <see cref="DpsWeatherOutbound"/> and <see cref="DpsClockModule"/>, that map the messages HROT sends to DPS samples.
/// </summary>
public sealed class DpsSide : IProtocolSide
{
    /// <summary>"dps".</summary>
    public const string SideKey = "dps";

    /// <summary>The host's log, set when the plugin binds (the modules log through it; before that nothing is written).</summary>
    internal Action<string> Log { get; set; } = _ => { };

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
            Seats = (int)section.Integer("seats", d.Seats, 1, DpsSettings.MaxSeats),
            RenderSeat = (int)section.Integer("renderSeat", d.RenderSeat, 1, DpsSettings.MaxSeats),
            RenderRoot = section.String("renderRoot", d.RenderRoot),
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
        w.WriteNumber("seats", s.Seats);
        w.WriteNumber("renderSeat", s.RenderSeat);
        w.WriteString("renderRoot", s.RenderRoot);
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
        c.Builder.AddModule(new DpsBuildingOutbound(ports, () => Log));
        c.Builder.AddModule(new DpsGroundOutbound(ports, () => Log));
        c.Builder.AddModule(new DpsCarriedOutbound(ports));
        c.Builder.AddModule(new DpsCloudOutbound(ports));
        c.Builder.AddModule(new DpsTrafficOutbound(ports));
        c.Builder.AddModule(new DpsViewOutbound((DpsSettings)settings, ports));
        c.Builder.AddModule(new DpsStatusOutbound(ports));
        c.Builder.AddModule(new DpsVoiceOutbound(ports));
        c.Builder.AddModule(new DpsAmbienceOutbound(ports));
        c.Builder.AddModule(new DpsWeatherOutbound(ports));
        c.Builder.AddModule(new DpsClockModule(ports));
    }
}
