using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Remembers which entity the identity egress introduced to NED in which change set, so that the spatial and damage egress
/// rules of the same change set do not send again what the introduction already sent (master, geo-spatial, damage in order).
/// </summary>
public sealed class NedIntroductions
{
    private EntityId _entity;
    private CorrelationId _correlation;

    internal void Record(EntityId entity, CorrelationId correlation)
    {
        _entity = entity;
        _correlation = correlation;
    }

    internal bool IsCurrent(EntityId entity, CorrelationId correlation) => _entity == entity && _correlation == correlation;
}
