using System.Globalization;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Showcase;

/// <summary>A global invariant of DD-002 section 6 was violated.</summary>
public sealed class InvariantViolationException : Exception
{
    public InvariantViolationException(string message)
        : base(message)
    {
    }
}

/// <summary>One violation of a global invariant: the id (<c>G-1</c> .. <c>G-8</c>), the tick it was found at and what was wrong.</summary>
public readonly record struct Violation(string Id, long Tick, double SimTime, string Detail)
{
    public override string ToString() => Id + ": " + Detail;
}

/// <summary>
/// A diagnostic (warning or error) that a part of a run expects (G-7): the category of the diagnostic ("Module", "Transaction",
/// "Cascade", "Session", "Rule"; null = any) and a part of its message; with <paramref name="Severity"/> null, any severity.
/// </summary>
public readonly record struct ExpectedDiagnostic(string? Category, string MessagePart, DiagnosticSeverity? Severity = null)
{
    public bool Matches(in DiagnosticEntry entry) =>
        (Category is null || string.Equals(Category, entry.Category, StringComparison.Ordinal)) &&
        (Severity is null || Severity == entry.Severity) &&
        entry.Message.Contains(MessagePart, StringComparison.Ordinal);

    public override string ToString() => (Category ?? "*") + " '" + MessagePart + "'";
}

/// <summary>
/// The global invariants of DD-002 section 6, checked after every tick (<see cref="Evaluate"/> collects, <see cref="Check"/>
/// throws on the first tick with a violation).
/// <list type="bullet">
/// <item>G-1 no echo: no outbound message describes an entity of the receiving side's own origin, or an event of its own source.</item>
/// <item>G-2 identity consistency: every key round-trips, no key leaks, no destroyed entity is resolvable; NED never knows an
/// entity both as an individual and as a formation member.</item>
/// <item>G-3 index consistency: the three recency lists, the presence sets, the reverse reference index and the spatial grid equal
/// a full scan of the repository (brute force through the repository's diagnostic APIs).</item>
/// <item>G-4 received state is never overwritten by derived logic: every Spatial.* slot was written by an ingress side.</item>
/// <item>G-5 outbound references are valid: RPR handles were delivered by the RTI (and, for updates, not removed); NED ids are
/// wire ids the gateway received or allocated.</item>
/// <item>G-6 transaction atomicity: no committed record leaves a member without its parent, or a parent that is not alive; the
/// probe (<see cref="ProbeModule"/>), which sees every trigger, never sees such a state either.</item>
/// <item>G-7 diagnostics whitelist: every warning and error is one the script (or the test) expects now; no rule failed.</item>
/// <item>G-8 causality: correlation ids strictly increase, every CausedBy is an earlier correlation, and records and events are
/// dispatched in correlation order (the state triggers of a correlation precede its events).</item>
/// <item>G-9 (HLA-3, when <see cref="CheckRti"/> is on) what the RPR side receives is what the gateway sent: everything the gateway sent
/// to the RPR side in a tick (registrations, attribute updates, removals, fires, detonations) is, at the end of the tick, what the
/// scripted federate decoded from the RTI; the transport lost nothing and dropped only what the FOM cannot carry.</item>
/// </list>
/// Messages are inspected when they are sent (the identity maps still hold the keys of entities being removed); violations found
/// there are collected, because a rule that throws is swallowed by the engine, and raised by the next <see cref="Evaluate"/>.
/// </summary>
public sealed partial class InvariantChecker
{
    private readonly NedRprHost _h;
    private readonly List<(string Id, string Detail)> _violations = new();
    private readonly HashSet<RprObjectHandle> _removed = new();
    private readonly HashSet<EntityId> _destroyed = new();
    private readonly HashSet<EntityId> _nedIndividuals = new();
    private readonly HashSet<Guid> _nedFireIds = new();
    private readonly HashSet<Guid> _rprEventIds = new();
    private readonly HashSet<long> _seenCorrelations = new();
    private readonly Dictionary<Guid, SourceId> _eventSources = new();
    private readonly List<(Guid Id, SourceId Receiver, string Message)> _eventChecks = new();
    private readonly List<ExpectedDiagnostic> _expectedByTest = new();
    private readonly List<ExpectedDiagnostic> _expectedByScript = new();
    private readonly List<DiagnosticEntry> _warnings = new();
    private readonly HashSet<EntityId> _knownTargets = new();
    private readonly Dictionary<AttrId, HashSet<CellKey>> _knownCells = new();
    private long _lastCorrelation;
    private long _lastDispatched;
    private int _dispatchesSeen;
    private double _lastJournalWall = double.NegativeInfinity;
    private int _diagnosticsSeen;
    private long _checks;

    public InvariantChecker(NedRprHost harness)
    {
        _h = harness;
    }

    /// <summary>The probe whose records G-6 and G-8 use; optional.</summary>
    public ProbeModule? Probe { get; set; }

    /// <summary>Number of times <see cref="Evaluate"/> / <see cref="Check"/> ran (one per step).</summary>
    public long Checks => _checks;

    /// <summary>Warnings and errors that were expected (see <see cref="Expect(string)"/>), in order of appearance.</summary>
    public IReadOnlyList<DiagnosticEntry> Warnings => _warnings;

    /// <summary>Declares that warnings or errors containing the text are expected (from now on).</summary>
    public void Expect(string messagePart) => _expectedByTest.Add(new ExpectedDiagnostic(null, messagePart));

    /// <summary>Replaces the whitelist of the script (the diagnostics the current act expects).</summary>
    public void SetExpected(IEnumerable<ExpectedDiagnostic> expected)
    {
        _expectedByScript.Clear();
        _expectedByScript.AddRange(expected);
    }

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

    private void Violate(string id, string detail) => _violations.Add((id, detail));

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
                    Violate("G-5", $"NedWeaponFire names shooter member {m.ShooterMember} of {m.Shooter}, which ned.member does not know");
                }

                if (m.TargetMember >= 0 && !_h.NedMembers.TryResolve(new NedMemberKey(m.Target, m.TargetMember), out _))
                {
                    Violate("G-5", $"NedWeaponFire names member {m.TargetMember} of {m.Target}, which ned.member does not know");
                }

                if (_nedFireIds.Contains(m.FireId))
                {
                    Violate("G-1", $"NedWeaponFire {m.FireId} was sent back to NED, which sent that fire itself");
                }

                _eventChecks.Add((m.FireId, _h.NedSource, "NedWeaponFire " + m.FireId));
                break;
            default:
                Violate("G-1", $"the gateway sent {message.GetType().Name} to NED, which it never should");
                break;
        }
    }

    public void OnRprSent(IRprMessage message)
    {
        if (CheckRti)
        {
            MirrorSent(message);
        }

        switch (message)
        {
            case RprObjectUpdate m:
                CheckHandle(m.Handle, nameof(RprObjectUpdate), updating: true, checkOrigin: true);
                break;
            case RprObjectRemoved m:
                CheckHandle(m.Handle, nameof(RprObjectRemoved), updating: false, checkOrigin: true);
                if (!_removed.Add(m.Handle))
                {
                    Violate("G-5", $"RprObjectRemoved for handle {m.Handle.Value} sent twice");
                }

                break;
            case RprWeaponFire m:
                CheckOptionalHandle(m.FiringObject, "RprWeaponFire.FiringObject");
                CheckOptionalHandle(m.TargetObject, "RprWeaponFire.TargetObject");
                if (_rprEventIds.Contains(m.EventId))
                {
                    Violate("G-1", $"RprWeaponFire {m.EventId} was sent back to RPR, which sent that fire itself");
                }

                _eventChecks.Add((m.EventId, _h.RprSource, "RprWeaponFire " + m.EventId));
                break;
            case RprMunitionDetonation m:
                CheckOptionalHandle(m.FiringObject, "RprMunitionDetonation.FiringObject");
                CheckOptionalHandle(m.TargetObject, "RprMunitionDetonation.TargetObject");
                if (_rprEventIds.Contains(m.EventId))
                {
                    Violate("G-1", $"RprMunitionDetonation {m.EventId} was sent back to RPR, which sent that detonation itself");
                }

                _eventChecks.Add((m.EventId, _h.RprSource, "RprMunitionDetonation " + m.EventId));
                break;
            case RprRegisterObject:
            case RprSimulationControl:
                break;
            default:
                Violate("G-1", $"the gateway sent {message.GetType().Name} to RPR, which it never should");
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
                Violate("G-5", $"NedFormation {m.Id} lists member {member.Index}, which ned.member does not know");
            }
            else if (TryOriginOf(entity, out var origin) && origin == _h.NedSource)
            {
                Violate("G-1", $"NedFormation {m.Id} describes member {member.Index}, whose origin is ned");
            }
        }
    }

    private void CheckNedEntity(ulong id, string what, bool checkOrigin = true)
    {
        if (!_h.NedIds.TryResolve(id, out var entity))
        {
            Violate("G-5", $"{what} uses NED id {id}, which the gateway neither received nor allocated");
            return;
        }

        if (checkOrigin && TryOriginOf(entity, out var origin) && origin == _h.NedSource)
        {
            Violate("G-1", $"{what} (NED id {id}) describes entity {entity.Value}, whose origin is ned");
        }
    }

    private void CheckHandle(RprObjectHandle handle, string what, bool updating, bool checkOrigin)
    {
        if (!_h.IsRegisteredHandle(handle))
        {
            Violate("G-5", $"{what} uses handle {handle.Value}, which the RTI never delivered");
        }

        if (updating && _removed.Contains(handle))
        {
            Violate("G-5", $"{what} uses handle {handle.Value}, which was removed earlier");
        }

        if (checkOrigin && _h.RprHandles.TryResolve(handle, out var entity) && TryOriginOf(entity, out var origin) &&
            origin == _h.RprSource)
        {
            Violate("G-1", $"{what} (handle {handle.Value}) describes entity {entity.Value}, whose origin is rpr");
        }
    }

    private void CheckOptionalHandle(RprObjectHandle? handle, string what)
    {
        // a handle is valid when the RTI delivered it (an object the gateway registered) or when the RPR side announced it itself
        // (an RPR-origin object that is bound in rpr.handle), e.g. the target of a NED weapon fire
        if (handle is { } h && !_h.IsRegisteredHandle(h) && !_h.RprHandles.TryResolve(h, out _))
        {
            Violate("G-5", $"{what} uses handle {h.Value}, which the RTI never delivered");
        }
        else if (handle is { } removed && _removed.Contains(removed))
        {
            Violate("G-5", $"{what} uses handle {removed.Value}, which was removed earlier");
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

    /// <summary>Runs every check for the tick that just ended and throws on the first tick with a violation.</summary>
    public void Check()
    {
        var violations = Evaluate();
        if (violations.Count > 0)
        {
            var text = string.Join("\n", violations.Select(v => v.ToString()));
            throw new InvariantViolationException(string.Create(CultureInfo.InvariantCulture,
                $"Invariant violated at tick {_h.Engine.TickCount} (sim time {_h.SimTime:F3}):\n{text}"));
        }
    }

    /// <summary>Runs every check for the tick that just ended and returns the violations found (each is reported once).</summary>
    public IReadOnlyList<Violation> Evaluate()
    {
        _checks++;
        CheckJournal();
        CheckEvents();
        CheckDispatchOrder();
        CheckIdentities();
        CheckReceivedState();
        CheckStructure();
        CheckIndexes();
        CheckProbe();
        CheckDiagnostics();
        if (CheckRti)
        {
            CheckRtiMirror();
        }

        if (_violations.Count == 0)
        {
            return Array.Empty<Violation>();
        }

        var result = new List<Violation>(_violations.Count);
        foreach (var (id, detail) in _violations)
        {
            result.Add(new Violation(id, _h.Engine.TickCount, _h.SimTime, detail));
        }

        _violations.Clear();
        return result;
    }

    private void CheckJournal()
    {
        var schema = _h.Schema;
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

            foreach (var delta in record.Entities)
            {
                if (delta.Destroyed)
                {
                    _destroyed.Add(delta.Id);
                    continue;
                }

                // G-6: a record is complete - a member is never written without its parent (and the other way round), and
                // the parent it names is alive when the record is committed.
                bool parentWritten = delta.TryGetChange(schema.Parent, out var parentChange);
                bool indexWritten = delta.TryGetChange(schema.MemberIndex, out var indexChange);
                if (parentWritten != indexWritten)
                {
                    Violate("G-6", $"record {record.Correlation.Value} writes {(parentWritten ? "Structure.Parent" : "Structure.MemberIndex")} " +
                        $"of entity {delta.Id.Value} without {(parentWritten ? "Structure.MemberIndex" : "Structure.Parent")}");
                }
                else if (parentWritten && parentChange.New.IsNone != indexChange.New.IsNone)
                {
                    Violate("G-6", $"record {record.Correlation.Value} sets only one of Structure.Parent / Structure.MemberIndex of entity {delta.Id.Value}");
                }

                if (parentWritten && !parentChange.New.IsNone && _destroyed.Contains(parentChange.New.AsEntity()))
                {
                    Violate("G-6", $"record {record.Correlation.Value} makes entity {delta.Id.Value} a member of {parentChange.New.AsEntity().Value}, which was destroyed earlier");
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
                Violate("G-8", $"event {ev.Correlation.Value} is caused by {ev.CausedBy.Value}, which is not earlier");
            }
        }

        foreach (var (id, receiver, message) in _eventChecks)
        {
            if (!_eventSources.TryGetValue(id, out var source))
            {
                Violate("G-1", $"{message} does not correspond to any dispatched event");
            }
            else if (source == receiver)
            {
                Violate("G-1", $"{message} describes an event whose source is the receiving side itself");
            }
        }

        _eventChecks.Clear();
    }

    /// <summary>G-8: the probe saw every record and event in the order of its dispatch; that order is the correlation order.</summary>
    private void CheckDispatchOrder()
    {
        if (Probe is null)
        {
            return;
        }

        var log = Probe.Dispatches;
        for (; _dispatchesSeen < log.Count; _dispatchesSeen++)
        {
            var entry = log[_dispatchesSeen];
            long correlation = entry.Correlation;

            // An ingress message gives its record and its events ONE correlation: the record (the state triggers) comes first,
            // then its events. Anything else must have a higher correlation than everything dispatched before.
            bool wrong = correlation < _lastDispatched || (correlation == _lastDispatched && !entry.IsEvent);
            if (wrong)
            {
                Violate("G-8", $"{(entry.IsEvent ? "event" : "record")} {correlation} was dispatched after {_lastDispatched}" +
                    " (the state triggers of a correlation must precede its events, and correlations must not go back)");
            }

            _lastDispatched = Math.Max(_lastDispatched, correlation);
        }
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
                    Violate("G-2", $"destroyed entity {id.Value} is still bound in map '{map.Name}'");
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
                Violate("G-2", $"map '{map.Name}': entity {entity.Id.Value} has key {key}, which resolves to {back.Value}");
            }
        }

        if (bound != map.Count)
        {
            Violate("G-2", $"map '{map.Name}' holds {map.Count} bindings, but {bound} live entities have a key");
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
                Violate("G-2", $"structure: entity {entity.Id.Value} is a formation member but NED also knows it as an individual");
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
                    Violate("G-4", $"Spatial slot {_h.Engine.Attributes[attr].Name} of entity {entity.Id.Value} " +
                        $"was written by source {_h.Engine.Sources.NameOf(slot.Meta.Source)}");
                }
            }
        }
    }

    // ---- G-3: the indexes against a full scan -----------------------------------------------------------------------

    private void CheckIndexes()
    {
        var state = _h.Engine.State;
        var live = new List<EntityRecord>();
        foreach (var entity in state.Entities)
        {
            live.Add(entity);
        }

        CheckRecency("ByLastWrite", state.ByLastWrite, live, e => e.LastWriteWallTime);
        CheckRecency("ByLastChange", state.ByLastChange, live, e => e.LastChangeWallTime);
        CheckRecency("ByLastOriginWrite", state.ByLastOriginWrite, live, e => e.LastOriginWriteWallTime);

        foreach (var def in _h.Engine.Attributes)
        {
            int scanned = 0;
            foreach (var entity in live)
            {
                if (entity.Has(def.Id))
                {
                    scanned++;
                }
            }

            int indexed = state.PresenceCount(def.Id);
            if (indexed != scanned)
            {
                Violate("G-3", $"presence set of {def.Name} holds {indexed} entities, a scan finds {scanned}");
            }

            if ((def.Options & AttrOptions.IndexReferences) != 0)
            {
                CheckReferences(def, live);
            }

            if ((def.Options & AttrOptions.SpatialIndex) != 0)
            {
                CheckGrid(def, live);
            }
        }
    }

    private void CheckRecency(string name, RecencyEnumerable list, List<EntityRecord> live, Func<EntityRecord, double> time)
    {
        var seen = new HashSet<EntityId>();
        double previous = double.NegativeInfinity;
        foreach (var entity in list)
        {
            if (!seen.Add(entity.Id))
            {
                Violate("G-3", $"{name} lists entity {entity.Id.Value} twice");
                return;
            }

            if (!entity.IsAlive)
            {
                Violate("G-3", $"{name} lists the dead entity {entity.Id.Value}");
            }

            double t = time(entity);
            if (t < previous)
            {
                Violate("G-3", $"{name} is not ordered by time: entity {entity.Id.Value} at {t.ToString("R", CultureInfo.InvariantCulture)} follows {previous.ToString("R", CultureInfo.InvariantCulture)}");
            }

            previous = t;
        }

        if (seen.Count != live.Count)
        {
            Violate("G-3", $"{name} lists {seen.Count} entities, the repository holds {live.Count}");
        }
        else
        {
            foreach (var entity in live)
            {
                if (!seen.Contains(entity.Id))
                {
                    Violate("G-3", $"{name} misses entity {entity.Id.Value}");
                    break;
                }
            }
        }
    }

    private void CheckReferences(AttributeDef def, List<EntityRecord> live)
    {
        var state = _h.Engine.State;
        var byTarget = new Dictionary<EntityId, int>();
        foreach (var entity in live)
        {
            if (entity.TryGet(def.Id, out var value) && !value.AsEntity().IsNone)
            {
                var target = value.AsEntity();
                byTarget[target] = byTarget.GetValueOrDefault(target) + 1;
                _knownTargets.Add(target);
            }
        }

        foreach (var entity in live)
        {
            _knownTargets.Add(entity.Id);
        }

        foreach (var target in _knownTargets)
        {
            int scanned = byTarget.GetValueOrDefault(target);
            int indexed = state.ReferrerCount(def.Id, target);
            if (indexed != scanned)
            {
                Violate("G-3", $"reverse index of {def.Name} holds {indexed} referrers of entity {target.Value}, a scan finds {scanned}");
            }
        }
    }

    private void CheckGrid(AttributeDef def, List<EntityRecord> live)
    {
        var state = _h.Engine.State;
        var byCell = new Dictionary<CellKey, int>();
        foreach (var entity in live)
        {
            if (entity.TryGet(def.Id, out var value))
            {
                var cell = state.CellOf(def.Id, value.AsVec3());
                byCell[cell] = byCell.GetValueOrDefault(cell) + 1;
            }
        }

        if (!_knownCells.TryGetValue(def.Id, out var known))
        {
            _knownCells[def.Id] = known = new HashSet<CellKey>();
        }

        foreach (var cell in byCell.Keys)
        {
            known.Add(cell);
        }

        int occupied = state.OccupiedCellCount(def.Id);
        if (occupied != byCell.Count)
        {
            Violate("G-3", $"spatial grid of {def.Name} has {occupied} occupied cells, a scan finds {byCell.Count}");
        }

        foreach (var cell in known)
        {
            int scanned = byCell.GetValueOrDefault(cell);
            int indexed = state.CellEntityCount(def.Id, cell);
            if (indexed != scanned)
            {
                Violate("G-3", $"spatial grid of {def.Name}: cell ({cell.X},{cell.Y},{cell.Z}) holds {indexed} entities, a scan finds {scanned}");
            }
        }
    }

    // ---- G-6 through the probe ----------------------------------------------------------------------------------------

    private void CheckProbe()
    {
        if (Probe is null)
        {
            return;
        }

        foreach (string detail in Probe.TakeViolations())
        {
            Violate("G-6", detail);
        }
    }

    // ---- G-7 ------------------------------------------------------------------------------------------------------

    private void CheckDiagnostics()
    {
        var diagnostics = _h.Engine.Diagnostics;
        if (diagnostics.Count >= diagnostics.Capacity)
        {
            Violate("G-7", "the diagnostics ring is full; the harness cannot see every entry any more");
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

            if (IsExpected(entry))
            {
                _warnings.Add(entry);
            }
            else
            {
                Violate("G-7", $"unexpected {entry.Severity} from {entry.Origin} ({entry.Category}): {entry.Message}");
            }
        }

        _diagnosticsSeen = diagnostics.Count;
        if (diagnostics.RuleFailures > 0)
        {
            Violate("G-7", $"{diagnostics.RuleFailures} rule failure(s)");
        }
    }

    private bool IsExpected(in DiagnosticEntry entry)
    {
        foreach (var expected in _expectedByScript)
        {
            if (expected.Matches(entry))
            {
                return true;
            }
        }

        foreach (var expected in _expectedByTest)
        {
            if (expected.Matches(entry))
            {
                return true;
            }
        }

        return false;
    }
}
