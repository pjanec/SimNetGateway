namespace SimNetGateway.Common;

/// <summary>Degrees/radians conversion and angle normalisation.</summary>
public static class Angles
{
    public const double TwoPi = 2.0 * Math.PI;

    public static double ToRadians(double degrees) => degrees * (Math.PI / 180.0);

    public static double ToDegrees(double radians) => radians * (180.0 / Math.PI);

    /// <summary>Normalises to [0, 2pi).</summary>
    public static double NormalizeTwoPi(double radians)
    {
        double r = radians % TwoPi;
        if (r < 0)
        {
            r += TwoPi;
        }

        // r + 2pi can round up to exactly 2pi for a tiny negative input.
        return r >= TwoPi ? 0.0 : r;
    }

    /// <summary>Normalises to (-pi, pi].</summary>
    public static double NormalizePi(double radians)
    {
        double r = radians % TwoPi;
        if (r > Math.PI)
        {
            r -= TwoPi;
        }
        else if (r <= -Math.PI)
        {
            r += TwoPi;
        }

        return r;
    }
}
