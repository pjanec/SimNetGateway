using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Monitoring.Tests;

/// <summary>
/// Every model on one engine, refreshed together and compared with the brute-force oracle: random filters and sort orders for the
/// entity table, a random selected entity (kept across its destruction) for the inspector, random filters for the small models.
/// </summary>
internal sealed class AllModels
{
    private static readonly double[] Windows = [0.0625, 0.125, 0.25, 0.5, 1, 2, 5];
    private static readonly string[] Texts = ["unit-1", "Alpha", "-", "zzz", "N", "n"];

    private readonly GatewayEngine _engine;
    private readonly JournalMirror? _mirror;
    private readonly Func<IIdentityMap, AllocatorInfo?>? _hook;
    private readonly RuleOracle _ruleOracle;
    private readonly Random _rnd;
    private readonly List<EntityId> _live = new();

    // the inspector's last alive copy (to check "removed" keeps it)
    private List<SlotRow>? _lastSlots;
    private List<IdentityRow>? _lastIdentities;
    private List<ModuleStateRow>? _lastModuleState;
    private List<WriteRow>? _lastWrites;
    private EntityId _removedId;
    private double _removedWall;

    public readonly EntityTableOptions Options;
    public readonly EntityTableModel Table;
    public readonly InspectorModel Inspector;
    public readonly RuleTableModel Rules;
    public readonly JournalModel Journal;
    public readonly IdentityModel Identities;
    public readonly DiagnosticsModel Diagnostics;
    public readonly VocabularyModel Vocabulary;

    public AllModels(GatewayEngine engine, EntityTableOptions options, JournalMirror? mirror, int seed,
        Func<IIdentityMap, AllocatorInfo?>? allocatorHook = null, int maxRecords = 40, int maxWrites = 16)
    {
        _engine = engine;
        _mirror = mirror;
        _hook = allocatorHook;
        _rnd = new Random(seed);
        Options = options;
        Table = new EntityTableModel(engine, options);
        Inspector = new InspectorModel(engine, maxWrites);
        Rules = new RuleTableModel(engine, 0.5, 32);
        _ruleOracle = new RuleOracle(0.5, 32);
        Journal = new JournalModel(engine, maxRecords, maxRecords);
        Identities = new IdentityModel(engine, allocatorHook);
        Diagnostics = new DiagnosticsModel(engine);
        Vocabulary = new VocabularyModel(engine);
    }

    /// <summary>Refreshes all models and checks each against the oracle.</summary>
    public void RefreshAndCheck(string context)
    {
        var e = _engine;
        _live.Clear();
        foreach (var x in e.State.Entities)
        {
            _live.Add(x.Id);
        }

        _live.Sort((a, b) => a.Value.CompareTo(b.Value));

        // entity table
        var filter = RandomFilter();
        var column = (EntityColumn)_rnd.Next(11);
        bool descending = _rnd.Next(2) == 0;
        int keyMap = e.Identities.All.Count == 0 ? 0 : _rnd.Next(e.Identities.All.Count);
        Table.Filter = filter;
        Table.Sort(column, descending, keyMap);
        Table.Refresh();
        Oracle.AssertRows(Table.Rows, Oracle.ExpectedRows(e, Options, filter, column, descending, keyMap),
            $"{context} table {column}{(descending ? " desc" : "")} {filter}");
        Assert.Equal(e.State.Count, Table.TotalEntities);

        // inspector
        if (Inspector.Selected.IsNone || (_rnd.Next(6) == 0 && _live.Count > 0))
        {
            if (_live.Count > 0)
            {
                Inspector.Load(_live[_rnd.Next(_live.Count)]);
                _lastSlots = null;
            }
        }
        else
        {
            Inspector.Refresh();
        }

        CheckInspector(context);

        // rules
        var ruleFilter = new RuleFilter
        {
            Failed = _rnd.Next(5) == 0,
            Rejecting = _rnd.Next(5) == 0,
            NeverFired = _rnd.Next(5) == 0,
            Text = _rnd.Next(4) == 0 ? "W/" : null,
        };
        Rules.Filter = ruleFilter;
        Rules.Refresh();
        SmallModels.AssertRules(Rules.Rows, _ruleOracle.Expect(e, ruleFilter), context);
        Assert.Equal(e.Rules.Count, Rules.TotalRules);

        // journal
        Journal.Refresh();
        if (_mirror is not null)
        {
            Oracle.AssertJournal(e, Journal, _mirror, context);
        }

        // identities, diagnostics, vocabulary
        Identities.Refresh();
        SmallModels.AssertIdentities(e, Identities, _hook, context);
        Diagnostics.MinSeverity = (DiagnosticSeverity)_rnd.Next(3);
        Diagnostics.Text = _rnd.Next(3) == 0 ? "ransaction" : null;
        Diagnostics.Refresh();
        SmallModels.AssertDiagnostics(e, Diagnostics, context);
        Vocabulary.Refresh();
        SmallModels.AssertVocabulary(e, Vocabulary, context);
    }

    private void CheckInspector(string context)
    {
        var e = _engine;
        var id = Inspector.Selected;
        if (id.IsNone)
        {
            return;
        }

        if (e.State.Find(id) is { IsAlive: true })
        {
            Oracle.AssertInspector(e, Inspector, Options, _mirror, context);
            _lastSlots = Inspector.Slots.ToList();
            _lastIdentities = Inspector.Identities.ToList();
            _lastModuleState = Inspector.ModuleState.ToList();
            _lastWrites = Inspector.RecentWrites.ToList();
            return;
        }

        if (_lastSlots is null)
        {
            Assert.Equal(InspectorState.NoSelection, Inspector.State);
            return;
        }

        // removed: the last copy stays, with the time of the removal
        Assert.True(Inspector.IsRemoved, $"{context}: entity {id.Value} is gone but the inspector says {Inspector.State}");
        Assert.True(_lastSlots.SequenceEqual(Inspector.Slots), $"{context}: removed entity {id.Value} lost its slots");
        Assert.True(_lastIdentities!.SequenceEqual(Inspector.Identities), $"{context}: removed entity {id.Value} lost its identities");
        Assert.True(_lastModuleState!.SequenceEqual(Inspector.ModuleState), $"{context}: removed entity {id.Value} lost its module state");
        Assert.True(_lastWrites!.SequenceEqual(Inspector.RecentWrites), $"{context}: removed entity {id.Value} lost its writes");
        if (_removedId != id)
        {
            _removedId = id;
            double expected = e.WallTime;
            if (_mirror is not null && e.State.Journal.Capacity > 0)
            {
                var retained = _mirror.Records.Skip(_mirror.Records.Count - e.State.Journal.Count);
                var destroying = retained.LastOrDefault(r => r.Deltas.Any(d => d.Id == id && d.Destroyed));
                if (destroying is not null)
                {
                    expected = destroying.Wall;
                }
            }

            _removedWall = expected;
        }

        Assert.Equal(_removedWall, Inspector.RemovedWallTime);
    }

    private EntityFilter RandomFilter()
    {
        var e = _engine;
        var r = _rnd;
        bool Pick(int outOf = 4) => r.Next(outOf) == 0;
        double Window() => Windows[r.Next(Windows.Length)];
        var attrs = e.Attributes.Select(d => d.Id).ToList();
        var maps = e.Identities.All.Select(m => m.Name).ToList();
        SourceId Source() => new(r.Next(1, e.Sources.Count + 1));
        return new EntityFilter
        {
            Origin = Pick() ? Source() : null,
            Owner = Pick(6) ? Source() : null,
            ChangedWithin = Pick() ? Window() : null,
            WrittenWithin = Pick(6) ? Window() : null,
            NotWrittenWithin = Pick(6) ? Window() : null,
            OriginWrittenWithin = Pick(6) ? Window() : null,
            NotOriginWrittenWithin = Pick() ? Window() : null,
            Has = Pick() ? Take(attrs, r.Next(1, 3)) : null,
            Lacks = Pick() ? Take(attrs, r.Next(1, 3)) : null,
            KnownTo = Pick() && maps.Count > 0 ? Take(maps, r.Next(1, 3)) : null,
            UnknownTo = Pick() && maps.Count > 0 ? Take(maps, r.Next(1, 3)) : null,
            NameContains = Pick() ? Texts[r.Next(Texts.Length)] : null,
            Parent = Pick(6) && _live.Count > 0 && !Options.ParentAttribute.IsNone ? _live[r.Next(_live.Count)] : null,
        };
    }

    private List<T> Take<T>(List<T> source, int n)
    {
        var copy = source.ToList();
        var result = new List<T>();
        for (int i = 0; i < n && copy.Count > 0; i++)
        {
            int k = _rnd.Next(copy.Count);
            result.Add(copy[k]);
            copy.RemoveAt(k);
        }

        return result;
    }
}
