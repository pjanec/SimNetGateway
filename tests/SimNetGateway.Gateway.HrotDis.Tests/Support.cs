using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

internal static class Support
{
    public static void Near(Vec3 expected, Vec3 actual, double tolerance, string? what = null) =>
        Assert.True(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance && Math.Abs(expected.Z - actual.Z) <= tolerance,
            $"{what}: expected {expected} but was {actual} (tolerance {tolerance})");

    public static void Near(HrotVec3 expected, HrotVec3 actual, double tolerance, string? what = null) =>
        Near(new Vec3(expected.X, expected.Y, expected.Z), new Vec3(actual.X, actual.Y, actual.Z), tolerance, what);

    public static void Near(Vec3 expected, HrotVec3 actual, double tolerance, string? what = null) =>
        Near(expected, new Vec3(actual.X, actual.Y, actual.Z), tolerance, what);

    public static void SameRotation(HrotQuat expected, HrotQuat actual, double tolerance, string? what = null)
    {
        double angle = Quaternion.AngleBetween(GeoFrame.ToQuaternion(expected), GeoFrame.ToQuaternion(actual));
        Assert.True(angle <= tolerance, $"{what}: rotations differ by {angle} rad (tolerance {tolerance})");
    }

    public static Vec3 V(HrotVec3 v) => new(v.X, v.Y, v.Z);

    public static HrotVec3 H(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The events dispatched so far (the engine keeps the last ones), oldest first, copied out of the log.</summary>
    public static List<(EventTypeId Type, SourceId Source, AttributeBag Payload)> Events(HrotDisHarness h)
    {
        var list = new List<(EventTypeId, SourceId, AttributeBag)>();
        foreach (var ev in h.Engine.State.Journal.RecentEvents)
        {
            list.Add((ev.Type, ev.Source, ev.Payload));
        }

        return list;
    }

    public static AttributeBag LastEvent(HrotDisHarness h, EventTypeId type)
    {
        AttributeBag? found = null;
        foreach (var ev in h.Engine.State.Journal.RecentEvents)
        {
            if (ev.Type == type)
            {
                found = ev.Payload;
            }
        }

        return found ?? throw new InvalidOperationException("No such event was dispatched.");
    }

    public static int CountEvents(HrotDisHarness h, EventTypeId type)
    {
        int n = 0;
        foreach (var ev in h.Engine.State.Journal.RecentEvents)
        {
            if (ev.Type == type)
            {
                n++;
            }
        }

        return n;
    }
}
