using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class RegistryTests
{
    [Fact]
    public void Attribute_registration_is_idempotent_and_ids_start_at_one()
    {
        var reg = new AttributeRegistry();
        var a = reg.Register("a", AttrKind.Int);
        var b = reg.Register("b", AttrKind.Vec3, "pos");
        Assert.Equal(1, a.Value);
        Assert.Equal(2, b.Value);
        Assert.Equal(a, reg.Register("a", AttrKind.Int));
        Assert.Equal(2, reg.Count);
        Assert.Equal("b", reg[b].Name);
        Assert.Equal(AttrKind.Vec3, reg[b].Kind);
        Assert.Equal("pos", reg[b].Description);
        Assert.True(reg.TryGet("a", out var def));
        Assert.Equal(a, def.Id);
        Assert.False(reg.TryGet("zzz", out _));
        Assert.Equal(new[] { "a", "b" }, reg.Select(d => d.Name).ToArray());
    }

    [Fact]
    public void Description_first_non_null_wins()
    {
        var reg = new AttributeRegistry();
        var id = reg.Register("a", AttrKind.Int);
        Assert.Null(reg[id].Description);
        reg.Register("a", AttrKind.Int, "first");
        reg.Register("a", AttrKind.Int, "second");
        Assert.Equal("first", reg[id].Description);
    }

    [Fact]
    public void Conflicting_reregistration_throws()
    {
        var reg = new AttributeRegistry();
        reg.Register("a", AttrKind.Int);
        Assert.Throws<InvalidOperationException>(() => reg.Register("a", AttrKind.Double));
        Assert.Throws<InvalidOperationException>(() => reg.Register("a", AttrKind.Int, null, AttrOptions.IndexReferences));
    }

    [Fact]
    public void Registering_kind_none_throws()
    {
        var reg = new AttributeRegistry();
        Assert.Throws<InvalidOperationException>(() => reg.Register("a", AttrKind.None));
    }

    [Fact]
    public void Unknown_attribute_id_throws_from_indexer()
    {
        var reg = new AttributeRegistry();
        Assert.Throws<KeyNotFoundException>(() => reg[new AttrId(1)]);
        Assert.False(reg.TryGet(AttrId.None, out _));
    }

    [Fact]
    public void Event_type_registry_is_idempotent()
    {
        var reg = new EventTypeRegistry();
        var fire = reg.Register("fire", "weapon fire");
        Assert.Equal(1, fire.Value);
        Assert.Equal(fire, reg.Register("fire"));
        Assert.Equal("fire", reg.NameOf(fire));
        Assert.True(reg.TryGet("fire", out var id));
        Assert.Equal(fire, id);
        Assert.False(reg.TryGet("nope", out _));
        Assert.Equal(2, reg.Register("det").Value);
    }

    [Fact]
    public void Source_registry_pre_registers_engine_as_one()
    {
        var reg = new SourceRegistry();
        Assert.Equal(1, SourceRegistry.Engine.Value);
        Assert.True(reg.TryGet("engine", out var engine));
        Assert.Equal(SourceRegistry.Engine, engine);
        Assert.Equal("engine", reg.NameOf(SourceRegistry.Engine));
        var ned = reg.Register("ned");
        Assert.Equal(2, ned.Value);
        Assert.Equal(ned, reg.Register("ned"));
        Assert.Equal("ned", reg.NameOf(ned));
        Assert.False(reg.TryGet("rpr", out _));
    }
}
