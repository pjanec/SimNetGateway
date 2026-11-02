using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// Rule "Ownership": when an <c>Ownership.*</c> attribute of an entity of origin hrot changes, the set of groups owned outside HROT is
/// recomputed and, only when it differs from the set last sent for that entity, sent as the absolute <see cref="HrotDescriptorOwnership"/>
/// (DD-012 section 4a item 5). Rule "Remove" forgets a removed entity. The set is sent in the tick the owner changes, so HROT stops
/// writing the group in the same step.
/// </summary>
public sealed class HrotDescriptorOwnershipEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisSources _sources;
    private readonly IOutboundChannel<IGatewayToHrotMessage> _output;
    private readonly Dictionary<EntityId, HrotDescriptor> _sent = new();

    public HrotDescriptorOwnershipEgress(PivotSchema schema, HrotDisMaps maps, HrotDisSources sources, IOutboundChannel<IGatewayToHrotMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _sources = sources;
        _output = output;
    }

    public string Name => nameof(HrotDescriptorOwnershipEgress);

    public void Register(ModuleSetup setup)
    {
        setup.OnAttributesChanged(_schema.OwnerSpatial, _schema.OwnerInfo, _schema.OwnerDamage, _schema.OwnerArticulation)
            .Named("Ownership")
            .When(t => t.Entity.Origin == _sources.Hrot)
            .Do(Handle);
        setup.OnEntityRemoved()
            .Named("Remove")
            .When(t => t.Entity.Origin == _sources.Hrot)
            .Do(t => _sent.Remove(t.Id));
    }

    private void Handle(EntityTrigger t)
    {
        var set = _schema.ExternalSet(t.Entity, _sources.Hrot);
        _sent.TryGetValue(t.Id, out var last);
        if (set == last || !_maps.HrotEntity.TryGetKey(t.Id, out int hrotId))
        {
            return;
        }

        if (set == HrotDescriptor.None)
        {
            _sent.Remove(t.Id);
        }
        else
        {
            _sent[t.Id] = set;
        }

        _output.Send(new HrotDescriptorOwnership(hrotId, set));
    }
}

/// <summary>
/// Rule "External": when a writer other than the HROT side changes an attribute of an externally owned group of an entity of origin hrot,
/// one <see cref="ExternalState"/> carries the values of the changed external groups (DD-012 section 4a item 5). Health comes from
/// <c>Hrot.Health</c> when it was written in the same change set, else from the damage level. Articulation values are not sent yet (H4).
/// </summary>
public sealed class ExternalStateEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly HrotDisMaps _maps;
    private readonly HrotDisSources _sources;
    private readonly HrotDisServices _services;
    private readonly IOutboundChannel<IGatewayToHrotMessage> _output;

    public ExternalStateEgress(PivotSchema schema, HrotDisMaps maps, HrotDisSources sources, HrotDisServices services,
        IOutboundChannel<IGatewayToHrotMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _sources = sources;
        _services = services;
        _output = output;
    }

    public string Name => nameof(ExternalStateEgress);

    public void Register(ModuleSetup setup)
    {
        var attributes = _schema.Groups.Groups.Where(g => !ReferenceEquals(g, _schema.ArticulationGroup)).SelectMany(g => g.Attributes).ToArray();
        setup.Consumes(attributes);
        setup.OnAttributesChanged(attributes)
            .Named("External")
            .When(t => t.Entity.Origin == _sources.Hrot && t.Source != _sources.Hrot)
            .Do(Handle);
    }

    private void Handle(EntityTrigger t)
    {
        var entity = t.Entity;
        var sent = HrotDescriptor.None;
        foreach (var group in _schema.Groups.Groups)
        {
            if (ReferenceEquals(group, _schema.ArticulationGroup) || !_schema.Groups.IsExternal(entity, group, _sources.Hrot))
            {
                continue;
            }

            if (group.Attributes.Any(a => t.Changed(a)))
            {
                sent |= _schema.DescriptorOf(group);
            }
        }

        if (sent == HrotDescriptor.None || !_maps.HrotEntity.TryGetKey(t.Id, out int hrotId))
        {
            return;
        }

        var snapshot = default(HrotDrSnapshot);
        if ((sent & HrotDescriptor.Spatial) != 0)
        {
            var geo = _services.Geo;
            if (geo.IsValid && entity.Has(_schema.Position) && entity.Has(_schema.StateTime))
            {
                snapshot = ReplicaPublisher.Snapshot(_schema, geo, entity);
            }
            else
            {
                sent &= ~HrotDescriptor.Spatial;
            }
        }

        string marking = string.Empty;
        int force = 0;
        if ((sent & HrotDescriptor.Info) != 0)
        {
            marking = entity.TryGet(_schema.Marking, out var m) ? m.AsString() : string.Empty;
            force = entity.TryGet(_schema.Force, out var f) ? (int)f.AsInt() : 0;
        }

        double current = 0, max = 0;
        if ((sent & HrotDescriptor.Damage) != 0)
        {
            if (t.Changed(_schema.Health) && entity.TryGet(_schema.Health, out var health))
            {
                var v = health.AsVec3();
                current = v.X;
                max = v.Y;
            }
            else
            {
                int damage = entity.TryGet(_schema.Damage, out var d) ? (int)d.AsInt() : DamageMapping.None;
                current = _services.Damage.HealthOf(damage);
                max = _services.Damage.MaxHealth;
            }
        }

        if (sent == HrotDescriptor.None)
        {
            return;
        }

        _output.Send(new ExternalState(hrotId, sent, snapshot, marking, force, current, max));
    }
}
