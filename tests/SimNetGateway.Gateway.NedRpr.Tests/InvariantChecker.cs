using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>A global invariant of DD-002 section 6 was violated.</summary>
public sealed class InvariantViolationException : Exception
{
    public InvariantViolationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The global invariants of DD-002 section 6 that apply to the NED-RPR gateway, checked by <see cref="GatewayHarness.Step"/>
/// after every tick; the first violation fails the test.
/// <list type="bullet">
/// <item>G-1 no echo: no outbound message describes an entity of the receiving side's own origin, or an event of its own source.</item>
/// <item>G-2 identity consistency: every key round-trips, no key leaks, no destroyed entity is resolvable.</item>
/// <item>G-4 received state is never overwritten by derived logic: every Spatial.* slot was written by an ingress side.</item>
/// <item>G-5 outbound references are valid: RPR handles were delivered by the RTI (and, for updates, not removed); NED ids are
/// wire ids the gateway received or allocated.</item>
/// <item>G-8 causality: correlation ids strictly increase, every CausedBy is an earlier correlation.</item>
/// <item>The engine's diagnostics contain only warnings and errors the test expects; no rule failed.</item>
/// </list>
/// Messages are inspected when they are sent (the identity maps still hold the keys of entities being removed); violations found
/// there are collected, because a rule that throws is swallowed by the engine, and raised by <see cref="Check"/>.
/// </summary>
public sealed class InvariantChecker
{
    private readonly NedRprHost _h;
    private readonly List<string> _violations = new();
    private readonly HashSet<RprObjectHandle> _removed = new();
    private readonly HashSet<EntityId> _destroyed = new();
    private readonly HashSet<EntityId> _nedIndividuals = new();
    private readonly HashSet<Guid> _nedFireIds = new();
    private readonly HashSet<Guid> _rprEventIds = new();
    private readonly HashSet<long> _seenCorrelations = new();
    private readonly Dictionary<Guid, SourceId> _eventSources = new();
    private readonly List<(Guid Id, SourceId Receiver, string Message)> _eventChecks = new();
    private readonly List<string> _expectedWarnings = new();
    private readonly List<DiagnosticEntry> _warnings = new();
    private long _lastCorrelation;
    private double _lastJournalWall = double.NegativeInfinity;
    private int _diagnosticsSeen;
    private long _checks;

    public InvariantChecker(NedRprHost harness)
    {
        _h = harness;
    }

    /// <summary>Number of times <see cref="Check"/> ran (one per step).</summary>
    public long Checks => _checks;

    /// <summary>Warnings and errors that were expected (see <see cref="Expect"/>), in order of appearance.</summary>
    public IReadOnlyList<DiagnosticEntry> Warnings => _warnings;

    /// <summary>Declares that warnings or errors containing the text are expected (from now on).</summary>
    public void Expect(string messagePart) => _expectedWarnings.Add(messagePart);

    public int WarningsContaining(string messagePart)
    {
        int count = 0;
        foreach (var w in _warnings)
        {
            if (w.Message.Contains(messagePart, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    // ---- inspection of received and sent messages ---------------------------------------------------------------------

    /// <summary>Notes the fire and detonation ids that the NED side itself sent in (G-1: they must never be sent back to NED).</summary>
    public void OnNedReceived(INedMessage message)
    {
        switch (message)
        {
            case NedWeaponFire m: _nedFireIds.Add(m.FireId); break;
            case NedFireAmmunition m: _nedFireIds.Add(m.FireId); break;
            case NedDetonation m: _nedFireIds.Add(m.DetonationId); break;
        }
    }

    public void OnRprReceived(IRprMessage message)
    {
        switch (message)
        {
            case RprWeaponFire m: _rprEventIds.Add(m.EventId); break;
            case RprMunitionDetonation m: _rprEventIds.Add(m.EventId); break;
        }
    }

    public void OnNedSent(INedMessage message)
    {
        switch (message)
        {
            case NedEntityMaster m:
                CheckNedEntity(m.Id, nameof(NedEntityMaster));
                if (_h.NedIds.TryResolve(m.Id, out var introduced))
                {
                    _nedIndividuals.Add(introduced);
                }

                break;
            case NedFormation m: CheckFormation(m); break;
            case NedDamageNotification m: CheckNedEntity(m.Id, nameof(NedDamageNotification)); break;
            case NedGeoSpatial m: CheckNedEntity(m.Id, nameof(NedGeoSpatial)); break;
            case NedEntityDamage m: CheckNedEntity(m.Id, nameof(NedEntityDamage)); break;
            case NedEntityDisposed m:
                CheckNedEntity(m.Id, nameof(NedEntityDisposed));
                if (_h.NedIds.TryResolve(m.Id, out var disposed))
                {
                    _nedIndividuals.Remove(disposed);
                }

                break;
            case NedWeaponFire m:
                if (m.Shooter != 0)
                {
                    CheckNedEntity(m.Shooter, "NedWeaponFire.Shooter");
                }

                if (m.Target != 0)
                {
                    CheckNedEntity(m.Target, "NedWeaponFire.Target", checkOrigin: false);
                }

                if (m.ShooterMember >= 0 && !_h.NedMembers.TryResolve(new NedMemberKey(m.Shooter, m.ShooterMember), out _))
                {
                    _violations.Add($"G-5: NedWeaponFire names shooter member {m.ShooterMember} of {m.Shooter}, which ned.member does not know");
                }

                if (m.TargetMember >= 0 && !_h.NedMembers.TryResolve(new NedMemberKey(m.Target, m.TargetMember), out _))
                {
                    _violations.Add($"G-5: NedWeaponFire names member {m.TargetMember} of {m.Target}, which ned.member does not know");
                }

                if (_nedFireIds.Contains(m.FireId))
                {
                    _violations.Add($"G-1: NedWeaponFire {m.FireId} was sent back to NED, which sent that fire itself");
                }

                _eventChecks.Add((m.FireId, _h.NedSource, "NedWeaponFire " + m.FireId));
                break;
            default:
                _violations.Add($"G-1: the gateway sent {message.GetType().Name} to NED, which it never should");
                break;
        }
    }

    public void OnRprSent(IRprMessage message)
    {
        switch (message)
        {
            case RprObjectUpdate m:
                CheckHandle(m.Handle, nameof(RprObjectUpdate), updating: true, checkOrigin: true);
                break;
            case RprObjectRemoved m:
                CheckHandle(m.Handle, nameof(RprObjectRemoved), updating: false, checkOrigin: true);
                if (!_removed.Add(m.Handle))
                {
                    _violations.Add($"G-5: RprObjectRemoved for handle {m.Handle.Value} sent twice");
                }

                break;
            case RprWeaponFire m:
                CheckOptionalHandle(m.FiringObject, "RprWeaponFire.FiringObject");
                CheckOptionalHandle(m.TargetObject, "RprWeaponFire.TargetObject");
                if (_rprEventIds.Contains(m.EventId))
                {
                    _violations.Add($"G-1: RprWeaponFire {m.EventId} was sent back to RPR, which sent that fire itself");
                }

                _eventChecks.Add((m.EventId, _h.RprSource, "RprWeaponFire " + m.EventId));
                break;
            case RprMunitionDetonation m:
                CheckOptionalHandle(m.FiringObject, "RprMunitionDetonation.FiringObject");
                CheckOptionalHandle(m.TargetObject, "RprMunitionDetonation.TargetObject");
                if (_rprEventIds.Contains(m.EventId))
                {
                    _violations.Add($"G-1: RprMunitionDetonation {m.EventId} was sent back to RPR, which sent that detonation itself");
                }

                _eventChecks.Add((m.EventId, _h.RprSource, "RprMunitionDetonation " + m.EventId));
                break;
            case RprRegisterObject:
            case RprSimulationControl:
                break;
            default:
                _violations.Add($"G-1: the gateway sent {message.GetType().Name} to RPR, which it never should");
                break;
        }
    }

    private void CheckFormation(NedFormation m)
    {
        CheckNedEntity(m.Id, nameof(NedFormation));
        foreach (var member in m.Members)
        {
            if (!_h.NedMembers.TryResolve(new NedMemberKey(m.Id, member.Index), out var entity))
            {
                _violations.Add($"G-5: NedFormation {m.Id} lists member {member.Index}, which ned.member does not know");
            }
            else if (TryOriginOf(entity, out var origin) && origin == _h.NedSource)
            {
                _violations.Add($"G-1: NedFormation {m.Id} describes member {member.Index}, whose origin is ned");
            }
        }
    }

    private void CheckNedEntity(ulong id, string what, bool checkOrigin = true)
    {
        if (!_h.NedIds.TryResolve(id, out var entity))
        {
            _violations.Add($"G-5: {what} uses NED id {id}, which the gateway neither received nor allocated");
            return;
        }

        if (checkOrigin && TryOriginOf(entity, out var origin) && origin == _h.NedSource)
        {
            _violations.Add($"G-1: {what} (NED id {id}) describes entity {entity.Value}, whose origin is ned");
        }
    }

    private void CheckHandle(RprObjectHandle handle, string what, bool updating, bool checkOrigin)
    {
        if (!_h.Rti.Registered.Contains(handle))
        {
            _violations.Add($"G-5: {what} uses handle {handle.Value}, which the RTI never delivered");
        }

        if (updating && _removed.Contains(handle))
        {
            _violations.Add($"G-5: {what} uses handle {handle.Value}, which was removed earlier");
        }

        if (checkOrigin && _h.RprHandles.TryResolve(handle, out var entity) && TryOriginOf(entity, out var origin) &&
            origin == _h.RprSource)
        {
            _violations.Add($"G-1: {what} (handle {handle.Value}) describes entity {entity.Value}, whose origin is rpr");
        }
    }

    private void CheckOptionalHandle(RprObjectHandle? handle, string what)
    {
        // a handle is valid when the RTI delivered it (an object the gateway registered) or when the RPR side announced it itself
        // (an RPR-origin object that is bound in rpr.handle), e.g. the target of a NED weapon fire
        if (handle is { } h && !_h.Rti.Registered.Contains(h) && !_h.RprHandles.TryResolve(h, out _))
        {
            _violations.Add($"G-5: {what} uses handle {h.Value}, which the RTI never delivered");
        }
    }

    /// <summary>The origin of an entity, also of a destroyed one (found through the journal).</summary>
    private bool TryOriginOf(EntityId id, out SourceId origin)
    {
        var live = _h.Engine.State.Find(id);
        if (live is not null)
        {
            origin = live.Origin;
            return true;
        }

        foreach (var record in _h.Engine.State.Journal.Since(double.NegativeInfinity))
        {
            foreach (var delta in record.Entities)
            {
                if (delta.Id == id)
                {
                    origin = delta.Entity.Origin;
                    return true;
                }
            }
        }

        origin = default;
        return false;
    }

    // ---- the per-tick check -----------------------------------------------------------------------------------------

    public void Check()
    {
        _checks++;
        Fail(_violations);
        CheckJournal();
        CheckEvents();
        CheckIdentities();
        CheckReceivedState();
        CheckStructure();
        CheckDiagnostics();
        Fail(_violations);
    }

    private void Fail(List<string> violations)
    {
        if (violations.Count > 0)
        {
            var text = string.Join("\n", violations);
            violations.Clear();
            throw new InvariantViolationException($"Invariant violated at tick {_h.Engine.TickCount} (sim time {_h.SimTime:F3}):\n{text}");
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
                _violations.Add($"G-8: correlation {record.Correlation.Value} does not exceed the previous {_lastCorrelation}");
            }

            _lastCorrelation = Math.Max(_lastCorrelation, record.Correlation.Value);
            if (!record.CausedBy.IsNone && record.CausedBy.Value >= record.Correlation.Value)
            {
                _violations.Add($"G-8: correlation {record.Correlation.Value} is caused by {record.CausedBy.Value}, which is not earlier");
            }

            foreach (var delta in record.Entities)
            {
                if (delta.Destroyed)
                {
                    _destroyed.Add(delta.Id);
                }
            }
        }
    }

    private void CheckEvents()
    {
        foreach (var ev in _h.Engine.State.Journal.RecentEvents)
        {
            if (ev.Payload.TryGet(_h.Schema.EventId, out var id))
            {
                _eventSources[id.AsObject<Guid>()] = ev.Source;
            }

            if (!ev.CausedBy.IsNone && ev.CausedBy.Value >= ev.Correlation.Value)
            {
                _violations.Add($"G-8: event {ev.Correlation.Value} is caused by {ev.CausedBy.Value}, which is not earlier");
            }
        }

        foreach (var (id, receiver, message) in _eventChecks)
        {
            if (!_eventSources.TryGetValue(id, out var source))
            {
                _violations.Add($"G-1: {message} does not correspond to any dispatched event");
            }
            else if (source == receiver)
            {
                _violations.Add($"G-1: {message} describes an event whose source is the receiving side itself");
            }
        }

        _eventChecks.Clear();
    }

    private void CheckIdentities()
    {
        RoundTrip(_h.NedIds);
        RoundTrip(_h.RprHandles);
        RoundTrip(_h.RprEntityIds);
        RoundTrip(_h.DisplayLabels);
        RoundTrip(_h.NedMembers);
        RoundTrip(_h.NedFires);
        foreach (var map in _h.Engine.Identities.All)
        {
            foreach (var id in _destroyed)
            {
                if (map.Contains(id))
                {
                    _violations.Add($"G-2: destroyed entity {id.Value} is still bound in map '{map.Name}'");
                }
            }
        }
    }

    private void RoundTrip<TKey>(IdentityMap<TKey> map)
        where TKey : notnull, IEquatable<TKey>
    {
        int bound = 0;
        foreach (var entity in _h.Engine.State.Entities)
        {
            if (!map.TryGetKey(entity.Id, out var key))
            {
                continue;
            }

            bound++;
            if (!map.TryResolve(key, out var back) || back != entity.Id)
            {
                _violations.Add($"G-2: map '{map.Name}': entity {entity.Id.Value} has key {key}, which resolves to {back.Value}");
            }
        }

        if (bound != map.Count)
        {
            _violations.Add($"G-2: map '{map.Name}' holds {map.Count} bindings, but {bound} live entities have a key");
        }
    }

    /// <summary>NED never holds the same entity both as an individual id and as a formation member.</summary>
    private void CheckStructure()
    {
        var parent = _h.Schema.Parent;
        foreach (var entity in _h.Engine.State.Entities)
        {
            if (entity.Origin == _h.NedSource || !entity.Has(parent))
            {
                continue;
            }

            if (_h.NedIds.Contains(entity.Id) || _nedIndividuals.Contains(entity.Id))
            {
                _violations.Add($"G-structure: entity {entity.Id.Value} is a formation member but NED also knows it as an individual");
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
                if (entity.TryGetSlot(attr, out var slot) && slot.Meta.Source != _h.NedSource && slot.Meta.Source != _h.RprSource)
                {
                    _violations.Add($"G-4: Spatial slot {_h.Engine.Attributes[attr].Name} of entity {entity.Id.Value} " +
                        $"was written by source {_h.Engine.Sources.NameOf(slot.Meta.Source)}");
                }
            }
        }
    }

    private void CheckDiagnostics()
    {
        var diagnostics = _h.Engine.Diagnostics;
        if (diagnostics.Count >= diagnostics.Capacity)
        {
            _violations.Add("the diagnostics ring is full; the harness cannot see every entry any more");
        }

        int index = 0;
        foreach (var entry in diagnostics.Recent)
        {
            if (index++ < _diagnosticsSeen)
            {
                continue;
            }

            if (entry.Severity == DiagnosticSeverity.Info)
            {
                continue;
            }

            if (_expectedWarnings.Exists(part => entry.Message.Contains(part, StringComparison.Ordinal)))
            {
                _warnings.Add(entry);
            }
            else
            {
                _violations.Add($"unexpected {entry.Severity} from {entry.Origin} ({entry.Category}): {entry.Message}");
            }
        }

        _diagnosticsSeen = diagnostics.Count;
        if (diagnostics.RuleFailures > 0)
        {
            _violations.Add($"{diagnostics.RuleFailures} rule failure(s)");
        }
    }
}
