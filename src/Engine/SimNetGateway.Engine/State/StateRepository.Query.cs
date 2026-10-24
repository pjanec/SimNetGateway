namespace SimNetGateway.Engine;

public sealed partial class StateRepository
{
    /// <summary>Wall time that relative query filters (<c>ChangedWithin</c>, ...) are measured from.</summary>
    public double Now { get; private set; }

    /// <summary>Sim time that relative query filters are measured from after <c>InSimTime()</c>.</summary>
    public double SimNow { get; private set; }

    /// <summary>
    /// Sets the time used by relative query filters. The engine calls it whenever its wall time is refreshed and
    /// after session commands; a standalone repository sets it to the times of every commit.
    /// </summary>
    public void SetTime(double wall, double sim)
    {
        Now = wall;
        SimNow = sim;
    }

    /// <summary>Starts a query over the live entities (DD-001 3.4); a struct builder, nothing is allocated.</summary>
    public EntityQuery Query() => new(this);

    /// <summary>The grid cell of a position for a <see cref="AttrOptions.SpatialIndex"/> attribute (its registered cell size).</summary>
    public CellKey CellOf(AttrId attr, Vec3 position)
    {
        if (!_attributes.TryGet(attr, out var def) || (def.Options & AttrOptions.SpatialIndex) == 0)
        {
            throw new ArgumentException($"Attribute {attr.Value} is not registered with SpatialIndex.", nameof(attr));
        }

        return CellKey.Of(position, def.CellSize);
    }
}
