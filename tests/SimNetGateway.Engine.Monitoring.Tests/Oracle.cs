using System.Reflection;
using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Monitoring.Tests;

/// <summary>
/// Brute-force computations of what every model must show, written independently of the models: the engine's own query
/// engine for the entity filters, reflection over the typed maps and tables for keys and module state, the test's mirror for
/// the journal, plain scans for the structure.
/// </summary>
internal static class Oracle
{
    // ---- identity keys and module state, through the typed APIs ----------------------------------------------------

    public static string? KeyOf(IIdentityMap map, EntityId id)
    {
        var method = map.GetType().GetMethod("TryGetKey")!;
        var args = new object?[] { id, null };
        return (bool)method.Invoke(map, args)! ? args[1]!.ToString() : null;
    }

    public static string? StateOf(IEntityTable table, EntityId id)
    {
        var method = table.GetType().GetMethod("TryGet")!;
        var args = new object?[] { id, null };
        return (bool)method.Invoke(table, args)! ? args[1]!.ToString() : null;
    }

    // ---- entity table ----------------------------------------------------------------------------------------------

    public static List<EntityRow> ExpectedRows(GatewayEngine e, EntityTableOptions options, EntityFilter f,
        EntityColumn column, bool descending, int keyMap)
    {
        var q = e.State.Query();
        if (f.Origin is { } origin) q = q.FromOrigin(origin);
        if (f.ChangedWithin is { } c) q = q.ChangedWithin(TimeSpan.FromSeconds(c));
        if (f.WrittenWithin is { } w) q = q.WrittenWithin(TimeSpan.FromSeconds(w));
        if (f.NotWrittenWithin is { } nw) q = q.NotWrittenWithin(TimeSpan.FromSeconds(nw));
        if (f.OriginWrittenWithin is { } ow) q = q.OriginWrittenWithin(TimeSpan.FromSeconds(ow));
        if (f.NotOriginWrittenWithin is { } now) q = q.NotOriginWrittenWithin(TimeSpan.FromSeconds(now));
        foreach (var a in f.Has ?? Array.Empty<AttrId>()) q = q.Has(a);
        foreach (var a in f.Lacks ?? Array.Empty<AttrId>()) q = q.Lacks(a);
        var known = (f.KnownTo ?? Array.Empty<string>()).Select(n => e.Identities.Find(n)!).ToList();
        var unknown = (f.UnknownTo ?? Array.Empty<string>()).Select(n => e.Identities.Find(n)!).ToList();
        if (known.Count > 0) q = q.KnownTo(known[0]);
        if (f.Parent is { } parent) q = q.ReferencedBy(options.ParentAttribute, parent);
        string? text = f.NameContains;
        bool needPredicate = f.Owner is not null || text is not null || known.Count > 1 || unknown.Count > 0;
        if (needPredicate)
        {
            q = q.Where(r =>
                (f.Owner is not { } o || r.Owner == o)
                && (text is null || (r.TryGet(options.NameAttribute, out var nv) && nv.AsString().Contains(text, StringComparison.OrdinalIgnoreCase)))
                && known.Skip(1).All(m => m.Contains(r.Id))
                && unknown.All(m => !m.Contains(r.Id)));
        }

        var found = new List<EntityRecord>();
        q.CopyTo(found);
        var rows = found.Select(r => RowOf(e, options, r)).ToList();
        return Sort(rows, column, descending, keyMap);
    }

    public static EntityRow RowOf(GatewayEngine e, EntityTableOptions options, EntityRecord r)
    {
        string name = !options.NameAttribute.IsNone && r.TryGet(options.NameAttribute, out var nv) ? nv.AsString() : string.Empty;
        EntityId parent = !options.ParentAttribute.IsNone && r.TryGet(options.ParentAttribute, out var pv) ? pv.AsEntity() : default;
        string?[]? keys = options.IncludeKeys ? e.Identities.All.Select(m => KeyOf(m, r.Id)).ToArray() : null;
        return new EntityRow(r.Id, name, r.Origin, e.Sources.NameOf(r.Origin), r.Owner, e.Sources.NameOf(r.Owner), parent, r.Count,
            r.CreatedWallTime, r.LastWriteWallTime, r.LastChangeWallTime, r.LastOriginWriteWallTime, keys);
    }

    private static List<EntityRow> Sort(List<EntityRow> rows, EntityColumn column, bool descending, int keyMap)
    {
        var ord = StringComparer.Ordinal;
        IOrderedEnumerable<EntityRow> ordered = column switch
        {
            EntityColumn.Id => Order(rows, r => r.Id.Value, descending),
            EntityColumn.Name => Order(rows, r => r.Name, descending, ord),
            EntityColumn.Origin => Order(rows, r => r.OriginName, descending, ord),
            EntityColumn.Owner => Order(rows, r => r.OwnerName, descending, ord),
            EntityColumn.Parent => Order(rows, r => r.Parent.Value, descending),
            EntityColumn.Attributes => Order(rows, r => r.AttributeCount, descending),
            EntityColumn.Created => Order(rows, r => r.CreatedWallTime, descending),
            EntityColumn.LastWrite => Order(rows, r => r.LastWriteWallTime, descending),
            EntityColumn.LastChange => Order(rows, r => r.LastChangeWallTime, descending),
            EntityColumn.LastOriginWrite => Order(rows, r => r.LastOriginWriteWallTime, descending),
            _ => Order(rows, r => r.Keys?[keyMap], descending, ord),
        };
        return ordered.ThenBy(r => r.Id.Value).ToList();
    }

    private static IOrderedEnumerable<EntityRow> Order<TKey>(List<EntityRow> rows, Func<EntityRow, TKey> key, bool descending,
        IComparer<TKey>? comparer = null) =>
        descending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

    public static void AssertRows(IReadOnlyList<EntityRow> actual, IReadOnlyList<EntityRow> expected, string context)
    {
        Assert.True(actual.Count == expected.Count, $"{context}: {actual.Count} rows, expected {expected.Count}");
        for (int i = 0; i < expected.Count; i++)
        {
            var a = actual[i];
            var x = expected[i];
            bool same = a.Id == x.Id && a.Name == x.Name && a.Origin == x.Origin && a.OriginName == x.OriginName && a.Owner == x.Owner
                && a.OwnerName == x.OwnerName && a.Parent == x.Parent && a.AttributeCount == x.AttributeCount
                && a.CreatedWallTime == x.CreatedWallTime && a.LastWriteWallTime == x.LastWriteWallTime
                && a.LastChangeWallTime == x.LastChangeWallTime && a.LastOriginWriteWallTime == x.LastOriginWriteWallTime
                && ((a.Keys is null && x.Keys is null) || (a.Keys is not null && x.Keys is not null && a.Keys.SequenceEqual(x.Keys)));
            Assert.True(same, $"{context}: row {i} is {Show(a)}, expected {Show(x)}");
        }
    }

    private static string Show(EntityRow r) =>
        $"[{r.Id.Value} '{r.Name}' {r.OriginName}/{r.OwnerName} parent {r.Parent.Value} attrs {r.AttributeCount} " +
        $"w{r.LastWriteWallTime:F4} c{r.LastChangeWallTime:F4} o{r.LastOriginWriteWallTime:F4} keys {string.Join("|", r.Keys ?? Array.Empty<string?>())}]";

    // ---- inspector -------------------------------------------------------------------------------------------------

    public static void AssertInspector(GatewayEngine e, InspectorModel model, EntityTableOptions options, JournalMirror? mirror,
        string context)
    {
        var id = model.Selected;
        var entity = e.State.Find(id);
        Assert.NotNull(entity);
        Assert.Equal(InspectorState.Alive, model.State);

        // slots, in attribute id order
        var slots = new List<SlotRow>();
        foreach (var def in e.Attributes)
        {
            if (entity!.TryGetSlot(def.Id, out var slot))
            {
                var m = slot.Meta;
                slots.Add(new SlotRow(def.Id, def.Name, slot.Value.Kind, slot.Value, slot.Value.ToString(), m.Source,
                    e.Sources.NameOf(m.Source), m.Correlation, m.WallTime, m.SimTime, m.ChangedWallTime, m.ChangedSimTime, m.Version));
            }
        }

        Assert.True(slots.SequenceEqual(model.Slots), $"{context}: slots of entity {id.Value} differ");
        Assert.Equal(e.Sources.NameOf(entity!.Origin), model.OriginName);
        Assert.Equal(e.Sources.NameOf(entity.Owner), model.OwnerName);
        Assert.Equal(entity.LastOriginWriteWallTime, model.LastOriginWriteWallTime);

        // identities in every map
        var identities = e.Identities.All.Select(m => new IdentityRow(m.Name, e.Sources.NameOf(m.Side), m.KeyType.Name, KeyOf(m, id))).ToList();
        Assert.True(identities.SequenceEqual(model.Identities), $"{context}: identities of entity {id.Value} differ");

        // module state
        var state = new List<ModuleStateRow>();
        foreach (var table in e.EntityTables)
        {
            string? text = StateOf(table, id);
            if (text is not null)
            {
                state.Add(new ModuleStateRow(table.Name, text));
            }
        }

        Assert.True(state.SequenceEqual(model.ModuleState), $"{context}: module state of entity {id.Value} differs");

        // structure: references held and members found by scanning everything
        var refs = slots.Where(s => s.Kind == AttrKind.EntityRef).Select(s => new ReferenceRow(s.Attr, s.Name, s.Value.AsEntity())).ToList();
        Assert.True(refs.SequenceEqual(model.References), $"{context}: references of entity {id.Value} differ");
        var groups = new List<(AttrId, string, EntityId[])>();
        foreach (var def in e.Attributes.Where(d => (d.Options & AttrOptions.IndexReferences) != 0))
        {
            var members = new List<EntityId>();
            foreach (var other in e.State.Entities)
            {
                if (other.TryGet(def.Id, out var v) && v.AsEntity() == id)
                {
                    members.Add(other.Id);
                }
            }

            if (members.Count > 0)
            {
                members.Sort((a, b) => a.Value.CompareTo(b.Value));
                groups.Add((def.Id, def.Name, members.ToArray()));
            }
        }

        Assert.Equal(groups.Count, model.Members.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            Assert.Equal(groups[i].Item1, model.Members[i].Attr);
            Assert.Equal(groups[i].Item2, model.Members[i].AttrName);
            Assert.True(groups[i].Item3.SequenceEqual(model.Members[i].Members), $"{context}: members of entity {id.Value} differ");
        }

        // recent writes
        var expected = ExpectedWrites(e, id, model.MaxWrites, mirror);
        Assert.Equal(e.State.Journal.Capacity == 0, model.HistoryOff);
        Assert.True(expected.SequenceEqual(model.RecentWrites), $"{context}: recent writes of entity {id.Value} differ: " +
            $"{model.RecentWrites.Count} rows, expected {expected.Count}");
    }

    public static List<WriteRow> ExpectedWrites(GatewayEngine e, EntityId id, int max, JournalMirror? mirror)
    {
        var journal = e.State.Journal;
        var rows = new List<WriteRow>();
        if (journal.Capacity == 0 || max == 0)
        {
            return rows;
        }

        IEnumerable<MirrorRecord> retained = mirror is not null
            ? mirror.Records.Skip(mirror.Records.Count - journal.Count)
            : RetainedFromJournal(e);
        foreach (var record in retained)
        {
            string source = e.Sources.NameOf(record.Source);
            foreach (var delta in record.Deltas.Where(d => d.Id == id))
            {
                if (delta.Changes.Length == 0)
                {
                    rows.Add(new WriteRow(new CorrelationId(record.Corr), new CorrelationId(record.CausedBy), record.Source, source,
                        record.Wall, record.Sim, default, "(none)", default, default, false, delta.Created, delta.Destroyed, delta.OwnerChanged));
                }

                foreach (var ch in delta.Changes)
                {
                    rows.Add(new WriteRow(new CorrelationId(record.Corr), new CorrelationId(record.CausedBy), record.Source, source,
                        record.Wall, record.Sim, ch.Attr, e.Attributes[ch.Attr].Name, ch.Old, ch.New, ch.Changed, delta.Created,
                        delta.Destroyed, delta.OwnerChanged));
                }
            }
        }

        return rows.Skip(Math.Max(0, rows.Count - max)).ToList();
    }

    /// <summary>The retained records through the journal's other API (by correlation), as mirror records.</summary>
    private static List<MirrorRecord> RetainedFromJournal(GatewayEngine e)
    {
        var result = new List<MirrorRecord>();
        var correlations = new List<CorrelationId>();
        foreach (var r in e.State.Journal.Since(double.NegativeInfinity))
        {
            correlations.Add(r.Correlation);
        }

        foreach (var c in correlations)
        {
            Assert.True(e.State.Journal.TryGet(c, out var record));
            var deltas = new List<MirrorDelta>();
            foreach (var d in record.Entities)
            {
                var changes = new List<MirrorChange>();
                foreach (var w in e.State.Journal.GetWrites(c).ToEnumerable().Where(w => w.Entity == d.Id))
                {
                    changes.Add(new MirrorChange(w.Change.Attr, w.Change.Old, w.Change.New, w.Change.Changed));
                }

                deltas.Add(new MirrorDelta(d.Id, d.Created, d.Destroyed, d.OwnerChanged, d.PreviousOwner, changes.ToArray()));
            }

            result.Add(new MirrorRecord(c.Value, record.CausedBy.Value, record.Source, record.WallTime, record.SimTime, deltas.ToArray()));
        }

        return result;
    }

    private static IEnumerable<JournalWrite> ToEnumerable(this ChangeJournal.WriteEnumerator enumerator)
    {
        var list = new List<JournalWrite>();
        while (enumerator.MoveNext())
        {
            list.Add(enumerator.Current);
        }

        return list;
    }

    // ---- journal ---------------------------------------------------------------------------------------------------

    public static void AssertJournal(GatewayEngine e, JournalModel model, JournalMirror mirror, string context)
    {
        var journal = e.State.Journal;
        Assert.Equal(journal.Capacity == 0, model.HistoryOff);
        Assert.Equal(journal.EventCapacity == 0, model.EventsOff);
        Assert.Equal(journal.Count, model.RetainedRecords);
        Assert.Equal(journal.EventCount, model.RetainedEvents);

        var records = journal.Capacity == 0 ? new List<MirrorRecord>() : mirror.Records.Skip(mirror.Records.Count - journal.Count).ToList();
        records = records.Skip(Math.Max(0, records.Count - model.MaxRecords)).ToList();
        Assert.Equal(records.Count, model.Records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var m = model.Records[i];
            Assert.True(r.Corr == m.Correlation.Value && r.CausedBy == m.CausedBy.Value && r.Source == m.Source
                && r.Wall == m.WallTime && r.Sim == m.SimTime && r.Deltas.Length == m.Deltas.Count && m.EntityCount == r.Deltas.Length
                && e.Sources.NameOf(r.Source) == m.SourceName, $"{context}: record {i} header differs");
            for (int d = 0; d < r.Deltas.Length; d++)
            {
                var rd = r.Deltas[d];
                var md = m.Deltas[d];
                Assert.True(rd.Id == md.Entity && rd.Created == md.Created && rd.Destroyed == md.Destroyed
                    && rd.OwnerChanged == md.OwnerChanged && rd.PreviousOwner == md.PreviousOwner && rd.Changes.Length == md.Changes.Count,
                    $"{context}: record {i} delta {d} differs");
                for (int c = 0; c < rd.Changes.Length; c++)
                {
                    var rc = rd.Changes[c];
                    var mc = md.Changes[c];
                    Assert.True(rc.Attr == mc.Attr && rc.Old.Equals(mc.Old) && rc.New.Equals(mc.New) && rc.Changed == mc.Changed
                        && e.Attributes[rc.Attr].Name == mc.AttrName, $"{context}: record {i} delta {d} change {c} differs");
                }
            }
        }

        var events = journal.EventCapacity == 0 ? new List<MirrorEvent>() : mirror.Events.Skip(mirror.Events.Count - journal.EventCount).ToList();
        events = events.Skip(Math.Max(0, events.Count - model.MaxEvents)).ToList();
        Assert.Equal(events.Count, model.Events.Count);
        for (int i = 0; i < events.Count; i++)
        {
            var x = events[i];
            var m = model.Events[i];
            Assert.True(x.Type == m.Type && x.Corr == m.Correlation.Value && x.CausedBy == m.CausedBy.Value && x.Source == m.Source
                && x.Wall == m.WallTime && x.Sim == m.SimTime && x.Payload.Length == m.Payload.Count
                && e.EventTypes.NameOf(x.Type) == m.TypeName, $"{context}: event {i} differs");
            for (int p = 0; p < x.Payload.Length; p++)
            {
                Assert.True(x.Payload[p].Attr == m.Payload[p].Attr && x.Payload[p].Value.Equals(m.Payload[p].Value),
                    $"{context}: event {i} payload {p} differs");
            }
        }
    }
}
