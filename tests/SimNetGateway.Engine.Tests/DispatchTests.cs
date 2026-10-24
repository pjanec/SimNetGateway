using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class DispatchTests
{
    [Fact]
    public void A_rule_fires_once_when_several_of_its_attributes_change()
    {
        var f = new EngineFixture();
        int fired = 0;
        f.Module("m", s => s.OnAttributesChanged(f.Pos, f.Speed).Do(_ => fired++));
        var id = f.Create();

        var cs = f.Engine.BeginChange(f.Net);
        cs.Set(id, f.Pos, EngineFixture.V(1));
        cs.Set(id, f.Speed, EngineFixture.D(2));
        cs.Commit();

        Assert.Equal(1, fired);
    }

    [Fact]
    public void One_trigger_per_entity_carries_the_changes_and_metadata()
    {
        var f = new EngineFixture();
        var seen = new List<(EntityTriggerKind, EntityId, bool, bool, bool, SourceId, long)>();
        f.Module("m", s => s.OnAttributesWritten().Do(t =>
            seen.Add((t.Kind, t.Id, t.Changed(f.Pos), t.Changed(f.Speed), t.Written(f.Speed), t.Source, t.Correlation.Value))));
        var a = f.Create();
        var b = f.Create();
        seen.Clear();

        var cs = f.Engine.BeginChange(f.Proc);
        cs.Set(a, f.Pos, EngineFixture.V(1));
        cs.Set(b, f.Pos, EngineFixture.V(1));
        cs.Set(a, f.Speed, EngineFixture.D(5));
        var result = cs.Commit();

        Assert.Equal(2, seen.Count);
        Assert.Equal((EntityTriggerKind.Updated, a, true, true, true, f.Proc, result.Record!.Correlation.Value), seen[0]);
        Assert.Equal((EntityTriggerKind.Updated, b, true, false, false, f.Proc, result.Record!.Correlation.Value), seen[1]);
    }

    [Fact]
    public void A_false_condition_skips_the_action_and_counts_the_reject()
    {
        var f = new EngineFixture();
        int fired = 0;
        RuleInfo rule = null!;
        f.Module("m", s => rule = s.OnAttributesChanged(f.Pos).When(_ => false).Do(_ => fired++));
        var id = f.Create();

        f.Write(id, f.Pos, EngineFixture.V(1));

        Assert.Equal(0, fired);
        Assert.Equal(1, rule.ConditionRejects);
        Assert.Equal(0, rule.FireCount);
        Assert.Equal(0, rule.Failures);
    }

    [Fact]
    public void Several_conditions_are_anded_in_order()
    {
        var f = new EngineFixture();
        var log = new List<string>();
        RuleInfo rule = null!;
        f.Module("m", s => rule = s.OnEntityCreated()
            .When(_ => { log.Add("c1"); return true; })
            .When(_ => { log.Add("c2"); return false; })
            .When(_ => { log.Add("c3"); return true; })
            .Do(_ => log.Add("action")));

        f.Create();

        Assert.Equal(new[] { "c1", "c2" }, log);
        Assert.Equal(1, rule.ConditionRejects);
    }

    [Fact]
    public void Rules_fire_in_registration_order_across_kinds_and_modules()
    {
        var f = new EngineFixture();
        var log = new List<string>();
        f.Module("m1", s =>
        {
            s.OnAttributesChanged(f.Pos).Named("A").Do(_ => log.Add("A"));
            s.OnEntityCreated().Named("B").Do(_ => log.Add("B"));
        });
        f.Module("m2", s =>
        {
            s.OnAttributesWritten().Named("C").Do(_ => log.Add("C"));
            s.OnAttributesChanged().Named("D").Do(_ => log.Add("D"));
            s.OnAttributesWritten(f.Pos, f.Speed).Named("E").Do(_ => log.Add("E"));
        });

        var cs = f.Engine.BeginChange(f.Net);
        var id = cs.CreateEntity();
        cs.Set(id, f.Pos, EngineFixture.V(1));
        cs.Set(id, f.Speed, EngineFixture.D(1));
        cs.Commit();

        Assert.Equal(new[] { "A", "B", "C", "D", "E" }, log);
        Assert.Equal(new[] { "m1/A", "m1/B", "m2/C", "m2/D", "m2/E" }, f.Engine.Rules.Select(r => r.FullName).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, f.Engine.Rules.Select(r => r.RegistrationIndex).ToArray());
    }

    [Fact]
    public void Changed_and_written_subscriptions_differ_for_a_repeated_value()
    {
        var f = new EngineFixture();
        int changed = 0, written = 0, anyChanged = 0, anyWritten = 0;
        f.Module("m", s =>
        {
            s.OnAttributesChanged(f.Pos).Do(_ => changed++);
            s.OnAttributesWritten(f.Pos).Do(_ => written++);
            s.OnAttributesChanged().Do(_ => anyChanged++);
            s.OnAttributesWritten().Do(_ => anyWritten++);
        });
        var id = f.Create();
        f.Write(id, f.Pos, EngineFixture.V(1));
        Assert.Equal((1, 1, 1, 1), (changed, written, anyChanged, anyWritten));

        var result = f.Write(id, f.Pos, EngineFixture.V(1)); // same value

        Assert.Equal(CommitStatus.Committed, result.Status);
        Assert.Equal((1, 2, 1, 2), (changed, written, anyChanged, anyWritten));
    }

    [Fact]
    public void An_empty_attribute_list_means_any_attribute()
    {
        var f = new EngineFixture();
        int any = 0, pos = 0;
        f.Module("m", s =>
        {
            s.OnAttributesChanged().Do(_ => any++);
            s.OnAttributesChanged(f.Pos).Do(_ => pos++);
        });
        var id = f.Create(); // creation without attributes: no attribute entry, nothing fires

        Assert.Equal((0, 0), (any, pos));

        f.Write(id, f.Label, AttrValue.From("x"));
        Assert.Equal((1, 0), (any, pos));

        f.Write(id, f.Pos, EngineFixture.V(1));
        Assert.Equal((2, 1), (any, pos));
    }

    [Fact]
    public void A_clear_counts_as_a_change_of_the_attribute()
    {
        var f = new EngineFixture();
        int changed = 0;
        f.Module("m", s => s.OnAttributesChanged(f.Pos).Do(_ => changed++));
        var id = f.Create();
        f.Write(id, f.Pos, EngineFixture.V(1));

        var cs = f.Engine.BeginChange(f.Net);
        cs.Clear(id, f.Pos);
        cs.Commit();

        Assert.Equal(2, changed);
    }

    [Fact]
    public void A_created_entity_matches_created_and_changed_rules_in_one_trigger()
    {
        var f = new EngineFixture();
        var log = new List<string>();
        f.Module("m", s =>
        {
            s.OnEntityCreated().Do(t => log.Add("created:" + t.Kind));
            s.OnAttributesChanged(f.Pos).Do(t => log.Add("changed:" + t.Kind + ":" + t.Changed(f.Pos) + ":" + t.Delta.Created));
            s.OnAttributesWritten(f.Pos).Do(t => log.Add("written"));
            s.OnEntityRemoved().Do(t => log.Add("removed"));
        });

        var cs = f.Engine.BeginChange(f.Net);
        var id = cs.CreateEntity();
        cs.Set(id, f.Pos, EngineFixture.V(3));
        cs.Commit();

        Assert.Equal(new[] { "created:Created", "changed:Created:True:True", "written" }, log);
    }

    [Fact]
    public void A_removed_entity_trigger_can_still_read_the_record()
    {
        var f = new EngineFixture();
        var log = new List<string>();
        f.Module("m", s => s.OnEntityRemoved().Do(t =>
        {
            log.Add(t.Kind + ":" + t.Entity.IsAlive + ":" + t.Entity.Get(f.Pos).AsVec3().X + ":" + t.Delta.Destroyed);
            Assert.Null(f.Engine.State.Find(t.Id));
        }));
        var id = f.Create();
        f.Write(id, f.Pos, EngineFixture.V(7));

        var cs = f.Engine.BeginChange(f.Net);
        cs.DestroyEntity(id);
        cs.Commit();

        Assert.Equal(new[] { "Removed:False:7:True" }, log);
    }

    [Fact]
    public void A_removed_entity_does_not_match_attribute_rules()
    {
        var f = new EngineFixture();
        int attr = 0;
        f.Module("m", s => s.OnAttributesWritten().Do(_ => attr++));
        var id = f.Create();

        var cs = f.Engine.BeginChange(f.Net);
        cs.Set(id, f.Pos, EngineFixture.V(1));
        cs.DestroyEntity(id);
        cs.Commit();

        Assert.Equal(0, attr);
    }

    [Fact]
    public void An_owner_only_delta_fires_no_attribute_rule()
    {
        var f = new EngineFixture();
        int fired = 0;
        f.Module("m", s =>
        {
            s.OnAttributesChanged().Do(_ => fired++);
            s.OnAttributesWritten().Do(_ => fired++);
        });
        var id = f.Create();

        var cs = f.Engine.BeginChange(f.Net);
        cs.TransferOwner(id, f.Proc);
        Assert.Equal(CommitStatus.Committed, cs.Commit().Status);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void A_failing_rule_does_not_stop_the_others_and_is_counted()
    {
        var f = new EngineFixture();
        int second = 0;
        RuleInfo bad = null!, good = null!;
        f.Module("m", s =>
        {
            bad = s.OnAttributesChanged(f.Pos).Named("Bad").Do(_ => throw new InvalidOperationException("boom"));
            good = s.OnAttributesChanged(f.Pos).Named("Good").Do(_ => second++);
        });
        var id = f.Create();

        f.Write(id, f.Pos, EngineFixture.V(1));
        f.Write(id, f.Pos, EngineFixture.V(2));

        Assert.Equal(2, second);
        Assert.Equal((0, 2), (bad.FireCount, bad.Failures));
        Assert.Equal((2, 0), (good.FireCount, good.Failures));
        Assert.Equal(2, f.Engine.Diagnostics.RuleFailures);
        var entry = EngineFixture.Entries(f.Engine.Diagnostics).Last();
        Assert.Equal(DiagnosticSeverity.Error, entry.Severity);
        Assert.Equal("m/Bad", entry.Origin);
        Assert.Contains("boom", entry.Message);
    }

    [Fact]
    public void A_throwing_condition_is_a_rule_failure()
    {
        var f = new EngineFixture();
        RuleInfo rule = null!;
        f.Module("m", s => rule = s.OnEntityCreated().When(_ => throw new InvalidOperationException("cond")).Do(_ => { }));

        f.Create();

        Assert.Equal(1, rule.Failures);
    }

    [Fact]
    public void Disabled_rules_do_not_fire_and_their_counters_freeze()
    {
        var f = new EngineFixture();
        int fired = 0;
        RuleInfo rule = null!;
        f.Module("m", s => rule = s.OnAttributesChanged(f.Pos).When(t => t.Id.Value > 0).Do(_ => fired++));
        var id = f.Create();
        f.Write(id, f.Pos, EngineFixture.V(1));
        Assert.Equal(1, rule.FireCount);

        rule.Enabled = false;
        f.Write(id, f.Pos, EngineFixture.V(2));
        Assert.Equal((1, 1, 0, 0), (fired, rule.FireCount, rule.ConditionRejects, rule.Failures));

        rule.Enabled = true;
        f.Write(id, f.Pos, EngineFixture.V(3));
        Assert.Equal(2, fired);
        Assert.Same(rule, f.Engine.FindRule("m/OnAttributesChanged#1"));
    }

    [Fact]
    public void OnChangeSetEnd_fires_once_after_all_entity_triggers_of_a_record()
    {
        var f = new EngineFixture();
        var log = new List<string>();
        f.Module("m", s =>
        {
            s.OnChangeSetEnd().Do(t => log.Add("end:" + t.ChangeSet.Count));
            s.OnAttributesChanged(f.Pos).Do(t => log.Add("entity"));
        });
        var a = f.Create();
        var b = f.Create();
        log.Clear();

        var cs = f.Engine.BeginChange(f.Net);
        cs.Set(a, f.Pos, EngineFixture.V(1));
        cs.Set(b, f.Pos, EngineFixture.V(1));
        cs.Commit();

        Assert.Equal(new[] { "entity", "entity", "end:2" }, log);
    }

    [Fact]
    public void Default_names_descriptions_and_duplicates()
    {
        var f = new EngineFixture();
        RuleInfo r1 = null!, r2 = null!, r3 = null!;
        f.Module("mod", s =>
        {
            r1 = s.OnEntityCreated().Do(_ => { });
            r2 = s.OnAttributesChanged(f.Pos, f.Speed).Do(_ => { });
            r3 = s.OnEvent(f.Fired).Do(_ => { });
        });

        Assert.Equal("OnEntityCreated#1", r1.Name);
        Assert.Equal("OnAttributesChanged#2", r2.Name);
        Assert.Equal("mod/OnAttributesChanged#2", r2.FullName);
        Assert.Equal("OnAttributesChanged(pos, speed)", r2.Trigger);
        Assert.Equal("OnEvent(Fired)", r3.Trigger);
        Assert.True(r1.Enabled);

        Assert.Throws<InvalidOperationException>(() =>
            f.Module("mod", s =>
            {
                s.OnEntityCreated().Named("X").Do(_ => { });
                s.OnEntityRemoved().Named("X").Do(_ => { });
            }));
        Assert.Null(f.Engine.FindRule("nope/none"));
    }

    [Fact]
    public void A_rule_is_registered_only_by_Do()
    {
        var f = new EngineFixture();
        f.Module("m", s => s.OnEntityCreated().Named("never").When(_ => true));

        Assert.Empty(f.Engine.Rules);
    }

    [Fact]
    public void Produces_declarations_are_recorded_per_module()
    {
        var f = new EngineFixture();
        f.Module("m", s =>
        {
            s.Produces(f.Pos, f.Speed);
            s.Produces(f.Fired);
        });

        Assert.Empty(f.Engine.Rules);
        Assert.Equal(2, f.Engine.Productions.Count);
        Assert.Equal("m", f.Engine.Productions[0].Module);
        Assert.Equal(new[] { f.Pos, f.Speed }, f.Engine.Productions[0].Attributes.ToArray());
        Assert.Equal(f.Fired, f.Engine.Productions[1].EventType);
    }
}
