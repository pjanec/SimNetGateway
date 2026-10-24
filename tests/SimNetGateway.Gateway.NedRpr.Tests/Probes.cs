using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// A test-only module that watches the dispatch: what the shooter's ammunition is when a WeaponFire event is dispatched, and
/// the order in which Status.Damage changes and events are dispatched (with their correlation ids). It declares its
/// consumption, so it does not disturb the vocabulary validation of the gateway it is added to.
/// </summary>
public sealed class DispatchProbe : IGatewayModule
{
    private readonly PivotSchema _schema;

    public DispatchProbe(PivotSchema schema)
    {
        _schema = schema;
    }

    public string Name => "Probe";

    /// <summary>For each WeaponFire event: its correlation and the ammunition the shooter has at that moment (null: none stored).</summary>
    public List<(CorrelationId Correlation, long? Ammo)> FireSeen { get; } = new();

    /// <summary>The dispatch order: "damage:corr:entity:level" for damage changes, "fire:corr" and "detonation:corr" for events.</summary>
    public List<string> Order { get; } = new();

    public void Register(ModuleSetup setup)
    {
        var gateway = setup.Gateway;
        setup.OnEvent(_schema.WeaponFire).Named("Fire").Do(t =>
        {
            long? ammo = null;
            if (t.Payload.TryGet(_schema.EventShooter, out var shooter) &&
                gateway.State.Find(shooter.AsEntity()) is { } record && record.TryGet(_schema.Ammo, out var a))
            {
                ammo = a.AsInt();
            }

            FireSeen.Add((t.Correlation, ammo));
            Order.Add("fire:" + t.Correlation.Value);
        });
        setup.OnEvent(_schema.Detonation).Named("Detonation").Do(t => Order.Add("detonation:" + t.Correlation.Value));
        setup.OnAttributesChanged(_schema.Damage).Named("Damage").Do(t =>
            Order.Add($"damage:{t.Correlation.Value}:{t.Id.Value}:{t.Entity.Get(_schema.Damage).AsInt()}"));
    }
}
