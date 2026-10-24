using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>What a held event is: a weapon fire or a detonation.</summary>
public enum HeldEventKind : byte
{
    Fire,
    Detonation,
}

/// <summary>An outbound RPR event waiting for the handle of an entity it names (R-047), with everything but the handles resolved.</summary>
public readonly record struct HeldEvent(
    HeldEventKind Kind,
    Guid Id,
    EntityId Shooter,
    bool HasShooter,
    EntityId Target,
    bool HasTarget,
    WorldLocation Location,
    DisEntityType Munition,
    int Quantity,
    RprDetonationResult Result,
    double HeldAt);

/// <summary>
/// R-047, shared by <see cref="RprWeaponFireEgress"/> and <see cref="RprDetonationEgress"/>: an outbound fire or detonation
/// that names an entity whose RPR registration is still pending is held instead of sent, because its handle does not exist yet.
/// A held event is sent when the handles of all entities it names are bound (the registration answer, see
/// <see cref="Release"/>), or after the hold timeout of wall time with "no object" for the entities still without a handle
/// and a warning. Order is preserved: events are released strictly in the order they were held, and while any event is held a
/// later event is held too (it would otherwise overtake). In a replay session nothing is sent (C16), held events are dropped.
/// </summary>
public sealed class RprEventHold
{
    private readonly NedRprMaps _maps;
    private readonly RprPublication _publication;
    private readonly IOutboundChannel<IRprMessage> _output;
    private readonly double _timeoutSeconds;
    private readonly Queue<HeldEvent> _held = new();
    private IGatewayContext? _gateway;

    public RprEventHold(NedRprMaps maps, RprPublication publication, IOutboundChannel<IRprMessage> output, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(output);
        _maps = maps;
        _publication = publication;
        _output = output;
        _timeoutSeconds = timeout.TotalSeconds;
    }

    /// <summary>Events waiting for a handle.</summary>
    public int Count => _held.Count;

    public RprPublication Publication => _publication;

    /// <summary>Called by the modules from Register (attaches the publication service, which owns the state of every entity).</summary>
    public void Attach(ModuleSetup setup)
    {
        _publication.Attach(setup);
        _gateway = setup.Gateway;
    }

    /// <summary>True when the event must wait: an event is already held (order), or a named entity's handle is pending.</summary>
    public bool MustHold(bool hasShooter, EntityId shooter, bool hasTarget, EntityId target) =>
        _held.Count > 0 || (hasShooter && IsPending(shooter)) || (hasTarget && IsPending(target));

    public void Hold(in HeldEvent ev) => _held.Enqueue(ev);

    /// <summary>
    /// Sends the held events in order, from the oldest: an event whose entities have handles now, or whose time is up. Stops at
    /// the first event that still waits.
    /// </summary>
    public void Release()
    {
        if (_held.Count == 0)
        {
            return;
        }

        var gateway = _gateway ?? throw new InvalidOperationException("The event hold is not attached to an engine.");
        double now = gateway.WallTime;
        while (_held.Count > 0)
        {
            var ev = _held.Peek();
            bool pending = (ev.HasShooter && IsPending(ev.Shooter)) || (ev.HasTarget && IsPending(ev.Target));
            bool expired = now - ev.HeldAt >= _timeoutSeconds;
            if (pending && !expired)
            {
                break;
            }

            _held.Dequeue();
            if (gateway.Session.Mode != SessionMode.Live)
            {
                continue; // the replay policy: nothing reaches the federation during a debrief
            }

            if (pending)
            {
                gateway.Diagnostics.Warn(nameof(RprEventHold),
                    $"Held {(ev.Kind == HeldEventKind.Fire ? "fire" : "detonation")} {ev.Id} released after {_timeoutSeconds:0.###} s " +
                    "with no object for an entity whose RPR handle is still pending.");
            }

            Send(ev);
        }
    }

    private void Send(in HeldEvent ev)
    {
        var shooter = ev.HasShooter ? HandleOf(ev.Shooter) : null;
        var target = ev.HasTarget ? HandleOf(ev.Target) : null;
        if (ev.Kind == HeldEventKind.Fire)
        {
            _output.Send(new RprWeaponFire(ev.Id, shooter, target, ev.Location, ev.Munition, ev.Quantity));
        }
        else
        {
            _output.Send(new RprMunitionDetonation(ev.Id, shooter, target, ev.Location, ev.Munition, ev.Result));
        }
    }

    private RprObjectHandle? HandleOf(EntityId entity) =>
        _maps.RprHandle.TryGetKey(entity, out var handle) ? handle : null;

    private bool IsPending(EntityId entity) =>
        _publication.Table.TryGet(entity, out var state) && state.Status == RprPublicationStatus.Pending;
}
