using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>Rule "Detonation": a Detonation event of another source as RprMunitionDetonation (the damage goes out through RprEntityEgress).</summary>
public sealed class RprDetonationEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly TypeCatalog _catalog;
    private readonly SourceId _rpr;
    private readonly IOutboundChannel<IRprMessage> _output;
    private IGatewayContext _gateway = null!;

    public RprDetonationEgress(PivotSchema schema, NedRprMaps maps, TypeCatalog catalog, NedRprSources sources,
        IOutboundChannel<IRprMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _catalog = catalog;
        _rpr = sources.Rpr;
        _output = output;
    }

    public string Name => nameof(RprDetonationEgress);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        setup.Consumes(_schema.EventId, _schema.EventTarget, _schema.EventMunitionTkb, _schema.EventLocation, _schema.EventResult);
        setup.OnEvent(_schema.Detonation)
            .Named("Detonation")
            .When(t => t.Source != _rpr)
            .Do(OnDetonation);
    }

    private void OnDetonation(EventTrigger t)
    {
        var payload = t.Payload;
        var id = payload.TryGet(_schema.EventId, out var eventId) ? eventId.AsObject<Guid>() : Guid.Empty;
        var location = payload.TryGet(_schema.EventLocation, out var l) ? NedRprConversions.ToWorldLocation(l.AsVec3()) : default;
        var result = RprDetonationResult.Other;
        if (payload.TryGet(_schema.EventResult, out var r) && NedRprConversions.TryResultToRpr((int)r.AsInt(), out var mapped))
        {
            result = mapped;
        }

        var munition = RprEventWire.Munition(_schema, _catalog, _gateway, Name, payload, useSelected: false);
        _output.Send(new RprMunitionDetonation(
            id,
            RprEventWire.HandleOf(_maps, payload, _schema.EventShooter),
            RprEventWire.HandleOf(_maps, payload, _schema.EventTarget),
            location,
            munition,
            result));
    }
}
