using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Maps a gas or smoke cloud (DD-033) to its state topic: one <c>Cloud</c> instance per cloud, written when the host sends it (it appears, or its drift, size, kind or transmittance changed) and
/// disposed when the host says it ended. The cloud is no entity: it has no <c>Entity</c> sample. One instance per gateway runtime.
/// </summary>
public sealed class DpsCloudOutbound : IGatewayModule
{
    private readonly DpsPorts _ports;
    private readonly HashSet<int> _written = new();

    internal DpsCloudOutbound(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsCloudOutbound);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotCloud>(OnCloud);
        setup.OnMessage<HrotCloudEnded>(OnEnded);
    }

    private void OnCloud(in HrotCloud m, IngressContext ctx)
    {
        _written.Add(m.Id);
        _ports.Cloud.Send(new DpsItem<D.Cloud>(new D.Cloud
        {
            CloudId = m.Id,
            Kind = (D.CloudKind)(byte)m.Kind,   // the pivot enum mirrors the contract's in order (a rail pins it)
            SimTime = m.SimTime,
            Centre = new D.Vec3d { X = m.Centre.X, Y = m.Centre.Y, Z = m.Centre.Z },
            Drift = new D.Vec3f { X = (float)m.Drift.X, Y = (float)m.Drift.Y, Z = (float)m.Drift.Z },
            Radius = m.Radius,
            Height = m.Height,
            SightTransmittance = m.SightTransmittance,
            BornAt = m.BornAt,
            Seconds = m.Seconds,
        }));
    }

    private void OnEnded(in HrotCloudEnded m, IngressContext ctx)
    {
        if (_written.Remove(m.Id))
        {
            _ports.Cloud.Send(new DpsItem<D.Cloud>(new D.Cloud { CloudId = m.Id }, true));
        }
    }
}
