namespace SimNetGateway.Engine;

/// <summary>
/// Receives the entities of <see cref="EntityQuery.ForEach{TVisitor}"/>. Implement it as a struct so that the call is
/// not boxed. A visitor must not write to the repository while the query runs: collect, then write afterwards.
/// </summary>
public interface IEntityVisitor
{
    void Visit(EntityRecord entity);
}
