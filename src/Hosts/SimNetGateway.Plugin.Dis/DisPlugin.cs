using DISnet;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Transport.DisUdp;

namespace SimNetGateway.Plugin.Dis;

/// <summary>The DIS side as a plugin: binds the ports "dis" to a UDP transport. The transport is opened through <see cref="Open"/> (a seam for tests, a real socket by default).</summary>
public sealed class DisPlugin : IGatewayPlugin
{
    public string Name => DisSide.SideKey;

    public int InterfaceVersion => PluginInterface.Version;

    public IProtocolSide Side { get; } = new DisSide();

    /// <summary>Opens the transport for the settings and the source of the inbound port "dis".</summary>
    public Func<DisUdpSettings, SourceId, DisUdpTransport> Open { get; init; } = (settings, source) => DisUdpTransport.Open(settings, source);

    public string? Check(object settings, PluginHostContext context) =>
        settings is DisConfiguration { DisControlsHrot: true }
            ? "'dis.disControlsHrot' is refused - HROT exposes no time-command seam to extensions in this build (use hrotControlsDis)"
            : null;

    public PluginBound Bind(SideBinding binding)
    {
        var dis = (DisConfiguration)binding.Settings;
        var config = binding.Config;
        var settings = new DisUdpSettings
        {
            Address = dis.Address,
            Port = dis.Port,
            ExerciseId = dis.ExerciseId,
            OwnSite = config.Shared.Site,
            OwnApplication = config.Shared.Application,
        };
        var builder = binding.Builder;
        var transport = Open(settings, builder.RequireInbound("dis").Source);
        try
        {
            builder.BindInbound("dis", transport);
            builder.BindOutbound<Pdu>("dis", transport);
        }
        catch
        {
            transport.Dispose();
            throw;
        }

        return new PluginBound(transport);
    }
}
