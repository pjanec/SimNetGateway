using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// Processor (source <c>proc.fire</c>) that turns the helper entity of a split fire (see <see cref="NedSplitFireIngress"/>) into
/// one WeaponFire event (C8).
/// Rule "Complete" (<c>Fire.Pending</c> changed to false): the second part arrived. Rule "Timeout" (every 100 ms): a helper that
/// is still pending after <c>FireAssemblyTimeout</c> of wall time is completed with the shooter's selected munition and a
/// warning "fire GUID completed by timeout". Either way, in ONE transaction the shooter's <c>Weapon.Ammo</c> is decremented by the
/// quantity (not below 0) and the helper is destroyed (its fire id is released with it); then the event is published with the
/// source <c>ned</c> - the side the fire came from, so echo suppression keeps the event away from NED (R-022, R-061). The state
/// change is dispatched before the event, so event rules see the new ammunition (R-016).
/// </summary>
public sealed class FireAssemblerProcessor : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprSources _sources;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _checkPeriod;
    private readonly List<EntityRecord> _pending = new();
    private IGatewayContext _gateway = null!;

    public FireAssemblerProcessor(PivotSchema schema, NedRprSources sources, TimeSpan timeout, TimeSpan checkPeriod)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
        }

        _schema = schema;
        _sources = sources;
        _timeout = timeout;
        _checkPeriod = checkPeriod;
    }

    public string Name => nameof(FireAssemblerProcessor);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        setup.Produces(_schema.Ammo, _schema.EventId, _schema.EventShooter, _schema.EventTarget, _schema.EventMunitionTkb,
            _schema.EventLocation, _schema.EventQuantity);
        setup.Produces(_schema.WeaponFire);
        setup.Consumes(_schema.Ammo, _schema.SelectedMunition, _schema.FireId, _schema.FireShooter, _schema.FireTarget,
            _schema.FireLocation, _schema.FireMunition, _schema.FireQuantity, _schema.FireStartedAt);
        setup.OnAttributesChanged(_schema.FirePending)
            .Named("Complete")
            .When(t => t.Entity.TryGet(_schema.FirePending, out var pending) && !pending.AsBool())
            .Do(t => Assemble(t.Entity, timedOut: false));
        setup.Every(_checkPeriod)
            .Named("Timeout")
            .Do(_ => CompleteExpired());
    }

    private void CompleteExpired()
    {
        _gateway.State.Query().Has(_schema.FirePending).CopyTo(_pending);
        double now = _gateway.WallTime;
        try
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                var helper = _pending[i];
                if (helper.IsAlive && helper.TryGet(_schema.FirePending, out var pending) && pending.AsBool() &&
                    helper.TryGet(_schema.FireStartedAt, out var started) && now - started.AsDouble() >= _timeout.TotalSeconds)
                {
                    Assemble(helper, timedOut: true);
                }
            }
        }
        finally
        {
            _pending.Clear();
        }
    }

    private void Assemble(EntityRecord helper, bool timedOut)
    {
        var schema = _schema;
        var fireId = helper.TryGet(schema.FireId, out var idValue) ? idValue.AsObject<Guid>() : Guid.Empty;
        bool hasShooter = helper.TryGet(schema.FireShooter, out var shooterValue);
        bool hasTarget = helper.TryGet(schema.FireTarget, out var targetValue);
        var shooterRecord = hasShooter ? _gateway.State.Find(shooterValue.AsEntity()) : null;
        int munition = helper.TryGet(schema.FireMunition, out var m) ? (int)m.AsInt() : 0;
        int quantity = helper.TryGet(schema.FireQuantity, out var q) ? (int)q.AsInt() : 1;
        var location = helper.TryGet(schema.FireLocation, out var l) ? l : AttrValue.From(Vec3.Zero);

        if (timedOut)
        {
            if (shooterRecord is not null && shooterRecord.TryGet(schema.SelectedMunition, out var selected))
            {
                munition = (int)selected.AsInt();
            }

            _gateway.Diagnostics.Warn(Name, $"fire {fireId} completed by timeout");
        }

        var changes = _gateway.BeginChange(_sources.ProcFire);
        try
        {
            if (shooterRecord is not null && shooterRecord.TryGet(schema.Ammo, out var ammo))
            {
                changes.Set(shooterRecord.Id, schema.Ammo, AttrValue.From(Math.Max(0, ammo.AsInt() - Math.Max(0, quantity))));
            }

            changes.DestroyEntity(helper.Id);
            if (changes.Commit().Status == CommitStatus.Rejected)
            {
                return;
            }
        }
        finally
        {
            changes.Discard();
        }

        if (shooterRecord is null)
        {
            _gateway.Diagnostics.Warn(Name, $"fire {fireId} dropped: its shooter no longer exists");
            return;
        }

        var bag = _gateway.RentPayload();
        bag.Set(schema.EventId, AttrValue.FromObject(fireId));
        bag.Set(schema.EventShooter, shooterValue);
        if (hasTarget && _gateway.State.Find(targetValue.AsEntity()) is not null)
        {
            bag.Set(schema.EventTarget, targetValue);
        }

        bag.Set(schema.EventMunitionTkb, AttrValue.From(munition));
        bag.Set(schema.EventLocation, location);
        bag.Set(schema.EventQuantity, AttrValue.From(quantity));
        _gateway.Publish(_sources.Ned, schema.WeaponFire, bag);
    }
}
