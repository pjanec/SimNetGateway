using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class SpatialIndexTests
{
    private static EntityId Place(QueryFixture f, double time, double x, double y, double z, AttrId? attr = null)
    {
        var a = attr ?? f.Pos;
        return f.Create(time, f.A, (cs, id) => cs.Set(id, a, QueryFixture.P(x, y, z)));
    }

    // ---- registration -----------------------------------------------------------------------------------

    [Fact]
    public void Registration_stores_the_cell_size_and_checks_the_options()
    {
        var r = new AttributeRegistry();
        var pos = r.Register("pos", AttrKind.Vec3, null, AttrOptions.SpatialIndex, 25);
        Assert.Equal(25, r[pos].CellSize);
        Assert.Equal(0, r[r.Register("plain", AttrKind.Vec3)].CellSize);

        Assert.Throws<ArgumentException>(() => r.Register("a", AttrKind.Vec3, null, AttrOptions.SpatialIndex));          // no cell size
        Assert.Throws<ArgumentException>(() => r.Register("b", AttrKind.Vec3, null, AttrOptions.SpatialIndex, -1));
        Assert.Throws<ArgumentException>(() => r.Register("c", AttrKind.Vec3, null, AttrOptions.SpatialIndex, double.NaN));
        Assert.Throws<ArgumentException>(() => r.Register("d", AttrKind.Vec3, null, AttrOptions.None, 10));              // size without index
        Assert.Throws<ArgumentException>(() => r.Register("e", AttrKind.Double, null, AttrOptions.SpatialIndex, 10));    // wrong kind
        Assert.Throws<ArgumentException>(() => r.Register("f", AttrKind.Int, null, AttrOptions.IndexReferences));        // wrong kind
        Assert.Equal(pos, r.Register("pos", AttrKind.Vec3, null, AttrOptions.SpatialIndex, 25));                         // idempotent
        Assert.Throws<InvalidOperationException>(() => r.Register("pos", AttrKind.Vec3, null, AttrOptions.SpatialIndex, 30));
        Assert.Throws<InvalidOperationException>(() => r.Register("pos", AttrKind.Vec3));
    }

    [Fact]
    public void CellKey_uses_floor_division()
    {
        Assert.Equal(new CellKey(0, 0, 0), CellKey.Of(new Vec3(0, 9.999, 0), 10));
        Assert.Equal(new CellKey(1, 0, 0), CellKey.Of(new Vec3(10, 0, 0), 10));
        Assert.Equal(new CellKey(-1, -1, -1), CellKey.Of(new Vec3(-0.001, -10, -9.9), 10));
        Assert.Equal(new CellKey(-2, 2, 0), CellKey.Of(new Vec3(-10.5, 25, 0), 10));
        Assert.Equal(new CellKey(2, 0, 0), CellKey.Of(new Vec3(1, 0, 0), 0.5));
        Assert.Equal(new CellKey(int.MaxValue, int.MinValue, 0), CellKey.Of(new Vec3(1e300, -1e300, double.NaN), 1));
    }

    [Fact]
    public void CellOf_uses_the_registered_cell_size_of_the_attribute()
    {
        var f = new QueryFixture();
        Assert.Equal(new CellKey(1, 0, -1), f.Repo.CellOf(f.Pos, new Vec3(10, 0, -1)));
        Assert.Equal(new CellKey(2, 0, -1), f.Repo.CellOf(f.Target, new Vec3(10, 0, -1)));
        Assert.Throws<ArgumentException>(() => f.Repo.CellOf(f.Count, default));
    }

    // ---- queries ----------------------------------------------------------------------------------------

    [Fact]
    public void InCell_returns_the_entities_of_one_cell()
    {
        var f = new QueryFixture();
        var a = Place(f, 1, 1, 1, 1);
        var b = Place(f, 1, 9.9, 9.9, 9.9);
        var c = Place(f, 1, 10, 1, 1);
        var d = Place(f, 1, -1, 1, 1);
        Assert.Equal(QueryFixture.Ids(a, b), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(0, 0, 0))));
        Assert.Equal(QueryFixture.Ids(c), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(1, 0, 0))));
        Assert.Equal(QueryFixture.Ids(d), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(-1, 0, 0))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(5, 5, 5))));
        Assert.Equal(QueryFixture.Ids(a), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, f.Repo.CellOf(f.Pos, new Vec3(1, 1, 1))).Where(e => e.Id == a)));
    }

    [Fact]
    public void InBox_includes_its_bounds()
    {
        var f = new QueryFixture();
        var inside = Place(f, 1, 5, 5, 5);
        var onMin = Place(f, 1, 0, 0, 0);
        var onMax = Place(f, 1, 20, 20, 20);
        var justOutside = Place(f, 1, 20.001, 5, 5);
        var below = Place(f, 1, -0.001, 5, 5);
        var far = Place(f, 1, 500, 500, 500);

        var hit = QueryFixture.Ids(f.Repo.Query().InBox(f.Pos, new Vec3(0, 0, 0), new Vec3(20, 20, 20)));
        Assert.Equal(QueryFixture.Ids(inside, onMin, onMax), hit);
        Assert.DoesNotContain(justOutside.Value, hit);
        Assert.DoesNotContain(below.Value, hit);
        Assert.DoesNotContain(far.Value, hit);

        // a box that is not aligned to the grid
        Assert.Equal(QueryFixture.Ids(inside), QueryFixture.Ids(f.Repo.Query().InBox(f.Pos, new Vec3(4, 4, 4), new Vec3(6, 6, 6))));
        // an inverted box matches nothing
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().InBox(f.Pos, new Vec3(20, 0, 0), new Vec3(0, 20, 20))));
    }

    [Fact]
    public void WithinRadius_includes_the_boundary()
    {
        var f = new QueryFixture();
        var center = Place(f, 1, 100, 100, 100);
        var onSphere = Place(f, 1, 103, 104, 100);       // distance exactly 5
        var outside = Place(f, 1, 103, 104, 100.001);
        var other = Place(f, 1, 90, 100, 100);           // 10 away

        Assert.Equal(QueryFixture.Ids(center, onSphere), QueryFixture.Ids(f.Repo.Query().WithinRadius(f.Pos, new Vec3(100, 100, 100), 5)));
        Assert.Equal(QueryFixture.Ids(center), QueryFixture.Ids(f.Repo.Query().WithinRadius(f.Pos, new Vec3(100, 100, 100), 4.999)));
        Assert.Equal(QueryFixture.Ids(center, onSphere, outside, other), QueryFixture.Ids(f.Repo.Query().WithinRadius(f.Pos, new Vec3(98, 100, 100), 12)));
        Assert.Equal(QueryFixture.Ids(center), QueryFixture.Ids(f.Repo.Query().WithinRadius(f.Pos, new Vec3(100, 100, 100), 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Repo.Query().WithinRadius(f.Pos, default, -1));
    }

    [Fact]
    public void An_entity_moves_between_cells_when_its_position_changes()
    {
        var f = new QueryFixture();
        var e = Place(f, 1, 1, 1, 1);
        var other = Place(f, 1, 2, 2, 2);
        Assert.Equal(QueryFixture.Ids(e, other), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(0, 0, 0))));

        f.At(2, f.A, cs => cs.Set(e, f.Pos, QueryFixture.P(15, 1, 1)));            // another cell
        Assert.Equal(QueryFixture.Ids(other), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(0, 0, 0))));
        Assert.Equal(QueryFixture.Ids(e), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(1, 0, 0))));

        f.At(3, f.A, cs => cs.Set(e, f.Pos, QueryFixture.P(16, 2, 2)));            // same cell, other position
        Assert.Equal(QueryFixture.Ids(e), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(1, 0, 0))));

        f.At(4, f.A, cs => cs.Set(e, f.Pos, QueryFixture.P(16, 2, 2)));            // unchanged write
        Assert.Equal(QueryFixture.Ids(e), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(1, 0, 0))));

        f.At(5, f.A, cs => cs.Set(e, f.Pos, QueryFixture.P(-5, 2, 2)));            // back across the origin
        Assert.Equal(QueryFixture.Ids(e), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(-1, 0, 0))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(1, 0, 0))));
    }

    [Fact]
    public void Clear_and_destroy_remove_the_entity_from_its_cell()
    {
        var f = new QueryFixture();
        var cleared = Place(f, 1, 1, 1, 1);
        var destroyed = Place(f, 1, 2, 2, 2);
        var kept = Place(f, 1, 3, 3, 3);
        f.At(2, f.A, cs => cs.Clear(cleared, f.Pos));
        f.At(3, f.A, cs => cs.DestroyEntity(destroyed));
        Assert.Equal(QueryFixture.Ids(kept), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(0, 0, 0))));
        Assert.Equal(QueryFixture.Ids(kept), QueryFixture.Ids(f.Repo.Query().WithinRadius(f.Pos, default, 100)));

        // the emptied cell is dropped and can be formed again
        f.At(4, f.A, cs => cs.DestroyEntity(kept));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().WithinRadius(f.Pos, default, 100)));
        var again = Place(f, 5, 4, 4, 4);
        Assert.Equal(QueryFixture.Ids(again), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(0, 0, 0))));
    }

    [Fact]
    public void A_set_and_clear_in_one_transaction_leave_the_grid_consistent()
    {
        var f = new QueryFixture();
        var e = Place(f, 1, 1, 1, 1);
        f.At(2, f.A, cs =>
        {
            cs.Set(e, f.Pos, QueryFixture.P(50, 50, 50));
            cs.Clear(e, f.Pos);
        });
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().InBox(f.Pos, new Vec3(-1000, -1000, -1000), new Vec3(1000, 1000, 1000))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().Has(f.Pos)));
    }

    [Fact]
    public void A_huge_box_over_a_sparse_grid_walks_the_occupied_cells()
    {
        var f = new QueryFixture();
        var a = Place(f, 1, 0, 0, 0);
        var b = Place(f, 1, 1_000_000, -1_000_000, 5);
        var c = Place(f, 1, 9_999_999, 0, 0);
        var all = QueryFixture.Ids(f.Repo.Query().InBox(f.Pos, new Vec3(-1e7, -1e7, -1e7), new Vec3(1e7, 1e7, 1e7)));
        Assert.Equal(QueryFixture.Ids(a, b, c), all);
        Assert.Equal(QueryFixture.Ids(b), QueryFixture.Ids(f.Repo.Query().InBox(f.Pos, new Vec3(999_990, -1_000_010, 0), new Vec3(1_000_010, -999_990, 10))));
        // beyond the int range of cells: still consistent
        var far = Place(f, 1, 1e300, 0, 0);
        Assert.Contains(far.Value, QueryFixture.Ids(f.Repo.Query().InBox(f.Pos, new Vec3(0, -1, -1), new Vec3(double.MaxValue, 1, 1))));
        f.At(2, f.A, cs => cs.DestroyEntity(far));
        Assert.DoesNotContain(far.Value, QueryFixture.Ids(f.Repo.Query().InBox(f.Pos, new Vec3(0, -1, -1), new Vec3(double.MaxValue, 1, 1))));
    }

    [Fact]
    public void A_nan_position_is_indexed_consistently_and_matches_no_box()
    {
        var f = new QueryFixture();
        var e = Place(f, 1, double.NaN, 0, 0);
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().InBox(f.Pos, new Vec3(-10, -10, -10), new Vec3(10, 10, 10))));
        f.At(2, f.A, cs => cs.Set(e, f.Pos, QueryFixture.P(1, 1, 1)));
        Assert.Equal(QueryFixture.Ids(e), QueryFixture.Ids(f.Repo.Query().InBox(f.Pos, new Vec3(-10, -10, -10), new Vec3(10, 10, 10))));
        f.At(3, f.A, cs => cs.DestroyEntity(e));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().Has(f.Pos)));
    }

    [Fact]
    public void Two_spatial_attributes_have_independent_grids_and_cell_sizes()
    {
        var f = new QueryFixture();
        var e = f.Create(1, f.A, (cs, id) =>
        {
            cs.Set(id, f.Pos, QueryFixture.P(7, 0, 0));      // cell size 10 -> cell 0
            cs.Set(id, f.Target, QueryFixture.P(7, 0, 0));   // cell size 5  -> cell 1
        });
        Assert.Equal(QueryFixture.Ids(e), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(0, 0, 0))));
        Assert.Empty(QueryFixture.Ids(f.Repo.Query().InCell(f.Target, new CellKey(0, 0, 0))));
        Assert.Equal(QueryFixture.Ids(e), QueryFixture.Ids(f.Repo.Query().InCell(f.Target, new CellKey(1, 0, 0))));

        f.At(2, f.A, cs => cs.Set(e, f.Target, QueryFixture.P(40, 0, 0)));
        Assert.Equal(QueryFixture.Ids(e), QueryFixture.Ids(f.Repo.Query().InCell(f.Pos, new CellKey(0, 0, 0))));
        Assert.Equal(QueryFixture.Ids(e), QueryFixture.Ids(f.Repo.Query().InCell(f.Target, new CellKey(8, 0, 0))));
    }

    [Fact]
    public void Spatial_filters_need_a_spatially_indexed_attribute()
    {
        var f = new QueryFixture();
        var plain = f.Attributes.Register("plainVec", AttrKind.Vec3);
        Assert.Throws<InvalidOperationException>(() => f.Repo.Query().InCell(plain, default));
        Assert.Throws<InvalidOperationException>(() => f.Repo.Query().InBox(f.Count, default, default));
        Assert.Throws<InvalidOperationException>(() => f.Repo.Query().WithinRadius(new AttrId(999), default, 1));
        Assert.Throws<InvalidOperationException>(() =>
            f.Repo.Query().InBox(f.Pos, default, default).WithinRadius(f.Target, default, 1));
    }

    [Fact]
    public void A_spatial_filter_combines_with_other_drivers()
    {
        var f = new QueryFixture();
        var parent = f.Create(1, f.A);
        var near = f.Create(1, f.A, (cs, id) =>
        {
            cs.Set(id, f.Pos, QueryFixture.P(1, 1, 1));
            cs.Set(id, f.Parent, QueryFixture.R(parent));
        });
        var far = f.Create(1, f.A, (cs, id) =>
        {
            cs.Set(id, f.Pos, QueryFixture.P(100, 1, 1));
            cs.Set(id, f.Parent, QueryFixture.R(parent));
        });
        var noParent = Place(f, 1, 2, 2, 2);

        Assert.Equal(QueryFixture.Ids(near), QueryFixture.Ids(f.Repo.Query().ReferencedBy(f.Parent, parent).WithinRadius(f.Pos, default, 10)));
        Assert.Equal(QueryFixture.Ids(near, noParent), QueryFixture.Ids(f.Repo.Query().Has(f.Pos).WithinRadius(f.Pos, default, 10)));
        Assert.Equal(QueryFixture.Ids(near), QueryFixture.Ids(f.Repo.Query().ChangedWithin(TimeSpan.FromSeconds(1)).InCell(f.Pos, new CellKey(0, 0, 0)).Lacks(f.Target).Where(e => e.Id == near)));
        Assert.Equal(QueryFixture.Ids(near, far), QueryFixture.Ids(f.Repo.Query().ReferencedBy(f.Parent, parent).InBox(f.Pos, new Vec3(-1e3, -1e3, -1e3), new Vec3(1e3, 1e3, 1e3))));
    }

    [Fact]
    public void A_spatial_query_touches_only_the_entities_of_the_cells_in_the_box()
    {
        var f = new QueryFixture();
        f.At(1, f.A, cs =>
        {
            for (int x = 0; x < 100; x++)
            {
                for (int y = 0; y < 100; y++)
                {
                    cs.Set(cs.CreateEntity(), f.Pos, QueryFixture.P(x * 10 + 5, y * 10 + 5, 0));
                }
            }
        });

        var touched = new TouchCounter();
        var query = f.Repo.Query().InBox(f.Pos, new Vec3(0, 0, 0), new Vec3(29, 29, 9)).Where(touched, static (e, c) =>
        {
            c.Calls++;
            return true;
        });
        Assert.Equal(9, query.Count());
        Assert.Equal(9, touched.Calls); // 3 x 3 cells with one entity each, out of 10 000 entities
    }

    private sealed class TouchCounter
    {
        public int Calls;
    }
}
