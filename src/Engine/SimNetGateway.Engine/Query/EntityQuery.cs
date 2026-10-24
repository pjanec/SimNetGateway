namespace SimNetGateway.Engine;

internal enum SpatialFilterKind : byte
{
    None,
    Cell,
    Box,
    Radius,
}

/// <summary>
/// Fluent query over the live entities of a <see cref="StateRepository"/> (DD-001 3.4, R-031, R-036). A struct
/// builder: every filter returns an updated copy, nothing is allocated, and the terminal operations
/// (<see cref="CopyTo"/>, <see cref="ForEach{TVisitor}"/>, <see cref="Count"/>, <see cref="Any"/>) run it against
/// the committed state.
/// <para>
/// Planning: one source drives the iteration (in order of preference: <c>ReferencedBy</c>, <c>InCell</c>,
/// <c>InBox</c> / <c>WithinRadius</c>, a wall-time window over a recency list, the smallest presence set of the
/// <c>Has</c> filters, <c>KnownTo</c>, else all live entities); every filter is then applied to each candidate, so
/// the cost follows the driving source. Sim-time windows (<see cref="InSimTime"/>) are filters only and never drive.
/// Windows are inclusive at the edge (<c>ChangedWithin(s)</c> means <c>LastChangeWallTime &gt;= Now - s</c>) and are
/// measured from <see cref="StateRepository.Now"/> / <see cref="StateRepository.SimNow"/>. Recency-driven windows
/// assume commit wall times never decrease.
/// </para>
/// <para>
/// Each single-slot filter (a window kind, <c>FromOrigin</c>, <c>KnownTo</c>, <c>ReferencedBy</c>, one spatial
/// filter, <c>Where</c>, ...) may be given once; a second call throws <see cref="InvalidOperationException"/>.
/// <c>Has</c> and <c>Lacks</c> take up to four attributes each.
/// </para>
/// </summary>
public struct EntityQuery
{
    internal StateRepository? Repo;
    internal bool UseSim;

    internal bool HasChangedWithin;
    internal double ChangedWithinSeconds;
    internal bool HasChangedSince;
    internal double ChangedSinceTime;
    internal bool HasWrittenWithin;
    internal double WrittenWithinSeconds;
    internal bool HasNotWrittenWithin;
    internal double NotWrittenWithinSeconds;
    internal bool HasAttrChanged;
    internal AttrId AttrChangedAttr;
    internal double AttrChangedSeconds;

    internal int HasCount;
    internal AttrId H0, H1, H2, H3;
    internal int LacksCount;
    internal AttrId L0, L1, L2, L3;

    internal bool HasOrigin;
    internal SourceId Origin;
    internal bool HasNotOrigin;
    internal SourceId NotOrigin;
    internal IIdentityMap? Known;

    internal bool HasReference;
    internal AttrId ReferenceAttr;
    internal EntityId ReferenceTarget;

    internal SpatialFilterKind Spatial;
    internal AttrId SpatialAttr;
    internal CellKey Cell;
    internal Vec3 BoxMin;       // box minimum, or the radius center
    internal Vec3 BoxMax;
    internal double Radius;

    internal Func<EntityRecord, bool>? Predicate;

    internal EntityQuery(StateRepository repo)
    {
        Repo = repo;
    }

    // ---- time filters -----------------------------------------------------------------------------------

    /// <summary>Entities whose last change (any value, owner or lifetime) is not older than <paramref name="window"/>.</summary>
    public EntityQuery ChangedWithin(TimeSpan window)
    {
        var q = this;
        Once(q.HasChangedWithin, nameof(ChangedWithin));
        q.ChangedWithinSeconds = Seconds(window);
        q.HasChangedWithin = true;
        return q;
    }

    /// <summary>Entities changed at or after <paramref name="time"/> (wall time, or sim time after <see cref="InSimTime"/>).</summary>
    public EntityQuery ChangedSince(double time)
    {
        var q = this;
        Once(q.HasChangedSince, nameof(ChangedSince));
        q.ChangedSinceTime = time;
        q.HasChangedSince = true;
        return q;
    }

    /// <summary>Entities written (even with an unchanged value) within the window.</summary>
    public EntityQuery WrittenWithin(TimeSpan window)
    {
        var q = this;
        Once(q.HasWrittenWithin, nameof(WrittenWithin));
        q.WrittenWithinSeconds = Seconds(window);
        q.HasWrittenWithin = true;
        return q;
    }

    /// <summary>Entities not written within the window (stale entities).</summary>
    public EntityQuery NotWrittenWithin(TimeSpan window)
    {
        var q = this;
        Once(q.HasNotWrittenWithin, nameof(NotWrittenWithin));
        q.NotWrittenWithinSeconds = Seconds(window);
        q.HasNotWrittenWithin = true;
        return q;
    }

    /// <summary>Entities that have the attribute and whose slot's value changed within the window.</summary>
    public EntityQuery AttributeChangedWithin(AttrId attr, TimeSpan window)
    {
        var q = this;
        Once(q.HasAttrChanged, nameof(AttributeChangedWithin));
        q.AttrChangedAttr = attr;
        q.AttrChangedSeconds = Seconds(window);
        q.HasAttrChanged = true;
        return q;
    }

    /// <summary>Interprets all time filters in sim time (fields <c>Last*SimTime</c>, <see cref="StateRepository.SimNow"/>).</summary>
    public EntityQuery InSimTime()
    {
        var q = this;
        q.UseSim = true;
        return q;
    }

    // ---- attribute, origin, identity, reference ---------------------------------------------------------

    /// <summary>The entity has the attribute (at most four <c>Has</c> filters).</summary>
    public EntityQuery Has(AttrId attr)
    {
        var q = this;
        switch (q.HasCount)
        {
            case 0: q.H0 = attr; break;
            case 1: q.H1 = attr; break;
            case 2: q.H2 = attr; break;
            case 3: q.H3 = attr; break;
            default: throw new InvalidOperationException("A query supports at most 4 Has filters.");
        }

        q.HasCount++;
        return q;
    }

    /// <summary>The entity lacks the attribute (at most four <c>Lacks</c> filters).</summary>
    public EntityQuery Lacks(AttrId attr)
    {
        var q = this;
        switch (q.LacksCount)
        {
            case 0: q.L0 = attr; break;
            case 1: q.L1 = attr; break;
            case 2: q.L2 = attr; break;
            case 3: q.L3 = attr; break;
            default: throw new InvalidOperationException("A query supports at most 4 Lacks filters.");
        }

        q.LacksCount++;
        return q;
    }

    /// <summary>Entities introduced by the side.</summary>
    public EntityQuery FromOrigin(SourceId origin)
    {
        var q = this;
        Once(q.HasOrigin, nameof(FromOrigin));
        q.Origin = origin;
        q.HasOrigin = true;
        return q;
    }

    /// <summary>Entities not introduced by the side.</summary>
    public EntityQuery NotFromOrigin(SourceId origin)
    {
        var q = this;
        Once(q.HasNotOrigin, nameof(NotFromOrigin));
        q.NotOrigin = origin;
        q.HasNotOrigin = true;
        return q;
    }

    /// <summary>Entities with a committed key in the identity map (a map created by an <see cref="IdentityRegistry"/>).</summary>
    public EntityQuery KnownTo(IIdentityMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map is not IIdentityMapInternal)
        {
            throw new ArgumentException("Only identity maps created by an IdentityRegistry can be queried.", nameof(map));
        }

        var q = this;
        Once(q.Known is not null, nameof(KnownTo));
        q.Known = map;
        return q;
    }

    /// <summary>
    /// Entities whose <see cref="AttrKind.EntityRef"/> attribute points at <paramref name="target"/> (the members of
    /// a unit). The attribute must be registered with <see cref="AttrOptions.IndexReferences"/>.
    /// </summary>
    public EntityQuery ReferencedBy(AttrId attr, EntityId target)
    {
        var q = this;
        Once(q.HasReference, nameof(ReferencedBy));
        var repo = Require(q);
        if (!repo.Attributes.TryGet(attr, out var def) || (def.Options & AttrOptions.IndexReferences) == 0)
        {
            throw new InvalidOperationException($"Attribute {attr.Value} is not registered with IndexReferences.");
        }

        q.ReferenceAttr = attr;
        q.ReferenceTarget = target;
        q.HasReference = true;
        return q;
    }

    // ---- spatial ----------------------------------------------------------------------------------------

    /// <summary>Entities whose position attribute lies in the grid cell.</summary>
    public EntityQuery InCell(AttrId attr, CellKey cell)
    {
        var q = SpatialSlot(attr);
        q.Spatial = SpatialFilterKind.Cell;
        q.Cell = cell;
        return q;
    }

    /// <summary>Entities whose position lies in the box, bounds included.</summary>
    public EntityQuery InBox(AttrId attr, Vec3 min, Vec3 max)
    {
        var q = SpatialSlot(attr);
        q.Spatial = SpatialFilterKind.Box;
        q.BoxMin = min;
        q.BoxMax = max;
        return q;
    }

    /// <summary>Entities whose position is at most <paramref name="radius"/> from <paramref name="center"/>.</summary>
    public EntityQuery WithinRadius(AttrId attr, Vec3 center, double radius)
    {
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "The radius must not be negative.");
        }

        var q = SpatialSlot(attr);
        q.Spatial = SpatialFilterKind.Radius;
        q.BoxMin = center;
        q.Radius = radius;
        return q;
    }

    private EntityQuery SpatialSlot(AttrId attr)
    {
        var q = this;
        Once(q.Spatial != SpatialFilterKind.None, "a spatial filter");
        var repo = Require(q);
        if (!repo.Attributes.TryGet(attr, out var def) || (def.Options & AttrOptions.SpatialIndex) == 0)
        {
            throw new InvalidOperationException($"Attribute {attr.Value} is not registered with SpatialIndex.");
        }

        q.SpatialAttr = attr;
        return q;
    }

    // ---- predicates -------------------------------------------------------------------------------------

    /// <summary>Arbitrary condition (one slot). Prefer the generic overload with a <c>static</c> lambda: a capturing lambda allocates.</summary>
    public EntityQuery Where(Func<EntityRecord, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var q = this;
        Once(q.Predicate is not null, nameof(Where));
        q.Predicate = predicate;
        return q;
    }

    /// <summary>
    /// Arbitrary condition over an explicit state argument, so a <c>static</c> lambda needs no closure. Returns an
    /// <see cref="EntityQuery{TState}"/> that holds the state without boxing it; this must be the last call before
    /// the terminal operation.
    /// </summary>
    public EntityQuery<TState> Where<TState>(TState state, Func<EntityRecord, TState, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new EntityQuery<TState>(this, state, predicate);
    }

    // ---- terminal operations ----------------------------------------------------------------------------

    /// <summary>
    /// Clears <paramref name="target"/> and fills it with the matches. The result is a snapshot: the caller may
    /// write to the repository while iterating it.
    /// </summary>
    public void CopyTo(List<EntityRecord> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Clear();
        var ex = new QueryExecutor<ListSink, int>(in this, new ListSink(target), 0, null);
        ex.Run();
    }

    /// <summary>
    /// Calls the visitor for every match (the visitor is copied in and back out, so its fields hold the result).
    /// The visitor must not write to the repository: collect, then write afterwards.
    /// </summary>
    public void ForEach<TVisitor>(ref TVisitor visitor)
        where TVisitor : struct, IEntityVisitor
    {
        var ex = new QueryExecutor<VisitorSink<TVisitor>, int>(in this, new VisitorSink<TVisitor>(visitor), 0, null);
        ex.Run();
        visitor = ex.Sink.Visitor;
    }

    public int Count()
    {
        var ex = new QueryExecutor<CountSink, int>(in this, default, 0, null);
        ex.Run();
        return ex.Sink.Matches;
    }

    public bool Any()
    {
        var ex = new QueryExecutor<AnySink, int>(in this, default, 0, null);
        ex.Run();
        return ex.Sink.Found;
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static void Once(bool alreadySet, string what)
    {
        if (alreadySet)
        {
            throw new InvalidOperationException($"{what} may be given only once per query.");
        }
    }

    private static double Seconds(TimeSpan window)
    {
        if (window < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "The window must not be negative.");
        }

        return window.TotalSeconds;
    }

    private static StateRepository Require(in EntityQuery q) =>
        q.Repo ?? throw new InvalidOperationException("The query was not created by a StateRepository.");
}

/// <summary>
/// An <see cref="EntityQuery"/> closed with <c>Where(state, predicate)</c>: it holds the state without boxing it and
/// offers only the terminal operations.
/// </summary>
public readonly struct EntityQuery<TState>
{
    private readonly EntityQuery _query;
    private readonly TState _state;
    private readonly Func<EntityRecord, TState, bool> _predicate;

    internal EntityQuery(in EntityQuery query, TState state, Func<EntityRecord, TState, bool> predicate)
    {
        _query = query;
        _state = state;
        _predicate = predicate;
    }

    /// <summary>Clears <paramref name="target"/> and fills it with the matches (a snapshot).</summary>
    public void CopyTo(List<EntityRecord> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Clear();
        var ex = new QueryExecutor<ListSink, TState>(in _query, new ListSink(target), _state, _predicate);
        ex.Run();
    }

    /// <summary>Calls the visitor for every match; it must not write to the repository.</summary>
    public void ForEach<TVisitor>(ref TVisitor visitor)
        where TVisitor : struct, IEntityVisitor
    {
        var ex = new QueryExecutor<VisitorSink<TVisitor>, TState>(in _query, new VisitorSink<TVisitor>(visitor), _state, _predicate);
        ex.Run();
        visitor = ex.Sink.Visitor;
    }

    public int Count()
    {
        var ex = new QueryExecutor<CountSink, TState>(in _query, default, _state, _predicate);
        ex.Run();
        return ex.Sink.Matches;
    }

    public bool Any()
    {
        var ex = new QueryExecutor<AnySink, TState>(in _query, default, _state, _predicate);
        ex.Run();
        return ex.Sink.Found;
    }
}
