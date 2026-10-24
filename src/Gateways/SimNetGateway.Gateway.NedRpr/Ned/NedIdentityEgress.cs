using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Rule "Master": introduces an entity to NED once it has a name and a type (allocating a wire id from the gateway's
/// range) and re-sends the master descriptor when its identity changes. Id exhaustion is a warning; the entity is
/// retried on its next trigger. A newly introduced entity also gets the geo-spatial and damage state it already has.
/// </summary>
public sealed class NedIdentityEgress : IGatewayModule
{
    private struct Exhausted
    {
        public bool Warned;

        public override readonly string ToString() => $"id range exhaustion warned {Warned}";
    }

    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly SourceId _ned;
    private readonly NedIntroductions _introductions;
    private readonly IOutboundChannel<INedMessage> _output;
    private EntityTable<Exhausted> _exhausted = null!;
    private IGatewayContext _gateway = null!;

    public NedIdentityEgress(PivotSchema schema, NedRprMaps maps, NedRprSources sources, NedIntroductions introductions,
        IOutboundChannel<INedMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _ned = sources.Ned;
        _introductions = introductions;
        _output = output;
    }

    public string Name => nameof(NedIdentityEgress);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        _exhausted = setup.CreateEntityTable<Exhausted>("NedIdentityEgress.Exhausted");
        setup.Consumes(_schema.Position, _schema.Orientation, _schema.Velocity, _schema.Damage); // sent on introduction
        setup.Consumes(_schema.AggregateMemberIds); // a unit is described by its formation message, not by a master
        // Position is a retry trigger: an entity that found the id range exhausted is tried again on its next change.
        // Structure.Parent: an entity that is taken out of its unit is introduced to NED as an individual again.
        setup.OnAttributesChanged(_schema.Name, _schema.TypeTkb, _schema.Force, _schema.Position, _schema.Parent)
            .Named("Master")
            .When(t => t.Entity.Origin != _ned)
            .Do(OnTrigger);
    }

    private void OnTrigger(EntityTrigger t)
    {
        var entity = t.Entity;
        if (NedStructure.IsStructural(_schema, entity))
        {
            return; // members and units are described by NedFormation (NedFormationEgress), never individually
        }

        if (!entity.TryGet(_schema.Name, out var name) || !entity.TryGet(_schema.TypeTkb, out var type))
        {
            return;
        }

        bool known = _maps.NedEntity.TryGetKey(t.Id, out ulong key);
        bool identityChanged = t.Changed(_schema.Name) || t.Changed(_schema.TypeTkb) || t.Changed(_schema.Force);
        if (known && !identityChanged)
        {
            return;
        }

        bool introduced = false;
        if (!known)
        {
            if (!_maps.NedEntity.TryGetOrAllocate(t.Id, out key))
            {
                ref var state = ref _exhausted.GetOrAdd(t.Id);
                if (!state.Warned)
                {
                    state.Warned = true;
                    _gateway.Diagnostics.Warn(Name,
                        $"NED id range exhausted: entity {t.Id.Value} is not published to NED (retried on its next change).");
                }

                return;
            }

            introduced = true;
            _exhausted.Remove(t.Id);
        }

        var force = NedForce.Unknown;
        if (entity.TryGet(_schema.Force, out var f) && NedRprConversions.TryForceFromPivot((int)f.AsInt(), out var mapped))
        {
            force = mapped;
        }

        _output.Send(new NedEntityMaster(key, name.AsString(), (int)type.AsInt(), force));

        if (introduced)
        {
            // NED learns what the entity already has; the spatial and damage rules skip this change set (see NedIntroductions).
            _introductions.Record(t.Id, t.Correlation);
            NedWire.SendGeoSpatial(_schema, _output, key, entity);
            NedWire.SendDamage(_schema, _output, key, entity);
        }
    }
}
