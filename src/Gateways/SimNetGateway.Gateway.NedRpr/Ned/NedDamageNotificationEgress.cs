using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Rule "Notify" (C6, state -> event): NED wants an occurrence when the damage of an entity gets worse, RPR reports damage
/// only as a state. When <c>Status.Damage</c> of an entity of another origin than NED rises (an absent old value counts as 0)
/// NED gets a <see cref="NedDamageNotification"/> after the <c>NedEntityDamage</c> descriptor; for a member of a unit the
/// notification names (unit id, member index) instead. A decrease sends nothing; neither does the introduction of an entity
/// (the damage then goes out with the introduction) or an entity NED does not know. Registered after
/// <see cref="NedDamageEgress"/>.
/// </summary>
public sealed class NedDamageNotificationEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly SourceId _ned;
    private readonly NedIntroductions _introductions;
    private readonly IOutboundChannel<INedMessage> _output;

    public NedDamageNotificationEgress(PivotSchema schema, NedRprMaps maps, NedRprSources sources, NedIntroductions introductions,
        IOutboundChannel<INedMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _ned = sources.Ned;
        _introductions = introductions;
        _output = output;
    }

    public string Name => nameof(NedDamageNotificationEgress);

    /// <summary>
    /// The notification is an occurrence NED only wants in some profiles: the rule is registered in every configuration (so
    /// an operator can switch it on at runtime) and enabled by the showcase settings.
    /// </summary>
    public void Register(ModuleSetup setup)
    {
        setup.Consumes(_schema.Parent, _schema.MemberIndex);
        setup.OnAttributesChanged(_schema.Damage)
            .Named("Notify")
            .Disabled()
            .When(t => t.Entity.Origin != _ned && !_introductions.IsCurrent(t.Id, t.Correlation))
            .Do(OnDamage);
    }

    private void OnDamage(EntityTrigger t)
    {
        if (!t.Delta.TryGetChange(_schema.Damage, out var change) || change.New.IsNone)
        {
            return;
        }

        long newLevel = change.New.AsInt();
        long oldLevel = change.Old.IsNone ? 0 : change.Old.AsInt();
        if (newLevel <= oldLevel)
        {
            return;
        }

        ulong id;
        int member = -1;
        if (t.Entity.TryGet(_schema.Parent, out var parent))
        {
            if (!_maps.NedEntity.TryGetKey(parent.AsEntity(), out id))
            {
                return;
            }

            member = t.Entity.TryGet(_schema.MemberIndex, out var index) ? (int)index.AsInt() : -1;
        }
        else if (!_maps.NedEntity.TryGetKey(t.Id, out id))
        {
            return;
        }

        _output.Send(new NedDamageNotification(id, member, (int)oldLevel, (int)newLevel));
    }
}
