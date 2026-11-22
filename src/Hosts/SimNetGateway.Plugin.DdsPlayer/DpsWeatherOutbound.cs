using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Writes <see cref="D.Weather"/> (DD-028) beside <see cref="DpsOutbound"/>: one state instance per weather entity (the global weather has an empty outline, a region its outline), written once on the world
/// partition when the host sends a new or changed one, disposed when the host withdraws it. The world boundary ends the rest with the transport's writers. One instance per gateway runtime.
/// </summary>
public sealed class DpsWeatherOutbound : IGatewayModule
{
    /// <summary>The bound of <see cref="D.Weather.Outline"/>.</summary>
    public const int MaxOutline = 32;

    private readonly DpsPorts _ports;
    private readonly HashSet<int> _written = new();

    internal DpsWeatherOutbound(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsWeatherOutbound);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotWeather>(OnWeather);
        setup.OnMessage<HrotWeatherRemoved>(OnRemoved);
    }

    private void OnWeather(in HrotWeather m, IngressContext ctx)
    {
        var outline = new List<D.Vec3d>();
        if (m.Outline is not null)
        {
            for (int i = 0; i < m.Outline.Length && i < MaxOutline; i++)
            {
                outline.Add(new D.Vec3d { X = m.Outline[i].X, Y = m.Outline[i].Y, Z = m.Outline[i].Z });
            }
        }

        _written.Add(m.Id);
        _ports.Weather.Send(new DpsItem<D.Weather>(new D.Weather
        {
            Id = m.Id,
            Outline = outline,
            FeatherM = m.FeatherM,
            WindFromDeg = m.WindFromDeg,
            WindSpeedMps = m.WindSpeedMps,
            WindGustMps = m.WindGustMps,
            Precipitation = m.Precipitation <= (byte)D.PrecipitationKind.PrecipitationKindHail ? (D.PrecipitationKind)m.Precipitation : D.PrecipitationKind.PrecipitationKindNone,
            PrecipitationMmPerHour = m.PrecipitationMmPerHour,
            VisibilityM = m.VisibilityM,
            FogTopM = m.FogTopM,
            FogVisibilityM = m.FogVisibilityM,
            CloudOctas = m.CloudOctas,
            CloudBaseM = m.CloudBaseM,
            GroundTemperatureC = m.GroundTemperatureC,
            RelativeHumidity = m.RelativeHumidity,
        }));
    }

    private void OnRemoved(in HrotWeatherRemoved m, IngressContext ctx)
    {
        if (_written.Remove(m.Id))
        {
            _ports.Weather.Send(new DpsItem<D.Weather>(new D.Weather { Id = m.Id, Outline = new List<D.Vec3d>() }, true));
        }
    }
}
