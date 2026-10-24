namespace SimNetGateway.Engine;

/// <summary>Integer coordinates of a cell of the uniform spatial grid of a <see cref="AttrOptions.SpatialIndex"/> attribute.</summary>
public readonly record struct CellKey(int X, int Y, int Z)
{
    /// <summary>The cell containing <paramref name="position"/> (floor division by the cell edge; NaN maps to 0, huge values saturate).</summary>
    public static CellKey Of(Vec3 position, double cellSize) =>
        new(Axis(position.X, cellSize), Axis(position.Y, cellSize), Axis(position.Z, cellSize));

    private static int Axis(double value, double cellSize)
    {
        double cell = Math.Floor(value / cellSize);
        if (double.IsNaN(cell))
        {
            return 0;
        }

        if (cell >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return cell <= int.MinValue ? int.MinValue : (int)cell;
    }
}
