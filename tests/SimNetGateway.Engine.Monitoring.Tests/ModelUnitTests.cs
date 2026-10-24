using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Engine.Monitoring.Tests;

/// <summary>Hand-computed expectations for the models (the properties compare with a brute-force oracle; these pin the semantics).</summary>
public class ModelUnitTests
{
    private static World Scripted(out EntityId alpha, out EntityId beta, out EntityId child, GatewayOptions? options = null)
    {
        var w = new World(options ?? new GatewayOptions { DiagnosticsCapacity = 64 }, 1);
        w.ChannelA.Send(new CreateMsg(1, "unit-1-Alpha", default, 10));       // entity 1, side a, key 1
        w.ChannelB.Send(new CreateMsg(2, "unit-2", default, 20));             // entity 2, side b, key n2
        w.Step();
        alpha = w.Live[0];
        beta = w.Live[1];
        w.ChannelA.Send(new CreateMsg(3, "child-3", alpha, 30));              // entity 3, member of alpha; id 3: also bound in b.name
        w.Step();
        child = w.Live[2];
        return w;
    }

    private static void AssertKeys(EntityRow row, params string?[] expected) =>
        Assert.True(row.Keys!.SequenceEqual(expected), "keys " + string.Join("|", row.Keys!) + " != " + string.Join("|", expected));

    private static EntityTableModel Table(World w) =>
        new(w.Engine, new EntityTableOptions { NameAttribute = w.Name, ParentAttribute = w.Parent });

    [Fact]
    public void The_entity_table_shows_names_origins_parents_and_keys_and_filters_and_sorts_them()
    {
        var w = Scripted(out var alpha, out var beta, out var child);
        var table = Table(w);

        table.Refresh();

        Assert.Equal(3, table.Count);
        Assert.Equal(["a.id", "b.name"], table.MapNames.ToArray());
        var rows = table.Rows;
        Assert.Equal((alpha, "unit-1-Alpha", "a", "a", default(EntityId)), (rows[0].Id, rows[0].Name, rows[0].OriginName, rows[0].OwnerName, rows[0].Parent));
        AssertKeys(rows[0], "1", null);
        AssertKeys(rows[1], null, "n2");
        Assert.Equal("b", rows[1].OriginName);
        Assert.Equal(alpha, rows[2].Parent);
        AssertKeys(rows[2], "3", "x3"); // the gateway also bound entity 3 for side b (id divisible by 3)
        Assert.Equal(4, rows[0].AttributeCount);   // Name, Pos, Count, and the Speed the processor derived from the position

        table.Filter = new EntityFilter { NameContains = "UNIT" };
        table.Refresh();
        Assert.Equal([alpha, beta], table.Rows.Select(r => r.Id).ToArray());

        table.Filter = new EntityFilter { Parent = alpha };
        table.Refresh();
        Assert.Equal([child], table.Rows.Select(r => r.Id).ToArray());

        table.Filter = new EntityFilter { KnownTo = ["b.name"], UnknownTo = ["a.id"] };
        table.Refresh();
        Assert.Equal([beta], table.Rows.Select(r => r.Id).ToArray());

        table.Filter = new EntityFilter { KnownTo = ["a.id", "b.name"] };
        table.Refresh();
        Assert.Equal([child], table.Rows.Select(r => r.Id).ToArray());

        table.Filter = new EntityFilter { Origin = w.SideB, Has = [w.Name], Lacks = [w.Label] };
        table.Refresh();
        Assert.Equal([beta], table.Rows.Select(r => r.Id).ToArray());

        table.Filter = new EntityFilter();
        table.Sort(EntityColumn.Name, descending: true);
        table.Refresh();
        Assert.Equal([beta, alpha, child], table.Rows.Select(r => r.Id).ToArray()); // unit-2, unit-1-Alpha, child-3

        table.Sort(EntityColumn.Key, descending: false, keyMap: 1); // b.name: unknown first, then n2, then x3
        table.Refresh();
        Assert.Equal([alpha, beta, child], table.Rows.Select(r => r.Id).ToArray());

        var page = new EntityRow[2];
        Assert.Equal(2, table.CopyPage(1, page));
        Assert.Equal([beta, child], page.Select(r => r.Id).ToArray());
        Assert.Equal(1, table.CopyPage(2, page));
        Assert.Equal(0, table.CopyPage(3, page));
    }

    [Fact]
    public void Entities_the_owner_side_stopped_writing_are_found_by_not_origin_written_within()
    {
        var w = new World(new GatewayOptions { DiagnosticsCapacity = 64 }, 1);
        w.ChannelA.Send(new CreateMsg(1, "stale", default, 1));
        w.ChannelB.Send(new CreateMsg(2, "chatty", default, 2));
        w.Step();
        var stale = w.Live[0];
        var chatty = w.Live[1];
        var table = Table(w);
        for (int i = 0; i < 180; i++) // 3 seconds: only the chatty one is written by its own side; the processor timer writes entity 1's speed
        {
            w.ChannelB.Send(new SetMsg(chatty, 0, i));
            w.Step();
        }

        table.Filter = new EntityFilter { NotOriginWrittenWithin = 1 };
        table.Refresh();
        Assert.Equal([stale], table.Rows.Select(r => r.Id).ToArray());

        table.Filter = new EntityFilter { OriginWrittenWithin = 1 };
        table.Refresh();
        Assert.Equal([chatty], table.Rows.Select(r => r.Id).ToArray());

        // the processor's writes keep the entity "written" but do not make its side look alive
        table.Filter = new EntityFilter { WrittenWithin = 1 };
        table.Refresh();
        Assert.Contains(stale, table.Rows.Select(r => r.Id));
        table.Filter = new EntityFilter { NotWrittenWithin = 1, NotOriginWrittenWithin = 1 };
        table.Refresh();
        Assert.DoesNotContain(stale, table.Rows.Select(r => r.Id));

        w.ChannelA.Send(new SetMsg(stale, 0, 99)); // its side writes again: no longer stale
        w.Step();
        table.Filter = new EntityFilter { NotOriginWrittenWithin = 1 };
        table.Refresh();
        Assert.Empty(table.Rows);
    }

    [Fact]
    public void The_inspector_shows_slots_identities_module_state_structure_and_recent_writes_and_keeps_a_removed_entity()
    {
        var w = Scripted(out var alpha, out var beta, out var child);
        var inspector = new InspectorModel(w.Engine);

        inspector.Load(alpha);

        Assert.Equal(InspectorState.Alive, inspector.State);
        Assert.Equal(["Name", "Pos", "Count", "Speed"], inspector.Slots.Select(s => s.Name).ToArray());
        Assert.Equal("proc", inspector.Slots[3].SourceName);
        Assert.Equal("Double(5)", inspector.Slots[3].Text);
        Assert.Equal("String(\"unit-1-Alpha\")", inspector.Slots[0].Text);
        Assert.Equal("a", inspector.Slots[0].SourceName);
        Assert.Equal(1u, inspector.Slots[0].Version);
        Assert.Equal([("a.id", "1"), ("b.name", null)], inspector.Identities.Select(i => (i.Map, i.Key)).ToArray());
        Assert.False(inspector.Identities[1].Known);
        Assert.Equal([("w.slots", "Slot { N = 1, Flag = True }")], inspector.ModuleState.Select(m => (m.Table, m.Text)).ToArray());
        Assert.Empty(inspector.References);
        var members = Assert.Single(inspector.Members);
        Assert.Equal("Parent", members.AttrName);
        Assert.Equal([child], members.Members);
        Assert.Contains(inspector.RecentWrites, r => r.Created && r.AttrName == "Name" && r.New.Equals(AttrValue.From("unit-1-Alpha")));

        inspector.Load(child);
        Assert.Equal([("Parent", alpha)], inspector.References.Select(r => (r.AttrName, r.Target)).ToArray());
        Assert.Equal(["a.id", "b.name"], inspector.Identities.Where(i => i.Known).Select(i => i.Map).ToArray());

        inspector.Load(beta); // entity 2 is even: it also has an entry in the second table
        Assert.Equal(["w.slots", "w.pending"], inspector.ModuleState.Select(m => m.Table).ToArray());
        Assert.Equal("Pend { Text = p2 }", inspector.ModuleState[1].Text);

        // the entity is destroyed: the inspector keeps the last copy, marked removed
        var slotsBefore = inspector.Slots.ToList();
        w.ChannelB.Send(new DestroyMsg(beta));
        w.Step();
        Assert.Null(w.Engine.State.Find(beta));
        inspector.Refresh();
        Assert.True(inspector.IsRemoved);
        Assert.Equal(slotsBefore, inspector.Slots);
        Assert.Equal("Pend { Text = p2 }", inspector.ModuleState[1].Text);
        Assert.Equal(w.Mirror.Records.Last(r => r.Deltas.Any(d => d.Id == beta && d.Destroyed)).Wall, inspector.RemovedWallTime);
    }

    [Fact]
    public void With_the_journal_off_the_inspector_says_so_and_still_shows_the_current_state()
    {
        var w = Scripted(out var alpha, out _, out _, new GatewayOptions { JournalCapacity = 0, EventLogCapacity = 0, DiagnosticsCapacity = 64 });
        var inspector = new InspectorModel(w.Engine);
        inspector.Load(alpha);

        Assert.True(inspector.HistoryOff);
        Assert.Empty(inspector.RecentWrites);
        Assert.Equal(4, inspector.Slots.Count);

        var journal = new JournalModel(w.Engine);
        journal.Refresh();
        Assert.True(journal.HistoryOff && journal.EventsOff);
        Assert.Empty(journal.Records);
        Assert.Empty(journal.Events);

        // removal time without a journal: the time of the refresh that noticed it
        w.ChannelA.Send(new DestroyMsg(alpha));
        w.Step();
        inspector.Refresh();
        Assert.True(inspector.IsRemoved);
        Assert.Equal(w.Engine.WallTime, inspector.RemovedWallTime);
    }

    [Fact]
    public void The_journal_model_copies_records_and_events_and_follows_cause_chains()
    {
        var w = new World(new GatewayOptions { DiagnosticsCapacity = 64 }, 2);
        w.ChannelA.Send(new CreateMsg(1, "unit", default, 9)); // x = 9: the position rule publishes an event and writes the speed
        w.Step();
        var journal = new JournalModel(w.Engine, 100, 100);

        journal.Refresh();

        Assert.False(journal.HistoryOff);
        var first = journal.Records[0];
        Assert.Equal("a", first.SourceName);
        Assert.Equal(1, first.EntityCount);
        Assert.True(first.Deltas[0].Created);
        Assert.Equal(["Name", "Pos", "Count"], first.Deltas[0].Changes.Select(c => c.AttrName).ToArray());
        var cascade = journal.Records.First(r => !r.CausedBy.IsNone);
        Assert.Equal(first.Correlation, cascade.CausedBy);
        Assert.Equal("proc", cascade.SourceName);
        Assert.Equal([first.Correlation], journal.CausedByChain(cascade.Correlation));
        Assert.Empty(journal.CausedByChain(first.Correlation));
        var ev = Assert.Single(journal.Events);
        Assert.Equal("Other", ev.TypeName);
        Assert.Equal(first.Correlation, ev.CausedBy);
        Assert.Equal([("Speed", AttrValue.From(9.0))], ev.Payload.Select(p => (p.AttrName, p.Value)).ToArray());
        Assert.True(journal.TryFindRecord(cascade.Correlation, out var found));
        Assert.Same(cascade, found);
        Assert.False(journal.TryFindRecord(new CorrelationId(9999), out _));
    }

    [Fact]
    public void The_rule_model_computes_rates_per_second_over_its_window()
    {
        var clock = new ManualClock();
        var engine = new GatewayEngine(clock);
        RuleInfo steady = null!;
        engine.AddModule(new LambdaModule("r", s =>
        {
            steady = s.OnTickEnd().Named("Steady").Do(_ => { });
            s.OnTickEnd().Named("Rejecting").When(_ => false).Do(_ => { });
            s.OnTickEnd().Named("Failing").Do(_ => throw new InvalidOperationException());
            s.OnTickEnd().Named("Never").Disabled().Do(_ => { });
        }));
        var model = new RuleTableModel(engine, 1.0, 64);

        void Tick()
        {
            clock.Advance(0.25);
            engine.Tick();
            model.Refresh();
        }

        Tick();
        Assert.Equal(0.0, model.Rows[0].FireRate); // no earlier refresh to compare with
        for (int i = 0; i < 7; i++)
        {
            Tick();
        }

        // 8 ticks at 0.25 s: every active rule ran 4 times per second; the window holds 5 snapshots
        Assert.Equal([4.0, 0.0, 0.0, 0.0], model.Rows.Select(r => r.FireRate).ToArray());
        Assert.Equal([0.0, 4.0, 0.0, 0.0], model.Rows.Select(r => r.RejectRate).ToArray());
        Assert.Equal([0.0, 0.0, 4.0, 0.0], model.Rows.Select(r => r.FailureRate).ToArray());
        Assert.Equal([8L, 0L, 0L, 0L], model.Rows.Select(r => r.FireCount).ToArray());
        Assert.False(model.Rows[3].Enabled);
        Assert.Equal("r/Steady", model.Rows[0].FullName);
        Assert.Equal("OnTickEnd()", model.Rows[0].Trigger);

        steady.Enabled = false; // the counter freezes and the rate falls to 0 once the window has passed
        for (int i = 0; i < 4; i++)
        {
            Tick();
        }

        Assert.Equal(0.0, model.Rows[0].FireRate);
        Assert.Equal(8L, model.Rows[0].FireCount);
        Assert.False(model.Rows[0].Enabled);
        Assert.Equal(4.0, model.Rows[1].RejectRate);

        model.Filter = new RuleFilter { Failed = true };
        model.Refresh();
        Assert.Equal(["r/Failing"], model.Rows.Select(r => r.FullName).ToArray());
        model.Filter = new RuleFilter { NeverFired = true };
        model.Refresh();
        Assert.Equal(["r/Rejecting", "r/Failing", "r/Never"], model.Rows.Select(r => r.FullName).ToArray());
        model.Filter = new RuleFilter { Rejecting = true, Text = "rej" };
        model.Refresh();
        Assert.Equal(["r/Rejecting"], model.Rows.Select(r => r.FullName).ToArray());
        Assert.Equal(4, model.TotalRules);
    }

    [Fact]
    public void The_vocabulary_and_diagnostics_and_identity_models_show_what_the_engine_reports()
    {
        var clock = new ManualClock();
        var engine = new GatewayEngine(clock);
        var made = engine.Attributes.Register("Made", AttrKind.Int, "never read");
        var read = engine.Attributes.Register("Read", AttrKind.Int, "never written");
        engine.AddModule(new LambdaModule("m", s =>
        {
            s.Produces(made);
            s.OnAttributesChanged(read).Do(_ => { });
            s.Identities.Create<int>(engine.Sources.Register("side"), "side.id");
        }));
        var vocabulary = new VocabularyModel(engine);
        var diagnostics = new DiagnosticsModel(engine);
        var identities = new IdentityModel(engine, m => new AllocatorInfo("pool", 1, 2, 3, 6));

        engine.Diagnostics.Info("m", "hello");
        engine.Diagnostics.Warn("m", "careful");
        engine.Diagnostics.Error("n", "broken");
        vocabulary.Refresh();
        diagnostics.Refresh();
        identities.Refresh();

        Assert.False(vocabulary.HasErrors);
        Assert.Equal(["attribute-produced-never-consumed", "attribute-consumed-never-produced"],
            vocabulary.Warnings.OrderBy(w => w.Subject == "Made" ? 0 : 1).Select(w => w.Code).ToArray());
        Assert.Contains("Made : Int - never read", vocabulary.Dump);
        Assert.Contains("consumed by: m", vocabulary.Dump);

        Assert.Equal(3, diagnostics.Entries.Count);
        diagnostics.MinSeverity = DiagnosticSeverity.Warning;
        diagnostics.Refresh();
        Assert.Equal(["careful", "broken"], diagnostics.Entries.Select(e => e.Message).ToArray());
        diagnostics.Text = "BRO";
        diagnostics.Refresh();
        Assert.Equal(["broken"], diagnostics.Entries.Select(e => e.Message).ToArray());
        Assert.Equal(3, diagnostics.Counters.EntriesRetained);

        var map = Assert.Single(identities.Maps);
        Assert.Equal(("side.id", "side", "Int32", 0), (map.Name, map.Side, map.KeyType, map.Count));
        Assert.Equal(new AllocatorInfo("pool", 1, 2, 3, 6), map.Allocator);
    }

    [Fact]
    public void The_inspector_of_an_id_that_never_lived_stays_empty()
    {
        var w = Scripted(out _, out _, out _);
        var inspector = new InspectorModel(w.Engine);

        inspector.Load(new EntityId(4242));

        Assert.Equal(InspectorState.NoSelection, inspector.State);
        Assert.Empty(inspector.Slots);
        inspector.Refresh();
        Assert.Equal(InspectorState.NoSelection, inspector.State);
    }
}
