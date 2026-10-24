using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// C15, R-046: the customer federation reports and expects damage in percent. This module contributes the alternative damage
/// group to the pending RPR update (<see cref="RprPublication.ContributeDamagePercent"/>, the mechanism the built-in
/// contributors use): level 0..3 becomes 0, 33, 66 or 100 percent in <c>RprObjectUpdate.DamagePercent</c>. The contributor rule
/// <c>CustomerDamagePercent/Damage</c> is registered disabled in every configuration; the operator swaps it for
/// <c>RprEntityEgress/Damage</c> (disable that one, enable this one) at runtime, so the default behaviour is unchanged until then.
/// </summary>
public sealed class CustomerDamagePercent : IGatewayModule
{
    /// <summary>The percentage of each pivot damage level.</summary>
    public static readonly byte[] PercentOfLevel = [0, 33, 66, 100];

    private readonly PivotSchema _schema;
    private readonly RprPublication _publication;
    private readonly SourceId _rpr;

    public CustomerDamagePercent(PivotSchema schema, RprPublication publication, NedRprSources sources)
    {
        _schema = schema;
        _publication = publication;
        _rpr = sources.Rpr;
    }

    public string Name => nameof(CustomerDamagePercent);

    public void Register(ModuleSetup setup)
    {
        _publication.Attach(setup);
        _publication.DamagePercentRule = setup.OnAttributesChanged(_schema.Damage)
            .Named("Damage")
            .Disabled()
            .When(t => t.Entity.Origin != _rpr)
            .Do(t =>
            {
                if (t.Entity.TryGet(_schema.Damage, out var damage) && NedRprConversions.IsValidDamage((int)damage.AsInt()))
                {
                    _publication.ContributeDamagePercent(t.Id, PercentOfLevel[damage.AsInt()]);
                }
            });
    }
}
