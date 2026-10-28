using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// Body-axis conventions: FLU (x forward, y left, z up) and FRD (x forward, y right, z down). Both are right-handed and
/// differ by a half turn about the forward axis, <c>F = diag(1, -1, -1)</c>, which is its own inverse and transpose.
/// </summary>
public static class BodyAxes
{
    /// <summary>The flip <c>F = diag(1, -1, -1)</c>: FRD components = F * FLU components and vice versa.</summary>
    public static Matrix3 Flip => new(1, 0, 0, 0, -1, 0, 0, 0, -1);

    /// <summary>A vector given in FLU body axes to FRD body axes.</summary>
    public static Vec3 FluToFrd(Vec3 flu) => new(flu.X, -flu.Y, -flu.Z);

    /// <summary>A vector given in FRD body axes to FLU body axes (the same operation: F is an involution).</summary>
    public static Vec3 FrdToFlu(Vec3 frd) => new(frd.X, -frd.Y, -frd.Z);

    /// <summary>
    /// A rotation <c>C</c> from FLU body axes to some reference becomes the rotation from FRD body axes to the same
    /// reference: <c>C F</c>.
    /// </summary>
    public static Matrix3 BodyFluToFrd(in Matrix3 fluToReference) => fluToReference * Flip;

    /// <summary>A rotation from FRD body axes to some reference to the one from FLU body axes: <c>C F</c>.</summary>
    public static Matrix3 BodyFrdToFlu(in Matrix3 frdToReference) => frdToReference * Flip;
}
