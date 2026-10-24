using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.NedRpr.Tests;

public class SchemaTests
{
    private static readonly (string Name, AttrKind Kind, string DescriptionHas)[] Expected =
    [
        ("Identity.Name", AttrKind.String, "name"),
        ("Identity.TypeTkb", AttrKind.Int, "TKB"),
        ("Identity.Force", AttrKind.Int, "NedForce"),
        ("Spatial.Position", AttrKind.Vec3, "ECEF, metres"),
        ("Spatial.Velocity", AttrKind.Vec3, "ECEF, m/s"),
        ("Spatial.Acceleration", AttrKind.Vec3, "m/s^2"),
        ("Spatial.Orientation", AttrKind.Vec3, "radians"),
        ("Spatial.AngularVelocity", AttrKind.Vec3, "rad/s"),
        ("Spatial.DrAlgorithm", AttrKind.Int, "DIS"),
        ("Spatial.StateTime", AttrKind.Double, "seconds"),
        ("Status.Damage", AttrKind.Int, "0..3"),
        ("Weapon.SelectedMunition", AttrKind.Int, "TKB"),
        ("Weapon.Ammo", AttrKind.Int, "rounds"),
        ("Derived.PredictedPosition", AttrKind.Vec3, "ECEF, metres"),
        ("Derived.PredictedAt", AttrKind.Double, "seconds"),
        ("Event.Id", AttrKind.Object, "Guid"),
        ("Event.Shooter", AttrKind.EntityRef, "Shooting"),
        ("Event.Target", AttrKind.EntityRef, "Target"),
        ("Event.MunitionTkb", AttrKind.Int, "TKB"),
        ("Event.Location", AttrKind.Vec3, "ECEF, metres"),
        ("Event.Quantity", AttrKind.Int, "rounds"),
        ("Event.Result", AttrKind.Int, "numbering"),
        ("Structure.Parent", AttrKind.EntityRef, "unit"),
        ("Structure.MemberIndex", AttrKind.Int, "Position"),
        ("RprAggregate.MemberIds", AttrKind.Object, "DisEntityId[]"),
        ("Fire.Id", AttrKind.Object, "Guid"),
        ("Fire.Shooter", AttrKind.EntityRef, "shooting"),
        ("Fire.Target", AttrKind.EntityRef, "target"),
        ("Fire.Location", AttrKind.Vec3, "ECEF, metres"),
        ("Fire.Munition", AttrKind.Int, "TKB"),
        ("Fire.Quantity", AttrKind.Int, "rounds"),
        ("Fire.Pending", AttrKind.Bool, "second part"),
        ("Fire.StartedAt", AttrKind.Double, "wall time"),
    ];

    private static (AttributeRegistry A, EventTypeRegistry E, PivotSchema S) Create()
    {
        var a = new AttributeRegistry();
        var e = new EventTypeRegistry();
        return (a, e, new PivotSchema(a, e));
    }

    [Fact]
    public void Exactly_the_specified_attributes_exist_with_their_kinds_and_unit_descriptions()
    {
        var (a, _, _) = Create();
        Assert.Equal(Expected.Length, a.Count);
        foreach (var (name, kind, has) in Expected)
        {
            Assert.True(a.TryGet(name, out var def), name);
            Assert.Equal(kind, def.Kind);
            Assert.Contains(has, def.Description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Only_the_position_and_the_parent_have_an_index_the_position_a_spatial_one_with_cell_1000()
    {
        var (a, _, s) = Create();
        foreach (var def in a)
        {
            if (def.Id == s.Position)
            {
                Assert.Equal(AttrOptions.SpatialIndex, def.Options);
                Assert.Equal(1000.0, def.CellSize);
            }
            else if (def.Id == s.Parent)
            {
                Assert.Equal(AttrOptions.IndexReferences, def.Options); // the reverse index lists a unit's members (R-041)
                Assert.Equal(0.0, def.CellSize);
            }
            else
            {
                Assert.Equal(AttrOptions.None, def.Options);
                Assert.Equal(0.0, def.CellSize);
            }
        }
    }

    [Fact]
    public void Names_follow_Domain_dot_Name()
    {
        var (a, _, _) = Create();
        foreach (var def in a)
        {
            var parts = def.Name.Split('.');
            Assert.Equal(2, parts.Length);
            Assert.All(parts, p => Assert.True(p.Length > 0 && char.IsUpper(p[0]) && p.All(char.IsLetterOrDigit), def.Name));
        }
    }

    [Fact]
    public void The_two_event_types_are_registered_with_descriptions()
    {
        var (_, e, s) = Create();
        Assert.Equal(2, e.Count);
        Assert.Equal("WeaponFire", e.NameOf(s.WeaponFire));
        Assert.Equal("Detonation", e.NameOf(s.Detonation));
        Assert.NotEqual(s.WeaponFire, s.Detonation);
        Assert.False(string.IsNullOrWhiteSpace(e.DescriptionOf(s.WeaponFire)));
        Assert.False(string.IsNullOrWhiteSpace(e.DescriptionOf(s.Detonation)));
    }

    [Fact]
    public void The_exposed_ids_are_the_registered_ones_and_all_distinct()
    {
        var (a, _, s) = Create();
        var exposed = new (AttrId Id, string Name)[]
        {
            (s.Name, "Identity.Name"), (s.TypeTkb, "Identity.TypeTkb"), (s.Force, "Identity.Force"),
            (s.Position, "Spatial.Position"), (s.Velocity, "Spatial.Velocity"), (s.Acceleration, "Spatial.Acceleration"),
            (s.Orientation, "Spatial.Orientation"), (s.AngularVelocity, "Spatial.AngularVelocity"),
            (s.DrAlgorithm, "Spatial.DrAlgorithm"), (s.StateTime, "Spatial.StateTime"),
            (s.Damage, "Status.Damage"), (s.SelectedMunition, "Weapon.SelectedMunition"), (s.Ammo, "Weapon.Ammo"),
            (s.PredictedPosition, "Derived.PredictedPosition"), (s.PredictedAt, "Derived.PredictedAt"),
            (s.EventId, "Event.Id"), (s.EventShooter, "Event.Shooter"), (s.EventTarget, "Event.Target"),
            (s.EventMunitionTkb, "Event.MunitionTkb"), (s.EventLocation, "Event.Location"),
            (s.EventQuantity, "Event.Quantity"), (s.EventResult, "Event.Result"),
            (s.Parent, "Structure.Parent"), (s.MemberIndex, "Structure.MemberIndex"), (s.AggregateMemberIds, "RprAggregate.MemberIds"),
            (s.FireId, "Fire.Id"), (s.FireShooter, "Fire.Shooter"), (s.FireTarget, "Fire.Target"), (s.FireLocation, "Fire.Location"),
            (s.FireMunition, "Fire.Munition"), (s.FireQuantity, "Fire.Quantity"), (s.FirePending, "Fire.Pending"),
            (s.FireStartedAt, "Fire.StartedAt"),
        };
        Assert.Equal(Expected.Length, exposed.Length);
        foreach (var (id, name) in exposed)
        {
            Assert.Equal(name, a[id].Name);
        }

        Assert.Equal(exposed.Length, exposed.Select(x => x.Id).Distinct().Count());
        Assert.Equal(Expected.Select(x => x.Name).OrderBy(x => x), exposed.Select(x => x.Name).OrderBy(x => x));
    }

    [Fact]
    public void Constructing_twice_on_the_same_registries_yields_the_same_ids_and_registers_nothing_new()
    {
        var a = new AttributeRegistry();
        var e = new EventTypeRegistry();
        var one = new PivotSchema(a, e);
        int attrs = a.Count, events = e.Count;
        var two = new PivotSchema(a, e);

        Assert.Equal(attrs, a.Count);
        Assert.Equal(events, e.Count);
        Assert.Equal(one.Position, two.Position);
        Assert.Equal(one.Name, two.Name);
        Assert.Equal(one.EventResult, two.EventResult);
        Assert.Equal(one.Damage, two.Damage);
        Assert.Equal(one.PredictedAt, two.PredictedAt);
        Assert.Equal(one.WeaponFire, two.WeaponFire);
        Assert.Equal(one.Detonation, two.Detonation);
    }

    [Fact]
    public void Independent_registries_get_identical_ids()
    {
        var (_, _, one) = Create();
        var (_, _, two) = Create();
        Assert.Equal(one.Position, two.Position);
        Assert.Equal(one.Ammo, two.Ammo);
        Assert.Equal(one.Detonation, two.Detonation);
    }

    [Fact]
    public void A_conflicting_prior_registration_is_a_startup_error()
    {
        var a = new AttributeRegistry();
        a.Register("Spatial.Position", AttrKind.Vec3);
        Assert.Throws<InvalidOperationException>(() => new PivotSchema(a, new EventTypeRegistry()));
    }
}
