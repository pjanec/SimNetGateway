using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

/// <summary>Shared test setup: registries, a few attributes and a repository.</summary>
internal sealed class Fixture
{
    public readonly AttributeRegistry Attributes = new();
    public readonly SourceRegistry Sources = new();
    public readonly SourceId A;
    public readonly SourceId B;
    public readonly AttrId Pos;
    public readonly AttrId Speed;
    public readonly AttrId Count;
    public readonly AttrId Name;
    public readonly AttrId Flag;
    public readonly StateRepository Repo;
    public double Wall = 100;
    public double Sim = 10;

    public Fixture(IWriteAuthorityPolicy? policy = null, int journalCapacity = 1024)
    {
        A = Sources.Register("a");
        B = Sources.Register("b");
        Pos = Attributes.Register("pos", AttrKind.Vec3);
        Speed = Attributes.Register("speed", AttrKind.Double);
        Count = Attributes.Register("count", AttrKind.Int);
        Name = Attributes.Register("name", AttrKind.String);
        Flag = Attributes.Register("flag", AttrKind.Bool);
        Repo = new StateRepository(Attributes, policy, journalCapacity);
    }

    public ChangeSet Begin(SourceId source, CorrelationId causedBy = default) =>
        Repo.Begin(source, Wall, Sim, causedBy);

    /// <summary>Creates an entity owned by <paramref name="source"/> in its own committed transaction.</summary>
    public EntityId Create(SourceId source)
    {
        var cs = Begin(source);
        var id = cs.CreateEntity();
        var r = cs.Commit();
        Assert.Equal(CommitStatus.Committed, r.Status);
        return id;
    }
}
