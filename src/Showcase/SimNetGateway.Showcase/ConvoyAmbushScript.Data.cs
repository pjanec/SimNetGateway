using SimNetGateway.Engine;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Showcase;

// The messages of the script: the cast's positions, the paths of the moving entities, the RPR platforms, the keep-alives.
public sealed partial class ConvoyAmbushScript
{
    /// <summary>A state of a moving entity as a NED sender reports it: geodetic position, heading and speed (level flight / road).</summary>
    private readonly record struct Geo(double Lat, double Lon, double Alt, double Heading, double Speed)
    {
        public NedGeoSpatial Message(ulong id) => new(id, Lat, Lon, Alt, Heading, 0, 0, Speed);

        public NedFormationMember Member(int index, int tkb, int damage = 0) =>
            new(index, Lat, Lon, Alt, Heading, 0, 0, Speed, tkb, damage);

        /// <summary>The state <paramref name="dt"/> world seconds later at constant velocity (the dead-reckoning model of both sides), with a new heading and speed.</summary>
        public Geo After(double dt, double heading, double speed)
        {
            NedRprConversions.GeoToPivot(Lat, Lon, Alt, Heading, 0, 0, Speed, out var p, out _, out var v);
            var moved = new Vec3(p.X + v.X * dt, p.Y + v.Y * dt, p.Z + v.Z * dt);
            NedRprConversions.EcefToGeodetic(moved, out double lat, out double lon, out double alt);
            return new Geo(lat, lon, alt, heading, speed);
        }
    }

    // ---- the cast's states ----------------------------------------------------------------------------------------------

    private static readonly Geo HawkStart = new(50.0, 14.0, 500.0, 90.0, 50.0);

    private static readonly Geo GateGeo = new(50.05, 14.05, 300.0, 0.0, 0.0);

    private static readonly Geo KestrelGeo = new(50.03, 14.03, 600.0, 270.0, 40.0);

    private static Geo ViperStart(int index) => new(50.02, 14.02 + 0.0003 * index, 300.0, 90.0, 10.0);

    /// <summary>The runner times at which the convoy's sender reports its state (its dead-reckoning threshold is exceeded: irregular).</summary>
    private static readonly double[] ViperReportTimes = [21.4, 26.1, 31.7, 37.9, 43.3, 48.2, 54.6];

    private static readonly double[] ViperReportHeadings = [92, 88, 91, 95, 93, 90, 90];

    private static readonly double[] ViperReportSpeeds = [9, 11, 10, 12, 10, 10, 10];

    private static NedFormation ViperMessage(Geo[] members, int[]? indexes = null, int[]? damage = null)
    {
        indexes ??= [0, 1, 2];
        var wire = new NedFormationMember[indexes.Length];
        for (int k = 0; k < indexes.Length; k++)
        {
            int i = indexes[k];
            wire[k] = members[i].Member(i, TkbTruck, damage is null ? 0 : damage[i]);
        }

        return new NedFormation(ViperId, "Viper", TkbViperUnit, NedForce.Friendly, wire);
    }

    /// <summary>A complete RPR platform update of a new RPR-origin entity (the DIS entity identifier is (5, 7, number)).</summary>
    private static RprObjectUpdate Platform(uint handle, string marking, int tkb, double lat, double lon, double alt, double heading,
        double speed, ushort number)
    {
        NedRprConversions.GeoToPivot(lat, lon, alt, heading, 0, 0, speed, out var p, out var o, out var v);
        return new RprObjectUpdate(new RprObjectHandle(handle))
        {
            Marking = marking,
            EntityType = DisOf(tkb),
            ForceId = RprForceId.Opposing,
            EntityIdentifier = new DisEntityId(RedSite, RedApplication, number),
            WorldLocation = NedRprConversions.ToWorldLocation(p),
            Orientation = NedRprConversions.ToRprOrientation(o),
            VelocityVector = NedRprConversions.ToRprVector(v),
            DeadReckoningAlgorithm = (byte)(speed > 0 ? 2 : 1),
        };
    }

    private static DisEntityType DisOf(int tkb)
    {
        var catalog = new TypeCatalog();
        return catalog.TryToDis(tkb, out var dis) ? dis : throw new InvalidOperationException("TKB type " + tkb + " is not in the catalog.");
    }

    private static DisEntityId WolfDis(int number) => new(RedSite, RedApplication, (ushort)number);

    private static RprObjectHandle WolfMember(int number) => new(WolfFirstMember + (uint)(number - 1));

    private static RprAggregateEntity WolfAggregate() =>
        new(new RprObjectHandle(WolfHandle), "Wolf", DisOf(TkbEnemyTank), RprForceId.Opposing,
            [WolfDis(1), WolfDis(2), WolfDis(3), WolfDis(4)]);

    /// <summary>Wolf-1, 2 and 4 stand in ambush; Wolf-3 creeps west at 2 m/s (so its dead-reckoning prediction keeps changing after it falls silent: S-9.2).</summary>
    private static RprObjectUpdate WolfPlatform(int number) =>
        Platform(WolfFirstMember + (uint)(number - 1), "Wolf-" + number, TkbEnemyTank, 50.12, 14.12 + 0.0004 * number, 280.0, 270.0,
            number == 3 ? 2.0 : 0.0, (ushort)number);

    private static readonly Geo T1Geo = new(50.10, 14.10, 300.0, 270.0, 0.0);

    private static RprObjectUpdate T1First() =>
        Platform(T1Handle, "T-1", TkbEnemyTank, T1Geo.Lat, T1Geo.Lon, T1Geo.Alt, T1Geo.Heading, 0, T1Number);

    /// <summary>Reinforcement n (1 .. 6): a static red tank near the squad.</summary>
    private static RprObjectUpdate Reinforcement(int n, double lonShift = 0) =>
        Platform(ReinforcementFirst + (uint)(n - 1), "R-" + n, TkbEnemyTank, 50.14, 14.14 + 0.0003 * n + lonShift, 260.0, 270.0, 0, (ushort)(10 + n));

    private static uint ReinforcementHandle(int n) => ReinforcementFirst + (uint)(n - 1);

    // ---- keep-alives ----------------------------------------------------------------------------------------------------

    /// <summary>The runner times at which an entity is refreshed: start, start + period, ... up to <paramref name="until"/> (inclusive).</summary>
    private static IEnumerable<double> Every(double start, double period, double until)
    {
        for (double t = start; t <= until + 1e-9; t += period)
        {
            yield return Math.Round(t, 3);
        }
    }

    private void KeepAlive(double time, uint handle, string marking) =>
        ActAt(time).Inject(time, "keep-alive " + marking, h => h.SendRpr(new RprObjectUpdate(new RprObjectHandle(handle)) { Marking = marking }), quiet: true);

    /// <summary>
    /// The RPR side refreshes every object it publishes, otherwise the gateway's stale reaper (12 s) removes it. The refreshes are
    /// unchanged re-sends of the marking (a write of the RPR side that changes nothing). Deliberate exceptions: Wolf-1 and Wolf-2 are
    /// silent from 39.5 s to 50.8 s (S-4.5 asks for RPR entities silent for 10 s at t = 50 s, 11.3 s is still below the timeout),
    /// Wolf-3 stops after 101 s (S-9.2), R-1 and R-2 are removed at 101 s (act 9).
    /// </summary>
    private void KeepAlives()
    {
        foreach (double t in Every(11.0, 8.0, 124.0))
        {
            if (t < 47.9 || t > 48.1)
            {
                KeepAlive(t, T1Handle, "T-1");
            }
        }

        foreach (double t in Every(13.0, 8.0, 124.0))
        {
            KeepAlive(t + 0.6, WolfHandle, "Wolf"); // the unit itself (a re-sent aggregate would also re-write its members)
            if (t >= 12.0)
            {
                KeepAlive(t + 0.2, WolfMember(4).Value, "Wolf-4");
            }

            if (t <= 101.1)
            {
                KeepAlive(t + 0.1, WolfMember(3).Value, "Wolf-3");
            }
        }

        // Wolf-1 and Wolf-2: 11.3, 19.3, 27.3, 35.3, 39.5, then 50.8 (silent for 11.3 s), then every 8 s.
        double[] silentFrom = [11.3, 19.3, 27.3, 35.3, 39.5];
        foreach (int member in new[] { 1, 2 })
        {
            double shift = member == 1 ? 0.0 : 0.05;
            foreach (double t in silentFrom)
            {
                KeepAlive(t + shift, WolfMember(member).Value, "Wolf-" + member);
            }

            foreach (double t in Every(50.8, 8.0, 124.0))
            {
                KeepAlive(t + shift, WolfMember(member).Value, "Wolf-" + member);
            }
        }

        // the reinforcements, from 95 s on (they appear at 89 s); R-1 and R-2 are removed at 101 s
        for (int n = 1; n <= 6; n++)
        {
            foreach (double t in Every(95.0 + 0.3 * n, 8.0, 124.0))
            {
                if (n <= 2 && t > 100.9)
                {
                    continue;
                }

                KeepAlive(t, ReinforcementHandle(n), "R-" + n);
            }
        }
    }
}
