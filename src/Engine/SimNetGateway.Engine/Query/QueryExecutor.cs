namespace SimNetGateway.Engine;

/// <summary>Receives the matches of a query; returns false to stop the iteration.</summary>
internal interface IQuerySink
{
    bool Accept(EntityRecord entity);
}

internal struct ListSink : IQuerySink
{
    private readonly List<EntityRecord> _list;

    public ListSink(List<EntityRecord> list) => _list = list;

    public bool Accept(EntityRecord entity)
    {
        _list.Add(entity);
        return true;
    }
}

internal struct CountSink : IQuerySink
{
    public int Matches;

    public bool Accept(EntityRecord entity)
    {
        Matches++;
        return true;
    }
}

internal struct AnySink : IQuerySink
{
    public bool Found;

    public bool Accept(EntityRecord entity)
    {
        Found = true;
        return false;
    }
}

internal struct VisitorSink<TVisitor> : IQuerySink
    where TVisitor : struct, IEntityVisitor
{
    public TVisitor Visitor;

    public VisitorSink(TVisitor visitor) => Visitor = visitor;

    public bool Accept(EntityRecord entity)
    {
        Visitor.Visit(entity);
        return true;
    }
}

/// <summary>
/// Runs one query: picks the driving source, applies every filter to each candidate and feeds the sink. A generic
/// struct, so sinks and predicate state are never boxed. Not re-entrant per instance; every terminal makes its own.
/// </summary>
internal struct QueryExecutor<TSink, TState>
    where TSink : struct, IQuerySink
{
    public TSink Sink;

    private readonly EntityQuery _q;
    private readonly StateRepository _repo;
    private readonly TState _state;
    private readonly Func<EntityRecord, TState, bool>? _statePredicate;

    // Resolved thresholds (the query's relative windows measured from Now / SimNow).
    private readonly bool _useSim;
    private readonly bool _hasChanged;
    private readonly double _changedMin;       // LastChange >= this
    private readonly double _writtenMin;       // LastWrite >= this
    private readonly double _staleBefore;      // LastWrite < this
    private readonly double _attrChangedMin;   // slot ChangedTime >= this
    private readonly double _cellSize;

    private readonly int _hasCount;
    private readonly AttrId _h0, _h1, _h2, _h3;
    private readonly int _lacksCount;
    private readonly AttrId _l0, _l1, _l2, _l3;

    private bool _stopped;

    public QueryExecutor(in EntityQuery q, TSink sink, TState state, Func<EntityRecord, TState, bool>? statePredicate)
    {
        _repo = q.Repo ?? throw new InvalidOperationException("The query was not created by a StateRepository.");
        _q = q;
        Sink = sink;
        _state = state;
        _statePredicate = statePredicate;
        _stopped = false;

        _useSim = q.UseSim;
        double now = q.UseSim ? _repo.SimNow : _repo.Now;
        _hasChanged = q.HasChangedWithin || q.HasChangedSince;
        double changedMin = double.NegativeInfinity;
        if (q.HasChangedWithin)
        {
            changedMin = now - q.ChangedWithinSeconds;
        }

        if (q.HasChangedSince)
        {
            changedMin = Math.Max(changedMin, q.ChangedSinceTime);
        }

        _changedMin = changedMin;
        _writtenMin = now - q.WrittenWithinSeconds;
        _staleBefore = now - q.NotWrittenWithinSeconds;
        _attrChangedMin = now - q.AttrChangedSeconds;

        _hasCount = q.HasCount;
        _h0 = q.H0;
        _h1 = q.H1;
        _h2 = q.H2;
        _h3 = q.H3;
        _lacksCount = q.LacksCount;
        _l0 = q.L0;
        _l1 = q.L1;
        _l2 = q.L2;
        _l3 = q.L3;

        _cellSize = 0;
        if (q.Spatial != SpatialFilterKind.None)
        {
            _cellSize = _repo.GridOf(q.SpatialAttr)?.CellSize ?? 0;
        }
    }

    // ---- the filters ------------------------------------------------------------------------------------

    private bool Offer(EntityRecord e)
    {
        if (Matches(e) && !Sink.Accept(e))
        {
            _stopped = true;
            return false;
        }

        return true;
    }

    private readonly bool Matches(EntityRecord e)
    {
        if (_q.HasOrigin && e.Origin != _q.Origin)
        {
            return false;
        }

        if (_q.HasNotOrigin && e.Origin == _q.NotOrigin)
        {
            return false;
        }

        bool sim = _useSim;
        if (_hasChanged && (sim ? e.LastChangeSimTime : e.LastChangeWallTime) < _changedMin)
        {
            return false;
        }

        if (_q.HasWrittenWithin && (sim ? e.LastWriteSimTime : e.LastWriteWallTime) < _writtenMin)
        {
            return false;
        }

        if (_q.HasNotWrittenWithin && (sim ? e.LastWriteSimTime : e.LastWriteWallTime) >= _staleBefore)
        {
            return false;
        }

        if (_q.HasAttrChanged)
        {
            if (!e.TryGetSlot(_q.AttrChangedAttr, out var slot) ||
                (sim ? slot.Meta.ChangedSimTime : slot.Meta.ChangedWallTime) < _attrChangedMin)
            {
                return false;
            }
        }

        if (_hasCount > 0 && !e.Has(_h0)) return false;
        if (_hasCount > 1 && !e.Has(_h1)) return false;
        if (_hasCount > 2 && !e.Has(_h2)) return false;
        if (_hasCount > 3 && !e.Has(_h3)) return false;
        if (_lacksCount > 0 && e.Has(_l0)) return false;
        if (_lacksCount > 1 && e.Has(_l1)) return false;
        if (_lacksCount > 2 && e.Has(_l2)) return false;
        if (_lacksCount > 3 && e.Has(_l3)) return false;

        if (_q.Known is { } known && !known.Contains(e.Id))
        {
            return false;
        }

        if (_q.HasReference && (!e.TryGet(_q.ReferenceAttr, out var target) || target.AsEntity() != _q.ReferenceTarget))
        {
            return false;
        }

        if (_q.Spatial != SpatialFilterKind.None && !MatchesSpatial(e))
        {
            return false;
        }

        if (_q.Predicate is { } where && !where(e))
        {
            return false;
        }

        return _statePredicate is not { } predicate || predicate(e, _state);
    }

    private readonly bool MatchesSpatial(EntityRecord e)
    {
        if (!e.TryGet(_q.SpatialAttr, out var value))
        {
            return false;
        }

        var p = value.AsVec3();
        switch (_q.Spatial)
        {
            case SpatialFilterKind.Cell:
                return CellKey.Of(p, _cellSize) == _q.Cell;
            case SpatialFilterKind.Box:
                var min = _q.BoxMin;
                var max = _q.BoxMax;
                return p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;
            default:
                double dx = p.X - _q.BoxMin.X;
                double dy = p.Y - _q.BoxMin.Y;
                double dz = p.Z - _q.BoxMin.Z;
                return dx * dx + dy * dy + dz * dz <= _q.Radius * _q.Radius;
        }
    }

    // ---- the driving sources ----------------------------------------------------------------------------

    public void Run()
    {
        var repo = _repo;
        var q = _q;
        if (q.HasReference)
        {
            if (repo.TryGetReferrers(q.ReferenceAttr, q.ReferenceTarget, out var referrers))
            {
                RunSet(referrers);
            }

            return;
        }

        if (q.Spatial != SpatialFilterKind.None)
        {
            RunSpatial();
            return;
        }

        if (!_useSim)
        {
            // Wall-time windows walk a recency list and stop at the first entity outside the window.
            if (_hasChanged)
            {
                for (var e = repo.ChangeTail; e is not null; e = e.ChangePrev)
                {
                    if (e.LastChangeWallTime < _changedMin || !Offer(e))
                    {
                        return;
                    }
                }

                return;
            }

            if (q.HasWrittenWithin)
            {
                for (var e = repo.WriteTail; e is not null; e = e.WritePrev)
                {
                    if (e.LastWriteWallTime < _writtenMin || !Offer(e))
                    {
                        return;
                    }
                }

                return;
            }

            if (q.HasNotWrittenWithin)
            {
                for (var e = repo.WriteHead; e is not null; e = e.WriteNext)
                {
                    if (e.LastWriteWallTime >= _staleBefore || !Offer(e))
                    {
                        return;
                    }
                }

                return;
            }
        }

        if (_hasCount > 0)
        {
            RunSmallestPresence();
            return;
        }

        if (q.Known is IIdentityMapInternal map)
        {
            var entities = repo.EntityMap;
            int n = map.Count;
            for (int i = 0; i < n; i++)
            {
                // A destroyed entity stays bound until its removal triggers ran, but is no longer in the repository.
                if (entities.TryGetValue(map.BoundAt(i), out var e) && !Offer(e))
                {
                    return;
                }
            }

            return;
        }

        foreach (var e in repo.EntityMap.Values)
        {
            if (!Offer(e))
            {
                return;
            }
        }
    }

    private void RunSet(HashSet<EntityRecord> set)
    {
        foreach (var e in set)
        {
            if (!Offer(e))
            {
                return;
            }
        }
    }

    private void RunSmallestPresence()
    {
        var best = _repo.PresenceOf(_h0);
        if (_hasCount > 1)
        {
            best = Smaller(best, _repo.PresenceOf(_h1));
        }

        if (_hasCount > 2)
        {
            best = Smaller(best, _repo.PresenceOf(_h2));
        }

        if (_hasCount > 3)
        {
            best = Smaller(best, _repo.PresenceOf(_h3));
        }

        if (best is not null)
        {
            RunSet(best);
        }
    }

    /// <summary>The smaller set; a missing set (nobody has the attribute) is the smallest of all.</summary>
    private static HashSet<EntityRecord>? Smaller(HashSet<EntityRecord>? a, HashSet<EntityRecord>? b) =>
        a is null || b is null ? null : (b.Count < a.Count ? b : a);

    private void RunSpatial()
    {
        var grid = _repo.GridOf(_q.SpatialAttr);
        if (grid is null)
        {
            return;
        }

        if (_q.Spatial == SpatialFilterKind.Cell)
        {
            if (grid.TryGetCell(_q.Cell, out var set))
            {
                RunSet(set);
            }

            return;
        }

        CellKey lo, hi;
        if (_q.Spatial == SpatialFilterKind.Box)
        {
            lo = CellKey.Of(_q.BoxMin, grid.CellSize);
            hi = CellKey.Of(_q.BoxMax, grid.CellSize);
        }
        else
        {
            var c = _q.BoxMin;
            double r = _q.Radius;
            lo = CellKey.Of(new Vec3(c.X - r, c.Y - r, c.Z - r), grid.CellSize);
            hi = CellKey.Of(new Vec3(c.X + r, c.Y + r, c.Z + r), grid.CellSize);
        }

        if (hi.X < lo.X || hi.Y < lo.Y || hi.Z < lo.Z)
        {
            return; // empty box
        }

        double spanned = ((double)hi.X - lo.X + 1) * ((double)hi.Y - lo.Y + 1) * ((double)hi.Z - lo.Z + 1);
        if (spanned <= grid.CellCount)
        {
            for (long x = lo.X; x <= hi.X; x++)
            {
                for (long y = lo.Y; y <= hi.Y; y++)
                {
                    for (long z = lo.Z; z <= hi.Z; z++)
                    {
                        if (grid.TryGetCell(new CellKey((int)x, (int)y, (int)z), out var set))
                        {
                            RunSet(set);
                            if (_stopped)
                            {
                                return;
                            }
                        }
                    }
                }
            }

            return;
        }

        // The box spans more cells than are occupied: walk the occupied cells instead.
        foreach (var cell in grid.Cells)
        {
            var k = cell.Key;
            if (k.X >= lo.X && k.X <= hi.X && k.Y >= lo.Y && k.Y <= hi.Y && k.Z >= lo.Z && k.Z <= hi.Z)
            {
                RunSet(cell.Value);
                if (_stopped)
                {
                    return;
                }
            }
        }
    }
}
