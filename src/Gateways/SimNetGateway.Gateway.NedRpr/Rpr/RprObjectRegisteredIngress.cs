using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// RprObjectRegistered: the RTI answered a registration request. Binds the handle to the entity that requested the token
/// (buffered, in this transaction) and publishes the internal "handle bound" event; <see cref="RprEntityEgress"/> sends the
/// first full update on it. A transaction that only binds has no change set record and so no change-set-end trigger,
/// which is why an event carries the news.
/// </summary>
public sealed class RprObjectRegisteredIngress : IGatewayModule
{
    private readonly NedRprMaps _maps;
    private readonly RprPublication _publication;
    private EventTypeId _handleBound;

    public RprObjectRegisteredIngress(NedRprMaps maps, RprPublication publication)
    {
        _maps = maps;
        _publication = publication;
    }

    public string Name => nameof(RprObjectRegisteredIngress);

    public void Register(ModuleSetup setup)
    {
        _publication.Attach(setup);
        _handleBound = _publication.HandleBoundEvent;
        setup.Produces(_handleBound);
        setup.OnMessage<RprObjectRegistered>(Handle);
    }

    private void Handle(in RprObjectRegistered m, IngressContext ctx)
    {
        var answer = _publication.Registration.OnRegistered(m.Token, m.Handle, out var entity,
            id => ctx.State.Find(id) is { IsAlive: true });
        switch (answer)
        {
            case RegistrationAnswer.Unknown:
                ctx.Diagnostics.Warn(Name, $"Registration answer for unknown token {m.Token} (handle {m.Handle.Value}); ignored.");
                return;
            case RegistrationAnswer.Accepted:
                _maps.RprHandle.Bind(ctx.Changes, entity, m.Handle);
                break;
            // Released: the entity was removed while its registration was pending: the object exists at the RTI and is deleted again.
        }

        ctx.Publish(_handleBound, ctx.RentPayload());
    }
}
