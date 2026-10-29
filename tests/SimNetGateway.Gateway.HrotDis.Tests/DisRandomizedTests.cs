using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using static SimNetGateway.Gateway.HrotDis.Tests.DisPdus;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>
/// Seeded random traffic from both sides against the whole gateway (HROT states, removals, fire, detonation, sessions, origins; DIS
/// Entity State, Remove Entity, Fire, Detonation, Start/Resume, Stop/Freeze, the gateway's own PDUs looped back, time passing).
/// Every step runs the HROT-half invariants (G-1, G-2, G-4, G-5, G-7, G-8) and the DIS-half ones of <see cref="DisInvariantChecker"/>
/// (full PDUs, thresholds, heartbeat, no redundant send, one final PDU, numbers and quarantine, session and origin gates), and after
/// every operation a model of the DIS entities: one updated within the timeout exists, one silent beyond timeout and check period
/// does not, a deactivated or removed one is gone, and a DIS entity of the model that vanished only did so by an unload.
/// </summary>
public class DisRandomizedTests(Xunit.Abstractions.ITestOutputHelper output)
{
    public static TheoryData<int, bool, bool, bool> Seeds() => new()
    {
        // seed, small number pool, dis controls hrot, send events in replay
        { 1, false, false, false },
        { 2, false, true, true },
        { 3, false, true, false },
        { 4, true, false, true },
        { 5, true, true, false },
        { 6, true, false, false },
    };

    private static readonly long[] Tkbs = [100, 101, 102, 200, 300, 400, 9999];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_traffic_keeps_every_invariant(int seed, bool smallPool, bool disControlsHrot, bool eventsInReplay)
    {
        var random = new Random(seed);
        var config = GatewayConfiguration.CreateDefault() with
        {
            Shared = new SharedConfiguration { SendEventsInReplay = eventsInReplay },
            Dis = new DisConfiguration { ExerciseId = 1, DisControlsHrot = disControlsHrot },
        };
        var numbering = smallPool ? new DisNumbering(1, 24, 8.0) : null;
        var h = new HrotDisHarness(config, idLast: 5_000_080, numbering: numbering);
        h.DisChecker!.ExpectAllSent = !smallPool;
        double timeout = config.Dis!.TimeoutS;
        double reaperPeriod = HrotDisProfile.ReaperCheckPeriod.TotalSeconds;
        var positions = new Dictionary<long, HrotVec3>();
        var model = new Dictionary<DisEntityKey, double>(); // DIS entities that must exist: key -> time of the last accepted PDU
        var rows = SessionTests.Table;
        bool originValid = false;
        int generation = 0;

        for (int op = 0; op < 1500; op++)
        {
            var before = (h.Engine.Session.State, h.Engine.Session.Mode);
            switch (random.Next(28))
            {
                case 0:
                case 1:
                case 2:
                case 3:
                case 4:
                case 5:
                    long id = 1 + random.Next(40);
                    h.Hrot(RandomState(random, id, positions));
                    break;
                case 6:
                    h.Hrot(new HrotEntityRemoved(1 + random.Next(40)));
                    break;
                case 7:
                case 25:
                    h.Hrot(new HrotFire(LiveHrotId(h, random), random.Next(3) == 0 ? 0 : LiveHrotId(h, random), random.Next(3) == 0 ? 502 : 501, 0,
                        RandomPoint(random), RandomPoint(random), 1));
                    break;
                case 8:
                case 26:
                    h.Hrot(new HrotDetonation(LiveHrotId(h, random), random.Next(3) == 0 ? 0 : LiveHrotId(h, random), random.Next(3) == 0 ? 502 : 501, 0,
                        RandomPoint(random), default));
                    break;
                case 9:
                case 10:
                    var row = rows[random.Next(rows.Length)];
                    h.Hrot(new HrotSession(row.Phase, row.Advancing, 1.0, op));
                    break;
                case 11:
                    if (random.Next(3) == 0 || !originValid)
                    {
                        generation++;
                        h.SendOrigin(random.NextDouble() * 160 - 80, random.NextDouble() * 360 - 180, random.NextDouble() * 1000, generation);
                        originValid = true;
                    }

                    break;
                case 12:
                case 13:
                case 14:
                case 15:
                    if (originValid)
                    {
                        var key = RandomKey(random);
                        var pdu = RandomEntityState(h, random, key);
                        h.Dis(pdu);
                        bool deactivated = Protocol.Dis7.EntityAppearance.GetDeactivated(pdu.EntityAppearance);
                        if (deactivated)
                        {
                            model.Remove(key);
                        }
                        else
                        {
                            model[key] = h.Clock.Now + HrotDisHarness.Dt; // received in the coming tick
                        }
                    }

                    break;
                case 16:
                    {
                        var key = RandomKey(random);
                        h.Dis(RemoveEntity(key));
                        model.Remove(key);
                        break;
                    }
                case 17:
                    if (originValid)
                    {
                        var shooter = RandomKey(random);
                        DisEntityKey? target = random.Next(3) == 0 ? null : RandomKey(random);
                        h.Dis(Fire(shooter, target, h.Frame!.PointToEcef(Support.V(RandomPoint(random))), default, 0, (ushort)random.Next(1, 200)));
                    }

                    break;
                case 18:
                    if (originValid)
                    {
                        var shooter = RandomKey(random);
                        DisEntityKey? target = random.Next(3) == 0 ? null : RandomKey(random);
                        h.Dis(Detonation(shooter, target, h.Frame!.PointToEcef(Support.V(RandomPoint(random))), default, default, 0, (byte)random.Next(1, 4),
                            (ushort)random.Next(1, 200)));
                    }

                    break;
                case 19:
                    var to = random.Next(3) == 0 ? Receiving(1, 3001) : random.Next(2) == 0 ? DisConversions.AllEntities() : Receiving(7, 7);
                    if (random.Next(2) == 0)
                    {
                        h.Dis(StopFreeze(to));
                    }
                    else
                    {
                        h.Dis(StartResume(to));
                    }

                    break;
                case 20:
                case 21:
                    // the gateway's own traffic comes back (broadcast loopback)
                    var states = h.DisChecker.States;
                    if (states.Count > 0 && random.Next(2) == 0)
                    {
                        h.Dis(ThroughTheWire(states[random.Next(states.Count)].Pdu));
                    }
                    else if (h.DisChecker.Others.Count > 0)
                    {
                        switch (h.DisChecker.Others[random.Next(h.DisChecker.Others.Count)])
                        {
                            case FirePdu f: h.Dis(ThroughTheWire(f)); break;
                            case DetonationPdu d: h.Dis(ThroughTheWire(d)); break;
                            case StartResumePdu s: h.Dis(ThroughTheWire(s)); break;
                            case StopFreezePdu s: h.Dis(ThroughTheWire(s)); break;
                        }
                    }

                    break;
                default:
                    h.Run(random.Next(1, 8)); // time passes: heartbeats, drift, the reaper
                    break;
            }

            h.Step();
            h.Steps(random.Next(3));

            var after = (h.Engine.Session.State, h.Engine.Session.Mode);
            bool unloaded = before.State != SessionState.Idle && (after.State == SessionState.Idle || before.Mode != after.Mode ||
                            (after.State == SessionState.Loaded && before.State is SessionState.Running or SessionState.Paused));
            AssertModel(h, model, unloaded, timeout, reaperPeriod);
        }

        // unload everything: nothing may be left anywhere, every sent entity got its final PDU, every number is back
        h.Session(ClusterPhase.Live);
        h.Session(ClusterPhase.Idle);
        Assert.Equal(0, h.Engine.State.Count);
        Assert.Equal(0, h.DisIds!.Count);
        Assert.Equal(0, h.HrotIds.Count);
        Assert.Equal(0UL, h.Ids.InUse);
        Assert.Empty(h.Checker.Introduced);
        Assert.Equal(0, h.Engine.Diagnostics.RuleFailures);
        Assert.True(h.DisChecker.States.Count > 50, "the run should have produced entity state traffic");
        output.WriteLine($"states {h.DisChecker.States.Count} finals {h.DisChecker.States.Count(s => Protocol.Dis7.EntityAppearance.GetDeactivated(s.Pdu.EntityAppearance))} fires {h.DisChecker.Sent<FirePdu>().Count()} detonations {h.DisChecker.Sent<DetonationPdu>().Count()} start {h.DisChecker.Sent<StartResumePdu>().Count()} stop {h.DisChecker.Sent<StopFreezePdu>().Count()} echoes {h.Services.DisIngress.Echoes} siman-ignored {h.Services.DisIngress.SimanIgnored} injectFire {h.Sent<InjectFire>().Count} replicas {h.Sent<ReplicaState>().Count} removed {h.Sent<ReplicaRemoved>().Count} timeRequests {h.Sent<TimeRequest>().Count}");
    }

    private static HrotEntityState RandomState(Random random, long id, Dictionary<long, HrotVec3> positions)
    {
        positions.TryGetValue(id, out var last);
        HrotVec3 position = random.Next(10) switch
        {
            < 5 => new HrotVec3(last.X + random.NextDouble() * 0.2 - 0.1, last.Y + random.NextDouble() * 0.2 - 0.1, last.Z), // no drift
            < 8 => new HrotVec3(last.X + random.NextDouble() * 6 - 3, last.Y + random.NextDouble() * 6 - 3, last.Z),
            _ => RandomPoint(random),
        };
        positions[id] = position;
        long tkb = random.Next(12) == 0 ? 9999 : Tkbs[random.Next(Tkbs.Length - 1)];
        long disType = random.Next(25) == 0 ? new DisTypeCode(11, 1, 225, 1, 0, 0, 0).Pack() : 0;
        double yaw = random.Next(3) == 0 ? random.NextDouble() * 0.2 - 0.1 : 0.0;
        var rotation = new HrotQuat(0, 0, Math.Sin(yaw / 2), Math.Cos(yaw / 2));
        double health = random.Next(4) switch { 0 => 100, 1 => 80, 2 => 40, _ => 0 };
        string marking = random.Next(5) switch { 0 => "T-" + id, 1 => "café-" + id, 2 => "A-VERY-LONG-MARKING-" + id, _ => "M" + id };
        var velocity = random.Next(3) == 0 ? new HrotVec3(random.NextDouble() * 20, 0, 0) : default;
        return HrotDisHarness.State(id, position, tkb, marking, 1 + random.Next(3), health, 100, rotation: rotation, velocity: velocity, disType: disType);
    }

    /// <summary>The HROT id of a random live HROT entity (an id that may not exist when there is none).</summary>
    private static long LiveHrotId(HrotDisHarness h, Random random)
    {
        var ids = new List<long>();
        foreach (var e in h.Entities(h.HrotSource))
        {
            if (h.HrotIds.TryGetKey(e.Id, out long id))
            {
                ids.Add(id);
            }
        }

        return ids.Count == 0 ? 1 + random.Next(40) : ids[random.Next(ids.Count)];
    }

    private static HrotVec3 RandomPoint(Random random) =>
        new(random.NextDouble() * 4000 - 2000, random.NextDouble() * 4000 - 2000, random.NextDouble() * 100);

    private static DisEntityKey RandomKey(Random random) => new(PeerSite, random.Next(8) == 0 ? (ushort)78 : PeerApplication, (ushort)(1 + random.Next(30)));

    private static EntityStatePdu RandomEntityState(HrotDisHarness h, Random random, DisEntityKey key)
    {
        var ecef = h.Frame!.PointToEcef(Support.V(RandomPoint(random)));
        var velocity = h.Frame.VectorToEcef(new Vec3(random.NextDouble() * 20 - 10, random.NextDouble() * 20 - 10, 0));
        return EntityState(key.Number, ecef, new Vec3(random.NextDouble() * 6 - 3, random.NextDouble() * 2 - 1, random.NextDouble() * 6 - 3), velocity,
            algorithm: random.Next(1, 6), damage: random.Next(4), deactivated: random.Next(12) == 0, marking: "D" + random.Next(100),
            force: (byte)random.Next(4), site: key.Site, application: key.Application);
    }

    /// <summary>
    /// The DIS entities of the model exist while they are younger than the timeout, are gone once they are older than the timeout plus the
    /// reaper's period, and a model entity that vanished early did so by an unload (which empties the model). Nothing that is not in the
    /// model exists.
    /// </summary>
    private static void AssertModel(HrotDisHarness h, Dictionary<DisEntityKey, double> model, bool unloaded, double timeout, double reaperPeriod)
    {
        if (unloaded)
        {
            model.Clear();
        }

        double now = h.Clock.Now;
        var present = new HashSet<DisEntityKey>();
        foreach (var e in h.Entities(h.DisSource))
        {
            if (h.DisIds!.TryGetKey(e.Id, out var key))
            {
                present.Add(key);
            }
        }

        foreach (var (key, last) in model.ToList())
        {
            double age = now - last;
            bool exists = present.Contains(key);
            if (age <= timeout)
            {
                Assert.True(exists, $"DIS entity {key} is {age:F2} s old (timeout {timeout}) but gone");
            }
            else if (age > timeout + reaperPeriod + 2 * HrotDisHarness.Dt)
            {
                Assert.False(exists, $"DIS entity {key} is {age:F2} s old (timeout {timeout}) and still there");
                model.Remove(key);
            }
        }

        foreach (var key in present)
        {
            Assert.True(model.ContainsKey(key), $"DIS entity {key} exists but the model has no live PDU for it");
        }
    }
}
