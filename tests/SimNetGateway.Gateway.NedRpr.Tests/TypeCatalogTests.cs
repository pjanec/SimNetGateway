using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

public class TypeCatalogTests
{
    public static IEnumerable<object[]> Specified() =>
    [
        [100, new DisEntityType(1, 1, 225, 1, 1, 3, 0)],
        [101, new DisEntityType(1, 1, 225, 6, 1, 0, 0)],
        [102, new DisEntityType(1, 1, 225, 2, 1, 0, 0)],
        [200, new DisEntityType(1, 2, 225, 50, 1, 0, 0)],
        [300, new DisEntityType(1, 1, 225, 9, 0, 0, 0)],
        [400, new DisEntityType(1, 1, 222, 1, 2, 1, 0)],
        [501, new DisEntityType(2, 9, 225, 2, 1, 1, 0)],
        [502, new DisEntityType(2, 9, 225, 2, 14, 1, 0)],
    ];

    [Theory]
    [MemberData(nameof(Specified))]
    public void The_specified_entries_map_both_ways(int tkb, DisEntityType dis)
    {
        var catalog = new TypeCatalog();
        Assert.True(catalog.TryToDis(tkb, out var d));
        Assert.Equal(dis, d);
        Assert.True(catalog.TryToTkb(dis, out int t));
        Assert.Equal(tkb, t);
    }

    [Fact]
    public void The_catalogue_holds_exactly_the_specified_entries()
    {
        var catalog = new TypeCatalog();
        Assert.Equal(Specified().Select(r => (int)r[0]).OrderBy(x => x), catalog.TkbTypes.OrderBy(x => x));
        Assert.Equal(catalog.TkbTypes.Count(), catalog.DisTypes.Count());
    }

    [Fact]
    public void Tkb_to_dis_to_tkb_is_the_identity_for_every_catalogued_tkb()
    {
        var catalog = new TypeCatalog();
        foreach (int tkb in catalog.TkbTypes)
        {
            Assert.True(catalog.TryToDis(tkb, out var dis));
            Assert.True(catalog.TryToTkb(dis, out int back));
            Assert.Equal(tkb, back);
        }
    }

    [Fact]
    public void Dis_to_tkb_to_dis_is_the_identity_for_every_catalogued_dis_type()
    {
        var catalog = new TypeCatalog();
        foreach (var dis in catalog.DisTypes)
        {
            Assert.True(catalog.TryToTkb(dis, out int tkb));
            Assert.True(catalog.TryToDis(tkb, out var back));
            Assert.Equal(dis, back);
        }
    }

    [Fact]
    public void Every_catalogued_tkb_has_a_distinct_dis_type()
    {
        var catalog = new TypeCatalog();
        var dis = catalog.TkbTypes.Select(t => { catalog.TryToDis(t, out var d); return d; }).ToList();
        Assert.Equal(dis.Count, dis.Distinct().Count());
    }

    [Fact]
    public void Unknown_values_are_not_found_and_leave_the_out_value_at_the_default()
    {
        var catalog = new TypeCatalog();
        foreach (int tkb in new[] { int.MinValue, -1, 0, 99, 103, 199, 503, 1000, int.MaxValue })
        {
            Assert.False(catalog.TryToDis(tkb, out var dis));
            Assert.Equal(TypeCatalog.DefaultDis, dis);
        }

        Assert.False(catalog.TryToTkb(new DisEntityType(0, 0, 0, 0, 0, 0, 0), out int t));
        Assert.Equal(TypeCatalog.DefaultTkb, t);
        // near misses: one field different from a catalogued type
        Assert.False(catalog.TryToTkb(new DisEntityType(1, 1, 225, 1, 1, 3, 1), out _));
        Assert.False(catalog.TryToTkb(new DisEntityType(1, 1, 226, 1, 1, 3, 0), out _));
        Assert.False(catalog.TryToTkb(new DisEntityType(2, 9, 225, 2, 1, 1, 1), out _));
    }

    [Fact]
    public void The_defaults_are_tkb_0_and_the_all_zero_dis_type()
    {
        Assert.Equal(0, TypeCatalog.DefaultTkb);
        Assert.Equal(new DisEntityType(0, 0, 0, 0, 0, 0, 0), TypeCatalog.DefaultDis);
    }

    [Fact]
    public void An_unknown_tkb_is_new_only_the_first_time()
    {
        var catalog = new TypeCatalog();
        Assert.True(catalog.NoteUnknownTkb(999));
        Assert.False(catalog.NoteUnknownTkb(999));
        Assert.False(catalog.NoteUnknownTkb(999));
        Assert.True(catalog.NoteUnknownTkb(998));
        Assert.False(catalog.NoteUnknownTkb(998));
    }

    [Fact]
    public void An_unknown_dis_type_is_new_only_the_first_time()
    {
        var catalog = new TypeCatalog();
        var a = new DisEntityType(9, 9, 9, 9, 9, 9, 9);
        var b = a with { Extra = 8 };
        Assert.True(catalog.NoteUnknownDis(a));
        Assert.False(catalog.NoteUnknownDis(a));
        Assert.True(catalog.NoteUnknownDis(b));
        Assert.False(catalog.NoteUnknownDis(b));
    }

    [Fact]
    public void Warn_once_state_is_per_catalogue_instance_and_independent_per_direction()
    {
        var one = new TypeCatalog();
        var two = new TypeCatalog();
        Assert.True(one.NoteUnknownTkb(5));
        Assert.True(two.NoteUnknownTkb(5));
        // the TKB number 0 and the all-zero DIS type are different warnings
        Assert.True(one.NoteUnknownTkb(0));
        Assert.True(one.NoteUnknownDis(default));
    }
}
