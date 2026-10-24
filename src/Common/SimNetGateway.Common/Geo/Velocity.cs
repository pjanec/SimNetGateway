using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>Velocity conversions between (speed, heading, pitch) in the local tangent frame and ECEF velocity vectors.</summary>
public static class Velocity
{
    /// <summary>ECEF velocity of a speed along the given heading (from north, clockwise) and pitch (up positive).</summary>
    public static Vec3 ToEcef(double speed, double heading, double pitch, Geodetic at)
    {
        double cp = Math.Cos(pitch);
        var ne = new Vec3(speed * cp * Math.Cos(heading), speed * cp * Math.Sin(heading), -speed * Math.Sin(pitch));
        return NorthEastDown.ToEcef(ne, at);
    }

    /// <summary>Speed, heading [0, 2pi) and pitch of an ECEF velocity; a zero velocity gives heading and pitch 0.</summary>
    public static (double Speed, double Heading, double Pitch) ToLocal(Vec3 ecefVelocity, Geodetic at)
    {
        var ne = NorthEastDown.FromEcef(ecefVelocity, at);
        double horizontal = Math.Sqrt(ne.X * ne.X + ne.Y * ne.Y);
        double speed = Math.Sqrt(horizontal * horizontal + ne.Z * ne.Z);
        if (speed == 0.0)
        {
            return (0.0, 0.0, 0.0);
        }

        double heading = horizontal == 0.0 ? 0.0 : Angles.NormalizeTwoPi(Math.Atan2(ne.Y, ne.X));
        return (speed, heading, Math.Atan2(-ne.Z, horizontal));
    }
}
