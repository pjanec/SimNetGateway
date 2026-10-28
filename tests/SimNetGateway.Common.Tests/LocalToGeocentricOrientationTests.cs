using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Common.Tests;

public class LocalToGeocentricOrientationTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void Body_flip_is_an_involution_and_flips_y_and_z()
    {
        var f = BodyAxes.Flip;
        GeoAssert.Near(Matrix3.Identity, f * f, 0.0, "F F");
        GeoAssert.Near(f, f.Transpose(), 0.0, "F symmetric");
        Assert.Equal(new Vec3(1, -2, -3), BodyAxes.FluToFrd(new Vec3(1, 2, 3)));
        Assert.Equal(new Vec3(1, -2, -3), BodyAxes.FrdToFlu(new Vec3(1, 2, 3)));
        // det F = +1: a half turn about x, not a reflection
        Assert.Equal(1.0, f.Determinant(), 12);

        var random = new Random(301);
        for (int i = 0; i < 200; i++)
        {
            var v = GeoRandom.Vector(random, 10);
            Assert.Equal(v, BodyAxes.FrdToFlu(BodyAxes.FluToFrd(v)));
            var c = GeoRandom.Rotation(random).ToMatrix();
            // FRD body vector -> FLU -> reference equals the composed matrix applied to the FRD vector
            GeoAssert.Near(c * BodyAxes.FrdToFlu(v), BodyAxes.BodyFluToFrd(c) * v, 1e-12, "C F v");
        }
    }

    [Fact]
    public void Hrot_to_dis_to_hrot_is_the_identity_over_random_attitudes_and_origins()
    {
        var random = new Random(302);
        int checkedCount = 0;
        while (checkedCount < 2000)
        {
            var frame = new LocalTangentFrame(GeoRandom.Origin(random));
            var q = GeoRandom.Rotation(random).WithNonNegativeW();

            var euler = LocalToGeocentricOrientation.ToDisEuler(q, frame);
            if (Math.Abs(euler.Y) > Angles.ToRadians(88.5))
            {
                continue; // too close to the geocentric gimbal lock for a 1e-9 round trip
            }

            checkedCount++;
            Assert.InRange(euler.X, -Math.PI, Math.PI);
            Assert.InRange(euler.Y, -Math.PI / 2, Math.PI / 2);
            Assert.InRange(euler.Z, -Math.PI, Math.PI);
            var back = LocalToGeocentricOrientation.FromDisEuler(euler, frame);
            Assert.True(back.W >= 0.0);
            GeoAssert.NearQuaternion(q, back, Tolerance, "ENU/FLU quaternion round trip");
        }
    }

    [Fact]
    public void Dis_to_hrot_to_dis_is_the_identity_over_random_euler_angles()
    {
        var random = new Random(303);
        for (int i = 0; i < 2000; i++)
        {
            var frame = new LocalTangentFrame(GeoRandom.Origin(random));
            var (psi, theta, phi) = GeoRandom.Euler(random);
            var q = LocalToGeocentricOrientation.FromDisEuler(new Vec3(psi, theta, phi), frame);
            var back = LocalToGeocentricOrientation.ToDisEuler(q, frame);
            Assert.True(GeoAssert.AngleDistance(psi, back.X) <= Tolerance);
            Assert.True(Math.Abs(theta - back.Y) <= Tolerance);
            Assert.True(GeoAssert.AngleDistance(phi, back.Z) <= Tolerance);
        }
    }

    // Derivation of the known vectors (comment is the specification of these tests).
    //
    // At latitude 0, longitude 0 the ECEF axes are: up = +x, east = +y, north = +z (so down = -x).
    // HROT body axes are FLU: x forward, y left, z up. The identity quaternion puts them on ENU: forward = east, left = north,
    // up = up. DIS body axes are FRD: x forward, y right, z down:
    //   forward = east  = ECEF ( 0,  1,  0)
    //   right   = south = ECEF ( 0,  0, -1)     (right of a body that heads east is south)
    //   down    =       = ECEF (-1,  0,  0)
    // These are the columns of C_frd->ecef = R * I * F. The DIS Euler angles are the ZYX angles of that matrix in ECEF:
    //   M = [[0, 0, -1], [1, 0, 0], [0, -1, 0]]  ->  theta = atan2(-M20, sqrt(M00^2 + M10^2)) = atan2(0, 1) = 0,
    //   psi = atan2(M10, M00) = atan2(1, 0) = +90 deg, phi = atan2(M21, M22) = atan2(-1, 0) = -90 deg.
    // The geocentric angles are not the local heading/pitch/roll: converted back with the local tangent frame
    // (Orientation.ToLocal) they are heading 90 deg (east), pitch 0, roll 0 - a level, upright body heading east.
    [Fact]
    public void Identity_quaternion_at_lat0_lon0_is_a_level_upright_body_heading_east()
    {
        var origin = new Geodetic(0, 0, 0);
        var frame = new LocalTangentFrame(origin);

        var euler = LocalToGeocentricOrientation.ToDisEuler(Quaternion.Identity, frame);

        Assert.Equal(Math.PI / 2, euler.X, 12);
        Assert.Equal(0.0, euler.Y, 12);
        Assert.Equal(-Math.PI / 2, euler.Z, 12);
        var m = Orientation.ToMatrix(euler);
        GeoAssert.Near(new Vec3(0, 1, 0), m.Column(0), 1e-12, "forward = east");
        GeoAssert.Near(new Vec3(0, 0, -1), m.Column(1), 1e-12, "right = south");
        GeoAssert.Near(new Vec3(-1, 0, 0), m.Column(2), 1e-12, "down");

        var local = Orientation.ToLocal(euler, origin);
        Assert.Equal(Math.PI / 2, local.Heading, 12);
        Assert.Equal(0.0, local.Pitch, 12);
        Assert.Equal(0.0, local.Roll, 12);
    }

    // A rotation of +90 degrees about ENU up (counter-clockwise seen from above) turns the forward axis from east to
    // north: C = Rz(90 deg), forward = ECEF +z (north), right = east = ECEF +y, down = ECEF -x. Heading north is
    // local heading 0; geocentrically the nose points along +z, theta = -90 deg (the geocentric gimbal lock, phi = 0).
    [Fact]
    public void A_ninety_degree_yaw_left_in_enu_heads_north()
    {
        var origin = new Geodetic(0, 0, 0);
        var frame = new LocalTangentFrame(origin);
        var yawLeft = new Quaternion(Math.Cos(Math.PI / 4), 0, 0, Math.Sin(Math.PI / 4));

        var euler = LocalToGeocentricOrientation.ToDisEuler(yawLeft, frame);

        var m = Orientation.ToMatrix(euler);
        GeoAssert.Near(new Vec3(0, 0, 1), m.Column(0), 1e-9, "forward = north");
        GeoAssert.Near(new Vec3(0, 1, 0), m.Column(1), 1e-9, "right = east");
        GeoAssert.Near(new Vec3(-1, 0, 0), m.Column(2), 1e-9, "down");
        Assert.Equal(-Math.PI / 2, euler.Y, 7);
        Assert.Equal(0.0, euler.Z); // gimbal lock convention

        var local = Orientation.ToLocal(euler, origin);
        Assert.True(GeoAssert.AngleDistance(0.0, local.Heading) <= 1e-7, $"heading {local.Heading}");
        Assert.Equal(0.0, local.Pitch, 7);
        Assert.Equal(0.0, local.Roll, 7);
    }

    // Pitch and roll signs. HROT rotation about ENU north (+y) by +a takes the east-pointing nose toward -z: nose DOWN,
    // local pitch -a. Rotation about ENU east (+x) by +a lifts the left wing (FLU y) toward up: right wing down, roll +a.
    [Fact]
    public void Pitch_and_roll_signs_follow_the_aerospace_convention_at_any_origin()
    {
        var random = new Random(304);
        for (int i = 0; i < 300; i++)
        {
            var origin = GeoRandom.Origin(random);
            var frame = new LocalTangentFrame(origin);
            double a = GeoRandom.Uniform(random, 0.05, 1.2);

            var noseDown = Quaternion.FromMatrix(Matrix3.RotationY(a));
            var local = Orientation.ToLocal(LocalToGeocentricOrientation.ToDisEuler(noseDown, frame), origin);
            Assert.True(GeoAssert.AngleDistance(Math.PI / 2, local.Heading) <= 1e-8, "still heading east");
            Assert.Equal(-a, local.Pitch, 8);
            Assert.Equal(0.0, local.Roll, 8);

            var rollRight = Quaternion.FromMatrix(Matrix3.RotationX(a));
            local = Orientation.ToLocal(LocalToGeocentricOrientation.ToDisEuler(rollRight, frame), origin);
            Assert.True(GeoAssert.AngleDistance(Math.PI / 2, local.Heading) <= 1e-8);
            Assert.Equal(0.0, local.Pitch, 8);
            Assert.Equal(a, local.Roll, 8);
        }
    }

    [Fact]
    public void Heading_follows_a_yaw_about_up_at_any_origin()
    {
        var random = new Random(305);
        for (int i = 0; i < 300; i++)
        {
            var origin = GeoRandom.Origin(random);
            var frame = new LocalTangentFrame(origin);
            double yaw = GeoRandom.Uniform(random, -Math.PI, Math.PI);
            var q = Quaternion.FromEuler(yaw, 0, 0); // about ENU up
            var local = Orientation.ToLocal(LocalToGeocentricOrientation.ToDisEuler(q, frame), origin);

            // east = heading pi/2; a left (counter-clockwise) yaw decreases the compass heading
            Assert.True(GeoAssert.AngleDistance(Math.PI / 2 - yaw, local.Heading) <= 1e-8, $"yaw {yaw}: heading {local.Heading}");
            Assert.Equal(0.0, local.Pitch, 8);
            Assert.Equal(0.0, local.Roll, 8);
        }
    }

    [Fact]
    public void Vector_part_quaternion_reconstructs_w_and_treats_zero_as_identity()
    {
        Assert.Equal(Quaternion.Identity, LocalToGeocentricOrientation.FromVectorPart(0, 0, 0));
        var random = new Random(306);
        for (int i = 0; i < 2000; i++)
        {
            var q = GeoRandom.Rotation(random);
            var sent = LocalToGeocentricOrientation.ToVectorPart(q); // sender sends the w >= 0 representative
            var back = LocalToGeocentricOrientation.FromVectorPart(sent.X, sent.Y, sent.Z);
            Assert.True(back.W >= 0.0);
            GeoAssert.NearQuaternion(q.WithNonNegativeW(), back, 1e-7, "w reconstructed with a positive sign");
            Assert.True(Math.Abs(back.Length - 1.0) <= 1e-12);
        }

        // |v| slightly above 1 through rounding (float32 transport): w = 0 and the result is renormalised
        var edge = LocalToGeocentricOrientation.FromVectorPart(0.6, 0.8, 1e-6);
        Assert.Equal(0.0, edge.W);
        Assert.Equal(1.0, edge.Length, 12);
        var beyond = LocalToGeocentricOrientation.FromVectorPart(1, 1, 1);
        Assert.Equal(0.0, beyond.W);
        Assert.Equal(1.0, beyond.Length, 12);
        // the negative representative sends the same vector part as the positive one
        var qn = new Quaternion(-0.5, 0.5, 0.5, 0.5);
        Assert.Equal(new Vec3(-0.5, -0.5, -0.5), LocalToGeocentricOrientation.ToVectorPart(qn));
    }

    [Fact]
    public void Angular_velocity_between_enu_and_body_frd()
    {
        // Identity attitude: FLU = ENU axes. A turn about up is a turn about FRD z with the opposite sign.
        Assert.Equal(new Vec3(0, 0, -2), LocalToGeocentricOrientation.AngularVelocityEnuToBodyFrd(Quaternion.Identity, new Vec3(0, 0, 2)));
        // about ENU north (= FLU y = -FRD y)
        Assert.Equal(new Vec3(0, -3, 0), LocalToGeocentricOrientation.AngularVelocityEnuToBodyFrd(Quaternion.Identity, new Vec3(0, 3, 0)));
        // about the forward axis (east) it keeps its sign
        Assert.Equal(new Vec3(1, 0, 0), LocalToGeocentricOrientation.AngularVelocityEnuToBodyFrd(Quaternion.Identity, new Vec3(1, 0, 0)));

        // Yawed 90 degrees left (forward = north): a turn about east is a turn about the right axis: FRD +y.
        var yawLeft = new Quaternion(Math.Cos(Math.PI / 4), 0, 0, Math.Sin(Math.PI / 4));
        GeoAssert.Near(new Vec3(0, 1, 0), LocalToGeocentricOrientation.AngularVelocityEnuToBodyFrd(yawLeft, new Vec3(1, 0, 0)), 1e-12, "east axis for a north-heading body");

        var random = new Random(307);
        for (int i = 0; i < 2000; i++)
        {
            var q = GeoRandom.Rotation(random);
            var omega = GeoRandom.Vector(random, 3.0);
            var body = LocalToGeocentricOrientation.AngularVelocityEnuToBodyFrd(q, omega);
            GeoAssert.Near(omega, LocalToGeocentricOrientation.AngularVelocityBodyFrdToEnu(q, body), 1e-12, "round trip");
            Assert.True(Math.Abs(body.Length - omega.Length) <= 1e-12, "magnitude preserved");
            // consistent with the formula omega_body = F^T C^T omega_enu
            GeoAssert.Near(BodyAxes.Flip.Transpose() * (q.ToMatrix().Transpose() * omega), body, 1e-12, "formula");
        }
    }
}
