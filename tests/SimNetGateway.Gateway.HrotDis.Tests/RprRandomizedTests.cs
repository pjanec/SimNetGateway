using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using SimNetGateway.Protocol.Rpr;
using static SimNetGateway.Gateway.HrotDis.Tests.DisPdus;
using static SimNetGateway.Gateway.HrotDis.Tests.RprMessages;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>
/// Seeded random traffic from HROT, from DIS and from the federation against the whole gateway with BOTH sides running (HROT states,
/// removals, fire, detonation, sessions, origins; DIS entity states and events; RPR object updates, removals, interactions, update
/// requests, registration answers of an unknown token, the gateway's own updates coming back; a silent RTI; time passing). Every step
/// runs the HROT-half invariants, the DIS-half ones and the HLA-half ones (<see cref="RprInvariantChecker"/>): full updates, the
/// threshold invariant of EACH side with its own thresholds, the origin rule (no dis -> HLA, no rpr -> DIS, no echo, no hrot entity
/// from an rpr handle), exactly one removal per object, numbers; and after every operation a model of the rpr entities: one updated
/// within <c>hla.timeoutS</c> exists, one silent beyond timeout and check period does not.
/// </summary>
public class RprRandomizedTests(Xunit.Abstractions.ITestOutputHelper output)
{
    public static TheoryData<int, bool, bool> Seeds() => new()
    {
        // seed, small number pool, events in replay
        { 11, false, false },
        { 12, false, true },
        { 13, true, false },
        { 14, true, true },
        { 15, false, false },
        { 16, false, true },
    };

    private static readonly long[] Tkbs = [100, 101, 102, 200, 300, 400];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_traffic_keeps_every_invariant_on_both_sides(int seed, bool smallPool, bool eventsInReplay)
    {
        var random = new Random(seed);
        var config = Both(
            hla: x => x with { PositionThresholdM = 0.5 + random.NextDouble() * 3, OrientationThresholdDeg = 1 + random.NextDouble() * 6, HeartbeatS = 2 + random.Next(4), TimeoutS = 5 + random.Next(6) },
            dis: x => x with { PositionThresholdM = 0.5 + random.NextDouble() * 3, HeartbeatS = 2 + random.Next(4) },
            shared: new SharedConfiguration { SendEventsInReplay = eventsInReplay });
        var numbering = smallPool ? new DisNumbering(1, 20, 6.0) : null;
        var h = new HrotDisHarness(config, idLast: 5_000_080, numbering: numbering);
        h.DisChecker!.ExpectAllSent = !smallPool;
        h.RprChecker!.ExpectAllSent = !smallPool;
        double timeout = config.Hla!.TimeoutS;
        double reaperPeriod = HrotDisProfile.ReaperCheckPeriod.TotalSeconds;
        var positions = new Dictionary<long, HrotVec3>();
        var model = new Dictionary<uint, double>(); // rpr objects that must exist: handle -> time of the last accepted update
        var rows = SessionTests.Table;
        bool originValid = false;
        int generation = 0;
        int requests = 0;

        for (int op = 0; op < 1500; op++)
        {
            var before = (h.Engine.Session.State, h.Engine.Session.Mode);
            h.RprChecker.AllowRedundant = false;
            switch (random.Next(34))
            {
                case 0:
                case 1:
                case 2:
                case 3:
                case 4:
                case 5:
                    h.Hrot(RandomState(random, 1 + random.Next(40), positions));
                    break;
                case 6:
                    h.Hrot(new HrotEntityRemoved(1 + random.Next(40)));
                    break;
                case 7:
                case 8:
                    h.Hrot(new HrotFire(LiveHrotId(h, random), random.Next(3) == 0 ? 0 : LiveHrotId(h, random), random.Next(3) == 0 ? 502 : 501, 0,
                        RandomPoint(random), RandomPoint(random), 1));
                    break;
                case 9:
                case 10:
                    h.Hrot(new HrotDetonation(LiveHrotId(h, random), random.Next(3) == 0 ? 0 : LiveHrotId(h, random), random.Next(3) == 0 ? 502 : 501, 0,
                        RandomPoint(random), default));
                    break;
                case 11:
                case 12:
                    var row = rows[random.Next(rows.Length)];
                    h.Hrot(new HrotSession(row.Phase, row.Advancing, 1.0, op));
                    break;
                case 13:
                    if (random.Next(3) == 0 || !originValid)
                    {
                        generation++;
                        h.SendOrigin(random.NextDouble() * 160 - 80, random.NextDouble() * 360 - 180, random.NextDouble() * 1000, generation);
                        originValid = true;
                    }

                    break;
                case 14:
                case 15:
                    if (originValid)
                    {
                        var key = new DisEntityKey(PeerSite, PeerApplication, (ushort)(1 + random.Next(15)));
                        h.Dis(EntityState(key.Number, h.Frame!.PointToEcef(Support.V(RandomPoint(random))), marking: "D" + random.Next(50), force: (byte)random.Next(4)));
                    }

                    break;
                case 16:
                case 17:
                case 18:
                case 19:
                    if (originValid)
                    {
                        uint n = (uint)(1 + random.Next(20));
                        var update = Update(h, Peer(n), RandomEnu(random), "R" + random.Next(100), random.Next(5) == 0 ? new DisEntityType(1, 2, 225, 3, 1, 0, 0) : Tank,
                            (RprForceId)random.Next(4), (RprDamageState)random.Next(4), random.Next(3) == 0 ? new Vec3(random.NextDouble() * 10, 0, 0) : default,
                            (byte)random.Next(1, 6), new Vec3(random.NextDouble() * 6 - 3, random.NextDouble() * 2 - 1, random.NextDouble() * 6 - 3));
                        h.Rpr(update);
                        model[Peer(n).Value] = h.Clock.Now + HrotDisHarness.Dt;
                    }

                    break;
                case 20:
                    {
                        uint n = (uint)(1 + random.Next(20));
                        h.Rpr(new RprObjectRemoved(Peer(n)));
                        model.Remove(Peer(n).Value);
                        break;
                    }
                case 21:
                    if (originValid)
                    {
                        h.Rpr(new RprWeaponFire(Guid.NewGuid(), RandomHandle(h, random), random.Next(3) == 0 ? null : RandomHandle(h, random),
                            RprConversions.ToLocation(h.Frame!.PointToEcef(Support.V(RandomPoint(random)))), MunitionKind, random.Next(1, 5)));
                    }

                    break;
                case 22:
                    if (originValid)
                    {
                        h.Rpr(new RprMunitionDetonation(Guid.NewGuid(), RandomHandle(h, random), random.Next(3) == 0 ? null : RandomHandle(h, random),
                            RprConversions.ToLocation(h.Frame!.PointToEcef(Support.V(RandomPoint(random)))), MunitionKind, (RprDetonationResult)random.Next(0, 7)));
                    }

                    break;
                case 23:
                    h.RprChecker.AllowRedundant = true;
                    requests++;
                    h.Rpr(new RprUpdateRequested(RandomHandle(h, random) ?? Peer(1)));
                    break;
                case 24:
                    h.Rpr(new RprObjectRegistered(1_000_000 + random.Next(500), Peer((uint)random.Next(50, 60)))); // a token the gateway never issued
                    break;
                case 25:
                    // the gateway's own updates come back through the federation: an echo
                    // (only objects that still exist: an RTI delivers nothing for a deleted object)
                    var updates = h.RprChecker.Updates;
                    if (updates.Count > 0)
                    {
                        var echo = updates[random.Next(updates.Count)];
                        if (h.RprHandles!.TryResolve(echo.Handle, out var echoed) && h.Engine.State.Find(echoed) is not null)
                        {
                            h.Rpr(echo);
                        }
                    }

                    break;
                case 26:
                    h.DeliverRti = random.Next(3) != 0; // a silent RTI for a while
                    break;
                case 27:
                    if (originValid)
                    {
                        var shooter = new DisEntityKey(PeerSite, PeerApplication, (ushort)(1 + random.Next(15)));
                        h.Dis(Fire(shooter, null, h.Frame!.PointToEcef(Support.V(RandomPoint(random))), default, 0, (ushort)random.Next(1, 200)));
                    }

                    break;
                default:
                    h.Run(random.Next(1, 8)); // time passes: heartbeats, drift, the reapers
                    break;
            }

            h.Step();
            h.Steps(random.Next(3));

            var after = (h.Engine.Session.State, h.Engine.Session.Mode);
            bool unloaded = before.State != SessionState.Idle && (after.State == SessionState.Idle || before.Mode != after.Mode ||
                            (after.State == SessionState.Loaded && before.State is SessionState.Running or SessionState.Paused));
            AssertModel(h, model, unloaded, timeout, reaperPeriod);
        }

        // unload everything: nothing may be left anywhere, every registered object was removed, every number is back
        h.DeliverRti = true;
        h.Session(ClusterPhase.Live);
        h.Steps(3);
        h.Session(ClusterPhase.Idle);
        h.Steps(3);
        Assert.Equal(0, h.Engine.State.Count);
        Assert.Equal(0, h.RprIds!.Count);
        Assert.Equal(0, h.RprHandles!.Count);
        Assert.Equal(0, h.DisIds!.Count);
        Assert.Equal(0, h.HrotIds.Count);
        Assert.Equal(0UL, h.Ids.InUse);
        Assert.Equal(0, h.Engine.Diagnostics.RuleFailures);
        Assert.Equal(h.Answered.Count, h.RprChecker.Sent<RprObjectRemoved>().Count()); // every object the RTI gave was removed exactly once
        Assert.True(h.RprChecker.Updates.Count > 50, "the run should have produced HLA traffic");
        output.WriteLine($"updates {h.RprChecker.Updates.Count} registers {h.RprChecker.Sent<RprRegisterObject>().Count()} removals {h.RprChecker.Sent<RprObjectRemoved>().Count()} fires {h.RprChecker.Sent<RprWeaponFire>().Count()} detonations {h.RprChecker.Sent<RprMunitionDetonation>().Count()} requests {requests} | dis states {h.DisChecker.States.Count} | replicas {h.Sent<ReplicaState>().Count} injectFire {h.Sent<InjectFire>().Count} injectDetonation {h.Sent<InjectDetonation>().Count}");
    }

    private static Vec3 RandomEnu(Random random) => new(random.NextDouble() * 4000 - 2000, random.NextDouble() * 4000 - 2000, random.NextDouble() * 100);

    private static HrotVec3 RandomPoint(Random random) =>
        new(random.NextDouble() * 4000 - 2000, random.NextDouble() * 4000 - 2000, random.NextDouble() * 100);

    private static HrotEntityState RandomState(Random random, long id, Dictionary<long, HrotVec3> positions)
    {
        positions.TryGetValue(id, out var last);
        HrotVec3 position = random.Next(10) switch
        {
            < 5 => new HrotVec3(last.X + random.NextDouble() * 0.2 - 0.1, last.Y + random.NextDouble() * 0.2 - 0.1, last.Z),
            < 8 => new HrotVec3(last.X + random.NextDouble() * 6 - 3, last.Y + random.NextDouble() * 6 - 3, last.Z),
            _ => RandomPoint(random),
        };
        positions[id] = position;
        long tkb = Tkbs[random.Next(Tkbs.Length)];
        long disType = random.Next(25) == 0 ? new DisTypeCode(11, 1, 225, 1, 0, 0, 0).Pack() : 0;
        double yaw = random.Next(3) == 0 ? random.NextDouble() * 0.2 - 0.1 : 0.0;
        var rotation = new HrotQuat(0, 0, Math.Sin(yaw / 2), Math.Cos(yaw / 2));
        double health = random.Next(4) switch { 0 => 100, 1 => 80, 2 => 40, _ => 0 };
        string marking = random.Next(5) switch { 0 => "T-" + id, 1 => "café-" + id, 2 => "A-VERY-LONG-MARKING-" + id, _ => "M" + id };
        var velocity = random.Next(3) == 0 ? new HrotVec3(random.NextDouble() * 20, 0, 0) : default;
        return HrotDisHarness.State(id, position, tkb, marking, 1 + random.Next(3), health, 100, rotation: rotation, velocity: velocity, disType: disType);
    }

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

    /// <summary>A handle for an interaction: one of the gateway's objects, one of the federation's, or one nobody knows.</summary>
    private static RprObjectHandle? RandomHandle(HrotDisHarness h, Random random)
    {
        switch (random.Next(5))
        {
            case 0:
                return null;
            case 1:
                return Peer((uint)random.Next(100, 110));
            case 2:
            case 3:
                return Peer((uint)(1 + random.Next(20)));
            default:
                var ours = h.Answered.Values.ToList();
                return ours.Count == 0 ? Peer(1) : ours[random.Next(ours.Count)];
        }
    }

    private static void AssertModel(HrotDisHarness h, Dictionary<uint, double> model, bool unloaded, double timeout, double reaperPeriod)
    {
        if (unloaded)
        {
            model.Clear();
        }

        double now = h.Clock.Now;
        var present = new HashSet<uint>();
        foreach (var e in h.Entities(h.RprSource))
        {
            if (h.RprHandles!.TryGetKey(e.Id, out var handle))
            {
                present.Add(handle.Value);
            }
        }

        foreach (var (handle, last) in model.ToList())
        {
            double age = now - last;
            bool exists = present.Contains(handle);
            if (age <= timeout)
            {
                Assert.True(exists, $"rpr object {handle:X} is {age:F2} s old (timeout {timeout}) but gone");
            }
            else if (age > timeout + reaperPeriod + 2 * HrotDisHarness.Dt)
            {
                Assert.False(exists, $"rpr object {handle:X} is {age:F2} s old (timeout {timeout}) and still there");
                model.Remove(handle);
            }
        }

        foreach (var handle in present)
        {
            Assert.True(model.ContainsKey(handle), $"rpr object {handle:X} exists but the model has no live update for it");
        }
    }
}
