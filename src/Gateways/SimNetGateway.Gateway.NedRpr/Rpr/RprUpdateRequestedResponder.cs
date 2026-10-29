using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// RprUpdateRequested (R-105, DD-008 section 3.4): another federate asked for the attributes of one of our objects. Ingress
/// cannot send, so the handler notes the entity (one of our own publications; an unknown handle or an RPR-origin entity is
/// ignored) and publishes the internal "update requested" event; rule "Send" answers with the same full update the heartbeat
/// builds (<see cref="RprPublication.SendRequestedUpdates"/>) - no second way to build a full update.
/// </summary>
public sealed class RprUpdateRequestedResponder : IGatewayModule
{
    private readonly NedRprMaps _maps;
    private readonly RprPublication _publication;
    private EventTypeId _requested;

    public RprUpdateRequestedResponder(NedRprMaps maps, RprPublication publication)
    {
        _maps = maps;
        _publication = publication;
    }

    public string Name => nameof(RprUpdateRequestedResponder);

    public void Register(ModuleSetup setup)
    {
        _publication.Attach(setup);
        _requested = _publication.UpdateRequestedEvent;
        setup.Produces(_requested);
        setup.OnMessage<RprUpdateRequested>(Handle);
        setup.OnEvent(_requested).Named("Send").Do(_ => _publication.SendRequestedUpdates());
    }

    private void Handle(in RprUpdateRequested m, IngressContext ctx)
    {
        // Resolve reports an entity of another origin than RPR as Echo: that is one of our own publications.
        if (IngressEntities.Resolve(ctx, _maps.RprHandle, m.Handle, create: false, out var entity) != EntityResolution.Echo)
        {
            return;
        }

        _publication.AddUpdateRequest(entity);
        ctx.Publish(_requested, ctx.RentPayload());
    }
}
