using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// The invariants G-1 .. G-8 as properties: seeded random traffic of every kind on both input sides (formations, entities,
/// platforms, aggregates, removals, damage, weapon status and split fires, session commands, a loopback that comes and goes) runs
/// through the showcase runner, whose checker - with the probe - evaluates every invariant after every tick. No violation may
/// appear, no step may throw and no rule may fail, whatever the order of the messages.
/// </summary>
public class ShowcaseRandomTrafficTests
{
    public static IEnumerable<object[]> Seeds => Enumerable.Range(1, 12).Select(seed => new object[] { seed });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_traffic_violates_no_invariant(int seed)
    {
        var random = new DeterministicRandom(seed);
        var act = new ActBuilder(0, "random traffic", 0);
        act.Expect("Module", string.Empty);      // unknown types, exhausted pools, repeated fires, ...
        act.Expect("Transaction", string.Empty); // aborted messages (unknown shooter)
        act.Expect("Session", string.Empty);     // commands that are invalid in the current state
        const int operations = 220;
        for (int i = 0; i < operations; i++)
        {
            double time = 0.5 + 0.1 * i;
            AddOperation(act, random, time, i);
        }

        var script = new ScenarioScript("random traffic " + seed, [act.Build()], [0.0], [], 0.5 + 0.1 * operations + 3.0);
        var runner = new ScenarioRunner(NedRprConfigurations.Showcase);
        var report = runner.Run(script);

        Assert.True(report.Violations.Count == 0, $"seed {seed}: " + string.Join("\n", report.Violations.Take(5).Select(v => $"{v.Id} at tick {v.Tick}: {v.Detail}")));
        Assert.Empty(report.StepFailures);
        Assert.Equal(0, runner.Context!.Engine.Diagnostics.RuleFailures);
        Assert.True(runner.Context.Engine.Diagnostics.MessagesProcessed > operations / 2, "the traffic did not reach the gateway");
        Assert.Equal(report.Ticks, runner.Context.Checker.Checks);
    }

    private static NedGeoSpatial Geo(DeterministicRandom r, ulong id) =>
        new(id, 50.0 + r.NextDouble(0, 0.01), 14.0 + r.NextDouble(0, 0.01), r.NextDouble(100, 600), r.NextDouble(0, 360), 0, 0,
            r.NextInt(3) == 0 ? 0.0 : r.NextDouble(1, 60));

    private static void AddOperation(ActBuilder act, DeterministicRandom r, double time, int index)
    {
        ulong nedId = (ulong)(30 + r.NextInt(4));
        int number = 1 + r.NextInt(8);
        switch (r.NextInt(17))
        {
            case 0:
            case 1:
            {
                var members = new List<NedFormationMember>();
                for (int i = 0; i < 4; i++)
                {
                    if (r.NextInt(3) != 0)
                    {
                        var g = Geo(r, 7);
                        members.Add(new NedFormationMember(i, g.LatitudeDeg, g.LongitudeDeg, g.Altitude, g.HeadingDeg, 0, 0, g.Speed, 101, r.NextInt(4)));
                    }
                }

                var message = new NedFormation(7, "Viper", 100, NedForce.Friendly, members.ToArray());
                act.Inject(time, "random formation", h => h.SendNed(message));
                break;
            }

            case 2:
            {
                var master = new NedEntityMaster(nedId, "N" + nedId, 200, NedForce.Friendly);
                var geo = Geo(r, nedId);
                act.Inject(time, "random NED entity", h =>
                {
                    h.SendNed(master);
                    h.SendNed(geo);
                });
                break;
            }

            case 3:
            {
                var geo = Geo(r, nedId);
                act.Inject(time, "random NED geo-spatial", h => h.SendNed(geo));
                break;
            }

            case 4:
            {
                ulong target = r.NextInt(5) == 0 ? 7UL : nedId;
                act.Inject(time, "random NED dispose", h => h.SendNed(new NedEntityDisposed(target)));
                break;
            }

            case 5:
            {
                var damage = new NedEntityDamage(r.NextInt(4) == 0 ? 7UL : nedId, r.NextInt(4));
                act.Inject(time, "random NED damage", h => h.SendNed(damage));
                break;
            }

            case 6:
            {
                var platform = S.Platform(number, "P" + number, lonOffset: r.NextDouble(0, 0.001), speed: r.NextInt(2) * 5.0);
                act.Inject(time, "random RPR platform", h => h.SendRpr(platform));
                break;
            }

            case 7:
            {
                var move = new RprObjectUpdate(S.PlatformHandle(number))
                {
                    WorldLocation = new WorldLocation(3_973_000 + r.NextDouble(0, 500), 999_000 + r.NextDouble(0, 500), 4_871_000),
                };
                act.Inject(time, "random RPR move", h => h.SendRpr(move));
                break;
            }

            case 8:
                act.Inject(time, "random RPR removal", h => h.SendRpr(new RprObjectRemoved(S.PlatformHandle(number))));
                break;
            case 9:
            {
                var update = new RprObjectUpdate(S.PlatformHandle(number)) { DamageState = (RprDamageState)r.NextInt(4) };
                act.Inject(time, "random RPR damage", h => h.SendRpr(update));
                break;
            }

            case 10:
            case 11:
            {
                var ids = Enumerable.Range(1, 8).Where(_ => r.NextInt(2) == 0).ToArray();
                var aggregate = S.Aggregate(0x700, "Wolf", ids);
                act.Inject(time, "random aggregate", h => h.SendRpr(aggregate));
                break;
            }

            case 12:
                act.Inject(time, "random aggregate removal", h => h.SendRpr(new RprObjectRemoved(new RprObjectHandle(0x700))));
                break;
            case 13:
            {
                var status = new NedWeaponStatus(nedId, 501, 20, r.NextInt(2) == 0 ? -1 : r.NextInt(4));
                act.Inject(time, "random weapon status", h => h.SendNed(status));
                break;
            }

            case 14:
            {
                var fireId = new Guid(index, 1, 2, [0, 0, 0, 0, 0, 0, 0, 1]);
                var fire = new NedWeaponFire(fireId, r.NextInt(2) == 0 ? 7UL : nedId, r.NextInt(3) == 0 ? 0UL : (ulong)(30 + r.NextInt(4)), 0,
                    50.0, 14.0, 300.0, 1 + r.NextInt(3), TargetMember: r.NextInt(3) == 0 ? r.NextInt(4) : -1, ShooterMember: r.NextInt(3) == 0 ? r.NextInt(4) : -1);
                bool completed = r.NextInt(2) == 0;
                act.Inject(time, "random NED fire", h =>
                {
                    h.SendNed(fire);
                    if (completed)
                    {
                        h.SendNed(new NedFireAmmunition(fireId, 502, 1));
                    }
                });
                break;
            }

            case 15:
            {
                var fire = new RprWeaponFire(new Guid(index, 9, 9, [0, 0, 0, 0, 0, 0, 0, 2]), S.PlatformHandle(number), S.PlatformHandle(1 + r.NextInt(8)),
                    new WorldLocation(3_973_000, 999_000, 4_871_000), default, 1);
                act.Inject(time, "random RPR fire", h => h.SendRpr(fire));
                break;
            }

            default:
            {
                var kind = (NedControlKind)r.NextInt(6);
                bool loop = r.NextInt(2) == 0;
                act.Do(time, "random loopback", c => c.LoopbackNed = loop);
                act.Inject(time, "random session command", h => h.SendNed(new NedSimControl(kind, kind is NedControlKind.Load or NedControlKind.LoadDebrief ? "x" : null)));
                break;
            }
        }
    }
}
