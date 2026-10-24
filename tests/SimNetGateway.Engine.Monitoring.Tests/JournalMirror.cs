using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Monitoring.Tests;

internal sealed record MirrorChange(AttrId Attr, AttrValue Old, AttrValue New, bool Changed);

internal sealed record MirrorDelta(EntityId Id, bool Created, bool Destroyed, bool OwnerChanged, SourceId PreviousOwner, MirrorChange[] Changes);

internal sealed record MirrorRecord(long Corr, long CausedBy, SourceId Source, double Wall, double Sim, MirrorDelta[] Deltas);

internal sealed record MirrorEvent(EventTypeId Type, long Corr, long CausedBy, SourceId Source, double Wall, double Sim,
    (AttrId Attr, AttrValue Value)[] Payload);

/// <summary>
/// The test's own, unbounded copy of everything the engine committed and dispatched, taken inside rules at dispatch time
/// (before the engine can recycle anything). The engine retains the last records of this list in its journal, in the same order
/// (the work queue is first-in first-out), so the models can be checked against it whatever the capacities are.
/// </summary>
internal sealed class JournalMirror
{
    public readonly List<MirrorRecord> Records = new();
    public readonly List<MirrorEvent> Events = new();

    public void OnRecord(ChangeSetRecord record)
    {
        var deltas = new MirrorDelta[record.Count];
        for (int i = 0; i < deltas.Length; i++)
        {
            var d = record.Entities[i];
            var changes = new MirrorChange[d.Changes.Count];
            for (int c = 0; c < changes.Length; c++)
            {
                var ch = d.Changes[c];
                changes[c] = new MirrorChange(ch.Attr, ch.Old, ch.New, ch.Changed);
            }

            deltas[i] = new MirrorDelta(d.Id, d.Created, d.Destroyed, d.OwnerChanged, d.PreviousOwner, changes);
        }

        Records.Add(new MirrorRecord(record.Correlation.Value, record.CausedBy.Value, record.Source, record.WallTime, record.SimTime, deltas));
    }

    public void OnEvent(SimEvent ev)
    {
        var payload = new List<(AttrId, AttrValue)>();
        foreach (var item in ev.Payload)
        {
            payload.Add(item);
        }

        Events.Add(new MirrorEvent(ev.Type, ev.Correlation.Value, ev.CausedBy.Value, ev.Source, ev.WallTime, ev.SimTime, payload.ToArray()));
    }

    /// <summary>A module that mirrors by itself (for engines the test did not build).</summary>
    public IGatewayModule AsModule() => new MirrorModule(this);

    private sealed class MirrorModule : IGatewayModule
    {
        private readonly JournalMirror _mirror;

        public MirrorModule(JournalMirror mirror) => _mirror = mirror;

        public string Name => "TestMirror";

        public void Register(ModuleSetup setup)
        {
            setup.OnChangeSetEnd().Named("Record").Do(t => _mirror.OnRecord(t.ChangeSet));
            setup.OnAnyEvent().Named("Event").Do(t => _mirror.OnEvent(t.Event));
        }
    }
}
