using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Common.Tests;

/// <summary>A module built from a lambda.</summary>
internal sealed class LambdaModule : IGatewayModule
{
    private readonly Action<ModuleSetup> _register;

    public LambdaModule(string name, Action<ModuleSetup> register)
    {
        Name = name;
        _register = register;
    }

    public string Name { get; }

    public void Register(ModuleSetup setup) => _register(setup);
}

/// <summary>A small test schema in a real engine on a manual clock.</summary>
internal sealed class Rig
{
    public readonly ManualClock Clock = new();
    public readonly GatewayEngine Engine;
    public readonly AttrId Pos, Vel, Acc, Ori, AngVel, Alg, StateTime, PredPos, PredAt;
    public readonly SourceId Net, Other, Dr, Reaper, Host;
    public readonly PredictorRegistry Predictors = PredictorRegistry.CreateDis();
    public readonly List<(SourceId Source, int Entities, List<AttrId> Attrs)> ChangeSets = new();
    public readonly List<(EntityId Entity, Vec3 Pos, double At)> PredictionWrites = new();
    public readonly List<(EntityId Entity, SourceId Source)> Removals = new();

    public Rig(GatewayOptions? options = null)
    {
        Engine = new GatewayEngine(Clock, options);
        Pos = Engine.Attributes.Register("pos", AttrKind.Vec3);
        Vel = Engine.Attributes.Register("vel", AttrKind.Vec3);
        Acc = Engine.Attributes.Register("acc", AttrKind.Vec3);
        Ori = Engine.Attributes.Register("ori", AttrKind.Vec3);
        AngVel = Engine.Attributes.Register("angvel", AttrKind.Vec3);
        Alg = Engine.Attributes.Register("alg", AttrKind.Int);
        StateTime = Engine.Attributes.Register("t", AttrKind.Double);
        PredPos = Engine.Attributes.Register("predpos", AttrKind.Vec3);
        PredAt = Engine.Attributes.Register("predat", AttrKind.Double);
        Net = Engine.Sources.Register("net");
        Other = Engine.Sources.Register("other");
        Dr = Engine.Sources.Register("proc.dr");
        Reaper = Engine.Sources.Register("proc.reaper");
        Host = Engine.Sources.Register("host");
    }

    public DrAttributeSet Input => new(Pos, Vel, Acc, Ori, AngVel, Alg, StateTime);

    public DrExtrapolationProcessor AddDr(double periodSeconds = 0.5, IIdentityMap? knownTo = null,
        Func<EntityRecord, bool>? select = null, string name = "dr", int slices = 1)
    {
        var dr = new DrExtrapolationProcessor(name, Dr, TimeSpan.FromSeconds(periodSeconds), Input, PredPos, PredAt,
            Predictors, knownTo, select, slices);
        Engine.AddModule(dr);
        return dr;
    }

    /// <summary>Records every change set and every write of the derived position, and every removal.</summary>
    public void AddObservers()
    {
        Engine.AddModule(new LambdaModule("observer", s =>
        {
            s.OnChangeSetEnd().Do(t =>
            {
                var attrs = new List<AttrId>();
                foreach (var delta in t.ChangeSet.Entities)
                {
                    foreach (var change in delta.Changes)
                    {
                        attrs.Add(change.Attr);
                    }
                }

                ChangeSets.Add((t.ChangeSet.Source, t.ChangeSet.Entities.Count, attrs));
            });
            s.OnAttributesChanged(PredPos).Do(t => PredictionWrites.Add(
                (t.Id, t.Entity.Get(PredPos).AsVec3(), t.Entity.Get(PredAt).AsDouble())));
            s.OnEntityRemoved().Do(t => Removals.Add((t.Id, t.Source)));
        }));
    }

    public EntityId CreateMover(Vec3 pos, Vec3 vel, DisDeadReckoning alg, double stateTime = 0, Vec3? acc = null,
        SourceId? source = null)
    {
        var cs = Engine.BeginChange(source ?? Net);
        var id = cs.CreateEntity();
        cs.Set(id, Pos, AttrValue.From(pos));
        cs.Set(id, Vel, AttrValue.From(vel));
        if (acc is not null)
        {
            cs.Set(id, Acc, AttrValue.From(acc.Value));
        }

        cs.Set(id, Alg, AttrValue.From((long)alg));
        cs.Set(id, StateTime, AttrValue.From(stateTime));
        Assert.True(cs.Commit().Succeeded);
        return id;
    }

    public void TickAt(double time)
    {
        Clock.Now = time;
        Engine.Tick();
    }

    public Vec3 PredictedPosition(EntityId id) => Engine.State.Find(id)!.Get(PredPos).AsVec3();
}

public class ProcessorTests
{
    // ---- DrExtrapolationProcessor -----------------------------------------------------------------------

    [Fact]
    public void Fpw_prediction_is_written_at_the_processor_period()
    {
        var r = new Rig();
        r.AddDr(periodSeconds: 0.5);
        var id = r.CreateMover(new Vec3(100, 0, 0), new Vec3(10, 0, 0), DisDeadReckoning.Fpw);

        r.TickAt(0.25);
        Assert.False(r.Engine.State.Find(id)!.Has(r.PredPos));   // not due yet

        r.TickAt(0.5);
        Assert.Equal(new Vec3(105, 0, 0), r.PredictedPosition(id));
        Assert.Equal(0.5, r.Engine.State.Find(id)!.Get(r.PredAt).AsDouble());

        r.TickAt(0.75);
        Assert.Equal(new Vec3(105, 0, 0), r.PredictedPosition(id));

        r.TickAt(1.0);
        Assert.Equal(new Vec3(110, 0, 0), r.PredictedPosition(id));
        Assert.Equal(1.0, r.Engine.State.Find(id)!.Get(r.PredAt).AsDouble());
    }

    [Fact]
    public void Rvw_prediction_uses_the_acceleration_and_the_state_time()
    {
        var r = new Rig();
        r.AddDr(periodSeconds: 1.0);
        // Valid since sim time 0.5; the prediction at sim time 2 has dt = 1.5.
        var id = r.CreateMover(new Vec3(0, 0, 0), new Vec3(2, 0, 0), DisDeadReckoning.Rvw, stateTime: 0.5, acc: new Vec3(0, 4, 0));

        r.TickAt(1.0);
        r.TickAt(2.0);
        // dt = 1.5: x = 2 * 1.5 = 3, y = 0.5 * 4 * 2.25 = 4.5
        Assert.Equal(new Vec3(3, 4.5, 0), r.PredictedPosition(id));
    }

    [Fact]
    public void Entities_are_predicted_with_the_predictor_of_their_own_algorithm()
    {
        var r = new Rig();
        r.AddDr(periodSeconds: 1.0);
        var fpw = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw, acc: new Vec3(0, 2, 0));
        var fvw = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fvw, acc: new Vec3(0, 2, 0));
        r.TickAt(1.0);
        r.TickAt(2.0);
        Assert.Equal(new Vec3(2, 0, 0), r.PredictedPosition(fpw));
        Assert.Equal(new Vec3(2, 4, 0), r.PredictedPosition(fvw));
    }

    [Fact]
    public void A_static_entity_is_written_once_and_a_moving_one_every_period()
    {
        var r = new Rig();
        r.AddObservers();
        r.AddDr(periodSeconds: 0.5);
        var still = r.CreateMover(new Vec3(7, 8, 9), Vec3.Zero, DisDeadReckoning.Static);
        var moving = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw);

        for (int i = 1; i <= 6; i++)
        {
            r.TickAt(0.5 * i);
        }

        Assert.Equal(1, r.PredictionWrites.Count(w => w.Entity == still));
        Assert.Equal(6, r.PredictionWrites.Count(w => w.Entity == moving));
        Assert.Equal(new Vec3(7, 8, 9), r.PredictedPosition(still));
        Assert.Equal(new Vec3(3, 0, 0), r.PredictedPosition(moving));
    }

    [Fact]
    public void All_predictions_of_a_firing_are_one_transaction_from_the_processor_source_touching_only_derived_attributes()
    {
        var r = new Rig();
        r.AddObservers();
        r.AddDr(periodSeconds: 0.5);
        var a = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw);
        var b = r.CreateMover(Vec3.Zero, new Vec3(0, 1, 0), DisDeadReckoning.Fpw);
        r.ChangeSets.Clear();

        r.TickAt(0.5);

        var drSets = r.ChangeSets.Where(c => c.Source == r.Dr).ToList();
        var set = Assert.Single(drSets);
        Assert.Equal(2, set.Entities);
        Assert.All(set.Attrs, attr => Assert.True(attr == r.PredPos || attr == r.PredAt));
        Assert.Equal(2, set.Attrs.Count(x => x == r.PredPos));
        Assert.Equal(2, set.Attrs.Count(x => x == r.PredAt));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void The_input_attributes_are_never_written()
    {
        var r = new Rig();
        r.AddDr(periodSeconds: 0.5);
        var id = r.CreateMover(new Vec3(1, 2, 3), new Vec3(1, 1, 1), DisDeadReckoning.Fpw, acc: new Vec3(0, 0, 1));
        var inputs = new[] { r.Pos, r.Vel, r.Acc, r.Alg, r.StateTime };
        var versions = VersionsOf(r.Engine.State.Find(id)!, inputs);

        for (int i = 1; i <= 4; i++)
        {
            r.TickAt(0.5 * i);
        }

        var after = r.Engine.State.Find(id)!;
        Assert.Equal(new Vec3(1, 2, 3), after.Get(r.Pos).AsVec3());
        Assert.Equal(new Vec3(1, 1, 1), after.Get(r.Vel).AsVec3());
        Assert.Equal(versions, VersionsOf(after, inputs));
        Assert.Equal(new Vec3(3, 4, 5), r.PredictedPosition(id)); // FPW ignores the acceleration
    }

    private static long[] VersionsOf(EntityRecord entity, AttrId[] attributes)
    {
        var versions = new long[attributes.Length];
        for (int i = 0; i < attributes.Length; i++)
        {
            versions[i] = entity.TryGetSlot(attributes[i], out var slot) ? slot.Meta.Version : -1;
        }

        return versions;
    }

    [Fact]
    public void Only_entities_known_to_the_identity_map_are_extrapolated()
    {
        var r = new Rig();
        var display = r.Engine.Sources.Register("display");
        var labels = r.Engine.Identities.Create<string>(display, "display.label");
        var known = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw);
        var unknown = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw);
        Assert.True(labels.Bind(known, "K"));

        r.AddDr(periodSeconds: 1.0, knownTo: labels);
        r.TickAt(1.0);

        Assert.Equal(new Vec3(1, 0, 0), r.PredictedPosition(known));
        Assert.False(r.Engine.State.Find(unknown)!.Has(r.PredPos));
    }

    [Fact]
    public void Only_entities_accepted_by_the_selector_are_extrapolated()
    {
        var r = new Rig();
        var first = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw);
        var second = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw);
        r.AddDr(periodSeconds: 1.0, select: e => e.Id == second);
        r.TickAt(1.0);

        Assert.False(r.Engine.State.Find(first)!.Has(r.PredPos));
        Assert.Equal(new Vec3(1, 0, 0), r.PredictedPosition(second));
    }

    [Fact]
    public void Entities_without_the_required_attributes_are_ignored()
    {
        var r = new Rig();
        r.AddDr(periodSeconds: 1.0);
        var cs = r.Engine.BeginChange(r.Net);
        var noAlg = cs.CreateEntity();
        cs.Set(noAlg, r.Pos, AttrValue.From(Vec3.Zero));
        cs.Set(noAlg, r.StateTime, AttrValue.From(0.0));
        var noPos = cs.CreateEntity();
        cs.Set(noPos, r.Alg, AttrValue.From((long)DisDeadReckoning.Fpw));
        cs.Set(noPos, r.StateTime, AttrValue.From(0.0));
        Assert.True(cs.Commit().Succeeded);

        r.TickAt(1.0);

        Assert.False(r.Engine.State.Find(noAlg)!.Has(r.PredPos));
        Assert.False(r.Engine.State.Find(noPos)!.Has(r.PredPos));
    }

    [Fact]
    public void Entities_lacking_algorithm_or_state_time_are_ignored_without_a_warning()
    {
        var r = new Rig();
        r.AddDr(periodSeconds: 1.0);
        var cs = r.Engine.BeginChange(r.Net);
        var noAlg = cs.CreateEntity();
        cs.Set(noAlg, r.Pos, AttrValue.From(Vec3.Zero));
        cs.Set(noAlg, r.Vel, AttrValue.From(new Vec3(1, 0, 0)));
        cs.Set(noAlg, r.StateTime, AttrValue.From(0.0));
        var noTime = cs.CreateEntity();
        cs.Set(noTime, r.Pos, AttrValue.From(Vec3.Zero));
        cs.Set(noTime, r.Vel, AttrValue.From(new Vec3(1, 0, 0)));
        cs.Set(noTime, r.Alg, AttrValue.From((long)DisDeadReckoning.Fpw));
        Assert.True(cs.Commit().Succeeded);

        r.TickAt(1.0);
        r.TickAt(2.0);

        Assert.False(r.Engine.State.Find(noAlg)!.Has(r.PredPos));
        Assert.False(r.Engine.State.Find(noTime)!.Has(r.PredPos));
        foreach (var e in r.Engine.Diagnostics.Recent)
        {
            Assert.True(e.Severity == DiagnosticSeverity.Info, $"{e.Severity}: {e.Message}");
        }
    }

    [Fact]
    public void Orientation_and_angular_velocity_inputs_are_read_for_the_body_axis_algorithms()
    {
        var r = new Rig();
        r.AddDr(periodSeconds: 1.0);
        var basis = new PredictionBasis(new Vec3(1, 2, 3), new Vec3(10, 0, 0), Vec3.Zero, new Vec3(0.5, 0, 0), new Vec3(0, 0, 0.5), 0);
        var cs = r.Engine.BeginChange(r.Net);
        var id = cs.CreateEntity();
        cs.Set(id, r.Pos, AttrValue.From(basis.Position));
        cs.Set(id, r.Vel, AttrValue.From(basis.Velocity));
        cs.Set(id, r.Ori, AttrValue.From(basis.Orientation));
        cs.Set(id, r.AngVel, AttrValue.From(basis.AngularVelocity));
        cs.Set(id, r.Alg, AttrValue.From((long)DisDeadReckoning.Rpb));
        cs.Set(id, r.StateTime, AttrValue.From(0.0));
        Assert.True(cs.Commit().Succeeded);

        r.TickAt(4.0);

        var expected = new DisPredictor(DisDeadReckoning.Rpb).Predict(in basis, 4.0).Position;
        Assert.Equal(expected, r.PredictedPosition(id));
        var noRotation = new DisPredictor(DisDeadReckoning.Rpb).Predict(basis with { AngularVelocity = Vec3.Zero }, 4.0).Position;
        var noOrientation = new DisPredictor(DisDeadReckoning.Rpb).Predict(basis with { Orientation = Vec3.Zero }, 4.0).Position;
        Assert.NotEqual(noRotation, expected);
        Assert.NotEqual(noOrientation, expected);
    }

    [Fact]
    public void An_unknown_strategy_is_skipped_with_one_warning_per_id()
    {
        var r = new Rig();
        r.AddDr(periodSeconds: 1.0);
        var weird = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), (DisDeadReckoning)77);
        var other = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), (DisDeadReckoning)78);
        var fine = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw);

        r.TickAt(1.0);
        r.TickAt(2.0);
        r.TickAt(3.0);

        Assert.False(r.Engine.State.Find(weird)!.Has(r.PredPos));
        Assert.False(r.Engine.State.Find(other)!.Has(r.PredPos));
        Assert.Equal(new Vec3(3, 0, 0), r.PredictedPosition(fine));
        var warnings = new List<DiagnosticEntry>();
        foreach (var e in r.Engine.Diagnostics.Recent)
        {
            if (e.Severity == DiagnosticSeverity.Warning)
            {
                warnings.Add(e);
            }
        }

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("77"));
        Assert.Contains(warnings, w => w.Message.Contains("78"));
        Assert.All(warnings, w => Assert.Equal("dr", w.Origin));
    }

    [Fact]
    public void A_registered_custom_predictor_is_used()
    {
        var r = new Rig();
        r.Predictors.Register(new Jump());
        r.AddDr(periodSeconds: 1.0);
        var id = r.CreateMover(Vec3.Zero, Vec3.Zero, (DisDeadReckoning)500);
        r.TickAt(1.0);
        Assert.Equal(new Vec3(0, 0, 100), r.PredictedPosition(id));
    }

    private sealed class Jump : IPredictor
    {
        public int StrategyId => 500;
        public string Name => "jump";
        public PredictedState Predict(in PredictionBasis basis, double time) =>
            new(basis.Position + new Vec3(0, 0, 100), basis.Velocity, basis.Orientation);
    }

    [Fact]
    public void No_writes_while_the_session_is_paused()
    {
        var r = new Rig();
        r.AddObservers();
        r.AddDr(periodSeconds: 0.5);
        Assert.True(r.Engine.Execute(SessionCommand.Load("s"), r.Host));
        Assert.True(r.Engine.Execute(SessionCommand.Start, r.Host));
        var id = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw);

        r.TickAt(0.5);
        r.TickAt(1.0);
        Assert.Equal(2, r.PredictionWrites.Count);
        Assert.Equal(new Vec3(1, 0, 0), r.PredictedPosition(id));

        Assert.True(r.Engine.Execute(SessionCommand.Pause, r.Host));
        r.ChangeSets.Clear();
        r.TickAt(1.5);
        r.TickAt(2.0);
        r.TickAt(5.0);
        Assert.Equal(2, r.PredictionWrites.Count);
        Assert.DoesNotContain(r.ChangeSets, c => c.Source == r.Dr);
        Assert.Equal(new Vec3(1, 0, 0), r.PredictedPosition(id));

        Assert.True(r.Engine.Execute(SessionCommand.Resume, r.Host));
        r.TickAt(6.0);
        r.TickAt(6.5);
        Assert.True(r.PredictionWrites.Count > 2);
        Assert.True(r.PredictedPosition(id).X > 1);
    }

    [Fact]
    public void Disabling_the_extrapolate_rule_stops_the_writes()
    {
        var r = new Rig();
        r.AddObservers();
        r.AddDr(periodSeconds: 0.5);
        r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw);
        r.TickAt(0.5);
        Assert.Single(r.PredictionWrites);

        var rule = r.Engine.FindRule("dr/Extrapolate");
        Assert.NotNull(rule);
        rule!.Enabled = false;
        r.TickAt(1.0);
        r.TickAt(1.5);
        Assert.Single(r.PredictionWrites);

        rule.Enabled = true;
        r.TickAt(2.0);
        Assert.Equal(2, r.PredictionWrites.Count);
    }

    [Fact]
    public void The_processor_declares_what_it_produces_and_consumes()
    {
        var r = new Rig();
        r.AddDr();
        var production = Assert.Single(r.Engine.Productions);
        Assert.Equal("dr", production.Module);
        Assert.Equal(new[] { r.PredPos, r.PredAt }, production.Attributes);
    }

    [Fact]
    public void Constructor_arguments_are_validated()
    {
        var r = new Rig();
        var input = r.Input;
        Assert.Throws<ArgumentException>(() => new DrExtrapolationProcessor(
            "x", r.Dr, TimeSpan.FromSeconds(1), input with { Position = AttrId.None }, r.PredPos, r.PredAt, r.Predictors));
        Assert.Throws<ArgumentException>(() => new DrExtrapolationProcessor(
            "x", r.Dr, TimeSpan.FromSeconds(1), input with { Algorithm = AttrId.None }, r.PredPos, r.PredAt, r.Predictors));
        Assert.Throws<ArgumentException>(() => new DrExtrapolationProcessor(
            "x", r.Dr, TimeSpan.FromSeconds(1), input with { StateTime = AttrId.None }, r.PredPos, r.PredAt, r.Predictors));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DrExtrapolationProcessor(
            "x", r.Dr, TimeSpan.Zero, input, r.PredPos, r.PredAt, r.Predictors));
        Assert.Throws<ArgumentException>(() => new DrExtrapolationProcessor(
            "x", r.Dr, TimeSpan.FromSeconds(1), input, AttrId.None, r.PredAt, r.Predictors));
    }

    [Fact]
    public void Optional_inputs_may_be_left_out()
    {
        var r = new Rig();
        var minimal = new DrAttributeSet(r.Pos, AttrId.None, AttrId.None, AttrId.None, AttrId.None, r.Alg, r.StateTime);
        r.Engine.AddModule(new DrExtrapolationProcessor("dr", r.Dr, TimeSpan.FromSeconds(1), minimal, r.PredPos, r.PredAt, r.Predictors));
        var cs = r.Engine.BeginChange(r.Net);
        var id = cs.CreateEntity();
        cs.Set(id, r.Pos, AttrValue.From(new Vec3(5, 5, 5)));
        cs.Set(id, r.Alg, AttrValue.From((long)DisDeadReckoning.Rvw));
        cs.Set(id, r.StateTime, AttrValue.From(0.0));
        Assert.True(cs.Commit().Succeeded);

        r.TickAt(1.0);

        Assert.Equal(new Vec3(5, 5, 5), r.PredictedPosition(id));
    }

    [Fact]
    public void A_steady_state_firing_over_1000_moving_entities_allocates_nothing()
    {
        var r = new Rig(new GatewayOptions { JournalCapacity = 16 });
        r.AddDr(periodSeconds: 0.1);
        var cs = r.Engine.BeginChange(r.Net);
        EntityId first = default;
        for (int i = 0; i < 1000; i++)
        {
            var id = cs.CreateEntity();
            if (i == 0)
            {
                first = id;
            }

            cs.Set(id, r.Pos, AttrValue.From(new Vec3(i, 0, 0)));
            cs.Set(id, r.Vel, AttrValue.From(new Vec3(10, 1, 0)));
            cs.Set(id, r.Alg, AttrValue.From((long)DisDeadReckoning.Fpw));
            cs.Set(id, r.StateTime, AttrValue.From(0.0));
        }

        Assert.True(cs.Commit().Succeeded);

        double t = 0;
        for (int i = 0; i < 40; i++)
        {
            t += 0.1;
            r.TickAt(t);
        }

        var rule = r.Engine.FindRule("dr/Extrapolate")!;
        long firings = rule.FireCount;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++)
        {
            t += 0.1;
            r.TickAt(t);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(firings + 5, rule.FireCount);
        Assert.Equal(0, allocated);
        Assert.Equal(45 * 0.1 * 10, r.PredictedPosition(first).X, 6); // x0 = 0, v = 10 m/s, sim time 4.5 s
    }

    // ---- StaleEntityReaper ------------------------------------------------------------------------------

    private static Rig ReaperRig(out EntityId a1, out EntityId a2, out EntityId b1)
    {
        var r = new Rig();
        r.AddObservers();
        r.Engine.AddModule(new StaleEntityReaper("reaper", r.Net, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), r.Reaper));
        a1 = r.CreateMover(Vec3.Zero, Vec3.Zero, DisDeadReckoning.Static, source: r.Net);
        a2 = r.CreateMover(Vec3.Zero, Vec3.Zero, DisDeadReckoning.Static, source: r.Net);
        b1 = r.CreateMover(Vec3.Zero, Vec3.Zero, DisDeadReckoning.Static, source: r.Other);
        return r;
    }

    [Fact]
    public void The_reaper_destroys_stale_entities_of_its_side_only_after_the_timeout()
    {
        var r = ReaperRig(out var a1, out var a2, out var b1);

        r.TickAt(4.0);
        Assert.NotNull(r.Engine.State.Find(a1));
        r.TickAt(5.0);  // exactly the timeout old: NotWrittenWithin is exclusive at the edge
        Assert.NotNull(r.Engine.State.Find(a1));
        Assert.Empty(r.Removals);

        r.TickAt(6.0);
        Assert.Null(r.Engine.State.Find(a1));
        Assert.Null(r.Engine.State.Find(a2));
        Assert.NotNull(r.Engine.State.Find(b1));     // the other side is never reaped
        r.TickAt(100.0);
        Assert.NotNull(r.Engine.State.Find(b1));
    }

    [Fact]
    public void The_reaper_ignores_the_derived_writes_of_the_dr_processor_while_a_side_that_keeps_sending_survives()
    {
        // R-068: staleness counts only writes by the side itself. A real engine runs the DR processor at 10 Hz and the
        // reaper (timeout 12 s, checked every second) side by side.
        var r = new Rig();
        r.AddObservers();
        r.AddDr(periodSeconds: 0.1);
        r.Engine.AddModule(new StaleEntityReaper("reaper", r.Net, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(1), r.Reaper));
        var silent = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw, source: r.Net);
        var talking = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw, source: r.Net);
        var foreign = r.CreateMover(Vec3.Zero, new Vec3(1, 0, 0), DisDeadReckoning.Fpw, source: r.Other);

        for (int i = 1; i <= 120; i++)
        {
            double t = i / 10.0;
            if (i % 10 == 5)
            {
                var cs = r.Engine.BeginChange(r.Net);
                cs.Set(talking, r.Pos, AttrValue.From(new Vec3(t, 0, 0)));
                Assert.True(cs.Commit().Succeeded);
            }

            r.TickAt(t);
        }

        // 12 s after its last (creation) write the silent entity is still alive: the edge is inclusive
        var silentRecord = r.Engine.State.Find(silent)!;
        Assert.NotNull(silentRecord);
        Assert.Empty(r.Removals);
        // ...although the processor wrote its prediction in the very last period, so the plain write time is fresh
        Assert.True(silentRecord.LastWriteWallTime > 11.5);
        Assert.Equal(12.0, r.Engine.State.Now);
        Assert.Equal(0.0, silentRecord.LastOriginWriteWallTime);
        Assert.Equal(12.0, r.PredictedPosition(silent).X, 6); // predicted to the last tick

        for (int i = 121; i <= 129; i++)
        {
            r.TickAt(i / 10.0);
            Assert.NotNull(r.Engine.State.Find(silent)); // the reaper checks on whole seconds: not before 13 s
        }

        Assert.Empty(r.Removals);
        r.TickAt(13.0);
        Assert.Null(r.Engine.State.Find(silent));
        Assert.Equal(new[] { (silent, r.Reaper) }, r.Removals.ToArray());

        // the side that keeps sending, and the other side's entity, are never reaped, also much later
        for (int i = 131; i <= 400; i++)
        {
            double t = i / 10.0;
            if (i % 10 == 5)
            {
                var cs = r.Engine.BeginChange(r.Net);
                cs.Set(talking, r.Pos, AttrValue.From(new Vec3(t, 0, 0)));
                Assert.True(cs.Commit().Succeeded);
            }

            r.TickAt(t);
        }

        Assert.NotNull(r.Engine.State.Find(talking));
        Assert.NotNull(r.Engine.State.Find(foreign));
        Assert.Equal(new[] { (silent, r.Reaper) }, r.Removals.ToArray());
        Assert.True(r.PredictionWrites.Count(w => w.Entity == talking) > 300);
    }

    [Fact]
    public void Reaped_entities_leave_through_a_normal_removed_trigger_in_one_transaction()
    {
        var r = ReaperRig(out var a1, out var a2, out _);
        r.ChangeSets.Clear();

        r.TickAt(6.0);

        Assert.Equal(2, r.Removals.Count);
        Assert.Contains((a1, r.Reaper), r.Removals);
        Assert.Contains((a2, r.Reaper), r.Removals);
        var reaped = Assert.Single(r.ChangeSets, c => c.Source == r.Reaper);
        Assert.Equal(2, reaped.Entities);
        bool info = false;
        foreach (var e in r.Engine.Diagnostics.Recent)
        {
            info |= e.Severity == DiagnosticSeverity.Info && e.Origin == "reaper";
        }

        Assert.True(info);
    }

    [Fact]
    public void A_refreshed_entity_survives_until_it_is_stale_again()
    {
        var r = ReaperRig(out var a1, out var a2, out _);
        r.TickAt(4.0);
        var cs = r.Engine.BeginChange(r.Net);
        cs.Set(a2, r.Pos, AttrValue.From(new Vec3(1, 1, 1)));   // a write refreshes it
        Assert.True(cs.Commit().Succeeded);

        r.TickAt(6.0);
        Assert.Null(r.Engine.State.Find(a1));
        Assert.NotNull(r.Engine.State.Find(a2));
        r.TickAt(9.0);   // written at 4.0: exactly 5 s old
        Assert.NotNull(r.Engine.State.Find(a2));
        r.TickAt(10.0);
        Assert.Null(r.Engine.State.Find(a2));
    }

    [Fact]
    public void The_reaper_rule_can_be_disabled_and_it_checks_at_its_own_period()
    {
        var r = ReaperRig(out var a1, out _, out _);
        var rule = r.Engine.FindRule("reaper/Reap");
        Assert.NotNull(rule);
        rule!.Enabled = false;
        r.TickAt(50.0);
        Assert.NotNull(r.Engine.State.Find(a1));
        rule.Enabled = true;
        r.TickAt(50.5);       // enabled: fires at the next tick (due is in the past)
        Assert.Null(r.Engine.State.Find(a1));
    }

    [Fact]
    public void The_reaper_fires_every_check_period()
    {
        var r = ReaperRig(out var a1, out _, out _);
        var rule = r.Engine.FindRule("reaper/Reap");
        Assert.NotNull(rule);
        r.TickAt(4.0);
        Assert.NotNull(r.Engine.State.Find(a1));
        long fired = rule!.FireCount;
        r.TickAt(5.5);   // due at 5.0 with a 1 s period; the entities are 5.5 s old
        Assert.Equal(fired + 1, rule.FireCount);
        Assert.Null(r.Engine.State.Find(a1));
    }

    [Fact]
    public void Reaper_constructor_arguments_are_validated()
    {
        var r = new Rig();
        Assert.Throws<ArgumentOutOfRangeException>(() => new StaleEntityReaper("x", r.Net, TimeSpan.Zero, TimeSpan.FromSeconds(1), r.Reaper));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StaleEntityReaper("x", r.Net, TimeSpan.FromSeconds(1), TimeSpan.Zero, r.Reaper));
    }
}
