using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class ValuesTests
{
    [Fact]
    public void Ids_start_none_and_compare_by_value()
    {
        Assert.True(EntityId.None.IsNone);
        Assert.True(default(AttrId).IsNone);
        Assert.True(SourceId.None.IsNone);
        Assert.True(EventTypeId.None.IsNone);
        Assert.True(CorrelationId.None.IsNone);
        Assert.False(new EntityId(1).IsNone);
        Assert.Equal(new EntityId(5), new EntityId(5));
        Assert.NotEqual(new EntityId(5), new EntityId(6));
    }

    [Fact]
    public void Default_value_is_none()
    {
        AttrValue v = default;
        Assert.True(v.IsNone);
        Assert.Equal(AttrKind.None, v.Kind);
        Assert.Equal(AttrValue.None, v);
    }

    [Fact]
    public void Round_trips_each_kind()
    {
        Assert.True(AttrValue.From(true).AsBool());
        Assert.False(AttrValue.From(false).AsBool());
        Assert.Equal(-42L, AttrValue.From(-42L).AsInt());
        Assert.Equal(2.5, AttrValue.From(2.5).AsDouble());
        Assert.Equal(new Vec3(1, 2, 3), AttrValue.From(new Vec3(1, 2, 3)).AsVec3());
        Assert.Equal("x", AttrValue.From("x").AsString());
        Assert.Equal(new EntityId(7), AttrValue.From(new EntityId(7)).AsEntity());
        var o = new object();
        Assert.Same(o, AttrValue.FromObject(o).AsObject<object>());
        Assert.Equal(AttrKind.Double, AttrValue.From(1.0).Kind);
        Assert.Equal(AttrKind.Int, AttrValue.From(1L).Kind);
    }

    [Fact]
    public void Wrong_kind_access_throws()
    {
        Assert.Throws<InvalidOperationException>(() => AttrValue.From(1.0).AsInt());
        Assert.Throws<InvalidOperationException>(() => AttrValue.From(1L).AsDouble());
        Assert.Throws<InvalidOperationException>(() => AttrValue.From("s").AsVec3());
        Assert.Throws<InvalidOperationException>(() => AttrValue.None.AsBool());
        Assert.Throws<InvalidOperationException>(() => AttrValue.From(true).AsString());
        Assert.Throws<InvalidOperationException>(() => AttrValue.From(new EntityId(1)).AsInt());
        Assert.Throws<InvalidOperationException>(() => AttrValue.From("s").AsObject<string>());
        Assert.Throws<ArgumentNullException>(() => AttrValue.From((string)null!));
        Assert.Throws<ArgumentNullException>(() => AttrValue.FromObject(null!));
    }

    [Fact]
    public void Double_equality_uses_double_equals_semantics()
    {
        Assert.Equal(AttrValue.From(double.NaN), AttrValue.From(double.NaN));
        Assert.Equal(AttrValue.From(0.0), AttrValue.From(-0.0));
        Assert.Equal(AttrValue.From(0.0).GetHashCode(), AttrValue.From(-0.0).GetHashCode());
        Assert.Equal(AttrValue.From(double.NaN).GetHashCode(), AttrValue.From(double.NaN).GetHashCode());
        Assert.NotEqual(AttrValue.From(1.0), AttrValue.From(1.0000001));
        Assert.True(AttrValue.From(1.0) == AttrValue.From(1.0));
        Assert.True(AttrValue.From(1.0) != AttrValue.From(2.0));
    }

    [Fact]
    public void Vec3_equality_is_per_component()
    {
        Assert.Equal(AttrValue.From(new Vec3(1, 2, 3)), AttrValue.From(new Vec3(1, 2, 3)));
        Assert.NotEqual(AttrValue.From(new Vec3(1, 2, 3)), AttrValue.From(new Vec3(1, 2, 4)));
        Assert.Equal(AttrValue.From(new Vec3(double.NaN, -0.0, 0)), AttrValue.From(new Vec3(double.NaN, 0.0, -0.0)));
        Assert.Equal(
            AttrValue.From(new Vec3(1, 2, 3)).GetHashCode(),
            AttrValue.From(new Vec3(1, 2, 3)).GetHashCode());
    }

    [Fact]
    public void String_equality_is_ordinal()
    {
        Assert.Equal(AttrValue.From("abc"), AttrValue.From(new string("abc".AsSpan())));
        Assert.NotEqual(AttrValue.From("abc"), AttrValue.From("ABC"));
        Assert.Equal(AttrValue.From("abc").GetHashCode(), AttrValue.From(new string("abc".AsSpan())).GetHashCode());
    }

    [Fact]
    public void Object_equality_uses_object_equals()
    {
        var a = new List<int>();
        var b = new List<int>();
        Assert.Equal(AttrValue.FromObject(a), AttrValue.FromObject(a));
        Assert.NotEqual(AttrValue.FromObject(a), AttrValue.FromObject(b)); // reference equality for List
        Assert.Equal(AttrValue.FromObject(new Tag("x")), AttrValue.FromObject(new Tag("x"))); // record value equality
        Assert.NotEqual(AttrValue.FromObject(new Tag("x")), AttrValue.FromObject(new Tag("y")));
    }

    private sealed record Tag(string Name);

    [Fact]
    public void None_equals_none_and_kinds_never_mix()
    {
        Assert.Equal(AttrValue.None, default(AttrValue));
        Assert.NotEqual(AttrValue.None, AttrValue.From(0L));
        Assert.NotEqual(AttrValue.From(1L), AttrValue.From(1.0));
        Assert.NotEqual(AttrValue.From(1L), AttrValue.From(true));
        Assert.NotEqual(AttrValue.From(1L), AttrValue.From(new EntityId(1)));
        Assert.NotEqual(AttrValue.From(0.0), AttrValue.From(new Vec3(0, 0, 0)));
        Assert.NotEqual(AttrValue.From("1"), AttrValue.FromObject("1"));
    }

    [Fact]
    public void ToString_is_readable()
    {
        Assert.Equal("None", AttrValue.None.ToString());
        Assert.Contains("1.5", AttrValue.From(1.5).ToString());
        Assert.Contains("Vec3", AttrValue.From(new Vec3(1, 2, 3)).ToString());
        Assert.Contains("abc", AttrValue.From("abc").ToString());
    }

    [Fact]
    public void Vec3_ops()
    {
        var a = new Vec3(1, 2, 2);
        Assert.Equal(3.0, a.Length);
        Assert.Equal(new Vec3(2, 4, 4), a + a);
        Assert.Equal(Vec3.Zero, a - a);
        Assert.Equal(new Vec3(2, 4, 4), a * 2);
    }
}
