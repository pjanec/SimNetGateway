using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>
/// The invariants of DD-002 section 6 that apply to the HROT half, checked after every step (and, for the messages, at the moment they
/// are sent): G-1 no echo (a ReplicaState / ReplicaRemoved describes only entities of origin dis or rpr, an Inject* only events of the DIS
/// side), G-2 identity consistency (every key round-trips, nothing destroyed stays bound, a DIS entity has a HROT id exactly while
/// HROT knows it as a replica and every id it holds is the allocator's), G-4 received state is never overwritten by derived logic
/// (every Spatial slot was written by an ingress side), G-5 outbound references are valid (a replica id is bound to a live DIS
/// entity, an Inject* names ids that are bound), G-8 causality (correlations increase, CausedBy is earlier). G-3 and G-6 are the
/// engine's own business and covered by its tests.
/// </summary>
public sealed class InvariantChecker
{
    private readonly HrotDisHarness _h;
    private readonly HashSet<long> _introduced = new();
    private readonly HashSet<long> _seenCorrelations = new();
    private readonly List<string> _violations = new();
    private long _lastCorrelation;
    private double _lastJournalWall = double.NegativeInfinity;
    private int _checkedMessages;

    public InvariantChecker(HrotDisHarness harness) => _h = harness;

    /// <summary>The HROT ids that HROT currently knows as replicas, as the message stream says.</summary>
    public IReadOnlyCollection<long> Introduced => _introduced;

    /// <summary>Called by the outbound recorder at the moment the gateway sends (the entity and its key are still bound).</summary>
    public void OnSent(IGatewayToHrotMessage message)
    {
        switch (message)
        {
            case ReplicaState s:
                _introduced.Add(s.Id);
                RequireReplica(s.Id, "ReplicaState");
                break;
            case ReplicaRemoved r:
                if (!_introduced.Remove(r.Id))
                {
                    Violate("G-5", $"ReplicaRemoved {r.Id} for an id HROT does not know as a replica");
                }

                break;
            case InjectFire f:
                RequireKnownOrZero(f.ShooterId, "InjectFire.ShooterId");
                RequireTarget(f.TargetId, f.TargetIsHrot, "InjectFire");
                break;
            case InjectDetonation d:
                RequireKnownOrZero(d.ShooterId, "InjectDetonation.ShooterId");
                RequireTarget(d.TargetId, d.TargetIsHrot, "InjectDetonation");
                break;
        }
    }

    private void RequireReplica(long id, string what)
    {
        if (!_h.HrotIds.TryResolve(id, out var entity))
        {
            Violate("G-5", $"{what} {id}: the id is not bound");
            return;
        }

        var record = _h.Engine.State.Find(entity);
        if (record is null || !record.IsAlive)
        {
            Violate("G-5", $"{what} {id}: the entity is gone");
        }
        else if (!_h.IsReplica(record.Origin))
        {
            Violate("G-1", $"{what} {id} describes an entity of origin '{_h.Engine.Sources.NameOf(record.Origin)}' (echo)");
        }
    }

    private void RequireKnownOrZero(long id, string what)
    {
        if (id != 0 && !_h.HrotIds.TryResolve(id, out _))
        {
            Violate("G-5", $"{what} {id} is not bound");
        }
    }

    private void RequireTarget(long id, bool isHrot, string what)
    {
        RequireKnownOrZero(id, what + ".TargetId");
        if (id == 0)
        {
            if (isHrot)
            {
                Violate("G-5", $"{what}: TargetIsHrot without a target");
            }

            return;
        }

        if (_h.HrotIds.TryResolve(id, out var entity) && _h.Engine.State.Find(entity) is { } record)
        {
            bool origin = record.Origin == _h.HrotSource;
            if (origin != isHrot)
            {
                Violate("G-5", $"{what}: target {id} has origin '{_h.Engine.Sources.NameOf(record.Origin)}' but TargetIsHrot is {isHrot}");
            }
        }
    }

    /// <summary>Checks the state; fails the test with every violation found (and those recorded while sending).</summary>
    public void Check()
    {
        CheckMessagesAfterTheFact();
        CheckJournal();
        CheckIdentities();
        CheckReceivedState();
        if (_h.Engine.Diagnostics.RuleFailures != 0)
        {
            Violate("G-7", $"{_h.Engine.Diagnostics.RuleFailures} rule(s) threw an exception");
        }

        if (_violations.Count > 0)
        {
            var all = string.Join(Environment.NewLine, _violations);
            _violations.Clear();
            Assert.Fail($"Invariant violation(s) at tick {_h.Engine.TickCount}:{Environment.NewLine}{all}");
        }
    }

    private void Violate(string id, string detail) => _violations.Add($"{id}: {detail}");

    /// <summary>G-1 for events: an Inject* must come from an event whose source is not the HROT side.</summary>
    private void CheckMessagesAfterTheFact()
    {
        var messages = _h.HrotOut.Messages;
        for (; _checkedMessages < messages.Count; _checkedMessages++)
        {
            var recorded = messages[_checkedMessages];
            if (recorded.Message is not (InjectFire or InjectDetonation))
            {
                continue;
            }

            bool found = false;
            foreach (var ev in _h.Engine.State.Journal.RecentEvents)
            {
                if (ev.Correlation == recorded.Correlation)
                {
                    found = true;
                    if (ev.Source == _h.HrotSource)
                    {
                        Violate("G-1", $"{recorded.Message.GetType().Name} describes an event whose source is the HROT side");
                    }
                }
            }

            if (!found)
            {
                Violate("G-1", $"{recorded.Message.GetType().Name} does not correspond to any dispatched event (correlation {recorded.Correlation.Value})");
            }
        }
    }

    private void CheckJournal()
    {
        foreach (var record in _h.Engine.State.Journal.Since(_lastJournalWall))
        {
            if (!_seenCorrelations.Add(record.Correlation.Value))
            {
                continue;
            }

            _lastJournalWall = Math.Max(_lastJournalWall, record.WallTime);
            if (record.Correlation.Value <= _lastCorrelation)
            {
                Violate("G-8", $"correlation {record.Correlation.Value} does not exceed the previous {_lastCorrelation}");
            }

            _lastCorrelation = Math.Max(_lastCorrelation, record.Correlation.Value);
            if (!record.CausedBy.IsNone && record.CausedBy.Value >= record.Correlation.Value)
            {
                Violate("G-8", $"correlation {record.Correlation.Value} is caused by {record.CausedBy.Value}, which is not earlier");
            }
        }

        foreach (var ev in _h.Engine.State.Journal.RecentEvents)
        {
            if (!ev.CausedBy.IsNone && ev.CausedBy.Value >= ev.Correlation.Value)
            {
                Violate("G-8", $"event {ev.Correlation.Value} is caused by {ev.CausedBy.Value}, which is not earlier");
            }
        }
    }

    private void CheckIdentities()
    {
        var map = _h.HrotIds;
        int bound = 0;
        int disBound = 0;
        var ownedByHrot = new HashSet<long>();
        foreach (var entity in _h.Engine.State.Entities)
        {
            if (!map.TryGetKey(entity.Id, out long key))
            {
                continue;
            }

            bound++;
            if (!map.TryResolve(key, out var back) || back != entity.Id)
            {
                Violate("G-2", $"hrot.entity: entity {entity.Id.Value} has key {key}, which resolves to {back.Value}");
            }

            if (_h.IsReplica(entity.Origin))
            {
                disBound++;
                if (!_introduced.Contains(key))
                {
                    Violate("G-2", $"DIS entity {entity.Id.Value} holds HROT id {key}, but HROT does not know it as a replica");
                }
            }
            else
            {
                ownedByHrot.Add(key);
            }
        }

        if (bound != map.Count)
        {
            Violate("G-2", $"hrot.entity holds {map.Count} bindings, but {bound} live entities have a key");
        }

        if (disBound != _introduced.Count)
        {
            Violate("G-2", $"HROT knows {_introduced.Count} replicas, but {disBound} live DIS entities hold an id");
        }

        if ((ulong)disBound != _h.Ids.InUse)
        {
            Violate("G-2", $"the allocator has {_h.Ids.InUse} ids in use, but {disBound} DIS entities hold one (leak or double release)");
        }

        foreach (var key in _introduced)
        {
            if (ownedByHrot.Contains(key))
            {
                Violate("G-2", $"HROT id {key} is both a replica and a HROT entity");
            }
        }
    }

    private void CheckReceivedState()
    {
        var s = _h.Schema;
        AttrId[] spatial = [s.Position, s.Velocity, s.Acceleration, s.Orientation, s.AngularVelocity, s.DrAlgorithm, s.StateTime];
        foreach (var entity in _h.Engine.State.Entities)
        {
            foreach (var attr in spatial)
            {
                if (entity.TryGetSlot(attr, out var slot) && slot.Meta.Source != _h.HrotSource && !_h.IsReplica(slot.Meta.Source))
                {
                    Violate("G-4", $"Spatial slot {_h.Engine.Attributes[attr].Name} of entity {entity.Id.Value} was written by source {_h.Engine.Sources.NameOf(slot.Meta.Source)}");
                }
            }
        }
    }
}
