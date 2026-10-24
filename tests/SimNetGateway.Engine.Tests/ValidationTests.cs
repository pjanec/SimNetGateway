using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Engine.Tests;

public class ValidationTests
{
    private static GatewayEngine NewEngine() => new(new ManualClock());

    private static void Module(GatewayEngine engine, string name, Action<ModuleSetup> register) =>
        engine.AddModule(new LambdaModule(name, register));

    [Fact]
    public void An_empty_engine_validates_cleanly()
    {
        var report = NewEngine().Validate();
        Assert.False(report.HasErrors);
        Assert.Empty(report.Errors);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void A_consumed_attribute_that_nobody_produces_is_a_warning()
    {
        var engine = NewEngine();
        var speed = engine.Attributes.Register("Spatial.Speed", AttrKind.Double);
        Module(engine, "egress", s => s.OnAttributesChanged(speed).Do(_ => { }));

        var report = engine.Validate();

        Assert.False(report.HasErrors);
        var warning = Assert.Single(report.Warnings);
        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
        Assert.Equal(ValidationCodes.AttributeNeverProduced, warning.Code);
        Assert.Equal("Spatial.Speed", warning.Subject);
        Assert.Contains("egress", warning.Message);
    }

    [Fact]
    public void A_produced_attribute_that_nobody_consumes_is_a_warning()
    {
        var engine = NewEngine();
        var speed = engine.Attributes.Register("Spatial.Speed", AttrKind.Double);
        Module(engine, "ingress", s => s.Produces(speed));

        var report = engine.Validate();

        var warning = Assert.Single(report.Warnings);
        Assert.Equal(ValidationCodes.AttributeNeverConsumed, warning.Code);
        Assert.Equal("Spatial.Speed", warning.Subject);
        Assert.Contains("ingress", warning.Message);
    }

    [Fact]
    public void A_produced_and_consumed_attribute_is_clean_also_when_consumed_by_a_written_subscription()
    {
        var engine = NewEngine();
        var speed = engine.Attributes.Register("Spatial.Speed", AttrKind.Double);
        var pos = engine.Attributes.Register("Spatial.Position", AttrKind.Vec3);
        Module(engine, "ingress", s => s.Produces(speed, pos));
        Module(engine, "egress", s =>
        {
            s.OnAttributesChanged(speed).Do(_ => { });
            s.OnAttributesWritten(pos).Do(_ => { });
        });

        var report = engine.Validate();

        Assert.Empty(report.Warnings);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void Consumes_counts_an_attribute_read_without_a_subscription()
    {
        var engine = NewEngine();
        var munition = engine.Attributes.Register("Weapon.Munition", AttrKind.Int);
        var fired = engine.EventTypes.Register("Weapon.Fire");
        Module(engine, "ned.in", s =>
        {
            s.Produces(munition);
            s.Produces(fired);
        });
        Module(engine, "rpr.out", s =>
        {
            s.Consumes(munition);
            s.Consumes(fired);
        });

        Assert.Empty(engine.Validate().Warnings);
    }

    [Fact]
    public void An_empty_attribute_subscription_consumes_nothing_specific()
    {
        var engine = NewEngine();
        var speed = engine.Attributes.Register("Spatial.Speed", AttrKind.Double);
        Module(engine, "ingress", s => s.Produces(speed));
        Module(engine, "audit", s =>
        {
            s.OnAttributesChanged().Do(_ => { });
            s.OnAttributesWritten().Do(_ => { });
            s.OnAnyEvent().Do(_ => { });
            s.OnEntityCreated().Do(_ => { });
        });

        var warning = Assert.Single(engine.Validate().Warnings);
        Assert.Equal(ValidationCodes.AttributeNeverConsumed, warning.Code);
    }

    [Fact]
    public void Event_types_are_checked_like_attributes()
    {
        var engine = NewEngine();
        var fire = engine.EventTypes.Register("Weapon.Fire");
        var detonation = engine.EventTypes.Register("Weapon.Detonation");
        var unused = engine.EventTypes.Register("Unused");
        Module(engine, "ned.in", s => s.Produces(fire));
        Module(engine, "rpr.out", s => s.OnEvent(detonation).Do(_ => { }));

        var report = engine.Validate();

        Assert.False(report.HasErrors);
        Assert.Equal(2, report.Warnings.Count);
        var never = report.Warnings.Single(w => w.Code == ValidationCodes.EventNeverConsumed);
        Assert.Equal("Weapon.Fire", never.Subject);
        var missing = report.Warnings.Single(w => w.Code == ValidationCodes.EventNeverProduced);
        Assert.Equal("Weapon.Detonation", missing.Subject);
        Assert.DoesNotContain(report.Warnings, w => w.Subject == "Unused");
        Assert.NotEqual(default, unused);
    }

    [Fact]
    public void Ids_that_are_not_registered_are_errors()
    {
        var engine = NewEngine();
        var bogusAttr = new AttrId(99);
        var bogusEvent = new EventTypeId(77);
        Module(engine, "bad", s =>
        {
            s.Produces(bogusAttr);
            s.Produces(bogusEvent);
            s.Consumes(bogusAttr);
            s.Consumes(bogusEvent);
            s.OnAttributesChanged(bogusAttr).Do(_ => { });
            s.OnEvent(bogusEvent).Do(_ => { });
        });

        var report = engine.Validate();

        Assert.True(report.HasErrors);
        Assert.All(report.Errors, e => Assert.Equal(ValidationSeverity.Error, e.Severity));
        Assert.Equal(3, report.Errors.Count(e => e.Code == ValidationCodes.UnknownAttribute));   // Produces, Consumes, subscription
        Assert.Equal(3, report.Errors.Count(e => e.Code == ValidationCodes.UnknownEventType));
        Assert.All(report.Errors, e => Assert.Equal("bad", e.Subject));
        Assert.Contains("99", report.Errors.First(e => e.Code == ValidationCodes.UnknownAttribute).Message);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Kind_conflicts_are_rejected_when_the_attribute_is_registered()
    {
        var engine = NewEngine();
        engine.Attributes.Register("Spatial.Speed", AttrKind.Double);
        Module(engine, "a", s => s.Attributes.Register("Spatial.Speed", AttrKind.Double)); // same kind: shared
        Assert.Throws<InvalidOperationException>(() => Module(engine, "b", s => s.Attributes.Register("Spatial.Speed", AttrKind.Int)));
        Assert.False(engine.Validate().HasErrors); // nothing is left to report
    }

    [Fact]
    public void Dump_lists_every_attribute_and_event_type_with_producers_and_consumers()
    {
        var engine = NewEngine();
        var pos = engine.Attributes.Register("Spatial.Position", AttrKind.Vec3, "ECEF metres", AttrOptions.SpatialIndex, 250);
        var parent = engine.Attributes.Register("Structure.Parent", AttrKind.EntityRef, null, AttrOptions.IndexReferences);
        engine.Attributes.Register("Lonely", AttrKind.Bool);
        var fire = engine.EventTypes.Register("Weapon.Fire", "A shot");
        Module(engine, "ned.in", s =>
        {
            s.Produces(pos, parent);
            s.Produces(fire);
        });
        Module(engine, "rpr.out", s =>
        {
            s.OnAttributesChanged(pos).Do(_ => { });
            s.OnEvent(fire).Do(_ => { });
        });
        Module(engine, "display.out", s => s.OnAttributesWritten(pos).Do(_ => { }));

        var dump = engine.Validate().Dump();

        Assert.Contains("Spatial.Position : Vec3", dump);
        Assert.Contains("SpatialIndex", dump);
        Assert.Contains("cell 250", dump);
        Assert.Contains("ECEF metres", dump);
        Assert.Contains("Structure.Parent : EntityRef", dump);
        Assert.Contains("IndexReferences", dump);
        Assert.Contains("Lonely : Bool", dump);
        Assert.Contains("Weapon.Fire - A shot", dump);
        var lines = dump.Split('\n');
        int at = Array.FindIndex(lines, l => l.Contains("Spatial.Position : Vec3"));
        Assert.Contains("produced by: ned.in", lines[at + 1]);
        Assert.Contains("consumed by: display.out, rpr.out", lines[at + 2]);
        int lonely = Array.FindIndex(lines, l => l.Contains("Lonely : Bool"));
        Assert.Contains("produced by: (nobody)", lines[lonely + 1]);
        Assert.Contains("consumed by: (nobody)", lines[lonely + 2]);
        int ev = Array.FindIndex(lines, l => l.Contains("Weapon.Fire - A shot"));
        Assert.Contains("produced by: ned.in", lines[ev + 1]);
        Assert.Contains("consumed by: rpr.out", lines[ev + 2]);
    }

    [Fact]
    public void Any_entity_accepts_any_registered_attribute()
    {
        // R-034: the engine has no entity kinds and checks no entity structure
        var engine = NewEngine();
        var ids = new[]
        {
            engine.Attributes.Register("a", AttrKind.Int),
            engine.Attributes.Register("b", AttrKind.Vec3),
            engine.Attributes.Register("c", AttrKind.String),
            engine.Attributes.Register("d", AttrKind.EntityRef),
        };
        var source = engine.Sources.Register("s");
        var cs = engine.BeginChange(source);
        var e1 = cs.CreateEntity();
        var e2 = cs.CreateEntity();
        foreach (var e in new[] { e1, e2 })
        {
            cs.Set(e, ids[0], AttrValue.From(1L));
            cs.Set(e, ids[1], AttrValue.From(new Vec3(1, 2, 3)));
            cs.Set(e, ids[2], AttrValue.From("x"));
            cs.Set(e, ids[3], AttrValue.From(e1));
        }

        Assert.Equal(CommitStatus.Committed, cs.Commit().Status);
        Assert.Equal(4, engine.State.Find(e2)!.Count);
        Assert.Empty(engine.Validate().Errors);
    }
}
